using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Qingye.UI
{
    // The embedded Harness page is a full IDE shell. Qingye shows it as a plain
    // side panel, so this skin removes the shell chrome (rails, session header,
    // hero brand, workspace/model pickers, settings) and maps the native
    // Appearance palette onto the page's design tokens.
    //
    // Only local CSS/JS is injected: no network requests, no resource-fence or
    // navigation changes. The script installs once at document start and a
    // MutationObserver re-applies it after React re-renders.
    internal static class AgentSkin
    {
        internal const string StyleId = "qingye-agent-skin";
        const string InitialThemeToken = "__QINGYE_INITIAL_THEME__";

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        internal static string DocumentStyleId { get { return StyleId; } }

        static string Hex(string key, Window owner, string fallback)
        {
            var brush = owner == null ? null : owner.TryFindResource(key) as SolidColorBrush;
            if (brush == null) return fallback;
            System.Windows.Media.Color color = brush.Color;
            return "#" + color.R.ToString("X2", CultureInfo.InvariantCulture)
                + color.G.ToString("X2", CultureInfo.InvariantCulture)
                + color.B.ToString("X2", CultureInfo.InvariantCulture);
        }

        static string Shift(string hex, double factor)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length != 7) return hex;
            int value;
            if (!Int32.TryParse(hex.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return hex;
            int red = Math.Min(255, (int)Math.Round(((value >> 16) & 0xFF) * factor));
            int green = Math.Min(255, (int)Math.Round(((value >> 8) & 0xFF) * factor));
            int blue = Math.Min(255, (int)Math.Round((value & 0xFF) * factor));
            return "#" + red.ToString("X2", CultureInfo.InvariantCulture)
                + green.ToString("X2", CultureInfo.InvariantCulture)
                + blue.ToString("X2", CultureInfo.InvariantCulture);
        }

        internal static Dictionary<string, object> Theme(Window owner)
        {
            string surface = Hex("SurfaceBrush", owner, "#FFFFFF");
            string panel = Hex("PanelBrush", owner, "#F1F6F3");
            string hover = Hex("HoverBrush", owner, "#E3ECE6");
            string text = Hex("TextBrush", owner, "#243830");
            string line = Hex("LineBrush", owner, "#DFE7E1");
            string muted = Hex("MutedBrush", owner, "#5F7066");
            string accent = Hex("AccentBrush", owner, "#176E57");
            bool dark = String.Equals(surface, "#252D29", StringComparison.OrdinalIgnoreCase)
                || String.Equals(panel, "#1D2421", StringComparison.OrdinalIgnoreCase);
            var theme = new Dictionary<string, object> {
                { "surface", surface }, { "panel", panel }, { "hover", hover },
                { "text", text }, { "line", line }, { "muted", muted },
                { "accent", accent }, { "accentHover", Shift(accent, dark ? 1.16 : 0.86) },
                { "bubble", panel }, { "dark", dark },
            };
            return theme;
        }

        internal static System.Drawing.Color Background(Window owner)
        {
            var brush = owner == null ? null : owner.TryFindResource("SurfaceBrush") as SolidColorBrush;
            System.Windows.Media.Color color = brush == null ? System.Windows.Media.Color.FromRgb(255, 255, 255) : brush.Color;
            return System.Drawing.Color.FromArgb(255, color.R, color.G, color.B);
        }

        // Script registered with AddScriptToExecuteOnDocumentCreatedAsync: runs in
        // every document before page scripts, installs the skin, and keeps it
        // installed across React re-renders. The initial palette is baked in so
        // the panel never flashes the stock Harness chrome.
        internal static string DocumentScript(Window owner)
        {
            return Script.Replace(InitialThemeToken, Json.Serialize(Theme(owner)));
        }

        // Applied again after navigation and whenever the native theme changes.
        internal static string ApplyScript(Window owner)
        {
            return "window.__qingyeAgentSkin && window.__qingyeAgentSkin.apply(" + Json.Serialize(Theme(owner)) + ");";
        }

        const string Script = @"(() => {
  if (window.__qingyeAgentSkin) return;
  const STYLE_ID = 'qingye-agent-skin';
  const HIDDEN = 'data-qingye-hidden';
  const INITIAL = __QINGYE_INITIAL_THEME__;
  let theme = INITIAL || null;
  let scheduled = false;

  const CSS = `
/* One column: no left rail, no right drawer, no shell chrome. The sidebar
   column must keep its grid cell (zero-width) so the conversation column does
   not slide into it; the right drawer is removed outright. */
[data-sidebar-collapsed] { grid-template-columns: 0 minmax(0, 1fr) 0 !important; }
[data-sidebar-collapsed] > :first-child { width: 0 !important; min-width: 0 !important; overflow: hidden !important; visibility: hidden !important; }
[data-sidebar-collapsed] > :nth-child(2) { grid-column: 2 !important; }
[data-rightbar-col], [data-sidebar-right-expand], [data-sidebar-right-toggle],
[data-sidebar-opener], [data-shell-bottom] { display: none !important; }
header[data-window-drag] { display: none !important; }

/* Hero decoration: brand mark, headline, preview badge and pickers. */
.pXSMma_root { display: none !important; }
span:has(> [data-slot='conversation.hero.brand.mark']),
span:has(> [data-slot='conversation.hero.brand.mark']) + span { display: none !important; }
div:has(> button[aria-label='选择工作区']) { display: none !important; }
[data-slot='conversation.hero.agentPreset'] { display: none !important; }
[data-slot='conversation.hero.options']:empty { display: none !important; }
div:has(> button[aria-label^='选择模型']) { display: none !important; }

/* Empty state keeps only the composer, anchored to the panel bottom like a
   chat side bar instead of floating in a centered hero. */
[data-phase='hero'] [data-conversation-scroll] { justify-content: flex-end !important; }

/* Token meter / turn statistics under the composer are DSH dashboard chrome. */
[data-composer-dock] { display: none !important; }

/* Empty state hint above the composer (no hero logo or preview badge). */
[data-phase='hero'] [data-conversation-scroll]::before {
  content: '轻页 Agent · 在当前文件或工作区中读取与修改文本';
  display: block;
  box-sizing: border-box;
  width: 100%;
  padding: 0 20px 10px;
  color: var(--dsw-alias-label-tertiary);
  font-size: 12px;
  line-height: 18px;
  text-align: left;
}

/* Approval cards carry a multi-line reason/diff: keep its line structure. */
.mna1RW_headline, .mna1RW_command { white-space: pre-wrap !important; }
.mna1RW_headline { font-family: var(--ds-font-family-code) !important; font-size: 12px !important; line-height: 18px !important; }

/* Fallback hides applied by the observer. */
[data-qingye-hidden] { display: none !important; }

/* Panel geometry: fill the native side panel, never overflow 300-400 DIP. */
html, body { background-color: transparent !important; }
body { font-family: 'Segoe UI', 'Microsoft YaHei UI', sans-serif !important; }
[data-slot='root'], [data-slot='main'] { max-width: 100% !important; }
.wSkVaW_scrollBody { margin-right: 0 !important; scrollbar-gutter: auto !important; }
.wSkVaW_composerSeat, .wSkVaW_scrollBody > * { max-width: 100% !important; }
.uV2eYG_root { padding-left: 10px !important; padding-right: 10px !important; }
`;

  const tokens = {
    '--dsw-alias-bg-base': t => t.surface,
    '--dsw-alias-bg-document-preview': t => t.panel,
    '--dsw-alias-bg-layer-1': t => t.surface,
    '--dsw-alias-bg-layer-2': t => t.panel,
    '--dsw-alias-bg-layer-3': t => t.panel,
    '--dsw-alias-bg-module-platform': t => t.panel,
    '--dsw-alias-bg-multi-select': t => t.panel,
    '--dsw-alias-bg-overlay': t => t.panel,
    '--dsw-alias-label-primary': t => t.text,
    '--dsw-alias-label-secondary': t => t.text,
    '--dsw-alias-label-tertiary': t => t.muted,
    '--dsw-alias-label-caption': t => t.muted,
    '--dsw-alias-label-deep-diving': t => t.muted,
    '--dsw-alias-label-deep-diving-shimmer': t => t.line,
    '--dsw-alias-label-primary-inverted': t => t.surface,
    '--dsw-alias-label-primary-foreground': t => t.surface,
    '--dsw-alias-label-dimmed': t => t.line,
    '--dsw-alias-border-l1': t => t.line,
    '--dsw-alias-border-l2': t => t.line,
    '--dsw-alias-border-l3': t => t.line,
    '--dsw-alias-border-l4': t => t.line,
    '--dsw-alias-interactive-bg-hover': t => t.hover,
    '--dsw-alias-interactive-bg-hover-solid': t => t.hover,
    '--dsw-alias-interactive-bg-active': t => t.hover,
    '--dsw-alias-state-business-primary': t => t.accent,
    '--dsw-alias-brand-primary': t => t.accent,
    '--dsw-alias-brand-text': t => t.text,
    '--dsw-alias-link': t => t.accent,
    '--dsw-alias-button-primary-fill': t => t.accent,
    '--dsw-alias-button-primary-hover': t => t.accentHover,
    '--dsw-alias-button-info-fill': t => t.accent,
    '--dsw-alias-button-info-hover': t => t.accentHover,
    '--dsw-alias-button-contrast-fill': t => t.text,
    '--dsw-alias-button-ghost-active-fill': t => t.hover,
    '--dsw-alias-button-ghost-active-hover': t => t.hover,
    '--dsw-alias-markdown-code-block': t => t.panel,
    '--dsw-alias-markdown-code-block-banner': t => t.panel,
    '--dsw-alias-markdown-inline-code': t => t.hover,
    '--dsw-alias-markdown-placeholder': t => t.panel,
    '--dsw-alias-markdown-tag': t => t.panel,
    '--dsw-alias-markdown-citation': t => t.panel,
    '--dsw-alias-specific-image-hover': t => t.hover,
    '--dsw-specific-bubble': t => t.bubble,
    '--dsw-specific-bubble-highlight': t => t.hover,
    '--dsw-specific-input-major': t => t.surface,
    '--dsw-specific-selector': t => t.panel,
    '--dsw-specific-tip': t => t.panel,
    '--dsw-specific-sidebar-fill': t => t.panel,
    '--dsw-specific-sidebar-nav-item-hover': t => t.hover,
    '--dsw-specific-sidebar-nav-item-active': t => t.hover,
    '--dsw-specific-menu': t => t.surface,
    '--dsw-menu-surface-fill': t => t.surface,
    '--dsw-alias-menu-group-header-fill': t => t.surface,
    '--dsw-alias-toast-bg': t => t.text,
    '--dsw-alias-toast-label': t => t.surface,
    '--dsw-alias-tooltip-bg': t => t.text,
    '--dsw-alias-tooltip-label': t => t.surface,
    '--dsw-alias-turn-trigger-bg': t => t.panel,
    '--dsw-alias-turn-trigger-bg-hover': t => t.hover,
    '--dsw-alias-scrollbar-bg-l1': t => t.line,
    '--dsw-alias-scrollbar-bg-l2': t => t.line,
    '--dsw-alias-scrollbar-hover-l1': t => t.muted,
    '--dsw-alias-scrollbar-hover-l2': t => t.muted
  };

  const hide = element => { if (element && !element.hasAttribute(HIDDEN)) element.setAttribute(HIDDEN, ''); };

  const ensureStyle = () => {
    const host = document.head || document.documentElement;
    if (!host) return;
    let style = document.getElementById(STYLE_ID);
    if (!style) {
      style = document.createElement('style');
      style.id = STYLE_ID;
      style.textContent = CSS;
      host.appendChild(style);
      return;
    }
    if (style.textContent !== CSS) style.textContent = CSS;
  };

  const apply = next => {
    if (!next) return;
    theme = next;
    const body = document.body;
    if (!body) return;
    for (const name in tokens) body.style.setProperty(name, tokens[name](theme));
    body.style.setProperty('background-color', theme.surface);
    body.style.setProperty('color', theme.text);
    body.toggleAttribute('data-ds-dark-theme', !!theme.dark);
    const root = document.documentElement;
    root.style.setProperty('background-color', theme.surface);
    root.style.setProperty('color-scheme', theme.dark ? 'dark' : 'light');
    root.setAttribute('data-qingye-agent', theme.dark ? 'dark' : 'light');
  };

  // The hero brand slot and the workspace chip are owned by plugins that must
  // stay mounted (their dependants inject them), so the observer folds their
  // rendered ancestors away instead.
  const hideComposerDecoration = ownerSelector => {
    const node = document.querySelector(ownerSelector);
    if (!node) return;
    let element = node;
    while (element && element.parentElement) {
      const parentClass = element.parentElement.getAttribute('class') || '';
      if (/(^|\s)\S*_composerStack(\s|$)/.test(parentClass)) { hide(element); return; }
      element = element.parentElement;
    }
    hide(node.parentElement);
  };

  const sweep = () => {
    scheduled = false;
    ensureStyle();
    if (theme && document.body) apply(theme);
    hideComposerDecoration('[data-slot=""conversation.hero.brand.mark""]');
    const workspace = [...document.querySelectorAll('button')].find(b => b.getAttribute('aria-label') === '选择工作区');
    if (workspace) hide(workspace.parentElement);
    const model = [...document.querySelectorAll('button')].find(b => (b.getAttribute('aria-label') || '').indexOf('选择模型') === 0);
    if (model) hide(model.parentElement);
    ['header[data-window-drag]', '[data-sidebar-opener]', '[data-sidebar-right-expand]', '[data-sidebar-right-toggle]', '[data-rightbar-col]', '[data-shell-bottom]'].forEach(selector => {
      document.querySelectorAll(selector).forEach(hide);
    });
    // Approval cards render the proposed diff as one text node; keep its line
    // breaks readable even if the pinned CSS-module class changes.
    const answer = [...document.querySelectorAll('button')].find(b => b.innerText === '允许一次' || b.innerText === '拒绝');
    if (answer && answer.parentElement && answer.parentElement.parentElement) {
      const card = answer.parentElement.parentElement;
      for (const child of card.children) {
        if (child.querySelector('button')) continue;
        if (child.style.whiteSpace !== 'pre-wrap') child.style.whiteSpace = 'pre-wrap';
      }
    }
  };

  const schedule = () => {
    if (scheduled) return;
    scheduled = true;
    (window.requestAnimationFrame || (callback => setTimeout(callback, 16)))(sweep);
  };

  window.__qingyeAgentSkin = { apply: apply, refresh: sweep };
  ensureStyle();
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => { ensureStyle(); apply(theme); schedule(); });
  } else {
    ensureStyle();
    apply(theme);
    schedule();
  }
  const observer = new MutationObserver(schedule);
  const attach = () => {
    if (!document.documentElement) return false;
    observer.observe(document.documentElement, { childList: true, subtree: true });
    return true;
  };
  if (!attach()) {
    const retry = () => { if (attach()) document.removeEventListener('readystatechange', retry); };
    document.addEventListener('readystatechange', retry);
  }
})();";
    }
}
