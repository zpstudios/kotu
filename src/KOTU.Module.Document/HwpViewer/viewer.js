const bridge = window.chrome.webview;
const page = document.querySelector('#page');
const position = document.querySelector('#position');
const previous = document.querySelector('#previous');
const next = document.querySelector('#next');
const smaller = document.querySelector('#smaller');
const larger = document.querySelector('#larger');
const fit = document.querySelector('#fit');

let bytes;
let offset = 0;
let worker;
let timer;
let displayedUrl;
let closed = false;
let pending = true;
let index = 0;
let total = 0;
let zoom = 100;

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

function controls() {
  previous.disabled = pending || index === 0;
  next.disabled = pending || index + 1 >= total;
  smaller.disabled = !total || zoom <= 50;
  larger.disabled = !total || zoom >= 300;
  fit.disabled = !total;
}

function setZoom(value) {
  zoom = Math.min(300, Math.max(50, value));
  page.style.width = zoom + '%';
  fit.textContent = zoom === 100 ? 'Fit width' : zoom + '%';
  controls();
}

function requestPage(target) {
  if (closed || pending || target < 0 || target >= total) return;
  pending = true;
  controls();
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
    const first = total === 0;
    index = data.index;
    total = data.count;
    pending = false;
    clearTimeout(timer);
    page.alt = 'Page ' + (index + 1);
    page.hidden = false;
    page.parentElement.scrollTo(0, 0);
    position.textContent = (index + 1) + ' / ' + total;
    setZoom(zoom);
    if (first) bridge.postMessage({ kind: 'shown' });
  } catch {
    if (url) URL.revokeObjectURL(url);
    fail();
  }
}

bridge.addEventListener('message', ({ data }) => {
  if (closed) return;
  try {
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

previous.onclick = () => requestPage(index - 1);
next.onclick = () => requestPage(index + 1);
smaller.onclick = () => setZoom(zoom - 25);
larger.onclick = () => setZoom(zoom + 25);
fit.onclick = () => setZoom(100);
window.addEventListener('pagehide', release, { once: true });
bridge.postMessage({ kind: 'ready' });
