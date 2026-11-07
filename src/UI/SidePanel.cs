using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Qingye.Core;

namespace Qingye.UI
{
    // Left rail shared by every document type: open files, an optional workspace,
    // and a type-aware outline. Nothing here is written to disk.
    internal sealed class SidePanel : UserControl
    {
        readonly MainWindow owner;
        internal readonly WorkspaceTree Workspace;
        internal readonly OutlinePanel Outline;
        readonly Button filesTab, outlineTab;
        readonly ListBox openFiles = new ListBox();
        readonly TextBlock filesHint = new TextBlock();
        readonly Grid filesPage = new Grid();
        readonly DispatcherTimer outlineTimer = new DispatcherTimer();
        bool outlineMode,filesStale;
        bool? forcedVisible;

        internal SidePanel(MainWindow window, WorkspaceTree workspace)
        {
            owner = window;
            Workspace = workspace;
            Outline = new OutlinePanel(window);
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var root = new DockPanel { LastChildFill = true };
            var border = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = root };
            border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            Content = border;

            var header = new Grid { Margin = new Thickness(8, 8, 8, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tabs = new UniformGrid { Columns = 2 };
            var segment = new Border { Child = tabs, CornerRadius = new CornerRadius(6), Padding = new Thickness(3) };
            segment.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            filesTab = Theme.Button("files", "文件", delegate { SetMode(false); });
            outlineTab = Theme.Button("list-tree", "大纲", delegate { SetMode(true); });
            foreach (var button in new[] { filesTab, outlineTab })
            {
                button.MinHeight = 30;
                button.Padding = new Thickness(8, 4, 8, 4);
                button.FontSize = 12;
                tabs.Children.Add(button);
            }
            Grid.SetColumn(segment, 0);
            header.Children.Add(segment);
            var collapse = Theme.Button("chevron-left", "", delegate { owner.ToggleSidePanel(); });
            collapse.Width = 28;
            collapse.Height = 28;
            collapse.MinHeight = 0;
            collapse.Padding = new Thickness(0);
            collapse.Margin = new Thickness(4, 0, 0, 0);
            collapse.ToolTip = "收起侧栏";
            AutomationProperties.SetName(collapse, "收起侧栏");
            Theme.Tint(collapse, "MutedBrush");
            Grid.SetColumn(collapse, 1);
            header.Children.Add(collapse);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var filesHeader = new TextBlock { Text = "本次文件", FontSize = 11, Margin = new Thickness(12, 2, 8, 2) };
            filesHeader.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            openFiles.BorderThickness = new Thickness(0);
            openFiles.Background = Brushes.Transparent;
            openFiles.Padding = new Thickness(0, 0, 0, 6);
            openFiles.MaxHeight = 190;
            openFiles.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            openFiles.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            AutomationProperties.SetName(openFiles, "本次打开的文件");
            openFiles.SelectionChanged += delegate
            {
                var item = openFiles.SelectedItem as ListBoxItem;
                if (item == null || !(item.Tag is Document)) return;
                var doc = (Document)item.Tag;
                openFiles.SelectedItem = null;
                Dispatcher.BeginInvoke(new Action(delegate { owner.SelectDocument(doc, null); }));
            };

            filesPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            filesPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            filesPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            filesPage.RowDefinitions.Add(new RowDefinition());
            filesPage.Children.Add(filesHeader);
            Grid.SetRow(openFiles, 1);
            filesPage.Children.Add(openFiles);
            filesHint.Text = "导入工作区 Ctrl+K 后可在此浏览目录";
            filesHint.FontSize = 12;
            filesHint.TextWrapping = TextWrapping.Wrap;
            filesHint.Margin = new Thickness(12, 4, 10, 10);
            filesHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetRow(filesHint, 2);
            filesPage.Children.Add(filesHint);
            Grid.SetRow(Workspace, 3);
            filesPage.Children.Add(Workspace);

            var body = new Grid();
            body.Children.Add(filesPage);
            body.Children.Add(Outline);
            Outline.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(body, Dock.Top);
            root.Children.Add(body);

            outlineTimer.Interval = TimeSpan.FromMilliseconds(400);
            outlineTimer.Tick += delegate { outlineTimer.Stop(); RefreshOutline(owner.Current); };
            IsVisibleChanged+=delegate{if(IsVisible&&filesStale&&!outlineMode)RefreshFiles();};
            SetMode(false);
        }

        internal void SetMode(bool outline)
        {
            outlineMode = outline;
            filesPage.Visibility = outline ? Visibility.Collapsed : Visibility.Visible;
            Outline.Visibility = outline ? Visibility.Visible : Visibility.Collapsed;
            ApplyTab(filesTab, !outline);
            ApplyTab(outlineTab, outline);
            if (outline) RefreshOutline(owner.Current);
            else RefreshFiles();
        }

        static void ApplyTab(Button button, bool selected)
        {
            button.SetResourceReference(Control.BackgroundProperty, selected ? "SurfaceBrush" : "HoverBrush");
            button.SetResourceReference(Control.ForegroundProperty, selected ? "TextBrush" : "MutedBrush");
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            Theme.Tint(button, selected ? "AccentBrush" : "MutedBrush");
            AutomationProperties.SetHelpText(button, selected ? "当前面板" : "切换面板");
        }

        internal void Reveal() { forcedVisible = true; }
        internal bool ResolveVisible(bool workspace, double width) { return forcedVisible ?? (workspace && width >= 760); }
        internal void Toggle()
        {
            forcedVisible = !ResolveVisible(owner.WorkspaceRoot != null, owner.ActualWidth);
            owner.UpdateToolbar();
        }
        internal void Navigate(OutlineItem item)
        {
            if (item == null || owner.Current == null) return;
            if (owner.richEdit.Active) { owner.richEdit.Editor.ScrollToLine(item.Line); return; }
            if ((owner.Appearance.Layout == "reader" || owner.Appearance.Layout == "preview") && owner.preview.ScrollToSourceLine(item.Line)) return;
            if (!owner.active.IsVisible) owner.SetViewMode("single");
            var editor = owner.active.Editor;
            if (editor.Document == null) return;
            int line = Math.Max(1, Math.Min(item.Line, editor.Document.LineCount));
            editor.ScrollToLine(line);
            editor.TextArea.Caret.Line = line;
            editor.Select(Math.Max(0, Math.Min(item.Offset, editor.Document.TextLength)), 0);
            editor.Focus();
            owner.UpdateStatus();
        }
        internal void SetVisible(bool visible)
        {
            bool wasVisible = Visibility == Visibility.Visible;
            Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible || wasVisible) return;
            if (outlineMode) RefreshOutline(owner.Current);
            else RefreshFiles();
        }

        internal void RequestOutlineRefresh()
        {
            if (!IsVisible || !outlineMode) return;
            outlineTimer.Stop();
            outlineTimer.Start();
        }

        internal void RefreshOutline(Document doc)
        {
            Outline.Refresh(doc);
        }

        internal void RefreshFiles()
        {
            if(!IsVisible||outlineMode){filesStale=true;return;}filesStale=false;
            openFiles.Items.Clear();
            foreach (var doc in owner.Session.Documents)
            {
                var text = new TextBlock
                {
                    Text = doc.Title + (doc.IsDirty ? " •" : ""),
                    FontSize = 12,
                    FontWeight = doc == owner.Current ? FontWeights.SemiBold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = doc.Path ?? "未保存文档"
                };
                var row = new ListBoxItem
                {
                    Content = text,
                    Tag = doc,
                    Padding = new Thickness(12, 4, 8, 4),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                AutomationProperties.SetName(row, doc.Title);
                openFiles.Items.Add(row);
            }
            bool hasWorkspace = Workspace.Root != null;
            filesHint.Visibility = hasWorkspace ? Visibility.Collapsed : Visibility.Visible;
            Workspace.Visibility = hasWorkspace ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
