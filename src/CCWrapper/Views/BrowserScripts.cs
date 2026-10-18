using System.Text.Json;

namespace CCWrapper.Views;

/// <summary>The page-side half of the Browser tool: the JavaScript that BrowserPanel runs through Runtime.evaluate, and how a
/// target and its arguments get into it. Values only ever enter the script as JSON literals, never as concatenated text.</summary>
internal static class BrowserScripts
{
    /// <summary>All ASCII digits is a ref number from the latest snapshot; anything else is a CSS selector as given.</summary>
    public static string Selector(string target) =>
        target.Length > 0 && target.All(char.IsAsciiDigit) ? $"[data-ccw-ref=\"{target}\"]" : target;

    /// <summary>A call of the function expression <paramref name="fn"/> with each argument as a JSON string literal.</summary>
    public static string Call(string fn, params string[] args) =>
        $"({fn})({string.Join(",", args.Select(a => JsonSerializer.Serialize(a)))})";

    /// <summary>(selector, target) -> the element's viewport center [x, y] after scrolling it into view; null after
    /// element.click() when it has no box.</summary>
    public const string Click = """
        (s, t) => {
          const e = document.querySelector(s);
          if (!e) throw new Error('No element matches ' + t);
          e.scrollIntoView({ block: 'center', inline: 'center' });
          const r = e.getBoundingClientRect();
          if (!r.width || !r.height) { e.click(); return null; }
          return [r.x + r.width / 2, r.y + r.height / 2];
        }
        """;

    /// <summary>(selector, target) -> focuses the element and selects its whole content, ready for Input.insertText.</summary>
    public const string Focus = """
        (s, t) => {
          const e = document.querySelector(s);
          if (!e) throw new Error('No element matches ' + t);
          e.scrollIntoView({ block: 'center', inline: 'center' });
          e.focus();
          if (e.select) e.select();
          else if (e.isContentEditable) { const r = document.createRange(); r.selectNodeContents(e); const g = getSelection(); g.removeAllRanges(); g.addRange(r); }
          else throw new Error(t + ' is not an input, textarea or editable element');
        }
        """;

    /// <summary>() -> the page outline, about 30k characters at most. Tags the visible clickable and typeable elements with
    /// data-ccw-ref (renumbered from 1). shortcut: iframes and shadow DOM are not entered, upgrade if pages need it.</summary>
    public const string Snapshot = """
        (() => {
          for (const e of document.querySelectorAll('[data-ccw-ref]')) e.removeAttribute('data-ccw-ref');
          const MAX = 30000, out = [];
          let n = 0, size = 0, full = false;
          const q = s => JSON.stringify((s || '').replace(/\s+/g, ' ').trim().slice(0, 200));
          const add = line => {
            if (full) return;
            size += line.length + 1;
            if (size > MAX) { full = true; out.push('[truncated: the page has more]'); } else out.push(line);
          };
          const shown = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(e).visibility !== 'hidden'; };
          const tag = e => { e.setAttribute('data-ccw-ref', ++n); return '[' + n + '] '; };
          const field = e => {
            const t = e.tagName.toLowerCase(), type = (e.type || '').toLowerCase();
            let s = t === 'input' ? 'input ' + type : t;
            if (e.name) s += ' name=' + q(e.name);
            const l = e.labels?.[0]?.innerText || e.getAttribute('aria-label');
            if (l) s += ' label=' + q(l);
            if (e.placeholder) s += ' placeholder=' + q(e.placeholder);
            if (type === 'checkbox' || type === 'radio') s += e.checked ? ' [checked]' : ' [unchecked]';
            else if (t === 'select') s += ' value=' + q(e.selectedOptions[0]?.text) + ' options=' + q([...e.options].slice(0, 20).map(o => o.text).join(' | '));
            else s += ' value=' + (type === 'password' ? (e.value ? '"****"' : '""') : q(e.value));
            return s;
          };
          const walk = (node, plain) => {
            if (full) return;
            if (node.nodeType === 3) {
              const s = node.data.replace(/\s+/g, ' ').trim();
              if (s && !plain) add(s.slice(0, 300));
              return;
            }
            if (node.nodeType !== 1) return;
            const t = node.tagName.toLowerCase();
            if (['script', 'style', 'noscript', 'template', 'svg', 'head'].includes(t) || getComputedStyle(node).display === 'none') return;
            const vis = shown(node), type = (node.type || '').toLowerCase();
            if (t === 'iframe') { add('[iframe ' + q(node.src) + ' not shown]'); return; }
            if (/^h[1-6]$/.test(t)) {
              if (vis) add('#'.repeat(+t[1]) + ' ' + (node.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 200));
              for (const c of node.childNodes) walk(c, true);
              return;
            }
            if (t === 'a' && node.href) {
              if (vis) add(tag(node) + 'link ' + q(node.innerText || node.getAttribute('aria-label') || node.querySelector('img')?.alt) + ' -> ' + node.href);
              return;
            }
            if (t === 'button' || node.getAttribute('role') === 'button' || (t === 'input' && ['button', 'submit', 'reset', 'image'].includes(type))) {
              if (vis) add(tag(node) + 'button ' + q(node.innerText || node.value || node.getAttribute('aria-label') || node.title));
              return;
            }
            if (t === 'input' || t === 'select' || t === 'textarea') {
              if (vis && type !== 'hidden') add(tag(node) + field(node));
              return;
            }
            if (node.isContentEditable && !node.parentElement?.isContentEditable) {
              if (vis) add(tag(node) + 'editable ' + q(node.innerText));
              return;
            }
            for (const c of node.childNodes) walk(c, plain);
          };
          add('Title: ' + document.title);
          add('URL: ' + location.href);
          if (document.body) walk(document.body, false);
          return out.join('\n');
        })()
        """;
}
