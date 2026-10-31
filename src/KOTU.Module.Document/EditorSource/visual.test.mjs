import assert from 'node:assert/strict';
import { visualMetrics, visibleDecorations, createVisuals } from './visual.js';

const visual = { zoomPercent: 100, gutterDigits: 3, decorEnabled: true, showGuides: true, showMarks: true };
for (const zoom of [20, 30, 40, 50, 100, 130, 200, 500]) {
  const m = visualMetrics({ ...visual, zoomPercent: zoom });
  assert.ok(Math.abs(m.fontSize * m.renderScale - 14 * zoom / 100) < 1e-10);
  assert.ok(m.fontSize >= 6 && m.editorLineHeight >= 8, 'supported Monaco metrics at all saved zoom values');
  assert.equal(m.lineHeight, m.editorLineHeight * m.renderScale);
  assert.ok(Math.abs(m.left - (24 + 36 * zoom / 100)) < 1e-10);
  assert.equal(m.bottom, 68);
  assert.deepEqual(m, visualMetrics({ ...visual, zoomPercent: zoom, showGuides: false }));
}

function geometry({ count = 3, first = 1, last = count, top = 0, height = 100, rows = [1, 2, 1], zoom = 100 } = {}) {
  const metrics = visualMetrics({ ...visual, zoomPercent: zoom });
  let calls = 0;
  const y = line => (16 / metrics.renderScale) + rows.slice(0, line - 1).reduce((a, b) => a + b, 0) * metrics.editorLineHeight;
  const model = { getLineCount: () => count, getLineMaxColumn: () => { calls++; return 8; },
    getValue() { throw Error('full text forbidden'); }, getLineContent() { throw Error('line text forbidden'); } };
  const editor = {
    getLayoutInfo: () => ({ width: 600 / metrics.renderScale, height: height / metrics.renderScale }),
    getVisibleRanges: () => [{ startLineNumber: first, endLineNumber: last }],
    getScrollTop: () => top / metrics.renderScale,
    getTopForLineNumber: y,
    getTopForPosition: line => y(line) + ((rows[line - 1] || 1) - 1) * metrics.editorLineHeight,
    getBottomForLineNumber: line => y(line) + (rows[line - 1] || 1) * metrics.editorLineHeight,
    getScrolledVisiblePosition: p => ({ left: 30 / metrics.renderScale,
      top: y(p.lineNumber) + ((rows[p.lineNumber - 1] || 1) - 1) * metrics.editorLineHeight - top / metrics.renderScale })
  };
  return { editor, model, metrics, get calls() { return calls; }, run: v => visibleDecorations(editor, model, v || visual, metrics) };
}
let g = geometry();
let items = g.run();
assert.deepEqual(items.numbers.map(n => [n.label, n.y]), [['1',16],['2',35],['3',73]], 'wrapped continuation has no extra logical number');
assert.deepEqual(items.marks.map(n => [n.label, n.y]), [['¶',16],['¶',54],['·EOF',73]]);
assert.deepEqual(items.guides, [37,56,75,94], 'merged horizontal boundaries and final bottom; no first top line');
assert.equal(g.calls, 3);
items = g.run({ ...visual, viewSuppressed: true });
assert.equal(items.guides.length, 0); assert.equal(items.marks.length, 0); assert.equal(items.numbers.length, 3);
items = g.run({ ...visual, showGuides: false });
assert.equal(items.numbers.length, 0); assert.equal(items.guides.length, 0); assert.equal(items.marks.length, 3);
assert.deepEqual(g.run({ ...visual, decorEnabled: false }), { guides: [], numbers: [], marks: [], visited: 0 });
g = geometry({ top: 45 }); items = g.run();
assert.deepEqual(items.numbers.map(n => n.label), ['2','3'], 'partial top row retains its logical number until its first visual row is gone');
assert.ok(items.guides.every(y => y >= 0));
g = geometry({ count: 1, rows: [1] });
assert.equal(g.run().marks[0].label, '·EOF', 'empty document and final empty line use model end position');
g = geometry({ count: 1, rows: [100000000], top: 16000000 });
items = g.run();
assert.equal(items.visited, 1); assert.equal(g.calls, 1); assert.equal(items.marks.length, 0);
assert.ok(items.guides.length <= 8, 'one huge wrapped logical line performs only visible guide work');
g = geometry({ count: 100000000, last: 100000000, rows: [], height: 100000 });
items = g.run(); assert.equal(items.visited, 600); assert.equal(g.calls, 600); assert.ok(items.guides.length <= 600);
g = geometry({ zoom: 20 }); items = g.run();
assert.equal(items.numbers[0].y, 16); assert.equal(items.marks[0].x, g.metrics.left + 30 + 0.8);

// 실제 옵션/그리기 수명과 스크롤 보존을 DOM/Monaco 공개 API 대역으로 검증한다.
const frames = new Map(), listeners = new Map(), drawCalls = [];
let serial = 0, subscriptions = 0, unsubscriptions = 0, scroll = 1000, caretTop = 5000;
const nodes = { editor: { style: {} }, 'editor-frame': { style: {}, clientWidth: 700 },
  decor: { style: {}, getContext: () => new Proxy({}, { get: (_, method) => (...args) => drawCalls.push([method, ...args]) }) } };
let backingWrites = 0, pixelWidth = 0, pixelHeight = 0;
Object.defineProperties(nodes.decor, { width: { get: () => pixelWidth, set: v => { pixelWidth = v; backingWrites++; } },
  height: { get: () => pixelHeight, set: v => { pixelHeight = v; backingWrites++; } } });
globalThis.document = { visibilityState: 'visible', documentElement: { style: {} }, body: { style: {} },
  getElementById: id => nodes[id], addEventListener: (name, handler) => listeners.set(name, handler), removeEventListener: name => listeners.delete(name) };
globalThis.window = { devicePixelRatio: 2 };
globalThis.requestAnimationFrame = callback => { frames.set(++serial, callback); return serial; };
globalThis.cancelAnimationFrame = id => frames.delete(id);
const events = {};
const subscribe = key => callback => { subscriptions++; events[key] = callback; return { dispose() { unsubscriptions++; } }; };
const options = {}, themes = [], scrolls = [];
const editor = {
  getVisibleRanges: () => [{ startLineNumber: 5, startColumn: 3, endLineNumber: 8 }],
  getScrolledVisiblePosition: p => ({ top: p.lineNumber === 99 ? caretTop : -4, left: 20 }),
  hasTextFocus: () => true, getPosition: () => ({ lineNumber: 99, column: 1 }),
  getLayoutInfo: () => ({ width: 700, height: 600, contentWidth: 686 }),
  getScrollTop: () => scroll, getScrollLeft: () => 0, setScrollTop(value) { scrolls.push(value); scroll = value; },
  getTopForPosition: () => 1200, updateOptions(value) { Object.assign(options, value); }, layout() {},
  onDidScrollChange: subscribe('scroll'), onDidLayoutChange: subscribe('layout'),
  onDidContentSizeChange: subscribe('size'), onDidChangeCursorSelection: subscribe('cursor')
};
const model = { onDidChangeContent: subscribe('change') };
const api = createVisuals({ editor: { defineTheme: (_, theme) => themes.push(theme) } }, editor, model, { ...visual, decorEnabled: false });
scrolls.length = 0;
api.apply({ background: '#00000000', foreground: '#eeeeee', showGuides: false });
assert.equal(scrolls.length, 0, 'theme/toggle never jumps to a scrolled-away caret');
api.apply({ zoomPercent: 200 });
assert.deepEqual(scrolls, [1204], 'zoom restores top anchor without revealing far-off caret');
assert.equal(options.fontSize, 28); assert.equal(options.lineHeight, 38); assert.equal(options.padding.bottom, 68);
assert.equal(nodes['editor-frame'].style.left, '96px');
assert.equal(document.body.style.background, '#00000000');
assert.equal(themes.at(-1).colors['editor.selectionBackground'], themes.at(-1).colors['editor.inactiveSelectionBackground']);
api.apply({ highContrast: true, light: true, background: '#112233ff', foreground: '#abcdefFF', selectionBackground: '#abcdef', selectionForeground: '#123456' });
assert.equal(themes.at(-1).base, 'hc-light'); assert.equal(themes.at(-1).colors['editor.background'], '#112233ff');
assert.equal(themes.at(-1).colors['editor.selectionForeground'], '#123456');
assert.equal(options.autoDetectHighContrast, false, 'host system colors own the palette; accessibilitySupport remains auto in editor creation');
caretTop = 570; events.cursor(); assert.ok(scrolls.at(-1) > 1204, 'actual caret movement is kept above overlay');
for (let i = 0; i < 1000; i++) { events.scroll(); events.change(); }
assert.equal(frames.size, 1, 'decor refresh is coalesced to one frame');
for (const callback of frames.values()) callback(); frames.clear();
assert.ok(drawCalls.some(c => c[0] === 'setTransform' && c[1] === 2));
const firstBackingWrites = backingWrites;
events.scroll(); for (const callback of frames.values()) callback(); frames.clear();
assert.equal(backingWrites, firstBackingWrites, 'scroll redraw reuses the canvas backing store');
api.apply({ zoomPercent: 20 });
assert.equal(options.fontSize, 7); assert.equal(options.lineHeight, 9);
assert.equal(options.padding.bottom, 170); assert.equal(nodes.editor.style.transform, 'scale(0.4)');
assert.equal(options.scrollbar.verticalScrollbarSize * 0.4, 14);
assert.equal(api.viewport().height, 240); assert.equal(api.viewport().top, scroll * 0.4);
api.dispose(); assert.equal(unsubscriptions, subscriptions); assert.equal(frames.size, 0); assert.equal(listeners.size, 0);
console.log('PASS: zoom 20–500, fixed reserve, viewport geometry/wrap/partial/EOF, bounded 100M line/row cases, theme/selection, anchor/caret, coalescing and disposal');
