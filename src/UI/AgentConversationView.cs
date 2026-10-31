using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed class AgentConversationView : ScrollViewer
    {
        readonly StackPanel rows = new StackPanel
        {
            Margin = new Thickness(16, 8, 16, 16), MaxWidth = 960
        };
        readonly Dictionary<string, Expander> tools = new Dictionary<string, Expander>();
        TextBox streaming;
        bool following = true;
        bool empty;
        internal event Action<string> EditRequested;
        internal AgentConversationView()
        {
            Content = rows;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            ScrollChanged += delegate(object sender, ScrollChangedEventArgs args)
            {
                if (args.ExtentHeightChange == 0 && args.VerticalChange != 0) following = ScrollableHeight - VerticalOffset < 36;
                if (args.ExtentHeightChange != 0 && following) ScrollToEnd();
            };
        }
        internal void Render(AgentSession session)
        {
            rows.Children.Clear();
            tools.Clear();
            streaming = null;
            empty = session.Entries.Count == 0;
            if (session.Entries.Count == 0)
            {
                var welcome = new StackPanel
                {
                    Margin = new Thickness(6, 20, 6, 18)
                };
                welcome.Children.Add(Label("从当前文件开始", 21));
                welcome.Children.Add(Label("读取与搜索资料，整理内容，或提出修改。\n修改会先展示差异，由你决定是否写入。", 13));
                welcome.Children.Add(Label("仅本次使用 · 对话不留档，可随时清空。", 12));
                rows.Children.Add(welcome);
                return;
            }
            if (session.Entries.Count > 120) rows.Children.Add(Label("显示最近 120 项记录；完整临时会话保留在内存。", 12));
            foreach (var entry in session.Entries.Skip(Math.Max(0, session.Entries.Count - 120)))
            {
                if (entry.Kind == "tool-start") ToolStarted(entry.CallId, entry.Text);
                else if (entry.Kind == "attempt")
                {
                    rows.Children.Add(Label("未完成的回复", 12));
                    AddMessage("assistant", entry.Text);
                }
                else if (entry.Kind == "run-end") rows.Children.Add(Label(entry.Text, 12));
                else if (entry.Kind == "message" && entry.Message != null)
                {
                    string role = Convert.ToString(entry.Message["role"]);
                    string text = Convert.ToString(entry.Message["content"]);
                    if (role == "tool") ToolFinished(Convert.ToString(entry.Message["tool_call_id"]), text);
                    else if (!String.IsNullOrWhiteSpace(text)) AddMessage(role, text);
                }
            }
        }
        internal static TextBlock Label(string text, double size)
        {
            var label = new TextBlock
            {
                Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 7), LineHeight = size * 1.65
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, size < 14 ? "MutedBrush" : "TextBrush");
            return label;
        }
        internal void AddMessage(string role, string text)
        {
            if (empty)
            {
                rows.Children.Clear();
                empty = false;
            }
            var group = new StackPanel
            {
                Margin = new Thickness(0, 12, 0, 6)
            };
            var roleLabel = Label(role == "user" ? "你" : "轻页 Agent", 12);
            roleLabel.FontWeight = FontWeights.SemiBold;
            group.Children.Add(roleLabel);
            if (role == "user")
            {
                var box = Text(text, 13);
                box.Padding = new Thickness(12, 9, 12, 9);
                box.SetResourceReference(Control.BackgroundProperty, "HoverBrush");
                box.ContextMenu = MessageMenu(role, text);
                group.Children.Add(box);
            }
            else
            {
                var source = NativeMarkdown.Create(NativeMarkdown.Parse(text), null, null);
                var document = source.Document;
                source.Document = null;
                document.PagePadding = new Thickness(0);
                document.FontSize = 14;
                document.LineHeight = 23;
                var reader = new RichTextBox
                {
                    Document = document, IsReadOnly = true, IsDocumentEnabled = true, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                reader.ContextMenu = MessageMenu(role, text);
                group.Children.Add(reader);
            }
            rows.Children.Add(group);
            Follow();
        }
        ContextMenu MessageMenu(string role, string text)
        {
            var menu = new ContextMenu();
            var copy = new MenuItem { Header = "复制整段" };
            copy.Click += delegate {
                try { Clipboard.SetText(text); }
                catch (System.Runtime.InteropServices.ExternalException) { AddNotice("剪贴板暂时忙，请稍后重试。"); }
            };
            menu.Items.Add(copy);
            if (role == "user")
            {
                var edit = new MenuItem { Header = "编辑后重发" };
                edit.Click += delegate { if (EditRequested != null) EditRequested(text); };
                menu.Items.Add(edit);
            }
            return menu;
        }
        internal static TextBox Text(string text, double size)
        {
            var box = new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = size, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent
            };
            box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            return box;
        }
        internal void ToolStarted(string id, string text)
        {
            if (id == null || tools.ContainsKey(id)) return;
            string label = (text ?? "").Replace("read_file", "读取文件").Replace("write_file", "修改文件").Replace("edit_file", "编辑文件").Replace("create_file", "新建文件").Replace("list_files", "浏览文件").Replace("search_files", "搜索内容");
            var expander = new Expander
            {
                Header = "执行中 · " + label, FontSize = 12, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 3, 0, 3)
            };
            expander.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
            expander.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            tools[id] = expander;
            rows.Children.Add(expander);
            Follow();
        }
        internal void ToolFinished(string id, string result)
        {
            Expander expander;
            if (!tools.TryGetValue(id ?? "", out expander))
            {
                ToolStarted(id, "工具结果");
                if (!tools.TryGetValue(id ?? "", out expander)) return;
            }
            string heading = Convert.ToString(expander.Header);
            string state = result.Contains("\"error\"") ? "失败" : result.Contains("rejected") || result.Contains("用户未批准") ? "已拒绝" : result.Contains("interrupted") ? "已中断" : result.Contains("applied") || result.StartsWith("已写入", StringComparison.Ordinal) ? "已写入" : "完成";
            expander.Header = state + " · " + (heading.Contains(" · ") ? heading.Substring(heading.IndexOf(" · ", StringComparison.Ordinal) + 3) : heading);
            string preview = result.Length > 4096 ? result.Substring(0, 4096) + "\n… 结果已限量显示" : result;
            var box = Text(preview, 12);
            box.MaxHeight = 200;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.Margin = new Thickness(0, 8, 0, 0);
            expander.Content = box;
        }
        internal void AppendDelta(string delta)
        {
            if (streaming == null)
            {
                streaming = Text("", 14);
                streaming.Margin = new Thickness(0, 14, 0, 14);
                rows.Children.Add(streaming);
            }
            streaming.AppendText(delta);
            Follow();
        }
        internal void CompleteAssistant(string text)
        {
            if (streaming != null) rows.Children.Remove(streaming);
            streaming = null;
            AddMessage("assistant", text);
        }
        internal void BeginStep()
        {
            streaming = null;
        }
        internal void AddNotice(string text)
        {
            rows.Children.Add(Label(text, 12));
            Follow();
        }
        internal void AddApproval(FrameworkElement element)
        {
            rows.Children.Add(element);
            Follow();
        }
        internal void JumpToLatest()
        {
            following = true;
            ScrollToEnd();
        }
        void Follow()
        {
            while (rows.Children.Count > 120)
            {
                var oldest = rows.Children[0];
                if (oldest == streaming) streaming = null;
                foreach (string id in tools.Where(item => item.Value == oldest).Select(item => item.Key).ToArray()) tools.Remove(id);
                rows.Children.RemoveAt(0);
            }
            if (following) Dispatcher.BeginInvoke(new Action(ScrollToEnd));
        }
    }
}
