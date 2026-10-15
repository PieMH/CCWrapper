'use strict';
// N xterm.js terminals keyed by tab id; TerminalHost.xaml.cs is the other end of every message.
const host = window.chrome.webview;
const post = m => host.postMessage(JSON.stringify(m));
const $ = id => document.getElementById(id);

const tabs = new Map(); // id -> { id, term, fit, search, el, overlay }
let visible = null;
let webgl = null;

const decorations = {
  matchBackground: '#5c4a1f',
  matchOverviewRuler: '#d7a63e',
  activeMatchBackground: '#b8741a',
  activeMatchColorOverviewRuler: '#ffb000',
};

function create(id) {
  const el = document.createElement('div');
  el.className = 'tab';
  el.hidden = true;
  $('terms').appendChild(el);

  const link = (e, uri) => post({ t: 'link', url: uri });
  const term = new Terminal({
    allowProposedApi: true,
    scrollback: 10000,
    fontFamily: "'Cascadia Mono', Consolas, monospace",
    fontSize: 14,
    theme: { background: '#1e1e1e' },
    linkHandler: { activate: link }, // OSC 8 hyperlinks
  });
  const fit = new FitAddon.FitAddon();
  const search = new SearchAddon.SearchAddon();
  term.loadAddon(fit);
  term.loadAddon(search);
  term.loadAddon(new Unicode11Addon.Unicode11Addon());
  term.unicode.activeVersion = '11';
  term.loadAddon(new WebLinksAddon.WebLinksAddon(link));
  // OSC 52: writes go to the Windows clipboard through the host; reads are refused so output cannot exfiltrate it.
  term.loadAddon(new ClipboardAddon.ClipboardAddon(undefined, {
    readText: async () => '',
    writeText: async (_, text) => post({ t: 'copy', d: text }),
  }));
  term.open(el);

  const tab = { id, term, fit, search, el, overlay: exitOverlay(id, el) };
  term.onData(d => post({ t: 'in', id, d }));
  term.onBinary(d => post({ t: 'bin', id, d }));
  term.onResize(({ cols, rows }) => post({ t: 'resize', id, cols, rows }));
  term.attachCustomKeyEventHandler(e => onKey(tab, e));
  search.onDidChangeResults(r => tab === visible && showCount(r));
  el.addEventListener('contextmenu', e => {
    e.preventDefault();
    if (term.hasSelection()) copy(term); else post({ t: 'paste', id });
  });
  el.addEventListener('drop', e => {
    e.preventDefault();
    if (e.dataTransfer.files.length) host.postMessageWithAdditionalObjects(JSON.stringify({ t: 'drop', id }), e.dataTransfer.files);
  });
  tabs.set(id, tab);
  return tab;
}

function exitOverlay(id, el) {
  const overlay = document.createElement('div');
  overlay.className = 'exit';
  overlay.hidden = true;
  overlay.innerHTML = '<div class="box" role="dialog" aria-label="Session ended"><div class="msg"></div>' +
    '<div class="actions"><button class="primary" data-a="resume">Resume</button><button data-a="close">Close</button></div></div>';
  overlay.addEventListener('click', e => {
    const a = e.target.dataset?.a;
    if (a) post({ t: a, id });
  });
  el.appendChild(overlay);
  return overlay;
}

// Returning false stops xterm for keydown and keypress alike, so an intercepted key is never also typed.
function onKey(tab, e) {
  const key = e.key.toLowerCase();
  const ctrl = e.ctrlKey && !e.altKey && !e.metaKey; // Ctrl+Alt is AltGr on Windows
  let action = null;
  if (e.key === 'Enter' && e.shiftKey && !e.ctrlKey && !e.altKey) action = () => post({ t: 'in', id: tab.id, d: '\x1b\r' });
  else if (ctrl && key === 'v') action = () => post({ t: 'paste', id: tab.id });
  else if (ctrl && key === 'c' && (e.shiftKey || tab.term.hasSelection())) action = () => copy(tab.term);
  else if (ctrl && !e.shiftKey && key === 'f') action = openFind;
  if (!action) return true;
  e.preventDefault();
  if (e.type === 'keydown') action();
  return false;
}

function copy(term) {
  if (!term.hasSelection()) return;
  post({ t: 'copy', d: term.getSelection() });
  term.clearSelection();
}

function show(id) {
  const tab = tabs.get(id);
  if (!tab) return;
  closeFind();
  if (visible && visible !== tab) visible.el.hidden = true;
  webgl?.dispose(); // WebGL only on the visible terminal: contexts are scarce
  webgl = null;
  visible = tab;
  tab.el.hidden = false;
  try {
    webgl = new WebglAddon.WebglAddon();
    webgl.onContextLoss(() => { webgl?.dispose(); webgl = null; });
    tab.term.loadAddon(webgl);
  } catch {
    webgl = null; // DOM renderer fallback
  }
  fitVisible();
  tab.term.focus();
}

// Hidden terminals are never fitted: they would measure 0x0.
function fitVisible() {
  if (visible && !visible.el.hidden) visible.fit.fit();
}

let resizeTimer = 0;
new ResizeObserver(() => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(fitVisible, 80);
}).observe($('terms'));

// Find bar
function openFind() {
  if (!visible) return;
  $('find').hidden = false;
  $('find-q').select();
  $('find-q').focus();
  if ($('find-q').value) find(true, true);
}

function closeFind() {
  if ($('find').hidden) return;
  $('find').hidden = true;
  visible?.search.clearDecorations();
  visible?.term.focus();
}

function find(forward, incremental = false) {
  const q = $('find-q').value;
  if (!visible) return;
  if (!q) { visible.search.clearDecorations(); $('find-count').textContent = ''; return; }
  const opts = { decorations, incremental };
  const found = forward ? visible.search.findNext(q, opts) : visible.search.findPrevious(q, opts);
  if (!found) $('find-count').textContent = 'No results';
}

function showCount({ resultIndex, resultCount }) {
  $('find-count').textContent = !resultCount ? 'No results'
    : resultIndex >= 0 ? `${resultIndex + 1}/${resultCount}` : `${resultCount}+`;
}

$('find-q').addEventListener('input', () => find(true, true));
$('find-q').addEventListener('keydown', e => {
  if (e.key === 'Enter') { e.preventDefault(); find(!e.shiftKey); }
  else if (e.key === 'Escape') { e.preventDefault(); closeFind(); }
});
$('find-next').onclick = () => find(true);
$('find-prev').onclick = () => find(false);
$('find-close').onclick = closeFind;
$('find-full').onclick = () => visible && post({ t: 'transcript', id: visible.id, q: $('find-q').value });

// Files dropped anywhere else must not navigate the page away.
document.addEventListener('dragover', e => { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; });
document.addEventListener('drop', e => e.preventDefault());

host.addEventListener('message', e => {
  const m = JSON.parse(e.data);
  const tab = tabs.get(m.id);
  switch (m.t) {
    case 'open': { // new tab, or Resume of an exited one: clean screen, then report the size the PTY should start with
      const t = tab ?? create(m.id);
      t.term.reset();
      t.overlay.hidden = true;
      show(m.id);
      post({ t: 'resize', id: m.id, cols: t.term.cols, rows: t.term.rows });
      break;
    }
    case 'out':
      if (tab) tab.term.write(m.d, () => post({ t: 'ack', id: m.id, n: m.d.length }));
      break;
    case 'show': show(m.id); break;
    case 'paste': tab?.term.paste(m.d); break;
    case 'exit':
      if (!tab) break;
      tab.overlay.querySelector('.msg').textContent = m.code === 0 ? 'Claude Code exited.' : `Session ended (exit code ${m.code}).`;
      tab.overlay.hidden = false;
      if (tab === visible) tab.overlay.querySelector('.primary').focus();
      break;
    case 'close':
      if (!tab) break;
      if (tab === visible) { closeFind(); visible = null; webgl = null; }
      tab.term.dispose();
      tab.el.remove();
      tabs.delete(m.id);
      break;
  }
});

// Read back by the host: TerminalHost.ReadTextAsync (smoke test) and ReadTailAsync (bottom bar, TuiInput).
window.ccw = {
  text(id) {
    const buf = tabs.get(id)?.term.buffer.active;
    if (!buf) return null;
    const lines = [];
    for (let i = 0; i < buf.length; i++) lines.push(buf.getLine(i)?.translateToString(true) ?? '');
    return lines.join('\n');
  },
  // The last n non-blank rows of the screen (the bottom page, wherever the user has scrolled), top to bottom.
  tail(id, n) {
    const term = tabs.get(id)?.term;
    if (!term) return null;
    const buf = term.buffer.active, lines = [];
    for (let i = buf.baseY + term.rows - 1; i >= buf.baseY && lines.length < n; i--) {
      const line = buf.getLine(i)?.translateToString(true) ?? '';
      if (line.trim()) lines.unshift(line);
    }
    return lines.join('\n');
  },
  visibleId: () => visible?.id ?? null,
};

post({ t: 'ready' });
