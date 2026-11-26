// Run with Node 24+. For offline fixture access, pass a directory containing both HWP files.
// The inputs are unmodified pyhwp test documents. Neither the documents nor their images are shipped.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { runInNewContext } from 'node:vm';
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

// Run the shipped viewer with the same incoming/outgoing messages as the native WebView host.
async function testPageViewer() {
  const viewer = new URL('../src/KOTU.Module.Document/HwpViewer/', import.meta.url);
  const html = await readFile(new URL('index.html', viewer), 'utf8');
  assert.doesNotMatch(html, /<(button|nav)\b/i, 'Page and fit controls belong in the native document toolbar');
  const messages = [];
  const bridge = Object.assign(new EventTarget(), { postMessage: message => messages.push(message) });
  const window = Object.assign(new EventTarget(), { chrome: { webview: bridge } });
  const viewport = {
    dataset: {}, clientWidth: 600, clientHeight: 600, scrollTop: 0, scrollLeft: 0,
    getBoundingClientRect() { return { left: 0, top: 0 }; },
    scrollTo(_left, top) { this.scrollTop = top; }, scrollBy() {}
  };
  const overlay = {
    style: {}, children: [], attributes: {},
    replaceChildren() { this.children = []; },
    append(child) { this.children.push(child); },
    setAttribute(name, value) { this.attributes[name] = value; }
  };
  const page = {
    style: {}, naturalWidth: 800, naturalHeight: 1000, parentElement: viewport, async decode() {},
    get clientHeight() { return viewport.dataset.fit === 'ActualSize' ? 1000 : 750; },
    getBoundingClientRect() { return { left: 0, top: 0, width: 400, height: 500 }; }
  };
  const workerMessages = [];
  let worker;
  class RenderWorker {
    constructor() { worker = this; }
    postMessage(message) { workerMessages.push(message); }
    terminate() {}
  }
  const source = await readFile(new URL('viewer.js', viewer), 'utf8');
  let decodeGate;
  class CandidateImage { async decode() { if (decodeGate) await decodeGate; } }
  const timers = new Map();
  let timerId = 0;
  const document = {
    querySelector: selector => selector === '#page' ? page : selector === '#search-overlay' ? overlay : viewport,
    createElementNS() { return { attributes: {}, setAttribute(name, value) { this.attributes[name] = value; } }; }
  };
  runInNewContext(source, { window, document, Worker: RenderWorker, Image: CandidateImage, atob, Blob, URL,
    clearTimeout: id => timers.delete(id),
    setTimeout: (callback, delay) => { const id = ++timerId; timers.set(id, { callback, delay }); return id; } });
  const send = data => bridge.dispatchEvent(new MessageEvent('message', { data }));
  send({ kind: 'open', length: 1 });
  send({ kind: 'chunk', offset: 0, data: 'AA==' });
  assert.equal(workerMessages.at(-1)?.kind, 'open');
  async function show(index) {
    await worker.onmessage({ data: { index, count: 2, svg: '<svg/>' } });
    const state = messages.findLast(message => message.kind === 'shown');
    assert.equal(state?.index, index);
    assert.equal(state?.count, 2);
  }
  await show(0);
  viewport.scrollTop = 150;
  for (const mode of ['ActualSize', 'FitWidth', 'FitHeight', 'Contain']) {
    const fraction = viewport.scrollTop / page.clientHeight;
    send({ kind: 'fit', mode });
    assert.equal(viewport.dataset.fit, mode);
    const preservesPosition = mode === 'ActualSize' || mode === 'FitWidth';
    assert.equal(viewport.scrollTop, preservesPosition ? fraction * page.clientHeight : 0);
    viewport.scrollTop = 150;
  }
  send({ kind: 'page', index: 1 });
  assert.equal(workerMessages.at(-1).index, 1);
  await show(1);
  send({ kind: 'page', index: 0 });
  assert.equal(workerMessages.at(-1).index, 0);
  await show(0);

  function press(key, options = {}) {
    const event = Object.assign(new Event('keydown', { cancelable: true }), { key, ...options });
    window.dispatchEvent(event);
    return event.defaultPrevented;
  }
  for (const [key, kind] of [['f', 'reapply'], ['a', 'original'], ['PageDown', 'next'], ['PageUp', 'previous']]) {
    assert.equal(press(key), true, key);
    assert.equal(messages.at(-1)?.kind, kind);
  }
  assert.equal(press('+', { ctrlKey: true }), true);
  assert.equal(viewport.dataset.fit, 'Manual');
  assert.ok(Math.abs(parseFloat(page.style.width) - 440) < 0.01);
  send({ kind: 'fit', mode: 'FitWidth' });
  assert.equal(page.style.width, '', 'Choosing Fit after manual zoom restores responsive sizing');
  assert.equal(viewport.dataset.fit, 'FitWidth');
  for (const [key, options, kind] of [['f', { ctrlKey: true }, 'find'], ['h', { ctrlKey: true }, 'find-options'],
    ['F3', {}, 'find-next'], ['F3', { shiftKey: true }, 'find-previous']]) {
    assert.equal(press(key, options), true);
    assert.equal(messages.at(-1)?.kind, kind);
    const count = messages.length;
    assert.equal(press(key, { ...options, repeat: true }), true);
    assert.equal(messages.length, count, 'Held search keys must not repeat');
    assert.equal(press(key, { ...options, isComposing: true }), false);
  }

  const sentBeforeSearch = workerMessages.length;
  send({ kind: 'search', id: 1, query: 'old' });
  send({ kind: 'search', id: 2, query: 'obsolete' });
  send({ kind: 'search', id: 3, query: 'latest' });
  assert.equal(workerMessages.length, sentBeforeSearch + 1, 'Only one search enters the worker');
  assert.equal(messages.findLast(m => m.kind === 'search-result' && m.id === 2)?.count, 0, 'Superseded mailbox completes its host wait');
  send({ kind: 'page', index: 1 });
  await worker.onmessage({ data: { kind: 'search-result', id: 1, count: 3, limited: false } });
  assert.equal(workerMessages.at(-1).kind, 'page', 'Explicit page navigation is not starved by typing');
  await show(1);
  assert.equal(workerMessages.at(-1).id, 3, 'Only the latest query follows the page');
  await worker.onmessage({ data: { kind: 'search-result', id: 3, count: 4, limited: false } });
  send({ kind: 'select', id: 3, selection: 1, index: 0 });
  send({ kind: 'clear-search', id: 4 });
  await worker.onmessage({ data: { kind: 'selection', id: 3, selection: 1,
    rects: [{ pageIndex: 0, x: 20, y: 40, width: 30, height: 16 }] } });
  assert.equal(overlay.children.length, 0, 'Late selection cannot revive a cleared query');
  assert.equal(messages.findLast(m => m.kind === 'shown').index, 1, 'Late selection cannot navigate');
  send({ kind: 'search', id: 5, query: '한글😀' });
  await worker.onmessage({ data: { kind: 'search-result', id: 5, count: 1, limited: false } });
  send({ kind: 'select', id: 5, selection: 2, index: 0 });
  await worker.onmessage({ data: { kind: 'selection', id: 5, selection: 2,
    rects: [{ pageIndex: 1, x: 20, y: 40, width: 30, height: 16 }] } });
  assert.equal(overlay.children.length, 1);
  assert.equal(overlay.attributes.viewBox, '0 0 800 1000');
  assert.equal(overlay.style.width, '400px');
  send({ kind: 'select', id: 5, selection: 3, index: 0 });
  window.dispatchEvent(new Event('pointerdown'));
  await worker.onmessage({ data: { kind: 'selection', id: 5, selection: 3,
    rects: [{ pageIndex: 0, x: 20, y: 40, width: 30, height: 16 }] } });
  assert.equal(messages.findLast(m => m.kind === 'shown').index, 1, 'Body input cancels delayed navigation');
  send({ kind: 'select', id: 5, selection: 4, index: 0 });
  await worker.onmessage({ data: { kind: 'selection', id: 5, selection: 4,
    rects: [{ pageIndex: 0, x: 20, y: 40, width: 30, height: 16 }] } });
  assert.equal(workerMessages.at(-1).kind, 'page');
  const oldSource = page.src;
  let finishDecode;
  decodeGate = new Promise(resolve => { finishDecode = resolve; });
  const decoding = worker.onmessage({ data: { index: 0, count: 2, svg: '<svg/>' } });
  send({ kind: 'clear-search', id: 6 });
  finishDecode();
  await decoding;
  decodeGate = undefined;
  assert.equal(page.src, oldSource, 'A query cleared during image decoding cannot replace the current page');
  assert.equal(messages.findLast(m => m.kind === 'shown').index, 1);
  send({ kind: 'search', id: 7, query: 'slow' });
  send({ kind: 'page', index: 0 });
  assert.equal(timers.size, 1, 'A page queued behind a search must not start its render timeout yet');
  const timeout = Array.from(timers.values())[0];
  assert.equal(timeout.delay, 30000);
  timeout.callback();
  assert.equal(messages.at(-1).kind, 'search-result');
  assert.equal(messages.at(-1).error, true);
  assert.equal(page.src, oldSource, 'Search timeout preserves the visible page');
  await worker.onmessage({ data: { kind: 'search-result', id: 7, count: 0, limited: false } });
  assert.equal(workerMessages.at(-1).kind, 'page');
  await show(0);
  assert.ok(!messages.some(message => message.kind === 'failed'), 'Search failure does not fail the renderer');
  window.dispatchEvent(Object.assign(new Event('keyup'), { key: 'Escape' }));
  assert.equal(messages.at(-1).kind, 'search-escape-released', 'Escape release follows focus into the WebView');
  assert.equal(press('Escape'), true);
  assert.equal(press('Escape', { repeat: true }), true);
  for (const modifier of ['altKey', 'ctrlKey', 'metaKey', 'shiftKey', 'isComposing'])
    assert.equal(press('Escape', { [modifier]: true }), false, modifier);
  assert.equal(press('Escape', { keyCode: 229 }), false);
  assert.equal(press('Enter'), false);
  assert.equal(press('Escape'), true);
  window.dispatchEvent(new Event('pagehide'));
  assert.equal(press('Escape'), false);
  assert.equal(messages.filter(message => message.kind === 'escape').length, 2);
  assert.ok(!messages.some(message => message.kind === 'failed'));
}

async function testSearchWorkerProtocol() {
  const messages = [];
  let freed = false;
  const self = { postMessage: data => messages.push(data), close() { throw new Error('Search must not close the worker'); } };
  const body = { sec: 0, para: 0, charOffset: 3, length: 2 };
  const cell = { ...body, cellContext: { parentPara: 5, ctrlIdx: 0, cellIdx: 2, cellPara: 1 } };
  let selectionArguments;
  class FakeDocument {
    pageCount() { return 2; }
    renderPageSvg() { return '<svg/>'; }
    free() { freed = true; }
    searchAllText(query, caseSensitive, includeCells) {
      assert.equal(caseSensitive, false);
      assert.equal(includeCells, true);
      if (query === 'broken') throw new Error('Parser failure');
      if (query === 'oversized') return 'x'.repeat(16 * 1024 * 1024 + 1);
      return JSON.stringify(query === 'large' ? Array(10001).fill(body) : query === 'cell' ? [cell] : [body]);
    }
    getSelectionRects(...args) {
      selectionArguments = args;
      return JSON.stringify([{ pageIndex: 1, x: 10, y: 20, width: 30, height: 15 }]);
    }
    getSelectionRectsInCell(...args) { return this.getSelectionRects(...args); }
  }
  const source = (await readFile(new URL('../src/KOTU.Module.Document/HwpViewer/render-worker.js', import.meta.url), 'utf8'))
    .replace(/^import .*\r?\n/, '').replaceAll('import.meta.url', "'https://kotu-hwp.invalid/render-worker.js'");
  runInNewContext(source, { self, HwpDocument: FakeDocument, init: async () => {}, URL, Uint8Array });
  const send = data => self.onmessage({ data });
  await send({ kind: 'open', bytes: new Uint8Array([0]) });
  await send({ kind: 'search', id: 1, query: 'large' });
  assert.equal(messages.at(-1).count, 10000);
  assert.equal(messages.at(-1).limited, true);
  await send({ kind: 'select', id: 1, selection: 1, index: 0 });
  assert.deepEqual(selectionArguments, [0, 0, 3, 0, 5], 'Selection preserves engine scalar offsets');
  await send({ kind: 'search', id: 2, query: 'cell' });
  await send({ kind: 'select', id: 2, selection: 2, index: 0 });
  assert.deepEqual(selectionArguments, [0, 5, 0, 2, 1, 3, 1, 5], 'Table matches retain cell context');
  await send({ kind: 'select', id: 1, selection: 3, index: 0 });
  assert.equal(messages.at(-1).error, true, 'Old result IDs cannot select new matches');
  for (const query of ['broken', 'oversized']) {
    await send({ kind: 'search', id: 4, query });
    assert.equal(messages.at(-1).kind, 'search-result');
    assert.equal(messages.at(-1).error, true);
    await send({ kind: 'page', index: 1 });
    assert.equal(messages.at(-1).index, 1, 'Page rendering survives search errors and limits');
    assert.equal(freed, false);
  }
}

await testPageViewer();
await testSearchWorkerProtocol();
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
  for (const [query, pageIndex, table] of [['A0', 0, true], ['B1', 0, true], ['다음 페이지', 1, false]]) {
    const matches = JSON.parse(sample.searchAllText(query, false, true));
    assert.ok(matches.length > 0, 'Expected a real HWP search match: ' + query);
    for (const match of matches) {
      assert.equal(Boolean(match.cellContext), table);
      const cell = match.cellContext;
      const raw = cell ? sample.getSelectionRectsInCell(match.sec, cell.parentPara, cell.ctrlIdx, cell.cellIdx,
        cell.cellPara, match.charOffset, cell.cellPara, match.charOffset + match.length)
        : sample.getSelectionRects(match.sec, match.para, match.charOffset, match.para, match.charOffset + match.length);
      const rectangles = JSON.parse(raw);
      assert.ok(rectangles.some(rectangle => rectangle.pageIndex === pageIndex && rectangle.width > 0 && rectangle.height > 0));
    }
  }
  assertEmbeddedImage(first, 'image/jpeg', 'ec8fe383b6e15ed56abd24a8b8bc112317bd770c2de2fc770081a160d652ab67');
  assertEmbeddedImage(second, 'image/png', '175ef81be06278b02193605bedee6ff5fabe62b3265624cff03e42be97d19d59');
} finally {
  sample.free();
}
const unicode = HwpDocument.createEmpty();
try {
  unicode.insertText(0, 0, 0, 'aa😀한글 aaaa');
  unicode.renderPageSvg(0);
  const korean = JSON.parse(unicode.searchAllText('한글', false, true));
  assert.equal(korean[0].charOffset, 3, 'HWP offsets are Unicode scalar positions');
  assert.equal(JSON.parse(unicode.searchAllText('😀', false, true))[0].length, 1);
  assert.equal(JSON.parse(unicode.searchAllText('aa', false, true)).length, 3, 'Literal matches are non-overlapping');
  assert.equal(JSON.parse(unicode.searchAllText('', false, true)).length, 0);
} finally { unicode.free(); }
console.log('HWP rendering and search passed: two real documents, four pages, cell/Korean/Unicode matches, latest-query routing and stale-selection guards.');
