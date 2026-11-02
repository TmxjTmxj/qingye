using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Qingye.UI
{
    // Internal, typed window contract shared only with the local test executables.
    internal sealed class WindowTestHooks
    {
        readonly MainWindow window;
        internal readonly Toolbar Toolbar;
        internal readonly DocumentBar DocumentBar;
        internal readonly Footer Footer;
        internal readonly TabStrip TabStrip;
        internal readonly AppearancePopup Appearance;
        internal readonly WorkspaceTree Workspace;
        internal readonly SearchBar Search;
        internal readonly SidePanel Side;
        internal readonly TerminalPanel Terminal;
        internal EditorPane Primary { get { return window.primary; } }
        internal EditorPane Secondary { get { return window.secondary; } }
        internal PreviewPane Preview { get { return window.preview; } }
        internal Popup AppearancePopup { get { return Appearance.Popup; } }
        internal WindowTestHooks(MainWindow owner, Toolbar toolbar, DocumentBar documentBar,
            Footer footer, TabStrip tabs, AppearancePopup appearance, WorkspaceTree workspace, SearchBar search, SidePanel side, TerminalPanel terminal)
        {
            window = owner;
            Toolbar = toolbar;
            DocumentBar = documentBar;
            Footer = footer;
            TabStrip = tabs;
            Appearance = appearance;
            Workspace = workspace;
            Search = search;
            Side = side;
            Terminal = terminal;
        }
        internal void Activate(EditorPane pane) { window.ActivatePane(pane); }
        internal void ToggleSearch() { window.ToggleSearch(); }
    }
}
