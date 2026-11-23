"""Build original, unhinted TrueType symbols from the live Core contour export.

Set KOTU_DESIGN_ICON_EXPORT to an absolute JSON path, run DesignIconTests, then:
    python packaging/gen_design_icon_fonts.py export.json src/KOTU.App/Assets/Fonts
No third-party font outlines or installed fonts are read. --verify checks existing
font tables, checksums, codepoint mappings and every contour against the export.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct


def pack(fmt, *values):
    return struct.pack('>' + fmt, *values)


def checksum(data):
    data += b'\0' * (-len(data) % 4)
    return sum(struct.unpack('>' + 'I' * (len(data) // 4), data)) & 0xffffffff


def quantize(contour):
    # TrueType uses Y-up; the shared live geometry uses Y-down.
    return [(round(p['X'] * 1000 / 24), round(850 - p['Y'] * 1000 / 24)) for p in contour]


def glyph(contours):
    if not contours:
        return b'', 0, 0
    points = [p for c in contours for p in c]
    ends, count = [], 0
    for contour in contours:
        count += len(contour)
        ends.append(count - 1)
    data = pack('hhhhh', len(contours), min(x for x, y in points), min(y for x, y in points),
                max(x for x, y in points), max(y for x, y in points))
    data += pack('H' * len(ends), *ends) + pack('H', 0)
    data += b'\x01' * len(points)  # On-curve points, full signed delta coordinates.
    for axis in (0, 1):
        previous = 0
        for point in points:
            data += pack('h', point[axis] - previous)
            previous = point[axis]
    return data + b'\0' * (-len(data) % 4), len(points), len(contours)


def name_table(family):
    names = {0: 'Copyright (c) 2026 ZP Studios', 1: family, 2: 'Regular',
             3: family + ';1.0', 4: family, 5: 'Version 1.0', 6: family.replace(' ', '')}
    records, strings = [], b''
    for key, value in sorted(names.items()):
        encoded = value.encode('utf-16-be')
        records.append(pack('HHHHHH', 3, 1, 0x409, key, len(encoded), len(strings)))
        strings += encoded
    return pack('HHH', 0, len(records), 6 + 12 * len(records)) + b''.join(records) + strings


def cmap_table(mapping):
    mappings = sorted(mapping.items()) + [(0xffff, 0)]
    count = len(mappings)
    power = 2 ** (count.bit_length() - 1)
    body = pack('HHHHHHH', 4, 16 + 8 * count, 0, 2 * count, 2 * power, power.bit_length() - 1, 2 * (count - power))
    body += pack('H' * count, *(code for code, index in mappings)) + pack('H', 0)
    body += pack('H' * count, *(code for code, index in mappings))
    body += pack('H' * count, *((index - code) & 0xffff for code, index in mappings))
    body += b'\0\0' * count
    return pack('HH', 0, 2) + pack('HHI', 0, 3, 20) + pack('HHI', 3, 1, 20) + body


def make_font(profile):
    icons = sorted(profile['Icons'], key=lambda x: x['Codepoint'])
    outlines = [[], []] + [[quantize(c) for c in icon['Drawing']['Contours']] for icon in icons]
    glyf, offsets, max_points, max_contours = b'', [], 0, 0
    for contours in outlines:
        offsets.append(len(glyf))
        encoded, points, count = glyph(contours)
        glyf += encoded
        max_points = max(max_points, points)
        max_contours = max(max_contours, count)
    offsets.append(len(glyf))
    count = len(outlines)
    bearings = [min((x for c in glyph for x, y in c), default=0) for glyph in outlines]
    right_edges = [max((x for c in glyph for x, y in c), default=0) for glyph in outlines]
    mapping = {32: 1, **{icon['Codepoint']: i + 2 for i, icon in enumerate(icons)}}
    # head, hhea and OS/2 deliberately share fixed metrics across all ten packs.
    head = pack('IIIIHHQQhhhhHHhhh', 0x10000, 0x10000, 0, 0x5f0f3cf5, 3, 1000,
                0, 0, 0, -150, 1000, 850, 0, 8, 2, 1, 0)
    hhea = pack('IhhhHhhhhhhhhhhhH', 0x10000, 850, -150, 0, 1000, min(bearings), 1000-max(right_edges), max(right_edges),
                1, 0, 0, 0, 0, 0, 0, 0, count)
    maxp = pack('IH' + 'H' * 13, 0x10000, count, max_points, max_contours, 0, 0,
                2, 0, 0, 0, 0, 0, 0, 0, 0)
    os2 = pack('HhHHH', 0, 1000, 400, 5, 0)
    os2 += pack('hhhhhhhhhhh', 650, 650, 0, 75, 650, 650, 0, 350, 50, 250, 0)
    os2 += b'\0' * 10 + pack('IIII', 0, 0, 0, 0) + b'KOTU'
    os2 += pack('HHHhhhHH', 0x40, 32, max(mapping), 850, -150, 0, 1000, 200)
    tables = {
        b'OS/2': os2, b'cmap': cmap_table(mapping), b'glyf': glyf, b'head': head,
        b'hhea': hhea, b'hmtx': b''.join(pack('Hh', 1000, bearing) for bearing in bearings),
        b'loca': pack('I' * len(offsets), *offsets), b'maxp': maxp,
        b'name': name_table(profile['Family']), b'post': pack('IihhIIIII', 0x30000, 0, -75, 50, 1, 0, 0, 0, 0),
    }
    number = len(tables)
    power = 2 ** (number.bit_length() - 1)
    header = pack('IHHHH', 0x10000, number, power * 16, power.bit_length() - 1, number * 16 - power * 16)
    directory, contents = b'', b''
    head_offset = 0
    for tag, data in sorted(tables.items()):
        offset = 12 + number * 16 + len(contents)
        directory += tag + pack('III', checksum(data), offset, len(data))
        if tag == b'head':
            head_offset = offset
        contents += data + b'\0' * (-len(data) % 4)
    font = bytearray(header + directory + contents)
    struct.pack_into('>I', font, head_offset + 8, (0xb1b0afba - checksum(font)) & 0xffffffff)
    return bytes(font), outlines, mapping


def verify(data, outlines, mapping):
    assert checksum(data) == 0xb1b0afba, 'Whole-font checksum'
    number = struct.unpack_from('>H', data, 4)[0]
    tables = {}
    for i in range(number):
        tag, expected, offset, size = struct.unpack_from('>4sIII', data, 12 + i * 16)
        table = data[offset:offset + size]
        check = table if tag != b'head' else table[:8] + b'\0' * 4 + table[12:]
        assert checksum(check) == expected, ('Table checksum', tag)
        tables[tag] = table
    count = struct.unpack_from('>H', tables[b'maxp'], 4)[0]
    assert count == len(outlines)
    offsets = struct.unpack('>' + 'I' * (count + 1), tables[b'loca'])
    for i, expected in enumerate(outlines):
        shape = tables[b'glyf'][offsets[i]:offsets[i + 1]]
        if not expected:
            assert not shape
            continue
        contours = struct.unpack_from('>h', shape)[0]
        assert contours == len(expected)
        ends = struct.unpack_from('>' + 'H' * contours, shape, 10)
        n = ends[-1] + 1
        position = 10 + 2 * contours
        instruction_size = struct.unpack_from('>H', shape, position)[0]
        position += 2 + instruction_size
        assert shape[position:position + n] == b'\x01' * n
        position += n
        xy = []
        for axis in (0, 1):
            values, previous = [], 0
            for j in range(n):
                previous += struct.unpack_from('>h', shape, position)[0]
                position += 2
                values.append(previous)
            xy.append(values)
        actual, start = [], 0
        for end in ends:
            actual.append(list(zip(xy[0][start:end + 1], xy[1][start:end + 1])))
            start = end + 1
        assert actual == expected, ('Core outline mismatch', i)
    cmap = tables[b'cmap'][20:]
    segments = struct.unpack_from('>H', cmap, 6)[0] // 2
    end_codes = struct.unpack_from('>' + 'H' * segments, cmap, 14)
    starts = struct.unpack_from('>' + 'H' * segments, cmap, 16 + 2 * segments)
    deltas = struct.unpack_from('>' + 'H' * segments, cmap, 16 + 4 * segments)
    decoded = {start: (start + delta) & 0xffff for start, end, delta in zip(starts, end_codes, deltas) if start != 0xffff}
    assert decoded == mapping, 'Unicode map mismatch'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('export', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--verify', action='store_true')
    args = parser.parse_args()
    profiles = json.loads(args.export.read_text(encoding='utf-8-sig'))
    assert len(profiles) == 10 and all(p['Id'] != 'terminal' for p in profiles)
    args.output.mkdir(parents=True, exist_ok=True)
    for profile in profiles:
        built, outlines, mapping = make_font(profile)
        target = args.output / ('KotuDesign-' + profile['Id'] + '.ttf')
        if args.verify:
            actual = target.read_bytes()
            assert actual == built, 'Generated font differs: ' + str(target)
        else:
            target.write_bytes(built)
            actual = built
        verify(actual, outlines, mapping)
        print(profile['Id'], len(profile['Icons']), len(actual), hashlib.sha256(actual).hexdigest())


if __name__ == '__main__':
    main()
