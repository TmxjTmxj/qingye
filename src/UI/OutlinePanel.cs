using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed class OutlinePanel : UserControl
    {
        readonly MainWindow owner;
        internal readonly ListBox list = new ListBox();
        readonly TextBlock empty = new TextBlock();
        internal OutlinePanel(MainWindow window)
        {
            owner = window;
            var grid = new Grid();
            list.BorderThickness = new Thickness(0);
            list.Background = Brushes.Transparent;
            list.Padding = new Thickness(0, 6, 0, 10);
            list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            list.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            list.SelectionChanged += delegate
            {
                var item = list.SelectedItem as ListBoxItem;
                if (item != null && item.Tag is OutlineItem)
                {
                    var target = (OutlineItem)item.Tag;
                    list.SelectedItem = null;
                    Dispatcher.BeginInvoke(new Action(delegate { owner.NavigateToOutline(target); }));
                }
            };
            AutomationProperties.SetName(list, "文档大纲");
            grid.Children.Add(list);
            empty.Text = "此文件没有可用大纲";
            empty.FontSize = 12;
            empty.Margin = new Thickness(14, 14, 10, 0);
            empty.TextWrapping = TextWrapping.Wrap;
            empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            empty.Visibility = Visibility.Collapsed;
            grid.Children.Add(empty);
            Content = grid;
        }
        internal void Refresh(Document doc)
        {
            list.Items.Clear();
            var items = Outline.Build(doc);
            foreach (var item in items)
            {
                var text = new TextBlock
                {
                    Text = item.Title,
                    FontSize = item.Level == 1 ? 13 : 12,
                    FontWeight = item.Level == 1 ? FontWeights.SemiBold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = item.Title
                };
                var row = new ListBoxItem
                {
                    Content = text,
                    Tag = item,
                    Padding = new Thickness(10 + (item.Level - 1) * 12, 5, 8, 5),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                AutomationProperties.SetName(row, item.Title + "，第 " + item.Line + " 行");
                list.Items.Add(row);
            }
            list.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Text = doc == null ? "打开文件后显示大纲" : "此文件没有可用大纲";
        }
        internal int Count { get { return list.Items.Count; } }
    }
}
