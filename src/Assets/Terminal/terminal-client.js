(() => {
  'use strict';
  const post = message => window.chrome.webview.postMessage(message);
  const term = new Terminal({
    allowProposedApi: true, cursorBlink: true, cursorStyle: 'bar',
    fontFamily: '"Cascadia Mono", Consolas, "Segoe UI Emoji", "Microsoft YaHei UI", "Segoe UI Symbol", monospace',
    fontSize: 14, lineHeight: 1.15, letterSpacing: 0,
    scrollback: 5000, scrollOnUserInput: true, convertEol: false,
    windowsPty: { backend: 'conpty', buildNumber: __WINDOWS_BUILD__ },
    theme: { background: '#172229', foreground: '#e4edf1', cursor: '#75d6bc', selectionBackground: '#3b5966' },
    rescaleOverlappingGlyphs: true, drawBoldTextInBrightColors: true
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  const unicode = new UnicodeGraphemesAddon.UnicodeGraphemesAddon();
  term.loadAddon(unicode);
  term.unicode.activeVersion = term.unicode.versions[term.unicode.versions.length - 1];
  term.open(document.getElementById('terminal'));
  let webgl, renderer = 'dom';
  try {
    webgl = new WebglAddon.WebglAddon(); term.loadAddon(webgl); renderer = 'webgl';
    webgl.onContextLoss(() => { webgl.dispose(); renderer = 'dom'; term.refresh(0, term.rows - 1); });
  } catch (_) { if (webgl) { try { webgl.dispose(); } catch (_) {} } }
  let lastSize = '', resizeFrame = 0, hidden = false;
  function resize() {
    if (hidden || !document.body.clientWidth || !document.body.clientHeight) return;
    try { fit.fit(); } catch (_) { return; }
    const size = `${term.cols},${term.rows}`;
    if (size !== lastSize) { lastSize = size; post({ type: 'resize', cols: term.cols, rows: term.rows }); }
  }
  new ResizeObserver(() => { cancelAnimationFrame(resizeFrame); resizeFrame = requestAnimationFrame(resize); }).observe(document.body);
  term.onData(data => post({ type: 'input', text: data }));
  term.onBinary(data => post({ type: 'binary', text: btoa(data) }));
  term.attachCustomKeyEventHandler(event => {
    if (event.altKey && event.key === 'F4') { event.preventDefault(); if (event.type === 'keydown') post({ type: 'closeWindow' }); return false; }
    const control = event.ctrlKey || event.metaKey;
    if (!control) return true;
    const key = event.key.toLowerCase();
    const command = event.code === 'Backquote' ? 'toggle' :
      key === 'c' && (event.shiftKey || term.hasSelection()) ? 'copy' :
      key === 'n' && event.shiftKey ? 'newWindow' : key === 'v' ? 'paste' : key === '+' || key === '=' ? 'zoomIn' : key === '-' ? 'zoomOut' : key === '0' ? 'zoomReset' : null;
    if (command) {
      event.preventDefault();
      if (event.type === 'keydown') post({ type: command, text: command === 'copy' ? term.getSelection() : '' });
      return false;
    }
    return true;
  });
  document.addEventListener('contextmenu', event => { event.preventDefault(); post({ type: 'context', text: term.getSelection() }); });
  document.addEventListener('paste', event => { event.preventDefault(); term.paste(event.clipboardData.getData('text')); });
  window.chrome.webview.addEventListener('message', event => {
    const message = event.data;
    switch (message.type) {
      case 'write': term.write(message.text, () => post({ type: 'ack', id: message.id })); break;
      case 'focus': hidden = false; resize(); term.focus(); break;
      case 'hidden': hidden = true; break;
      case 'paste': term.paste(message.text); break;
      case 'theme': term.options.theme = message.theme; document.body.style.background = message.theme.background; document.documentElement.style.setProperty('--terminal-background', message.theme.background); document.documentElement.style.setProperty('--terminal-foreground', message.theme.foreground); break;
      case 'font': term.options.fontSize = message.size; requestAnimationFrame(resize); break;
      case 'reset': term.write('', () => { term.reset(); resize(); post({ type: 'resetReady', id: message.id }); }); break;
      case 'clear': term.clear(); break;
      case 'selection': post({ type: 'copy', text: term.getSelection() }); break;
    }
  });
  // Read-only diagnostics used by the native acceptance harness, never interpreted as commands.
  window.qingyeTerminal = {
    inspect(row, column) { const cell = term.buffer.active.getLine(row)?.getCell(column); return cell ? { chars: cell.getChars(), width: cell.getWidth(), fg: cell.getFgColor(), bg: cell.getBgColor(), bold: !!cell.isBold(), palette: cell.isFgPalette() } : null; },
    state() {
      const buffer = term.buffer.active, lines = [];
      for (let i = Math.max(0, buffer.length - 5000); i < buffer.length; i++) lines.push(buffer.getLine(i).translateToString(true));
      return { text: lines.join('\n'), cols: term.cols, rows: term.rows, cursorX: buffer.cursorX, cursorY: buffer.cursorY,
        viewportWidth: innerWidth, viewportHeight: innerHeight, canvasWidth: document.querySelector('.xterm-screen').getBoundingClientRect().width, viewportWidth: innerWidth, viewportHeight: innerHeight, canvasWidth: document.querySelector('.xterm-screen').getBoundingClientRect().width, alternate: buffer.type === 'alternate', unicode: term.unicode.activeVersion, renderer,
        cell: (buffer.getLine(buffer.cursorY + buffer.baseY)?.getCell(Math.max(0, buffer.cursorX - 1)))?.getWidth() ?? 0 };
    }
  };
  document.fonts.ready.then(() => { resize(); post({ type: 'ready', cols: term.cols, rows: term.rows }); term.focus(); });
  window.addEventListener('error', event => post({ type: 'error', text: String(event.message).substring(0, 300) }));
})();
