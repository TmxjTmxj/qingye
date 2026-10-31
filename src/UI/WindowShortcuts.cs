using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Diagnostics;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit;
using Qingye.Core;

namespace Qingye.UI
{
    internal static class WindowShortcuts
    {
        internal static void Handle(MainWindow owner,KeyEventArgs e)
        {
            bool control=(Keyboard.Modifiers&ModifierKeys.Control)!=0,shift=(Keyboard.Modifiers&ModifierKeys.Shift)!=0,alt=(Keyboard.Modifiers&ModifierKeys.Alt)!=0;
            if(control && alt)
            {
                switch(e.Key)
                {
                    case Key.A: owner.ToggleAgent(); e.Handled=true; return;
                    case Key.P: owner.PrintCurrent(); e.Handled=true; return;
                    case Key.F: owner.ToggleAgentFocus(); e.Handled=true; return;
                    case Key.N: owner.NewAgentTask(); e.Handled=true; return;
                    case Key.H: owner.ToggleAgentHistory(); e.Handled=true; return;
                    case Key.M: owner.OpenAgentSettings(); e.Handled=true; return;
                    case Key.S: owner.StopAgent(); e.Handled=true; return;
                }
            }
            if(e.Key==Key.Escape && owner.search.searchStrip.Visibility==Visibility.Visible)
            {
                owner.ToggleSearch();
                e.Handled=true;
                return;
            }
            if(!control)return;
            if(!alt && (e.Key==Key.Tab || e.Key==Key.PageUp || e.Key==Key.PageDown))
            {
                int count=owner.Session.Documents.Count;
                if(count>1)
                {
                    int current=owner.Session.Documents.IndexOf(owner.Current);
                    int direction=e.Key==Key.PageUp || e.Key==Key.Tab && shift ? -1 : 1;
                    owner.SelectDocument(owner.Session.Documents[(current+direction+count)%count],null);
                }
                e.Handled=true;
                return;
            }
            if(e.Key==Key.L)
            {
                owner.ShowAgent();
                e.Handled=true;
                return;
            }
            if(owner.Current!=null && owner.Current.Kind==DocumentKind.Pdf && owner.Appearance.Layout=="reader" && (e.Key==Key.F || e.Key==Key.OemPlus || e.Key==Key.OemMinus || e.Key==Key.Add || e.Key==Key.Subtract))return;
            if(e.Key==Key.O)
            {
                owner.Observe(owner.OpenPathsAsync(Dialogs.PickFiles(owner,shift),shift));
                e.Handled=true;
            }
            else if(e.Key==Key.P && !shift)
            {
                QuickOpen.Show(owner);
                e.Handled=true;
            }
            else if(e.Key==Key.E && shift)
            {
                if(owner.Appearance.Layout=="wysiwyg")owner.SetViewMode("single");
                else owner.SetViewMode("wysiwyg");
                e.Handled=true;
            }
            else if(e.Key==Key.E)
            {
                if(owner.Appearance.Layout=="reader" && owner.documentBar.sourceViewButton.IsEnabled)owner.SetViewMode("single");
                else if(owner.documentBar.readViewButton.IsEnabled)owner.SetViewMode("reader");
                e.Handled=true;
            }
            else if(e.Key==Key.B && shift)
            {
                owner.ToggleSidePanel();
                e.Handled=true;
            }
            else if(e.Key==Key.V && shift)
            {
                if(owner.documentBar.splitViewButton.IsEnabled)owner.SetViewMode("preview");
                e.Handled=true;
            }
            else if(e.Key==Key.N)
            {
                owner.NewDocument(shift);
                e.Handled=true;
            }
            else if(e.Key==Key.S)
            {
                if(owner.Current!=null)owner.SaveDocument(owner.Current,shift);
                e.Handled=true;
            }
            else if(e.Key==Key.W)
            {
                if(owner.Current!=null)owner.CloseTab(owner.Current);
                e.Handled=true;
            }
            else if(e.Key==Key.F || e.Key==Key.H)
            {
                if(owner.search.searchStrip.Visibility!=Visibility.Visible)owner.ToggleSearch();
                else owner.search.query.Focus();
                e.Handled=true;
            }
            else if(e.Key==Key.K)
            {
                var path=Dialogs.PickFolder(owner);
                if(path!=null)owner.ImportWorkspace(path);
                e.Handled=true;
            }
            else if(e.Key==Key.G)
            {
                owner.ShowGoToLine();
                e.Handled=true;
            }
            else if(e.Key==Key.OemPlus || e.Key==Key.Add)
            {
                owner.Appearance.FontSize=Math.Min(40,owner.Appearance.FontSize+1);
                owner.ApplyAppearance();
                e.Handled=true;
            }
            else if(e.Key==Key.OemMinus || e.Key==Key.Subtract)
            {
                owner.Appearance.FontSize=Math.Max(10,owner.Appearance.FontSize-1);
                owner.ApplyAppearance();
                e.Handled=true;
            }
        }
    }
}
