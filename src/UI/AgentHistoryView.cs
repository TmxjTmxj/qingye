using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed class AgentHistoryView : Border
    {
        readonly TextBox query = new TextBox
        {
            Margin = new Thickness(0, 6, 0, 8), Padding = new Thickness(8, 5, 8, 5), ToolTip = "搜索历史会话标题或文件范围"
        };
        readonly StackPanel results = new StackPanel();
        readonly TextBlock headingText = AgentConversationView.Label("本次会话 · 退出即清空", 12);
        readonly List<Button> openButtons = new List<Button>();
        readonly List<string> visibleIds = new List<string>();
        readonly Button more;
        readonly System.Windows.Threading.DispatcherTimer searchTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        IList<AgentSessionInfo> sessions = new List<AgentSessionInfo>();
        internal event Action<string> OpenRequested;
        internal event Action<string> DeleteRequested;
        internal event Action ClearRequested;
        internal event Action MoreRequested;
        internal event Action<string> SearchRequested;
        internal AgentHistoryView()
        {
            Padding = new Thickness(12, 6, 12, 10);
            SetResourceReference(BackgroundProperty, "PanelBrush");
            var root = new DockPanel();
            var heading = new DockPanel();
            var clear = Theme.Button(null, "清空历史", delegate
            {
                if (ClearRequested != null) ClearRequested();
            });
            clear.FontSize = 12;
            clear.Padding = new Thickness(6, 2, 6, 2);
            DockPanel.SetDock(clear, Dock.Right);
            heading.Children.Add(clear);
            heading.Children.Add(headingText);
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
            DockPanel.SetDock(query, Dock.Top);
            root.Children.Add(query);
            more = Theme.Button(null, "加载更早会话", delegate
            {
                if (MoreRequested != null) MoreRequested();
            });
            more.FontSize = 12;
            DockPanel.SetDock(more, Dock.Bottom);
            root.Children.Add(more);
            root.Children.Add(new ScrollViewer
            {
                Content = results, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
            Child = root;
            query.TextChanged += delegate
            {
                Filter();
                searchTimer.Stop();
                searchTimer.Start();
            };
            searchTimer.Tick += delegate
            {
                searchTimer.Stop();
                if (SearchRequested != null) SearchRequested(query.Text.Trim());
            };
            System.Windows.Automation.AutomationProperties.SetName(query, "搜索历史会话");
            query.KeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs args)
            {
                if (args.Key == System.Windows.Input.Key.Enter && visibleIds.Count > 0)
                {
                    if (OpenRequested != null) OpenRequested(visibleIds[0]);
                    args.Handled = true;
                }
                else if (args.Key == System.Windows.Input.Key.Down && openButtons.Count > 0)
                {
                    openButtons[0].Focus();
                    args.Handled = true;
                }
            };
            Visibility = Visibility.Collapsed;
        }
        // Shortcut entry: Ctrl+Alt+H puts the caret in the history search box.
        internal void FocusSearch()
        {
            query.Focus();
            query.SelectAll();
        }
        internal void SetTemporary(bool temporary)
        {
            headingText.Text = temporary ? "本次会话 · 退出即清空" : "旧版本地存档";
        }
        internal void SetSessions(IList<AgentSessionInfo> values, bool hasMore)
        {
            sessions = values;
            more.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
            Filter();
        }
        void Filter()
        {
            results.Children.Clear();
            openButtons.Clear();
            visibleIds.Clear();
            foreach (var session in sessions.Where(value => ((value.Title ?? "") + " " + value.Scope).IndexOf(query.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
            {
                string id = session.Id;
                var row = new DockPanel
                {
                    Margin = new Thickness(0, 2, 0, 2)
                };
                var delete = Theme.Button(null, "删除", delegate
                {
                    if (DeleteRequested != null) DeleteRequested(id);
                });
                delete.FontSize = 11;
                delete.Padding = new Thickness(6, 2, 6, 2);
                DockPanel.SetDock(delete, Dock.Right);
                row.Children.Add(delete);
                var open = Theme.Button(null, "", delegate
                {
                    if (OpenRequested != null) OpenRequested(id);
                });
                open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                open.Padding = new Thickness(6, 4, 6, 4);
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = session.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis
                });
                content.Children.Add(new TextBlock
                {
                    Text = session.UpdatedUtc.ToLocalTime().ToString("MM-dd HH:mm") + " · " + System.IO.Path.GetFileName(session.Scope), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis
                });
                open.Content = content;
                open.ToolTip = session.Scope;
                open.KeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs args)
                {
                    int index = openButtons.IndexOf((Button)sender);
                    if (index < 0) return;
                    if (args.Key == System.Windows.Input.Key.Down && index + 1 < openButtons.Count)
                    {
                        openButtons[index + 1].Focus();
                        args.Handled = true;
                    }
                    else if (args.Key == System.Windows.Input.Key.Up)
                    {
                        if (index > 0) openButtons[index - 1].Focus();
                        else query.Focus();
                        args.Handled = true;
                    }
                };
                openButtons.Add(open);
                visibleIds.Add(id);
                row.Children.Add(open);
                results.Children.Add(row);
            }
            if (results.Children.Count == 0) results.Children.Add(AgentConversationView.Label("还没有历史会话。发送第一条消息后会自动保存。", 12));
        }
    }
}
