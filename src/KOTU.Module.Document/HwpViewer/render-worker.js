import init, { HwpDocument } from './engine/rhwp.js';

let documentModel;
let pageCount;
const maxSvgCharacters = 16 * 1024 * 1024;

// Use the installed fonts for line breaking and justification in the document's font and size.
let metrics;
let lastFont;
globalThis.measureTextWidth = (font, text) => {
  metrics ??= new OffscreenCanvas(1, 1).getContext('2d');
  if (font !== lastFont) { metrics.font = font; lastFont = font; }
  return metrics.measureText(text).width;
};

self.onmessage = async ({ data }) => {
  try {
    let index;
    if (data.kind === 'open' && !documentModel) {
      if (!(data.bytes instanceof Uint8Array) || data.bytes.length > 64 * 1024 * 1024)
        throw new Error('Invalid document bytes.');
      await init({ module_or_path: new URL('./engine/rhwp_bg.wasm', import.meta.url) });
      documentModel = new HwpDocument(data.bytes);
      pageCount = documentModel.pageCount();
      if (!Number.isInteger(pageCount) || pageCount < 1 || pageCount > 10000)
        throw new Error('Invalid page count.');
      index = 0;
    } else if (data.kind === 'page' && documentModel) {
      index = data.index;
    } else {
      throw new Error('Invalid page request.');
    }
    if (!Number.isInteger(index) || index < 0 || index >= pageCount) throw new Error('Invalid page.');
    const svg = documentModel.renderPageSvg(index);
    if (typeof svg !== 'string' || !svg.length || svg.length > maxSvgCharacters)
      throw new Error('Page exceeds the preview limit.');
    self.postMessage({ index, count: pageCount, svg });
  } catch {
    try { documentModel?.free(); } finally { self.postMessage({ error: true }); self.close(); }
  }
};
