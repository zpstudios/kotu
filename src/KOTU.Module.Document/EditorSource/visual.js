// 네이티브와 같은 문서 배율·여백. 거터 토글은 본문 폭을 바꾸지 않는다.
export function visualMetrics(value = {}) {
  const scale = Math.min(500, Math.max(20, Number(value.zoomPercent) || 100)) / 100;
  const digits = Math.max(0, Math.min(12, value.gutterDigits | 0));
  // Monaco는 fontSize 6·lineHeight 8 미만을 거부한다. 낮은 배율의 잔여 비율은 같은 축에서만 유도한다.
  const layoutScale = Math.max(0.5, scale), renderScale = scale / layoutScale;
  // Pinned Monaco's Windows default (fontInfo.js): allow fallback glyphs room above/below the baseline.
  const fontSize = 14 * layoutScale, editorLineHeight = Math.max(8, Math.round(fontSize * 1.35));
  // Monaco accepts integer decoration widths. Share that rounded width with every overlay.
  const gutter = digits ? Math.round((12 + digits * 8) * scale / renderScale) * renderScale : 0;
  return { scale, renderScale, fontSize, editorLineHeight, gutter,
    lineHeight: editorLineHeight * renderScale,
    left: 24, right: 24, textLeft: 24 + gutter, top: 16, bottom: 68 };
}

// Monaco의 공개 좌표 API만 사용한다. 길이가 긴 한 논리 줄도 화면 밖 랩을 순회하지 않는다.
export function visibleDecorations(editor, model, visual, metrics) {
  const result = { guides: [], numbers: [], marks: [], visited: 0 };
  if (!visual.decorEnabled) return result;
  const rawLayout = editor.getLayoutInfo(), q = metrics.renderScale;
  const layout = { width: rawLayout.width * q, height: rawLayout.height * q }, ranges = editor.getVisibleRanges();
  if (!ranges.length || layout.width <= 0 || layout.height <= 0) return result;
  const top = editor.getScrollTop() * q, count = model.getLineCount(), step = metrics.lineHeight;
  const first = ranges[0].startLineNumber, last = Math.min(count, ranges.at(-1).endLineNumber);
  const startY = editor.getTopForLineNumber(first) * q - top;
  const endY = editor.getBottomForLineNumber(last) * q - top;
  if (visual.showGuides && !visual.viewSuppressed) {
    const firstRow = Math.max(1, Math.ceil((Math.max(0, metrics.top - top) - startY - 2 * metrics.scale) / step));
    for (let i = firstRow; result.guides.length < 600; i++) {
      const y = startY + i * step + 2 * metrics.scale;
      if (y > Math.min(layout.height, endY + 2 * metrics.scale)) break;
      result.guides.push(y);
    }
  }
  const digitWidth = String(count).length * 8 * metrics.scale;
  const gutterX = Math.max(metrics.left, metrics.textLeft - 12 * metrics.scale - digitWidth);
  const gutterVisible = visual.showGuides && gutterX + digitWidth <= metrics.textLeft - 2 * metrics.scale;
  for (let line = first; line <= last && result.visited < 600; line++) {
    result.visited++;
    const y = editor.getTopForLineNumber(line) * q - top;
    if (gutterVisible && y + step > 0 && y < layout.height)
      result.numbers.push({ label: String(line), x: gutterX + digitWidth, y });
    if (visual.showMarks && !visual.viewSuppressed) {
      const column = model.getLineMaxColumn(line);
      const endTop = editor.getTopForPosition(line, column) * q - top;
      if (endTop + step <= 0 || endTop >= layout.height) continue;
      const end = editor.getScrolledVisiblePosition({ lineNumber: line, column });
      if (end && end.top * q + step > 0 && end.top * q < layout.height)
        result.marks.push({ label: line === count ? '·EOF' : '¶', x: end.left * q + metrics.left + 4 * metrics.scale, y: end.top * q });
    }
  }
  return result;
}

export function createVisuals(monaco, editor, model, initial) {
  const host = document.getElementById('editor'), frameHost = document.getElementById('editor-frame'), canvas = document.getElementById('decor');
  const numbersHost = document.getElementById('decor-numbers'), marksHost = document.getElementById('decor-marks');
  const numbers = [], marks = [];
  const context = canvas.getContext('2d');
  let visual = {}, metrics = visualMetrics(), frame = null, disposed = false, applying = false;
  let previousHeight = editor.getLayoutInfo().height;
  const subscriptions = [];
  // The editor and decorations share actual DOM font/line boxes, including the small-zoom transform.
  // Canvas's 'top' baseline is a font metric, not Monaco's line-box top. No guessed y offset is used.
  function drawLabels(parent, pool, items, rightAligned) {
    for (let i = 0; i < items.length; i++) {
      let label = pool[i];
      if (!label) {
        label = document.createElement('span');
        label.style.position = 'absolute';
        label.style.whiteSpace = 'pre';
        if (rightAligned) label.style.transform = 'translateX(-100%)';
        parent.appendChild(label); pool.push(label);
      }
      const item = items[i];
      label.hidden = false;
      if (label.textContent !== item.label) label.textContent = item.label;
      label.style.left = `${item.x / metrics.renderScale}px`;
      label.style.top = `${item.y / metrics.renderScale}px`;
    }
    for (let i = items.length; i < pool.length; i++) pool[i].hidden = true;
  }
  function draw() {
    frame = null;
    if (disposed) return;
    const layout = editor.getLayoutInfo(), width = frameHost.clientWidth + metrics.left + metrics.right, height = layout.height * metrics.renderScale;
    const ratio = window.devicePixelRatio || 1;
    const pixelWidth = Math.ceil(width * ratio), pixelHeight = Math.ceil(height * ratio);
    if (canvas.width !== pixelWidth) canvas.width = pixelWidth;
    if (canvas.height !== pixelHeight) canvas.height = pixelHeight;
    canvas.style.width = `${width}px`; canvas.style.height = `${height}px`;
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.clearRect(0, 0, width, height);
    const items = visibleDecorations(editor, model, visual, metrics);
    context.fillStyle = visual.foreground || '#ffffff';
    context.save(); context.beginPath(); context.rect(0, Math.max(0, metrics.top - editor.getScrollTop() * metrics.renderScale), width - metrics.right, height); context.clip();
    context.globalAlpha = 0.14;
    for (const y of items.guides) context.fillRect(metrics.textLeft, y, Math.max(0, width - metrics.textLeft - metrics.right), 1);
    context.restore();
    const q = metrics.renderScale, clipTop = Math.max(0, metrics.top - editor.getScrollTop() * q);
    numbersHost.style.clipPath = `inset(${clipTop / q}px ${metrics.right / q}px 0 ${metrics.left / q}px)`;
    // Pinned Monaco's contentWidth includes the scrollbar (editorOptions.js, EditorLayoutInfoComputer).
    // Keep end markers out of the gutter and the scrollbar, just like the editor's text viewport.
    const contentRight = metrics.left + (layout.contentLeft + layout.contentWidth - layout.verticalScrollbarWidth) * q;
    marksHost.style.clipPath = `inset(${clipTop / q}px ${Math.max(metrics.right, width - contentRight) / q}px 0 ${metrics.textLeft / q}px)`;
    drawLabels(numbersHost, numbers, items.numbers, true);
    drawLabels(marksHost, marks, items.marks, false);
  }
  function schedule() {
    if (!disposed && frame === null && document.visibilityState !== 'hidden') frame = requestAnimationFrame(draw);
  }
  function keepCaretVisible() {
    if (!editor.hasTextFocus()) return;
    const caret = editor.getScrolledVisiblePosition(editor.getPosition());
    if (!caret || caret.top < 0) return;
    const bottom = Math.max(metrics.editorLineHeight, editor.getLayoutInfo().height - metrics.bottom / metrics.renderScale);
    if (caret.top + metrics.editorLineHeight > bottom)
      editor.setScrollTop(editor.getScrollTop() + caret.top + metrics.editorLineHeight - bottom);
  }
  function layoutChanged() {
    const caret = editor.hasTextFocus() && editor.getScrolledVisiblePosition(editor.getPosition());
    if (!applying && caret && caret.top >= 0 && caret.top + metrics.editorLineHeight <= previousHeight) keepCaretVisible();
    previousHeight = editor.getLayoutInfo().height;
    schedule();
  }
  function apply(value) {
    applying = true;
    const anchor = editor.getVisibleRanges()[0];
    const position = anchor && { lineNumber: anchor.startLineNumber, column: anchor.startColumn };
    const before = position && editor.getScrolledVisiblePosition(position);
    const oldCaret = editor.hasTextFocus() && editor.getScrolledVisiblePosition(editor.getPosition());
    const caretWasVisible = oldCaret && oldCaret.top >= 0 && oldCaret.top + metrics.editorLineHeight <= editor.getLayoutInfo().height;
    const previousMetrics = metrics;
    visual = { ...visual, ...value }; metrics = visualMetrics(visual);
    frameHost.style.left = `${metrics.left}px`; frameHost.style.right = `${metrics.right}px`;
    host.style.width = `${100 / metrics.renderScale}%`; host.style.height = `${100 / metrics.renderScale}%`;
    host.style.transform = `scale(${metrics.renderScale})`;
    const background = visual.background || '#00000000', foreground = visual.foreground || '#ffffff';
    document.documentElement.style.background = background;
    document.body.style.background = background;
    monaco.editor.defineTheme('kotu', {
      base: visual.highContrast ? (visual.light ? 'hc-light' : 'hc-black') : (visual.light ? 'vs' : 'vs-dark'),
      inherit: true, rules: [], colors: {
        'editor.background': background, 'editorGutter.background': background,
        'editor.foreground': foreground, 'editorCursor.foreground': foreground,
        'editor.selectionBackground': visual.selectionBackground || '#0078d4',
        'editor.inactiveSelectionBackground': visual.selectionBackground || '#0078d4',
        'editor.selectionForeground': visual.selectionForeground || '#ffffff',
        'editor.selectionHighlightBackground': '#00000000'
      }
    });
    editor.updateOptions({ fontSize: metrics.fontSize, lineHeight: metrics.editorLineHeight,
      lineDecorationsWidth: metrics.gutter / metrics.renderScale,
      scrollbar: { verticalScrollbarSize: 14 / metrics.renderScale, horizontalScrollbarSize: 14 / metrics.renderScale },
      padding: { top: metrics.top / metrics.renderScale, bottom: metrics.bottom / metrics.renderScale }, theme: 'kotu', autoDetectHighContrast: false });
    editor.layout();
    for (const layer of [numbersHost, marksHost]) {
      editor.applyFontInfo(layer);
      layer.style.color = foreground;
      layer.style.width = `${100 / metrics.renderScale}%`; layer.style.height = `${100 / metrics.renderScale}%`;
      layer.style.transform = `scale(${metrics.renderScale})`;
    }
    const geometryChanged = previousMetrics.scale !== metrics.scale || previousMetrics.gutter !== metrics.gutter;
    if (geometryChanged && position && before) editor.setScrollTop(editor.getTopForPosition(position.lineNumber, position.column) - before.top * previousMetrics.renderScale / metrics.renderScale);
    if (geometryChanged && caretWasVisible) keepCaretVisible();
    applying = false;
    previousHeight = editor.getLayoutInfo().height;
    schedule();
  }
  subscriptions.push(editor.onDidScrollChange(schedule), editor.onDidLayoutChange(layoutChanged),
    editor.onDidContentSizeChange(schedule), model.onDidChangeContent(schedule),
    editor.onDidChangeCursorSelection(() => { keepCaretVisible(); schedule(); }));
  document.addEventListener('visibilitychange', schedule);
  apply(initial || {});
  return { apply, viewport() {
    const layout = editor.getLayoutInfo(), q = metrics.renderScale;
    return { top: editor.getScrollTop() * q, left: editor.getScrollLeft() * q,
      width: layout.width * q + metrics.left + metrics.right, height: layout.height * q };
  }, dispose() {
    disposed = true; if (frame !== null) cancelAnimationFrame(frame);
    for (const subscription of subscriptions) subscription.dispose();
    document.removeEventListener('visibilitychange', schedule);
    numbersHost.replaceChildren(); marksHost.replaceChildren();
  } };
}
