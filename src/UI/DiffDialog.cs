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
    internal static class DiffDialog
    {
        internal static bool Show(Window owner, WriteProposal proposal, bool reviewOnly = false)
        {
            if (proposal == null) return false;
            var dialog = new Window
            {
                Owner = owner,
                Title = reviewOnly ? "本次改动" : "Agent 修改确认",
                Width = 780,
                Height = 580,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResize
            };
            Theme.Install(dialog);
            var main = owner as MainWindow;
            Theme.Apply(dialog, main == null ? new Settings() : main.Appearance);
            var root = new DockPanel { LastChildFill = true };
            var header = new StackPanel { Margin = new Thickness(20, 16, 20, 10) };
            header.Children.Add(new TextBlock
            {
                Text = proposal.IsNew ? "Agent 想新建文件" : "Agent 想修改文件",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold
            });
            var path = new TextBlock
            {
                Text = proposal.RelativePath,
                FontSize = 13,
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            path.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            header.Children.Add(path);
            var warning = new TextBlock
            {
                Text = reviewOnly ? "本次会话内的文件改动。关闭此窗口不会改写文件。" : "应用后会直接写入磁盘。请确认差异符合你的要求。",
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0)
            };
            warning.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            header.Children.Add(warning);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 10, 20, 16) };
            DockPanel.SetDock(actions, Dock.Bottom);
            root.Children.Add(actions);
            bool accepted = false;
            actions.Children.Add(Theme.Button(null, reviewOnly ? "关闭" : "拒绝", delegate { dialog.Close(); }));
            if (!reviewOnly)
            {
                var apply = Theme.Button(null, "应用修改", delegate { accepted = true; dialog.Close(); });
                apply.SetResourceReference(Control.BackgroundProperty, "HoverBrush");
                actions.Children.Add(apply);
            }

            var viewer = new RichTextBox
            {
                IsReadOnly = true,
                BorderThickness = new Thickness(1),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 13,
                Padding = new Thickness(12),
                Margin = new Thickness(20, 0, 20, 0)
            };
            viewer.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
            viewer.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            viewer.Document = BuildDocument(proposal);
            root.Children.Add(viewer);
            dialog.Content = root;
            dialog.Loaded += delegate { viewer.Focus(); };
            dialog.ShowDialog();
            return accepted;
        }

        internal static FlowDocument BuildDocument(WriteProposal proposal)
        {
            var flow = new FlowDocument { FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 13, PagePadding = new Thickness(0) };
            string oldText = (proposal.OldText ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            string newText = (proposal.NewText ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (var line in Diff(oldText, newText))
            {
                var paragraph = new Paragraph { Margin = new Thickness(0), Padding = new Thickness(6, 0, 6, 0) };
                var run = new Run(line.Text ?? "");
                string foreground = line.Kind == '-' ? "DiffRemovedText" : line.Kind == '+' ? "DiffAddedText" : "MutedBrush";
                run.SetResourceReference(TextElement.ForegroundProperty, foreground);
                if (line.Kind != ' ') paragraph.SetResourceReference(TextElement.BackgroundProperty, line.Kind == '-' ? "DiffRemovedBackground" : "DiffAddedBackground");
                var marker = new Run(line.Kind + " ");
                marker.SetResourceReference(TextElement.ForegroundProperty, foreground);
                paragraph.Inlines.Add(marker);
                paragraph.Inlines.Add(run);
                flow.Blocks.Add(paragraph);
            }
            if (flow.Blocks.Count == 0) flow.Blocks.Add(new Paragraph(new Run("（空文件）")));
            return flow;
        }

        sealed class DiffLine
        {
            public char Kind;
            public string Text;
        }

        static IEnumerable<DiffLine> Diff(string oldText, string newText)
        {
            string[] oldLines = oldText.Split('\n');
            string[] newLines = newText.Split('\n');
            if (oldLines.Length > 800 || newLines.Length > 800)
            {
                foreach (string line in oldLines) yield return new DiffLine { Kind = '-', Text = line };
                foreach (string line in newLines) yield return new DiffLine { Kind = '+', Text = line };
                yield break;
            }
            int[,] table = new int[oldLines.Length + 1, newLines.Length + 1];
            for (int i = oldLines.Length - 1; i >= 0; i--)
                for (int j = newLines.Length - 1; j >= 0; j--)
                    table[i, j] = oldLines[i] == newLines[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            int a = 0, b = 0;
            while (a < oldLines.Length && b < newLines.Length)
            {
                if (oldLines[a] == newLines[b]) { yield return new DiffLine { Kind = ' ', Text = oldLines[a] }; a++; b++; }
                else if (table[a + 1, b] >= table[a, b + 1]) { yield return new DiffLine { Kind = '-', Text = oldLines[a++] }; }
                else { yield return new DiffLine { Kind = '+', Text = newLines[b++] }; }
            }
            while (a < oldLines.Length) yield return new DiffLine { Kind = '-', Text = oldLines[a++] };
            while (b < newLines.Length) yield return new DiffLine { Kind = '+', Text = newLines[b++] };
        }
    }
}
