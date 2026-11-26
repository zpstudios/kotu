import init, { HwpDocument } from './engine/rhwp.js';

let documentModel;
let pageCount;
const maxSvgCharacters = 16 * 1024 * 1024;
let matches = [];
let searchId = 0;
const maxMatches = 10000;
const maxSearchJsonCharacters = 16 * 1024 * 1024;
const validOffset = value => Number.isInteger(value) && value >= 0 && value <= 0xffffffff;
function validMatch(match) {
  if (!match || ![match.sec, match.para, match.charOffset, match.length].every(validOffset) ||
      match.length === 0 || match.charOffset + match.length > 0xffffffff) return false;
  const cell = match.cellContext;
  return cell == null || [cell.parentPara, cell.ctrlIdx, cell.cellIdx, cell.cellPara].every(validOffset);
}

// Use the installed fonts for line breaking and justification in the document's font and size.
let metrics;
let lastFont;
globalThis.measureTextWidth = (font, text) => {
  metrics ??= new OffscreenCanvas(1, 1).getContext('2d');
  if (font !== lastFont) { metrics.font = font; lastFont = font; }
  return metrics.measureText(text).width;
};

self.onmessage = async ({ data }) => {
  if (data.kind === 'search' || data.kind === 'select') {
    try {
      if (!documentModel || !Number.isSafeInteger(data.id)) throw new Error('Invalid search.');
      if (data.kind === 'search') {
        searchId = data.id;
        matches = [];
        if (typeof data.query !== 'string' || data.query.length > 1024) throw new Error('Invalid query.');
        if (!data.query) { self.postMessage({ kind: 'search-result', id: searchId, count: 0, limited: false }); return; }
        // rhwp uses Unicode-scalar offsets and table-cell context. getPageText cannot map these.
        const json = documentModel.searchAllText(data.query, false, true);
        if (typeof json !== 'string' || json.length > maxSearchJsonCharacters) throw new Error('Search exceeds limit.');
        const found = JSON.parse(json);
        if (!Array.isArray(found)) throw new Error('Invalid search result.');
        matches = found.slice(0, maxMatches);
        if (!matches.every(validMatch)) { matches = []; throw new Error('Invalid search offsets.'); }
        self.postMessage({ kind: 'search-result', id: searchId, count: matches.length, limited: found.length > maxMatches });
      } else {
        if (data.id !== searchId || !Number.isInteger(data.index) || data.index < 0 || data.index >= matches.length)
          throw new Error('Stale selection.');
        const match = matches[data.index];
        const cell = match.cellContext;
        const raw = cell
          ? documentModel.getSelectionRectsInCell(match.sec, cell.parentPara, cell.ctrlIdx, cell.cellIdx,
            cell.cellPara, match.charOffset, cell.cellPara, match.charOffset + match.length)
          : documentModel.getSelectionRects(match.sec, match.para, match.charOffset,
            match.para, match.charOffset + match.length);
        if (typeof raw !== 'string' || raw.length > maxSearchJsonCharacters) throw new Error('Match geometry exceeds limit.');
        const rectangles = JSON.parse(raw);
        if (!Array.isArray(rectangles)) throw new Error('Invalid match position.');
        const rects = rectangles.filter(r => Number.isInteger(r.pageIndex) && r.pageIndex >= 0 && r.pageIndex < pageCount &&
          [r.x, r.y, r.width, r.height].every(Number.isFinite) && r.x >= 0 && r.y >= 0 && r.width > 0 && r.height > 0).slice(0, 256);
        if (!rects.length) throw new Error('No visible match position.');
        self.postMessage({ kind: 'selection', id: data.id, selection: data.selection, rects });
      }
    } catch {
      self.postMessage({ kind: data.kind === 'search' ? 'search-result' : 'selection',
        id: data.id, selection: data.selection, error: true });
    }
    return; // A failed search never frees the renderer or switches to the text preview.
  }
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
