// Run with Node 24+. For offline fixture access, pass a directory containing both HWP files.
// The inputs are unmodified pyhwp test documents. Neither the documents nor their images are shipped.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { packageDirectory, restoreHwpRenderer } from './Restore-HwpRenderer.mjs';

// The corresponding .hwp.formatted.xml files specify the section labels, paper dimensions,
// table cell widths and image IDs. sample-5017.html.d/bindata contains the reference image bytes.
const fixtureBase = 'https://raw.githubusercontent.com/mete0r/pyhwp/83239f0d3bdf438b2c9f7dcff455a6e841154a39/tests/hwp5_tests/fixtures/';
const fixtureDirectory = process.argv[2];

function sha256(bytes) {
  return createHash('sha256').update(bytes).digest('hex');
}

async function loadFixture(name, expectedHash) {
  let bytes;
  if (fixtureDirectory) {
    bytes = await readFile(join(fixtureDirectory, name));
  } else {
    const response = await fetch(new URL(name, fixtureBase), { signal: AbortSignal.timeout(30000) });
    assert.ok(response.ok, `Fetching ${name}: HTTP ${response.status}`);
    bytes = new Uint8Array(await response.arrayBuffer());
  }
  assert.equal(sha256(bytes), expectedHash, name);
  return bytes;
}

function attribute(tag, name) {
  const match = tag.match(new RegExp(`\\b${name}="([^"]+)"`));
  assert.ok(match, `Missing SVG attribute ${name}`);
  return Number(match[1]);
}

function renderPage(doc, index) {
  const svg = doc.renderPageSvg(index);
  const root = svg.match(/<svg\b[^>]*>/)?.[0];
  assert.ok(root, `Page ${index + 1} must produce SVG`);
  // A renderer may emit one glyph per text element. Word spaces can be encoded by x positions.
  const text = Array.from(svg.matchAll(/<text\b[^>]*>([\s\S]*?)<\/text>/g),
    match => match[1].replace(/<[^>]+>/g, '')).join('').replace(/\s+/g, '');
  return { svg, text, width: attribute(root, 'width'), height: attribute(root, 'height') };
}

function assertA4(page, landscape) {
  // The source PageDef specifies 59528 × 84188 in 1/7200-inch units, SVG uses 96 px/in.
  const width = (landscape ? 84188 : 59528) / 75;
  const height = (landscape ? 59528 : 84188) / 75;
  assert.ok(Math.abs(page.width - width) < 1 && Math.abs(page.height - height) < 1,
    `Unexpected paper size ${page.width} × ${page.height}`);
}

function assertEmbeddedImage(page, type, expectedHash) {
  const images = Array.from(page.svg.matchAll(/<image\b[^>]*?href="data:(image\/[^;"]+);base64,([^"]+)"/g));
  assert.ok(images.some(match => match[1] === type && sha256(Buffer.from(match[2], 'base64')) === expectedHash),
    `Missing source ${type} image`);
}

await restoreHwpRenderer();
const { default: init, HwpDocument } = await import(pathToFileURL(join(packageDirectory, 'rhwp.js')));
await init({ module_or_path: await readFile(join(packageDirectory, 'rhwp_bg.wasm')) });

const pageDefs = new HwpDocument(await loadFixture('pagedefs.hwp',
  '9c4428dcc55b1d1872c2ae7e494f8bc8a080673a9c0a2965eb2f090a1635a81b'));
try {
  assert.equal(pageDefs.pageCount(), 2);
  const portrait = renderPage(pageDefs, 0);
  const landscape = renderPage(pageDefs, 1);
  assertA4(portrait, false);
  assertA4(landscape, true);
  assert.equal(portrait.text, 'Section1:A4portrait');
  assert.equal(landscape.text, 'Section2:A4landscape');
} finally {
  pageDefs.free();
}

const sample = new HwpDocument(await loadFixture('sample-5017.hwp',
  'ff3faae938610fcfbedc21c27c507ca0cc038b44872b65d10fcad0b55af67ba2'));
try {
  assert.equal(sample.pageCount(), 2);
  const first = renderPage(sample, 0);
  const second = renderPage(sample, 1);
  assertA4(first, false);
  assertA4(second, false);
  assert.ok(first.text.includes('A0B0A1B10B11'), 'Expected both rows of table cell text');
  const horizontalRules = Array.from(first.svg.matchAll(/<line\b[^>]*>/g), match => match[0])
    .filter(line => attribute(line, 'y1') === attribute(line, 'y2'))
    .map(line => Math.abs(attribute(line, 'x2') - attribute(line, 'x1')));
  const cellWidth = 20227 / 75;
  assert.ok(horizontalRules.some(width => Math.abs(width - cellWidth) < 1), 'Expected a single cell border');
  assert.ok(horizontalRules.some(width => Math.abs(width - 2 * cellWidth) < 1), 'Expected a two-column border');
  assert.equal(second.text, '다음페이지');
  assertEmbeddedImage(first, 'image/jpeg', 'ec8fe383b6e15ed56abd24a8b8bc112317bd770c2de2fc770081a160d652ab67');
  assertEmbeddedImage(second, 'image/png', '175ef81be06278b02193605bedee6ff5fabe62b3265624cff03e42be97d19d59');
} finally {
  sample.free();
}
console.log('HWP rendering passed for two real documents (four pages), table borders, cell text and both source images.');
