import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import vm from 'node:vm';
import { gzipSync } from 'node:zlib';

const source = readFileSync(new URL('editor.js', import.meta.url), 'utf8')
  .replace(/^import .*?;\r?\n/gm, '').replaceAll('import.meta.url', "'https://kotu-editor.invalid/editor.js'");
function harness(text = 'a', readOnly = false) {
  let receive, change, cursor, scroll, compositionStart, compositionEnd, raw = [], current = text, version = 1;
  // Monaco on Windows defaults to CRLF when the initial text contains no line break.
  let eol = text.includes('\n') ? '\n' : '\r\n';
  let anchor = 0, position = 0, scrollTop = 0, frameId = 0, textReads = 0, undoStops = 0, focuses = 0, revealed, modelCreations = 0;
  const frames = new Map(), windowEvents = {}, documentEvents = {};
  const options = {}, triggers = [], undo = [], redo = [];
  const getPositionAt = n => {
    const lines = current.slice(0, n).split('\n');
    return { lineNumber: lines.length, column: lines.at(-1).length + 1 };
  };
  const getOffsetAt = p => current.split('\n').slice(0, p.lineNumber - 1).reduce((n, line) => n + line.length + 1, 0) + p.column - 1;
  class Selection {
    constructor(al, ac, pl, pc) { Object.assign(this, { selectionStartLineNumber: al, selectionStartColumn: ac, positionLineNumber: pl, positionColumn: pc }); }
    getSelectionStart() { return { lineNumber: this.selectionStartLineNumber, column: this.selectionStartColumn }; }
    getPosition() { return { lineNumber: this.positionLineNumber, column: this.positionColumn }; }
  }
  class Range {
    constructor(sl, sc, el, ec) { Object.assign(this, { startLineNumber: sl, startColumn: sc, endLineNumber: el, endColumn: ec }); }
  }
  const selection = () => { const a = getPositionAt(anchor), p = getPositionAt(position); return new Selection(a.lineNumber, a.column, p.lineNumber, p.column); };
  const model = {
    getVersionId: () => version, getValue: () => { textReads++; return current; }, getValueLength: () => current.length,
    canUndo: () => undo.length > 0, canRedo: () => redo.length > 0,
    getValueInRange: r => current.slice(getOffsetAt({ lineNumber: r.startLineNumber, column: r.startColumn }), getOffsetAt({ lineNumber: r.endLineNumber, column: r.endColumn })),
    updateOptions(o) { Object.assign(options, o); },
    setEOL(value) { const next = value === 1 ? '\r\n' : '\n'; if (next !== eol) { eol = next; version++; } },
    onDidChangeContent(callback) { change = callback; },
    getOffsetAt, getPositionAt, dispose() {}
  };
  const editor = {
    updateOptions(o) { Object.assign(options, o); }, getLayoutInfo: () => ({ width: 800, height: 600 }),
    getSelection: selection,
    setSelection(s) { anchor = getOffsetAt(s.getSelectionStart()); position = getOffsetAt(s.getPosition()); cursor({ source: 'api' }); },
    revealPositionInCenterIfOutsideViewport(p) { revealed = getOffsetAt(p); },
    getScrollTop: () => scrollTop, getScrollLeft: () => 0,
    onDidChangeCursorSelection(callback) { cursor = callback; }, onDidScrollChange(callback) { scroll = callback; },
    onDidCompositionStart(callback) { compositionStart = callback; }, onDidCompositionEnd(callback) { compositionEnd = callback; },
    trigger(_, action) {
      triggers.push(action);
      const source = action === 'undo' ? undo : action === 'redo' ? redo : null;
      if (source?.length) {
        const old = current;
        (action === 'undo' ? redo : undo).push(old);
        current = source.pop(); version++;
        change({ versionId: version, changes: [{ rangeOffset: 0, rangeLength: old.length, text: current }] });
        anchor = position = current.length; cursor({ source: action });
      }
    },
    focus() { focuses++; }, dispose() {}, pushUndoStop() { undoStops++; },
    executeEdits(_, operations, endCursorState) {
      undo.push(current);
      redo.length = 0;
      const changes = operations.map(e => ({ rangeOffset: getOffsetAt({ lineNumber: e.range.startLineNumber, column: e.range.startColumn }),
        rangeLength: getOffsetAt({ lineNumber: e.range.endLineNumber, column: e.range.endColumn }) - getOffsetAt({ lineNumber: e.range.startLineNumber, column: e.range.startColumn }), text: e.text }));
      for (const e of changes) current = current.slice(0, e.rangeOffset) + e.text + current.slice(e.rangeOffset + e.rangeLength);
      version++; change({ versionId: version, changes });
      if (endCursorState) editor.setSelection(endCursorState()[0]);
      return true;
    }
  };
  const document = { visibilityState: 'visible', getElementById() { return {}; }, addEventListener(name, callback) { documentEvents[name] = callback; } };
  const context = vm.createContext({ location: { hash: '#test' }, URL, Worker: class {},
    requestAnimationFrame(callback) { frames.set(++frameId, callback); return frameId; }, cancelAnimationFrame(id) { frames.delete(id); },
    chrome: { webview: { postMessage(value) { raw.push(JSON.stringify(value)); }, addEventListener(_, callback) { receive = callback; } } },
    window: { addEventListener(name, callback) { windowEvents[name] = callback; } }, document,
    createVisuals: (_, editor, model, visual) => { options.visual = visual; return { apply(value) { options.visual = value; }, viewport() { return { top: editor.getScrollTop(), left: 0, ...editor.getLayoutInfo() }; }, dispose() {} }; },
    monaco: { Selection, Range, editor: { EndOfLinePreference: { LF: 1 }, EndOfLineSequence: { LF: 0, CRLF: 1 },
      createModel: () => { modelCreations++; return model; }, create(_, initial) { Object.assign(options, initial); return editor; } } }
  });
  vm.runInContext(source, context);
  const send = data => {
    if (data.kind === 'select' && data.payload.selectionGeneration === undefined)
      data.payload.selectionGeneration = raw.map(JSON.parse).findLast(m => m.kind === 'state')?.selectionGeneration ?? 0;
    receive({ data: { epoch: 'test', ...data } });
  };
  send({ kind: 'load', text, readOnly });
  return { send, options, raw, context, triggers,
    key(key, modifiers = {}) { let prevented = 0; windowEvents.keydown({ key, preventDefault() { prevented++; }, stopImmediatePropagation() {}, ...modifiers }); return prevented; },
    keyup(key) { windowEvents.keyup({ key }); },
    menu() { windowEvents.contextmenu({ clientX: 20, clientY: 40, preventDefault() {}, stopImmediatePropagation() {} }); return raw.map(JSON.parse).at(-1); },
    wheel(event) { windowEvents.wheel(event); },
    get text() { return current; }, get version() { return version; }, get selection() { return { anchor, position }; },
    get pendingFrames() { return frames.size; }, get textReads() { return textReads; }, get undoStops() { return undoStops; },
    get focuses() { return focuses; }, get revealed() { return revealed; },
    get modelCreations() { return modelCreations; },
    messages: () => raw.map(JSON.parse),
    edit(insert) { current += insert; version++; change({ versionId: version, changes: [{ rangeOffset: current.length - insert.length, rangeLength: 0, text: insert }] }); },
    compose(active) { (active ? compositionStart : compositionEnd)(); },
    select(a, p) { anchor = a; position = p; cursor({ source: 'mouse' }); },
    scroll(top) { scrollTop = top; scroll(); },
    frame() { const callbacks = [...frames.values()]; frames.clear(); callbacks.forEach(callback => callback()); },
    blur() { windowEvents.blur(); },
    visibility(value) { document.visibilityState = value; documentEvents.visibilitychange(); }
  };
}

const h = harness();
assert.equal(h.messages().find(m => m.kind === 'loaded').version, 1, 'normalized Windows initial content starts at host revision1');
for (const initial of ['', 'single line', 'two\nlines']) {
  const session = harness(initial);
  const loaded = session.messages().find(m => m.kind === 'loaded');
  assert.equal(loaded.version, 1);
  assert.equal(loaded.text, initial);
  assert.equal(session.version, initial.includes('\n') ? 1 : 2, 'exercise both actual Monaco initial revisions');
  session.send({ kind: 'activate' });
  session.send({ kind: 'replace', id: 900, expectedVersion: 1,
    payload: { edits: [{ offset: initial.length, length: 0, text: '\nnext' }] } });
  assert.equal(session.messages().findLast(m => m.kind === 'change').version, 2);
  session.send({ kind: 'snapshot', id: 901, expectedVersion: 2 });
  assert.equal(session.messages().at(-1).text, initial + '\nnext');
  assert.equal(session.messages().at(-1).version, 2);
  const menu = session.menu();
  assert.equal(menu.version, 2, 'context tickets use the same relative revision');
  session.send({ kind: 'edit-command', action: 'undo', ticketId: menu.id });
  session.send({ kind: 'snapshot', id: 902, expectedVersion: 3 });
  assert.equal(session.messages().at(-1).text, initial, 'Undo preserves the initial empty or single-line content');
  assert.equal(session.messages().at(-1).version, 3);
  const redoMenu = session.menu();
  session.send({ kind: 'edit-command', action: 'redo', ticketId: redoMenu.id });
  session.send({ kind: 'snapshot', id: 903, expectedVersion: 4 });
  assert.equal(session.messages().at(-1).text, initial + '\nnext');
  assert.equal(session.messages().at(-1).version, 4, 'normalization baseline remains constant across Undo/Redo');
  session.send({ kind: 'replace', id: 904, expectedVersion: 3,
    payload: { edits: [{ offset: 0, length: 0, text: 'stale' }] } });
  assert.match(session.messages().at(-1).error, /document changed/, 'relative revisions still reject stale edits');
}
assert.equal(h.options.readOnly, true, 'input stays blocked until host verifies initial content/layout');
h.send({ kind: 'activate' }); assert.equal(h.options.readOnly, false);
h.edit('\n');
h.send({ kind: 'snapshot', id: 1, expectedVersion: 1 });
let response = h.messages().at(-1);
assert.equal(response.version, 2); assert.equal(response.text, 'a\n');
assert.equal(h.messages().at(-2).kind, 'state', 'latest selection precedes snapshot');
assert.equal(h.messages().at(-3).kind, 'change', 'earlier delta precedes snapshot and selection');
h.send({ kind: 'freeze', id: 2, expectedVersion: 1 });
assert.equal(h.options.readOnly, true); assert.equal(h.messages().at(-1).frozen, true);
h.send({ kind: 'release', freezeId: 99 }); assert.equal(h.options.readOnly, true);
h.send({ kind: 'thaw', id: 3, expectedVersion: 2, payload: { freezeId: 2 } });
assert.equal(h.options.readOnly, false);
h.compose(true); h.send({ kind: 'freeze', id: 4, expectedVersion: 2 });
assert.match(h.messages().at(-1).error, /composition/); assert.equal(h.options.readOnly, false);
h.compose(false); h.send({ kind: 'freeze', id: 5, expectedVersion: 2 });
h.send({ kind: 'release', freezeId: 5 }); assert.equal(h.options.readOnly, false, 'timeout release restores editing');
h.send({ kind: 'replace', id: 6, expectedVersion: 1, payload: [] });
assert.match(h.messages().at(-1).error, /document changed/);
const ro = harness('a', true); ro.send({ kind: 'activate' });
ro.send({ kind: 'freeze', id: 1, expectedVersion: 1 });
ro.send({ kind: 'thaw', id: 2, expectedVersion: 1, payload: { freezeId: 1 } });
assert.equal(ro.options.readOnly, true, 'close cancel preserves user read-only choice');

const lifecycle = harness('original');
lifecycle.send({ expectedVersion: 2, kind: 'activate' });
lifecycle.edit('\nlatest😀');
for (let round = 0; round < 3; round++) {
  lifecycle.send({ expectedVersion: 2, kind: 'options', id: 10 + round * 4, payload: { readOnly: true } });
  assert.equal(lifecycle.options.readOnly, true);
  lifecycle.send({ expectedVersion: 2, kind: 'snapshot', id: 11 + round * 4 });
  assert.equal(lifecycle.messages().at(-1).text, 'original\nlatest😀', 'render/print consume latest ordered text');
  lifecycle.send({ expectedVersion: 2, kind: 'replace', id: 12 + round * 4, expectedVersion: 2, payload: { edits: [{ offset: 0, length: 1, text: 'x' }] } });
  assert.match(lifecycle.messages().at(-1).error, /read-only/);
  lifecycle.send({ expectedVersion: 2, kind: 'options', id: 13 + round * 4, payload: { readOnly: false } });
}
assert.equal(lifecycle.modelCreations, 1, 'ordinary view/edit transitions keep the same model');
assert.equal(lifecycle.undoStops, 0, 'view transitions do not alter the undo history');
lifecycle.compose(true);
lifecycle.send({ expectedVersion: 2, kind: 'options', id: 30, payload: { readOnly: true } });
assert.match(lifecycle.messages().at(-1).error, /composition/);
assert.equal(lifecycle.options.readOnly, false, 'failed transition leaves the previous editing mode');
lifecycle.compose(false);
lifecycle.send({ expectedVersion: 2, kind: 'freeze', id: 31 });
lifecycle.send({ expectedVersion: 2, kind: 'options', id: 32, payload: { readOnly: false } });
assert.equal(lifecycle.options.readOnly, true, 'view options cannot remove an owned close freeze');
lifecycle.send({ expectedVersion: 2, kind: 'thaw', id: 33, payload: { freezeId: 31 } });
assert.equal(lifecycle.options.readOnly, false);

// 실제 브라우저 소스를 실행한다. Monaco의 UI/IME/Undo 동작 자체는 별도 실기기 검증 대상이다.
const v = harness('한글😀\ncat cat\n');
v.send({ kind: 'activate' });
const beforeScroll = v.raw.length, beforeReads = v.textReads;
for (let i = 0; i < 5000; i++) { v.scroll(i); v.select(8, 5); }
assert.equal(v.raw.length, beforeScroll);
assert.equal(v.pendingFrames, 1);
v.frame();
assert.equal(v.raw.length, beforeScroll + 1);
assert.equal(v.messages().at(-1).viewport.top, 4999);
assert.equal(v.messages().at(-1).anchor, 8);
assert.equal(v.messages().at(-1).position, 5);
assert.equal(v.textReads, beforeReads, 'viewport/selection messages never copy the document');
v.select(5, 8); v.blur();
assert.equal(v.pendingFrames, 0);
assert.equal(v.messages().findLast(m => m.kind === 'state').position, 8, 'blur flushes before animation frames can be suspended');
v.select(8, 5); v.visibility('hidden');
assert.equal(v.pendingFrames, 0);
assert.equal(v.messages().at(-1).anchor, 8);
v.select(0, 0);
assert.equal(v.pendingFrames, 0, 'hidden documents do not depend on animation frames');
v.visibility('visible');
v.send({ kind: 'select', id: 1, expectedVersion: 1, payload: { anchor: 8, position: 5, reveal: true } });
assert.deepEqual(v.selection, { anchor: 8, position: 5 }, 'selection direction is retained');
assert.equal(v.revealed, 5);
assert.equal(v.messages().at(-2).kind, 'state');
assert.equal(v.pendingFrames, 0, 'select acknowledges the final state');
v.send({ kind: 'focus', id: 2, expectedVersion: 1 });
assert.equal(v.focuses, 1); assert.deepEqual(v.selection, { anchor: 8, position: 5 }, 'focus does not clear the match');
v.select(9, 12);
v.send({ kind: 'replace', id: 3, expectedVersion: 1,
  payload: { edits: [{ offset: 5, length: 3, text: '끝' }], selectionEnd: 6, expectedSelection: { anchor: 8, position: 5 } } });
assert.match(v.messages().at(-1).error, /selection changed/);
assert.equal(v.text, '한글😀\ncat cat\n'); assert.equal(v.undoStops, 0);
v.select(8, 5);
v.send({ kind: 'replace', id: 4, expectedVersion: 1,
  payload: { edits: [{ offset: 5, length: 3, text: '끝\n😀' }], selectionEnd: 9, expectedSelection: { anchor: 8, position: 5 } } });
assert.equal(v.text, '한글😀\n끝\n😀 cat\n');
assert.deepEqual(v.selection, { anchor: 9, position: 9 });
assert.equal(v.undoStops, 2, 'replacement is enclosed in one pair of undo stops');
assert.equal(v.messages().at(-3).kind, 'change');
assert.equal(v.messages().at(-2).kind, 'state');
assert.equal(v.messages().at(-2).version, 2);
assert.equal(v.messages().at(-1).kind, 'response');
assert.equal(v.pendingFrames, 0);
const unchanged = v.text;
v.send({ kind: 'replace', id: 5, expectedVersion: 1, payload: { edits: [{ offset: 0, length: 1, text: 'x' }] } });
assert.match(v.messages().at(-1).error, /document changed/); assert.equal(v.text, unchanged);
v.send({ kind: 'select', id: 6, expectedVersion: 1, payload: { anchor: 0, position: 0, reveal: true } });
assert.match(v.messages().at(-1).error, /document changed/); assert.deepEqual(v.selection, { anchor: 9, position: 9 });
v.compose(true);
v.send({ kind: 'select', id: 7, expectedVersion: 2, payload: { anchor: 0, position: 0 } });
assert.match(v.messages().at(-1).error, /document changed/); assert.equal(v.messages().at(-2).composing, true);
v.compose(false);
v.send({ kind: 'options', id: 8, expectedVersion: 2, payload: { readOnly: true } });
v.send({ kind: 'replace', id: 9, expectedVersion: 2, payload: { edits: [{ offset: 0, length: 1, text: 'x' }] } });
assert.match(v.messages().at(-1).error, /read-only/); assert.equal(v.text, unchanged);
v.send({ kind: 'select', id: 10, expectedVersion: 2, payload: { anchor: v.text.length, position: v.text.length, reveal: true } });
assert.equal(v.messages().at(-2).line, 4, 'empty final line remains selectable in viewing mode');
assert.equal(v.messages().at(-2).column, 1);

const ordered = harness('x');
const orderedStart = ordered.raw.length;
ordered.edit('\n'); ordered.edit('😀'); ordered.scroll(123); ordered.select(4, 4);
assert.deepEqual(ordered.messages().slice(orderedStart).map(m => [m.kind, m.version]), [['change', 2], ['change', 3]]);
ordered.send({ kind: 'snapshot', id: 1, expectedVersion: 1 });
assert.equal(ordered.messages().at(-2).position, 4);
assert.equal(ordered.messages().at(-2).version, 3);
assert.equal(ordered.messages().at(-1).text, 'x\n😀');
assert.equal(ordered.pendingFrames, 0);

const navigation = harness('one two three');
navigation.send({ kind: 'select', id: 1, expectedVersion: 1, payload: { anchor: 0, position: 3, reveal: true, selectionGeneration: 0 } });
navigation.send({ kind: 'select', id: 2, expectedVersion: 1, payload: { anchor: 4, position: 7, reveal: true, selectionGeneration: 0 } });
assert.equal(navigation.messages().at(-1).error, undefined, 'programmatic selections share the user selection generation');
navigation.select(8, 13); // 사용자의 새 선택 알림은 아직 프레임 대기 중이다.
navigation.send({ kind: 'select', id: 3, expectedVersion: 1, payload: { anchor: 0, position: 3, reveal: true, selectionGeneration: 0 } });
assert.match(navigation.messages().at(-1).error, /selection changed/);
assert.deepEqual(navigation.selection, { anchor: 8, position: 13 }, 'pending search navigation never overwrites a newer manual selection');
assert.equal(navigation.messages().at(-2).selectionGeneration, 1, 'rejection flushes the manual selection to the host');

const empty = harness('');
empty.send({ kind: 'select', id: 1, expectedVersion: 1, payload: { anchor: 0, position: 0, reveal: true } });
assert.equal(empty.messages().at(-1).error, undefined);
empty.send({ kind: 'replace', id: 2, expectedVersion: 1, payload: { edits: [{ offset: 0, length: 0, text: '\n' }], selectionEnd: 1 } });
assert.equal(empty.text, '\n'); assert.equal(empty.messages().at(-2).line, 2);

// 실제 source post 함수를 사용하며 브라우저 객체 전송의 JSON 직렬화/재파싱을 거친다.
const frameChars = 1024 * 1024;
const prefix = JSON.stringify({ kind: 'probe', text: '' }).indexOf('""') + 1;
const emojiText = 'x'.repeat(frameChars - prefix - 1) + '😀end';
h.context.payloadText = emojiText;
const first = h.raw.length;
vm.runInContext("post({ kind: 'probe', text: payloadText })", h.context);
const wire = h.raw.slice(first);
const frames = wire.map(JSON.parse);
assert.equal(frames.length, 2);
assert.equal(frames[0].data.length, frameChars - 1);
assert.equal(JSON.parse(frames.map(f => f.data).join('')).text, emojiText);
for (const frame of frames) assert.equal(frame.data.isWellFormed(), true);
if (process.argv[2]) writeFileSync(process.argv[2], wire.join('\n'));
if (process.argv[3]) writeFileSync(process.argv[3], gzipSync(wire.join('\n')));
console.log('PASS: activation, ordered snapshot, freeze/thaw/cancel, composition refusal, stale selection/replacement, selection-direction/guard, atomic cursor transaction, readonly selection, empty/Unicode/final-line, bounded viewport messages and boundary flush, surrogate-safe wire frames');

const appearance = harness('한글😀\n');
appearance.send({ kind: 'options', id: 100, expectedVersion: 1, payload: { visual: { zoomPercent: 20, showGuides: false, showMarks: true } } });
assert.equal(appearance.options.visual.zoomPercent, 20);
assert.equal(appearance.options.visual.showGuides, false);
assert.equal(appearance.modelCreations, 1); assert.equal(appearance.undoStops, 0);
const readsBeforeWheel = appearance.textReads;
let prevented = 0;
const wheel = { ctrlKey: true, altKey: false, deltaY: -25, deltaMode: 0, preventDefault() { prevented++; }, stopImmediatePropagation() {} };
for (let i = 0; i < 4; i++) appearance.wheel(wheel);
assert.equal(prevented, 4); assert.equal(appearance.messages().filter(m => m.kind === 'zoom').length, 1);
assert.equal(appearance.messages().find(m => m.kind === 'zoom').steps, 1);
appearance.wheel({ ...wheel, deltaY: 3, deltaMode: 1 });
assert.equal(appearance.messages().filter(m => m.kind === 'zoom').at(-1).steps, -1);
appearance.wheel({ ...wheel, ctrlKey: false }); assert.equal(prevented, 5);
assert.equal(appearance.textReads, readsBeforeWheel); assert.equal(appearance.modelCreations, 1);
assert.equal(appearance.options.mouseWheelZoom, false);
console.log('PASS: visual options preserve model/Undo, one zoom owner, fractional wheel accumulation and no text reads');

// 실제 capture 처리기에 네이티브 입력과 같은 키 조합을 넣는다.
const keys = harness('text');
const shortcuts = () => keys.messages().filter(m => m.kind === 'shortcut' && m.key !== 'body-interaction');
for (const key of ['s', 'f', 'h', 'p']) {
  const before = shortcuts().length;
  assert.equal(keys.key(key, { ctrlKey: true }), 1);
  assert.equal(keys.key(key, { ctrlKey: true, repeat: true }), 1);
  assert.equal(shortcuts().length, before + 1);
}
keys.key('F3'); keys.key('F3', { shiftKey: true });
assert.deepEqual(shortcuts().slice(-2).map(m => m.key), ['next', 'previous']);
keys.key('Escape'); keys.key('Escape', { repeat: true }); keys.keyup('Escape'); keys.key('Escape'); keys.blur(); keys.key('Escape');
assert.deepEqual(shortcuts().slice(-5).map(m => m.key), ['escape', 'escape-released', 'escape', 'escape-released', 'escape']);
keys.key('+', { ctrlKey: true }); keys.key('+', { ctrlKey: true, repeat: true }); keys.key('*', { ctrlKey: true, code: 'NumpadMultiply' });
assert.deepEqual(shortcuts().slice(-3).map(m => m.key), ['zoom-in', 'zoom-in', 'zoom-original']);
const keyCount = shortcuts().length;
keys.compose(true);
for (const key of ['Escape', 'Enter', 'f', 's', 'h', 'p']) assert.equal(keys.key(key, { ctrlKey: true, isComposing: true }), 0);
keys.compose(false);
assert.equal(keys.key('f', { ctrlKey: true, keyCode: 229 }), 0);
assert.equal(shortcuts().length, keyCount, 'composition owns its keys');
for (const key of ['a', '1', '`', 'Backspace', 'Enter', 'ArrowLeft', 'Home', 'End']) assert.equal(keys.key(key), 0);
for (const key of ['c', 'x', 'v', 'a', 'z', 'y']) assert.equal(keys.key(key, { ctrlKey: true }), 0);
assert.equal(keys.key('z', { ctrlKey: true, shiftKey: true }), 0);
for (const key of ['Insert', 'Delete']) assert.equal(keys.key(key, { shiftKey: true }), 0);
assert.equal(keys.key('F11'), 0); assert.equal(keys.key('F12'), 0);
assert.equal(keys.messages().filter(m => m.kind === 'clipboard').length, 0, 'trusted native clipboard keys never create asynchronous bridge actions');
assert.equal(keys.options.emptySelectionClipboard, false); assert.equal(keys.options.copyWithSyntaxHighlighting, false); assert.equal(keys.options.multiCursorLimit, 1);

function menuAction(h, action) { const menu = h.menu(); h.send({ kind: 'edit-command', ticketId: menu.id, action }); }
function clipboardResult(h, text, ok = true) {
  const pending = h.messages().findLast(m => m.kind === 'clipboard');
  h.send({ kind: 'clipboard-result', ticketId: pending.id, text, ok });
  return pending.id;
}
const clip = harness('hello'); clip.send({ kind: 'activate' }); clip.select(0, 5);
menuAction(clip, 'copy'); assert.equal(clip.focuses, 1, 'chosen menu action restores DOM focus before clipboard dispatch');
assert.equal(clip.messages().at(-1).text, 'hello'); clipboardResult(clip, null);
clip.send({ kind: 'edit-command', ticketId: -1, action: 'copy' });
assert.equal(clip.focuses, 1, 'unowned or dismissed menu cannot steal focus');
menuAction(clip, 'cut'); clipboardResult(clip, null); assert.equal(clip.text, '');
menuAction(clip, 'paste'); clipboardResult(clip, '한글\r\n😀'); assert.equal(clip.text, '한글\n😀');
assert.equal(clip.undoStops, 4, 'cut and plain text paste each have one undo transaction');
menuAction(clip, 'undo'); assert.equal(clip.text, '');
menuAction(clip, 'selectAll'); assert.deepEqual(clip.selection, { anchor: 0, position: 0 });
const rapid = harness(''); rapid.send({ kind: 'activate' });
menuAction(rapid, 'paste'); menuAction(rapid, 'paste');
assert.equal(rapid.messages().filter(m => m.kind === 'clipboard').length, 1);
const firstPaste = clipboardResult(rapid, 'a');
assert.equal(rapid.messages().filter(m => m.kind === 'clipboard').length, 2);
rapid.send({ kind: 'clipboard-result', ticketId: firstPaste, text: 'ignored', ok: true });
clipboardResult(rapid, 'b'); assert.equal(rapid.text, 'ab', 'finite rapid menu actions preserve order');
menuAction(rapid, 'paste'); menuAction(rapid, 'undo'); clipboardResult(rapid, 'c'); assert.equal(rapid.text, 'ab', 'menu undo after a pending menu paste executes after that paste');
for (const mutate of [h => h.select(0, 0), h => h.edit('new'), h => { h.send({ kind: 'select', id: 90, expectedVersion: 1, payload: { anchor: 0, position: 0 } }); h.send({ kind: 'select', id: 91, expectedVersion: 1, payload: { anchor: 1, position: 1 } }); }]) {
  const stale = harness('x'); stale.send({ kind: 'activate' }); stale.select(1, 1); menuAction(stale, 'paste'); mutate(stale); const before = stale.text; clipboardResult(stale, 'old'); assert.equal(stale.text, before, 'new manual, model or search selection rejects old menu paste');
}
const failedClipboard = harness(''); failedClipboard.send({ kind: 'activate' });
menuAction(failedClipboard, 'paste'); clipboardResult(failedClipboard, '', false);
menuAction(failedClipboard, 'paste'); clipboardResult(failedClipboard, 'retry'); assert.equal(failedClipboard.text, 'retry');
const menuReadOnly = harness('text', true); menuReadOnly.send({ kind: 'activate' }); menuReadOnly.select(0, 4);
for (const action of ['cut', 'paste', 'undo', 'redo']) menuAction(menuReadOnly, action);
assert.equal(menuReadOnly.messages().filter(m => m.kind === 'clipboard').length, 0);
menuAction(menuReadOnly, 'copy'); assert.equal(menuReadOnly.messages().at(-1).text, 'text');
console.log('PASS: shortcut ownership/repeat/release/blur/IME guard, trusted native clipboard/Undo keys, guarded text menu, ordered menu clipboard, stale async rejection and retry');
const saveAfterPaste = harness(''); saveAfterPaste.send({ kind: 'activate' });
menuAction(saveAfterPaste, 'paste');
saveAfterPaste.send({ kind: 'snapshot', id: 501, expectedVersion: 1 });
assert.equal(saveAfterPaste.messages().some(m => m.kind === 'response' && m.id === 501), false);
clipboardResult(saveAfterPaste, 'before save');
assert.equal(saveAfterPaste.messages().find(m => m.kind === 'response' && m.id === 501).text, 'before save');
const canceledFreeze = harness(''); canceledFreeze.send({ kind: 'activate' }); menuAction(canceledFreeze, 'paste');
canceledFreeze.send({ kind: 'freeze', id: 502, expectedVersion: 1 }); canceledFreeze.send({ kind: 'release', freezeId: 502 });
clipboardResult(canceledFreeze, 'kept'); assert.equal(canceledFreeze.options.readOnly, false);
console.log('PASS: explicit menu paste precedes save snapshot; canceled queued freeze never acquires ownership');
const viewAfterPaste = harness(''); viewAfterPaste.send({ kind: 'activate' }); menuAction(viewAfterPaste, 'paste');
viewAfterPaste.send({ kind: 'options', id: 601, expectedVersion: 1, payload: { readOnly: true } });
assert.equal(viewAfterPaste.options.readOnly, false);
clipboardResult(viewAfterPaste, 'before viewing'); assert.equal(viewAfterPaste.options.readOnly, true);
viewAfterPaste.send({ kind: 'snapshot', id: 602, expectedVersion: 1 });
assert.equal(viewAfterPaste.messages().at(-1).text, 'before viewing');
console.log('PASS: menu paste precedes view-mode read-only boundary and render snapshot');
const tabs = harness('one\ntwo'); tabs.send({ kind: 'activate' }); tabs.select(0, 7);
assert.equal(tabs.key('Tab'), 1); assert.equal(tabs.text, '\t', 'Tab replaces selection with one literal tab, without code indentation');
menuAction(tabs, 'undo'); assert.equal(tabs.text, 'one\ntwo', 'one Undo restores the whole selected range');
tabs.key('Tab', { shiftKey: true }); assert.equal(tabs.messages().at(-1).key, 'focus-previous');
tabs.send({ kind: 'options', id: 701, expectedVersion: 2, payload: { readOnly: true } });
tabs.key('Tab'); assert.equal(tabs.messages().at(-1).key, 'focus-next');
console.log('PASS: literal Tab and Shift+Tab/read-only focus navigation');
