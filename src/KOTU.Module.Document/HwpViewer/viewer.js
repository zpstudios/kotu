const bridge = window.chrome.webview;
const page = document.querySelector('#page');
const viewport = page.parentElement;

let bytes;
let offset = 0;
let worker;
let timer;
let displayedUrl;
let closed = false;
let pending = true;
let index = 0;
let total = 0;
let manualPercent;

function release() {
  closed = true;
  clearTimeout(timer);
  bytes = undefined;
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

function applyFit(mode) {
  if (!['Contain', 'FitWidth', 'FitHeight', 'ActualSize'].includes(mode)) return;
  const before = page.clientHeight;
  const relativeTop = before ? viewport.scrollTop / before : 0;
  manualPercent = undefined;
  page.style.width = '';
  viewport.dataset.fit = mode;
  const keepPosition = mode === 'FitWidth' || mode === 'ActualSize';
  viewport.scrollTo(0, keepPosition ? relativeTop * page.clientHeight : 0);
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
}

function requestPage(target) {
  if (closed || pending || !Number.isInteger(target) || target < 0 || target >= total) return;
  pending = true;
  startTimer();
  worker.postMessage({ kind: 'page', index: target });
}

async function showPage({ data }) {
  if (closed) return;
  let url;
  try {
    if (data.error) throw new Error('Unable to render page.');
    url = URL.createObjectURL(new Blob([data.svg], { type: 'image/svg+xml' }));
    // An image document disables scripts, hyperlinks and external references in the rendered SVG.
    page.src = url;
    await page.decode();
    if (closed) { URL.revokeObjectURL(url); return; }
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
    bridge.postMessage({ kind: 'shown', index, count: total });
  } catch {
    if (url) URL.revokeObjectURL(url);
    fail();
  }
}

bridge.addEventListener('message', ({ data }) => {
  if (closed) return;
  try {
    if (data.kind === 'fit') { applyFit(data.mode); return; }
    if (data.kind === 'page') { requestPage(data.index); return; }
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
    } else {
      throw new Error('Invalid document message.');
    }
    startTimer();
    if (offset < bytes.length) {
      bridge.postMessage({ kind: 'read', offset });
    } else {
      worker = new Worker('./render-worker.js', { type: 'module' });
      worker.onerror = fail;
      worker.onmessageerror = fail;
      worker.onmessage = showPage;
      worker.postMessage({ kind: 'open', bytes }, [bytes.buffer]);
      bytes = undefined;
    }
  } catch { fail(); }
});

window.addEventListener('wheel', event => {
  if (!total || !event.ctrlKey || event.deltaY === 0) return;
  event.preventDefault();
  zoomBy(event.deltaY < 0 ? 1.1 : 1 / 1.1, event.clientX, event.clientY);
}, { passive: false });
window.addEventListener('keydown', event => {
  if (closed || event.isComposing || event.keyCode === 229 || event.altKey || event.metaKey) return;
  const key = event.key;
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
window.addEventListener('pagehide', release, { once: true });
bridge.postMessage({ kind: 'ready' });
