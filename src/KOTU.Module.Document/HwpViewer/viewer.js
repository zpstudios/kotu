const bridge = window.chrome.webview;
const page = document.querySelector('#page');
const viewport = page.parentElement;
const overlay = document.querySelector('#search-overlay');

let bytes;
let offset = 0;
let worker;
let timer;
let searchTimer;
let displayedUrl;
let closed = false;
let pending = true;
let busy;
let index = 0;
let total = 0;
let manualPercent;
let latestSearch;
let latestSelection;
let latestPage;
let searchId = 0;
let selectionId = 0;
let searchUnavailable = false;
let rectangles = [];
let revealAfterPage = false;
let pageRevision = 0;
let selectionPageRevision = 0;

function release() {
  closed = true;
  clearTimeout(timer);
  clearTimeout(searchTimer);
  bytes = latestSearch = latestSelection = latestPage = undefined;
  worker?.terminate();
  if (displayedUrl) URL.revokeObjectURL(displayedUrl);
}

function fail() {
  if (closed) return;
  release();
  bridge.postMessage({ kind: 'failed' });
}

function startTimer() {
  clearTimeout(timer);
  timer = setTimeout(fail, 30000);
}

function clearHighlight() {
  rectangles = [];
  revealAfterPage = false;
  overlay.replaceChildren();
}

function paintHighlight(reveal = false) {
  overlay.replaceChildren();
  if (closed || !rectangles.length || !page.naturalWidth || !page.naturalHeight) return;
  const bounds = page.getBoundingClientRect();
  const viewBounds = viewport.getBoundingClientRect();
  overlay.style.left = bounds.left - viewBounds.left + viewport.scrollLeft + 'px';
  overlay.style.top = bounds.top - viewBounds.top + viewport.scrollTop + 'px';
  overlay.style.width = bounds.width + 'px';
  overlay.style.height = bounds.height + 'px';
  overlay.setAttribute('viewBox', '0 0 ' + page.naturalWidth + ' ' + page.naturalHeight);
  const visible = rectangles.filter(r => r.pageIndex === index && r.x + r.width <= page.naturalWidth + 1 &&
    r.y + r.height <= page.naturalHeight + 1);
  for (const rectangle of visible) {
    const marker = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
    for (const name of ['x', 'y', 'width', 'height']) marker.setAttribute(name, rectangle[name]);
    overlay.append(marker);
  }
  if (!reveal || !visible.length) return;
  const first = visible[0];
  const left = bounds.left - viewBounds.left + first.x * bounds.width / page.naturalWidth;
  const top = bounds.top - viewBounds.top + first.y * bounds.height / page.naturalHeight;
  const height = first.height * bounds.height / page.naturalHeight;
  const width = first.width * bounds.width / page.naturalWidth;
  viewport.scrollBy(left < 9 || left + width > viewport.clientWidth - 9 ? left - viewport.clientWidth * .2 : 0,
    top < 9 || top + height > viewport.clientHeight - 9 ? top - viewport.clientHeight * .2 : 0);
}

function applyFit(mode) {
  if (!['Contain', 'FitWidth', 'FitHeight', 'ActualSize'].includes(mode)) return;
  const before = page.clientHeight;
  const relativeTop = before ? viewport.scrollTop / before : 0;
  manualPercent = undefined;
  page.style.width = '';
  viewport.dataset.fit = mode;
  const keepPosition = mode === 'FitWidth' || mode === 'ActualSize';
  viewport.scrollTo(0, keepPosition ? relativeTop * page.clientHeight : 0);
  paintHighlight();
}

function zoomBy(factor, x, y) {
  if (!total || !page.naturalWidth) return;
  const before = page.getBoundingClientRect();
  if (!before.width) return;
  const positionX = (x - before.left) / before.width;
  const positionY = (y - before.top) / before.height;
  manualPercent = Math.min(500, Math.max(20, before.width / page.naturalWidth * 100 * factor));
  viewport.dataset.fit = 'Manual';
  page.style.width = page.naturalWidth * manualPercent / 100 + 'px';
  const after = page.getBoundingClientRect();
  viewport.scrollBy(after.left + positionX * after.width - x, after.top + positionY * after.height - y);
  paintHighlight();
}

// The worker's synchronous engine is shared with rendering. Only one message is in flight;
// typed queries and navigation replace one mailbox instead of accumulating worker messages.
function pump() {
  if (closed || !worker || busy || pending) return;
  if (latestPage && !currentPageRequest(latestPage)) latestPage = undefined;
  const operation = latestPage || latestSearch || latestSelection;
  if (!operation) return;
  if (operation === latestPage) latestPage = undefined;
  else if (operation === latestSearch) latestSearch = undefined;
  else latestSelection = undefined;
  busy = operation;
  if (operation.kind === 'page') { pending = true; startTimer(); }
  else {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => {
      if (closed || busy !== operation) return;
      searchUnavailable = true;
      latestSearch = latestSelection = undefined;
      clearHighlight();
      bridge.postMessage(operation.kind === 'search'
        ? { kind: 'search-result', id: operation.id, error: true }
        : { kind: 'search-selected', selection: operation.selection, error: true });
      // Keep the current page. A search timeout does not free the render model or claim no text.
      // The page watchdog starts only when its own request can actually enter the worker.
    }, 30000);
  }
  worker.postMessage(operation);
}

function requestPage(target) {
  if (closed || !Number.isInteger(target) || target < 0 || target >= total) return;
  pageRevision++;
  revealAfterPage = false;
  if (latestSelection) bridge.postMessage({ kind: 'search-selected', selection: latestSelection.selection });
  latestSelection = undefined;
  latestPage = { kind: 'page', index: target };
  pump();
}

function cancelSelection() {
  pageRevision++;
  revealAfterPage = false;
  if (latestPage?.searchId !== undefined) latestPage = undefined;
  if (latestSelection) {
    bridge.postMessage({ kind: 'search-selected', selection: latestSelection.selection });
    latestSelection = undefined;
  }
}

function currentPageRequest(operation) {
  return operation?.searchId === undefined || (operation.searchId === searchId &&
    operation.selection === selectionId && operation.pageRevision === pageRevision);
}

async function showPage(data, operation) {
  let url;
  try {
    if (data.error) throw new Error('Unable to render page.');
    if (!currentPageRequest(operation)) { pending = false; clearTimeout(timer); return; }
    url = URL.createObjectURL(new Blob([data.svg], { type: 'image/svg+xml' }));
    // SVG remains an image document: embedded scripts, hyperlinks and external references cannot run.
    const candidate = new Image();
    candidate.src = url;
    await candidate.decode();
    if (closed || !currentPageRequest(operation)) {
      URL.revokeObjectURL(url);
      pending = false;
      clearTimeout(timer);
      return;
    }
    page.src = url;
    if (displayedUrl) URL.revokeObjectURL(displayedUrl);
    displayedUrl = url;
    index = data.index;
    total = data.count;
    pending = false;
    clearTimeout(timer);
    page.alt = 'Page ' + (index + 1);
    page.hidden = false;
    if (manualPercent !== undefined) page.style.width = page.naturalWidth * manualPercent / 100 + 'px';
    viewport.scrollTo(0, 0);
    paintHighlight(revealAfterPage && selectionPageRevision === pageRevision);
    revealAfterPage = false;
    bridge.postMessage({ kind: 'shown', index, count: total });
  } catch {
    if (url) URL.revokeObjectURL(url);
    fail();
  }
}

async function receiveWorker({ data }) {
  if (closed) return;
  const operation = busy;
  if (data.kind === 'search-result') {
    clearTimeout(searchTimer);
    bridge.postMessage(data);
  } else if (data.kind === 'selection') {
    clearTimeout(searchTimer);
    if (!data.error && data.id === searchId && data.selection === selectionId &&
      operation?.pageRevision === pageRevision && !searchUnavailable) {
      rectangles = data.rects;
      selectionPageRevision = pageRevision;
      const target = rectangles[0]?.pageIndex;
      if (Number.isInteger(target) && target !== index) {
        revealAfterPage = true;
        latestPage = { kind: 'page', index: target, searchId: data.id, selection: data.selection, pageRevision };
      } else paintHighlight(true);
    }
    bridge.postMessage({ kind: 'search-selected', selection: data.selection, ...(data.error ? { error: true } : {}) });
  } else await showPage(data, operation);
  busy = undefined;
  pump();
}

bridge.addEventListener('message', ({ data }) => {
  if (closed) return;
  try {
    if (data.kind === 'fit') { applyFit(data.mode); return; }
    if (data.kind === 'page') { requestPage(data.index); return; }
    if (data.kind === 'clear-search') {
      searchId = data.id;
      if (latestSearch) bridge.postMessage({ kind: 'search-result', id: latestSearch.id, count: 0, limited: false });
      latestSearch = undefined;
      cancelSelection();
      clearHighlight();
      return;
    }
    if (data.kind === 'cancel-selection') {
      if (data.selection === selectionId) cancelSelection();
      return;
    }
    if (data.kind === 'search') {
      if (!Number.isSafeInteger(data.id) || typeof data.query !== 'string' || data.query.length > 1024) return;
      searchId = data.id;
      clearHighlight();
      if (latestSearch) bridge.postMessage({ kind: 'search-result', id: latestSearch.id, count: 0, limited: false });
      latestSearch = data;
      if (searchUnavailable) {
        latestSearch = undefined;
        bridge.postMessage({ kind: 'search-result', id: data.id, error: true });
      } else pump();
      return;
    }
    if (data.kind === 'select') {
      if (data.id !== searchId || searchUnavailable) {
        bridge.postMessage({ kind: 'search-selected', selection: data.selection });
        return;
      }
      if (latestSelection) bridge.postMessage({ kind: 'search-selected', selection: latestSelection.selection });
      selectionId = data.selection;
      latestSelection = { ...data, pageRevision };
      pump();
      return;
    }
    if (data.kind === 'open' && !bytes && !worker) {
      if (!Number.isInteger(data.length) || data.length <= 0 || data.length > 64 * 1024 * 1024)
        throw new Error('Invalid size.');
      bytes = new Uint8Array(data.length);
    } else if (data.kind === 'chunk' && bytes && data.offset === offset) {
      const raw = atob(data.data);
      if (!raw.length || raw.length > 96 * 1024 || raw.length > bytes.length - offset)
        throw new Error('Invalid document chunk.');
      for (let i = 0; i < raw.length; i++) bytes[offset + i] = raw.charCodeAt(i);
      offset += raw.length;
    } else throw new Error('Invalid document message.');
    startTimer();
    if (offset < bytes.length) bridge.postMessage({ kind: 'read', offset });
    else {
      worker = new Worker('./render-worker.js', { type: 'module' });
      worker.onerror = fail;
      worker.onmessageerror = fail;
      worker.onmessage = receiveWorker;
      busy = { kind: 'open' };
      worker.postMessage({ kind: 'open', bytes }, [bytes.buffer]);
      bytes = undefined;
    }
  } catch { fail(); }
});

function observeBodyInteraction() {
  cancelSelection();
  bridge.postMessage({ kind: 'search-body-interaction' });
}
window.addEventListener('pointerdown', observeBodyInteraction);
window.addEventListener('wheel', event => {
  observeBodyInteraction();
  if (!total || !event.ctrlKey || event.deltaY === 0) return;
  event.preventDefault();
  zoomBy(event.deltaY < 0 ? 1.1 : 1 / 1.1, event.clientX, event.clientY);
}, { passive: false });
window.addEventListener('keydown', event => {
  if (closed || event.isComposing || event.keyCode === 229 || event.altKey || event.metaKey) return;
  const key = event.key;
  const find = event.ctrlKey && !event.shiftKey && (key.toLowerCase() === 'f' || key.toLowerCase() === 'h')
    ? (key.toLowerCase() === 'h' ? 'find-options' : 'find')
    : !event.ctrlKey && key === 'F3' ? event.shiftKey ? 'find-previous' : 'find-next' : undefined;
  if (find) {
    event.preventDefault();
    event.stopImmediatePropagation();
    if (!event.repeat) bridge.postMessage({ kind: find });
    return;
  }
  if (total && event.ctrlKey && ['+', '=', '-', '0'].includes(key)) {
    event.preventDefault();
    if (key === '0') bridge.postMessage({ kind: 'original' });
    else zoomBy(key === '-' ? 1 / 1.1 : 1.1, viewport.clientWidth / 2, viewport.clientHeight / 2);
    return;
  }
  if (event.ctrlKey || event.shiftKey) return;
  const commands = { Escape: 'escape', f: 'reapply', a: 'original', PageUp: 'previous', PageDown: 'next' };
  const command = Object.hasOwn(commands, key) && commands[key];
  if (!command || (!total && command !== 'escape')) return;
  event.preventDefault();
  event.stopImmediatePropagation();
  if (!event.repeat) bridge.postMessage({ kind: command });
}, true);
window.addEventListener('keyup', event => {
  if (!closed && event.key === 'Escape') bridge.postMessage({ kind: 'search-escape-released' });
}, true);
if (typeof ResizeObserver !== 'undefined') new ResizeObserver(() => paintHighlight()).observe(page);
window.addEventListener('pagehide', release, { once: true });
bridge.postMessage({ kind: 'ready' });
