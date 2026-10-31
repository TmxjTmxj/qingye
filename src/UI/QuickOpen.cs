using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Markup;
using Qingye.Core;

namespace Qingye.UI
{
    internal static class QuickOpen
    {
        sealed class Entry { internal string Path; internal string Name; internal Document Document; }

        internal static void Show(MainWindow owner)
        {
            var entries = owner.Session.Documents.Select(doc => new Entry { Path = doc.Path, Name = doc.Title, Document = doc }).ToList();
            var seen = new HashSet<string>(entries.Where(entry => entry.Path != null).Select(entry => Path.GetFullPath(entry.Path)), StringComparer.OrdinalIgnoreCase);
            string workspace = owner.WorkspaceRoot;
            var cancellation = new CancellationTokenSource();
            var dialog = new Window
            {
                Owner = owner, Title = "快速打开", Width = Math.Max(240, Math.Min(560, owner.ActualWidth - 24)),
                Height = Math.Max(200, Math.Min(440, owner.ActualHeight - 24)),
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize
            };
            Theme.Install(dialog);
            Theme.Apply(dialog, owner.Appearance);
            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(16) };
            var heading=new TextBlock{Text="快速打开",FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(2,0,0,3)};
            DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
            var hint=new TextBlock{Text="输入文件名或路径，也支持模糊匹配",FontSize=12,Margin=new Thickness(2,0,0,12)};
            hint.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");DockPanel.SetDock(hint,Dock.Top);root.Children.Add(hint);
            var search = new TextBox { FontSize = 15, Padding = new Thickness(10, 8, 10, 8), ToolTip = "输入文件名或路径；↑↓ 选择，Enter 打开，Esc 关闭" };
            DockPanel.SetDock(search, Dock.Top);
            root.Children.Add(search);
            var status = new TextBlock { Margin = new Thickness(8, 8, 8, 0), TextWrapping = TextWrapping.Wrap, FontSize = 11 };
            status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            DockPanel.SetDock(status, Dock.Bottom);
            root.Children.Add(status);
            var list = new ListBox { Margin = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
            ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
            list.ItemContainerStyle=(Style)XamlReader.Parse(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ListBoxItem'><Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='Margin' Value='0,2'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'><Border x:Name='row' Padding='{TemplateBinding Padding}' Background='Transparent' BorderBrush='Transparent' BorderThickness='1' CornerRadius='6'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='row' Property='Background' Value='{DynamicResource HoverBrush}'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter TargetName='row' Property='Background' Value='{DynamicResource HoverBrush}'/><Setter TargetName='row' Property='BorderBrush' Value='{DynamicResource AccentBrush}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
            list.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            root.Children.Add(list);
            dialog.Content = root;
            bool scanning = workspace != null, limited = false;
            Action refresh = delegate
            {
                string query = search.Text.Trim();
                var selected = (list.SelectedItem as ListBoxItem);
                var previous = selected == null ? null : selected.Tag as Entry;
                list.Items.Clear();
                var matches = entries.Where(entry => QuickOpenFiles.Matches(entry.Name, entry.Path, query)).Take(81).ToArray();
                foreach (var entry in matches.Take(80))
                {
                    var panel = new StackPanel();
                    var name = new TextBlock { Text = entry.Name + (entry.Document != null && entry.Document.IsDirty ? " •" : ""), FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                    var directory = new TextBlock { Text = entry.Path == null ? "未保存文档" : Path.GetDirectoryName(entry.Path), FontSize = 11, Margin=new Thickness(0,3,0,0), TextTrimming = TextTrimming.CharacterEllipsis };
                    directory.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                    panel.Children.Add(name); panel.Children.Add(directory);
                    list.Items.Add(new ListBoxItem { Content = panel, Tag = entry, Padding = new Thickness(10, 8, 10, 8), ToolTip=entry.Path??"未保存文档" });
                }
                if (list.Items.Count > 0)
                {
                    list.SelectedIndex = 0;
                    for (int i = 0; i < list.Items.Count; i++) if (Object.ReferenceEquals(((ListBoxItem)list.Items[i]).Tag, previous)) { list.SelectedIndex = i; break; }
                }
                string message = scanning ? "正在查找工作区文件…" : matches.Length == 0 ? "没有匹配的文件" : matches.Length > 80 ? "仅显示前 80 项，请继续输入缩小范围" : "↑↓ 选择   Enter 打开   Esc 关闭";
                if (!scanning && workspace != null) message += limited ? " · 已达到 2000 个文件上限" : " · 工作区最多搜索 6 层目录";
                status.Text = message;
            };
            var filterTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(70)};
            filterTimer.Tick+=delegate{filterTimer.Stop();refresh();};
            search.TextChanged += delegate { filterTimer.Stop();if(entries.Count<100)refresh();else filterTimer.Start(); };
            list.MouseDoubleClick += delegate { Open(owner, dialog, list); };
            search.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Down && list.Items.Count > 0) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; }
                else if (e.Key == Key.Up && list.Items.Count > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; }
                else if (e.Key == Key.Enter) { if(filterTimer.IsEnabled){filterTimer.Stop();refresh();}Open(owner, dialog, list); e.Handled = true; }
                else if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; }
            };
            dialog.PreviewKeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape){dialog.Close();e.Handled=true;}};
            dialog.Closed += delegate { filterTimer.Stop();cancellation.Cancel(); };
            dialog.Loaded += async delegate
            {
                search.Focus(); refresh();
                if (workspace == null) return;
                try
                {
                    var files = await Task.Run(() => QuickOpenFiles.Collect(workspace, 2000, cancellation.Token));
                    if (cancellation.IsCancellationRequested) return;
                    foreach (string path in files) if (seen.Add(path)) entries.Add(new Entry { Path = path, Name = Path.GetFileName(path) });
                    limited = files.Count >= 2000;
                    scanning = false; refresh();
                }
                catch (OperationCanceledException) { }
                catch (Exception) { if (!cancellation.IsCancellationRequested) { scanning = false; status.Text = "无法读取工作区，请检查目录权限"; } }
            };
            dialog.ShowDialog();
        }

        static void Open(MainWindow owner, Window dialog, ListBox list)
        {
            var item = list.SelectedItem as ListBoxItem;
            var entry = item == null ? null : item.Tag as Entry;
            if (entry == null) return;
            dialog.Close();
            if (entry.Document != null) owner.SelectDocument(entry.Document, null);
            else owner.Observe(owner.OpenPathsAsync(new[] { entry.Path }, true));
        }
    }
}
