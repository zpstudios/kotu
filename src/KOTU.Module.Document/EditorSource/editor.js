import * as monaco from 'monaco-editor/esm/vs/editor/editor.main.js';
import { createVisuals } from './visual.js';

// 본문은 메시지 데이터로만 교환한다. 상태의 시계는 브라우저 내부에서만 사용한다.
globalThis.MonacoEnvironment = { getWorker: () => new Worker(new URL('./editor.worker.js', import.meta.url), { type: 'module' }) };
const epoch = location.hash.slice(1);
let transportId = 0;
const frameChars = 1024 * 1024;
function post(value) {
  const message = { ...value, epoch };
  const json = JSON.stringify(message);
  if (json.length <= frameChars) { chrome.webview.postMessage(message); return; }
  const transfer = ++transportId;
  for (let offset = 0, index = 0; offset < json.length; index++) {
    let end = Math.min(offset + frameChars, json.length);
    if (end < json.length && json.charCodeAt(end - 1) >= 0xD800 && json.charCodeAt(end - 1) <= 0xDBFF &&
        json.charCodeAt(end) >= 0xDC00 && json.charCodeAt(end) <= 0xDFFF) end--;
    chrome.webview.postMessage({ kind: 'frame', epoch, transfer, index, total: json.length, data: json.slice(offset, end) });
    offset = end;
  }
}
let editor, model, loaded = false, composing = false, readOnly = false, freezeId = null;
// Normalizing a Windows empty/single-line model to LF can advance Monaco's initial revision.
// Keep the wire session at revision1; subsequent edits still advance exactly once.
let initialVersionOffset = 0;
function wireVersion() { return model.getVersionId() - initialVersionOffset; }
let stateFrame = null;
let selectionGeneration = 0, actionGeneration = 0;
let visuals;
let wheelRemainder = 0;

function state() {
  if (!loaded) return;
  const s = editor.getSelection();
  post({ kind: 'state', version: wireVersion(), composing, selectionGeneration,
    anchor: model.getOffsetAt(s.getSelectionStart()), position: model.getOffsetAt(s.getPosition()),
    line: s.positionLineNumber, column: s.positionColumn,
    viewport: visuals.viewport() });
}
// 뷰포트 이벤트는 프레임당 하나만 보낸다. 델타/리비전 메시지의 순서는 합치지 않는다.
function scheduleState() {
  if (!loaded) return;
  if (document.visibilityState === 'hidden') { flushState(); return; }
  if (stateFrame === null) stateFrame = requestAnimationFrame(() => { stateFrame = null; state(); });
}
function flushState() {
  if (stateFrame !== null) { cancelAnimationFrame(stateFrame); stateFrame = null; }
  state();
}
window.addEventListener('blur', () => { flushState(); bodyInteracted = false; releaseEscape(); });
document.addEventListener('visibilitychange', flushState);
function updateReadOnly() { editor.updateOptions({ readOnly: readOnly || freezeId !== null }); }
function response(data, extra = {}) {
  flushState();
  post({ kind: 'response', id: data.id, version: wireVersion(), ...extra });
}
function validOffset(n) { return Number.isSafeInteger(n) && n >= 0 && n <= model.getValueLength(); }
window.addEventListener('wheel', event => {
  if (!loaded || !event.ctrlKey || event.altKey) return;
  event.preventDefault(); event.stopImmediatePropagation();
  // DOM 픽셀 휠은 Windows 한 노치(120)가 보통 100 CSS 픽셀로 전달된다.
  wheelRemainder -= event.deltaY * (event.deltaMode === 1 ? 100 / 3 : event.deltaMode === 2 ? 100 : 1);
  const steps = Math.trunc(wheelRemainder / 100);
  if (steps) { wheelRemainder -= steps * 100; post({ kind: 'zoom', version: wireVersion(), steps }); }
}, { capture: true, passive: false });
// 키의 소유자는 브라우저 하나다. IME 키는 전달하거나 강제 확정하지 않는다.
let bodyInteracted = false;
let escapeHeld = false, editId = 0, pendingClipboard = null, contextTicket = null;
const inputQueue = [];
function releaseEscape() {
  if (!loaded) return;
  escapeHeld = false;
  post({ kind: 'shortcut', version: wireVersion(), key: 'escape-released' });
}
function ticket() {
  const s = editor.getSelection();
  return { version: wireVersion(), selectionGeneration, actionGeneration,
    anchor: model.getOffsetAt(s.getSelectionStart()), position: model.getOffsetAt(s.getPosition()) };
}
function currentTicket(t) {
  const now = ticket();
  return !composing && t && Object.keys(now).every(k => now[k] === t[k]);
}
function editable() { return !readOnly && freezeId === null && !composing; }
function selectedText(t) {
  const a = model.getPositionAt(Math.min(t.anchor, t.position)), b = model.getPositionAt(Math.max(t.anchor, t.position));
  return model.getValueInRange(new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column));
}
function replaceSelection(t, text) {
  const start = Math.min(t.anchor, t.position), end = Math.max(t.anchor, t.position);
  const a = model.getPositionAt(start), b = model.getPositionAt(end);
  editor.pushUndoStop();
  editor.executeEdits('kotu', [{ range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column), text, forceMoveMarkers: true }], () => {
    const p = model.getPositionAt(start + text.length);
    return [new monaco.Selection(p.lineNumber, p.column, p.lineNumber, p.column)];
  });
  editor.pushUndoStop();
}
function editAction(action, expected = ticket()) {
  inputQueue.push({ ...expected, id: ++editId, action });
  drainInput();
}
function rebaseQueue(before) {
  for (const queued of inputQueue) if (sameTicket(queued, before)) Object.assign(queued, ticket());
}
function sameTicket(a, b) {
  return ['version', 'selectionGeneration', 'actionGeneration', 'anchor', 'position'].every(key => a[key] === b[key]);
}
function drainInput() {
  if (pendingClipboard) return;
  while (inputQueue.length) {
    const next = inputQueue.shift(), action = next.action;
    if (next.hostMessage) { handleHostMessage(next.hostMessage, true); continue; }
    if (!currentTicket(next) || (['cut', 'paste', 'undo', 'redo'].includes(action) && !editable())) continue;
    if (action === 'undo' || action === 'redo') {
      const before = ticket();
      if (action === 'undo' ? model.canUndo() : model.canRedo()) editor.trigger('kotu', action, null);
      rebaseQueue(before);
    } else if (action === 'selectAll') {
      const before = ticket(), end = model.getPositionAt(model.getValueLength());
      editor.setSelection(new monaco.Selection(1, 1, end.lineNumber, end.column));
      selectionGeneration++; flushState(); rebaseQueue(before);
    } else if (['copy', 'cut', 'paste'].includes(action)) {
      if (action !== 'paste' && next.anchor === next.position) continue;
      pendingClipboard = next;
      flushState();
      post({ kind: 'clipboard', ...next, text: action === 'paste' ? null : selectedText(next) });
      return;
    }
  }
}
function bodyInteraction() {
  if (!loaded || bodyInteracted) return;
  bodyInteracted = true;
  post({ kind: 'shortcut', version: wireVersion(), key: 'body-interaction' });
}
window.addEventListener('keyup', event => { if (event.key === 'Escape') releaseEscape(); }, true);
window.addEventListener('keydown', event => {
  if (!loaded || event.isComposing || event.keyCode === 229 || composing) return;
  const key = event.key.toLowerCase();
  if (key === 'tab' && !event.ctrlKey && !event.altKey && !event.metaKey) {
    event.preventDefault(); event.stopImmediatePropagation();
    if (!event.shiftKey && editable()) { bodyInteraction(); replaceSelection(ticket(), '\t'); }
    else if (!event.repeat) { flushState(); post({ kind: 'shortcut', version: wireVersion(), key: event.shiftKey ? 'focus-previous' : 'focus-next' }); }
    return;
  }
  let action = null;
  if (!event.altKey && !event.metaKey) {
    if (event.ctrlKey) {
      if (!event.shiftKey && ['s', 'f', 'h', 'p'].includes(key)) action = 'ctrl+' + key;
      else if (['+', '=', 'add'].includes(key)) action = 'zoom-in';
      else if (['-', '_', 'subtract'].includes(key)) action = 'zoom-out';
      else if (event.code === 'NumpadMultiply') action = 'zoom-original';
    } else if (key === 'f3') action = event.shiftKey ? 'previous' : 'next';
    else if (!event.shiftKey && key === 'escape') action = 'escape';
  }
  if (!action) {
    if (!['control', 'shift', 'alt', 'meta'].includes(key)) bodyInteraction();
    return;
  }
  event.preventDefault(); event.stopImmediatePropagation();
  if (event.repeat && !['zoom-in', 'zoom-out'].includes(action)) return;
  if (action === 'escape') { if (escapeHeld) return; escapeHeld = true; }
  flushState();
  post({ kind: 'shortcut', version: wireVersion(), key: action });
}, true);
window.addEventListener('pointerdown', bodyInteraction, true);
window.addEventListener('contextmenu', event => {
  if (!loaded) return;
  event.preventDefault(); event.stopImmediatePropagation();
  if (composing) return;
  contextTicket = { ...ticket(), id: ++editId };
  flushState();
  post({ kind: 'context', ...contextTicket, x: event.clientX, y: event.clientY,
    editable: editable(), canUndo: model.canUndo(), canRedo: model.canRedo() });
}, true);

function handleHostMessage(event, queued = false) {
  const data = event.data;
  if (!data || data.epoch !== epoch) return;
  if (data.kind === 'load' && !loaded && typeof data.text === 'string') {
    if (data.text.includes('\r')) return;
    readOnly = data.readOnly === true;
    model = monaco.editor.createModel(data.text, 'plaintext');
    model.setEOL(monaco.editor.EndOfLineSequence.LF);
    initialVersionOffset = model.getVersionId() - 1;
    model.updateOptions({ insertSpaces: false, tabSize: 4 });
    editor = monaco.editor.create(document.getElementById('editor'), {
      model, readOnly: true, fontFamily: 'Consolas', fontSize: 14, lineHeight: 0,
      mouseWheelZoom: false, renderWhitespace: 'none',
      copyWithSyntaxHighlighting: false, emptySelectionClipboard: false, multiCursorLimit: 1,
      pasteAs: { enabled: false }, dropIntoEditor: { enabled: false },
      wordWrap: 'on', wrappingIndent: 'none', minimap: { enabled: false }, lineNumbers: 'off',
      glyphMargin: false, lineDecorationsWidth: 0, folding: false, renderLineHighlight: 'none',
      padding: { top: 16, bottom: 68 }, scrollBeyondLastLine: false, smoothScrolling: false,
      automaticLayout: true, roundedSelection: false, contextmenu: false, links: false,
      quickSuggestions: false, suggestOnTriggerCharacters: false, wordBasedSuggestions: 'off',
      parameterHints: { enabled: false }, inlineSuggest: { enabled: false }, hover: { enabled: false },
      autoIndent: 'none', autoClosingBrackets: 'never', autoClosingQuotes: 'never', autoSurround: 'never',
      formatOnType: false, formatOnPaste: false, trimAutoWhitespace: false, detectIndentation: false,
      occurrencesHighlight: 'off', selectionHighlight: false, bracketPairColorization: { enabled: false },
      guides: { indentation: false, bracketPairs: false }, unicodeHighlight: { ambiguousCharacters: false, invisibleCharacters: false },
      ariaLabel: 'Text editor', accessibilitySupport: 'auto'
    });
    visuals = createVisuals(monaco, editor, model, data.visual);
    model.onDidChangeContent(e => {
      let changes = e.changes.map(c => ({ offset: c.rangeOffset, length: c.rangeLength, text: c.text })).sort((a, b) => b.offset - a.offset);
      if (changes.length > 10000) {
        const start = changes[changes.length - 1].offset, end = changes[0].offset + changes[0].length;
        const shift = changes.reduce((n, c) => n + c.text.length - c.length, 0);
        const a = model.getPositionAt(start), b = model.getPositionAt(end + shift);
        changes = [{ offset: start, length: end - start, text: model.getValueInRange(new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column)) }];
      }
      post({ kind: 'change', version: e.versionId - initialVersionOffset, changes });
      scheduleState();
    });
    editor.onDidChangeCursorSelection(event => {
      actionGeneration++;
      // 직접 입력/클릭으로 바꾼 선택 뒤에 도착한 검색 이동은 사용자의 새 선택을 덮지 않는다.
      if (event.source !== 'api' && event.source !== 'kotu') selectionGeneration++;
      scheduleState();
    });
    editor.onDidScrollChange(scheduleState);
    editor.onDidCompositionStart(() => { composing = true; flushState(); });
    editor.onDidCompositionEnd(() => { composing = false; flushState(); });
    loaded = true;
    const layout = visuals.viewport();
    post({ kind: 'loaded', version: wireVersion(), text: model.getValue(monaco.editor.EndOfLinePreference.LF), width: layout.width, height: layout.height });
    state();
    return;
  }
  if (!loaded) return;
  if (data.kind === 'edit-command') {
    if (contextTicket?.id === data.ticketId) { const expected = contextTicket; contextTicket = null; editor.focus(); editAction(data.action, expected); }
    return;
  }
  if (data.kind === 'clipboard-result') {
    const pending = pendingClipboard;
    if (!pending || pending.id !== data.ticketId) return;
    pendingClipboard = null;
    if (data.ok && currentTicket(pending)) {
      const before = ticket();
      if (pending.action === 'cut' && editable()) replaceSelection(pending, '');
      else if (pending.action === 'paste' && editable() && typeof data.text === 'string')
        replaceSelection(pending, data.text.replaceAll('\r\n', '\n').replaceAll('\r', '\n'));
      // 같은 입력 소유자의 연속 동작만 방금 적용한 편집 뒤로 옮긴다.
      rebaseQueue(before);
    }
    drainInput();
    return;
  }
  if (data.kind === 'activate') { updateReadOnly(); return; }
  // 시간 초과된 freeze는 응답이 도착하기 전에도 같은 소유권으로 해제할 수 있다.
  if (data.kind === 'release') {
    for (let i = inputQueue.length - 1; i >= 0; i--)
      if (inputQueue[i].hostMessage?.data.kind === 'freeze' && inputQueue[i].hostMessage.data.id === data.freezeId) inputQueue.splice(i, 1);
    if (freezeId === data.freezeId) { freezeId = null; updateReadOnly(); }
    return;
  }
  if (!Number.isSafeInteger(data.id) || !Number.isSafeInteger(data.expectedVersion)) return;
  if (!queued && pendingClipboard && (['snapshot', 'freeze'].includes(data.kind) ||
      (data.kind === 'options' && typeof data.payload?.readOnly === 'boolean'))) {
    inputQueue.push({ hostMessage: event }); return;
  }
  try {
    if (data.expectedVersion > wireVersion()) throw new Error('Editor revision is behind the host. Keep this document open and retry.');
    if (data.kind === 'snapshot' || data.kind === 'freeze') {
      if (composing) throw new Error('Finish the current text composition, then save or close again.');
      if (data.kind === 'freeze') {
        if (freezeId !== null) throw new Error('A close operation is already pending.');
        freezeId = data.id; updateReadOnly();
      }
      response(data, { text: model.getValue(monaco.editor.EndOfLinePreference.LF), frozen: freezeId !== null });
      return;
    }
    if (data.kind === 'thaw') {
      if (freezeId === data.payload.freezeId) { freezeId = null; updateReadOnly(); }
      response(data); return;
    }
    if (data.kind === 'select' || data.kind === 'replace') {
      if (composing || data.expectedVersion !== wireVersion()) throw new Error('The document changed. Retry the operation with current text.');
    }
    if (data.kind === 'select') {
      const p = data.payload;
      if (p.selectionGeneration !== selectionGeneration) throw new Error('The selection changed. Retry the search navigation.');
      if (!validOffset(p.anchor) || !validOffset(p.position)) throw new Error('Invalid selection offsets.');
      const a = model.getPositionAt(p.anchor), b = model.getPositionAt(p.position);
      editor.setSelection(new monaco.Selection(a.lineNumber, a.column, b.lineNumber, b.column));
      if (p.reveal) editor.revealPositionInCenterIfOutsideViewport(b);
    } else if (data.kind === 'replace') {
      if (readOnly || freezeId !== null) throw new Error('The document is read-only.');
      const edits = data.payload.edits;
      if (!Array.isArray(edits) || !edits.length || edits.length > 10000) throw new Error('Invalid edit count.');
      let previous = model.getValueLength() + 1, size = model.getValueLength();
      for (const e of edits) {
        if (!validOffset(e.offset) || !Number.isSafeInteger(e.length) || e.length < 0 ||
            e.offset + e.length > model.getValueLength() || e.offset >= previous || e.offset + e.length > previous ||
            typeof e.text !== 'string' || e.text.includes('\r')) throw new Error('Invalid edit range.');
        previous = e.offset; size += e.text.length - e.length;
      }
      if (!Number.isSafeInteger(size) || size > 2147483631) throw new Error('The edit exceeds the supported UTF16 address range.');
      const selectionEnd = data.payload.selectionEnd ?? null;
      if (selectionEnd !== null && (!Number.isSafeInteger(selectionEnd) || selectionEnd < 0 || selectionEnd > size))
        throw new Error('Invalid replacement selection.');
      const expectedSelection = data.payload.expectedSelection;
      if (expectedSelection) {
        const selection = editor.getSelection();
        if (model.getOffsetAt(selection.getSelectionStart()) !== expectedSelection.anchor ||
            model.getOffsetAt(selection.getPosition()) !== expectedSelection.position)
          throw new Error('The selection changed. Select the match again before replacing.');
      }
      const operations = edits.map(e => {
        const a = model.getPositionAt(e.offset), b = model.getPositionAt(e.offset + e.length);
        return { range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column), text: e.text, forceMoveMarkers: true };
      });
      editor.pushUndoStop();
      editor.executeEdits('kotu', operations, selectionEnd === null ? undefined : () => {
        const p = model.getPositionAt(selectionEnd);
        return [new monaco.Selection(p.lineNumber, p.column, p.lineNumber, p.column)];
      });
      editor.pushUndoStop();
    } else if (data.kind === 'options') {
      const p = data.payload ?? {};
      if (composing && typeof p.readOnly === 'boolean' && p.readOnly !== readOnly)
        throw new Error('Finish text composition before switching view.');
      if (typeof p.readOnly === 'boolean') readOnly = p.readOnly;
      const options = {};
      for (const key of ['wordWrap'])
        if (Object.hasOwn(p, key)) options[key] = p[key];
      editor.updateOptions(options); updateReadOnly();
      if (p.visual) visuals.apply(p.visual);
    } else if (data.kind === 'focus') editor.focus();
    else throw new Error('Unknown editor command.');
    response(data);
  } catch (error) { response(data, { error: error.message }); }
}
chrome.webview.addEventListener('message', handleHostMessage);
window.addEventListener('unload', () => {
  if (stateFrame !== null) cancelAnimationFrame(stateFrame);
  visuals?.dispose(); editor?.dispose(); model?.dispose();
});
post({ kind: 'ready' });
