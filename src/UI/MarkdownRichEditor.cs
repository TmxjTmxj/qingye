using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Qingye.Core;
using W = System.Windows.Documents;

namespace Qingye.UI
{
    // Typora-style in-place editing for the supported Markdown subset.
    // The rich document is the editing surface; MarkdownWriter normalizes it back
    // to Markdown when the user saves or leaves the mode.
    internal sealed class MarkdownRichEditor : UserControl
    {
        internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables().UseListExtras().UseEmphasisExtras().DisableHtml().Build();
        static readonly double[] HeadingSizes = { 30, 24, 20, 18, 16, 14 };

        readonly MainWindow owner;
        readonly RichTextBox box = new RichTextBox();
        readonly ComboBox headingChoice = new ComboBox();
        readonly WrapPanel tools = new WrapPanel();
        Document bound;
        string directory;
        bool loading, dirty, active, updatingHeading;

        internal event Action Changed;

        internal MarkdownRichEditor(MainWindow window)
        {
            owner = window;
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());

            var bar = new Border { Padding = new Thickness(10, 5, 10, 5), BorderThickness = new Thickness(0, 0, 0, 1) };
            bar.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            bar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            tools.VerticalAlignment = VerticalAlignment.Center;
            bar.Child = tools;
            root.Children.Add(bar);

            AddTool("B", "加粗 Ctrl+B", delegate { EditingCommands.ToggleBold.Execute(null, box); }, button => button.FontWeight = FontWeights.Bold);
            AddTool("I", "斜体 Ctrl+I", delegate { EditingCommands.ToggleItalic.Execute(null, box); }, button => button.FontStyle = FontStyles.Italic);
            AddTool("S", "删除线", ToggleStrike, null);
            AddTool("`", "行内代码", ToggleInlineCode, button => button.FontFamily = new FontFamily("Consolas"));
            headingChoice.ItemsSource = new[] { "正文", "标题 1", "标题 2", "标题 3", "标题 4", "标题 5", "标题 6" };
            headingChoice.SelectedIndex = 0;
            headingChoice.Width = 92;
            headingChoice.Margin = new Thickness(6, 2, 6, 2);
            headingChoice.FontSize = 12;
            headingChoice.SelectionChanged += delegate { if (!updatingHeading && active) ApplyHeading(headingChoice.SelectedIndex); };
            tools.Children.Add(headingChoice);
            AddTool("• 列表", "无序列表", delegate { EditingCommands.ToggleBullets.Execute(null, box); }, null);
            AddTool("1. 列表", "有序列表", delegate { EditingCommands.ToggleNumbering.Execute(null, box); }, null);
            AddTool("引用", "引用块", ToggleQuote, null);
            AddTool("代码块", "代码块", ToggleCodeBlock, button => button.FontFamily = new FontFamily("Consolas"));
            AddTool("表格", "插入 2×2 表格", InsertTable, null);
            AddTool("链接", "插入链接", InsertLink, null);
            AddTool("图片", "插入本地图片", InsertImage, null);
            AddTool("撤销", "撤销", delegate { if (box.CanUndo) box.Undo(); }, null);
            AddTool("重做", "重做", delegate { if (box.CanRedo) box.Redo(); }, null);

            box.BorderThickness = new Thickness(0);
            box.Background = Brushes.Transparent;
            box.Padding = new Thickness(0);
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            box.AcceptsTab = true;
            box.SpellCheck.IsEnabled = false;
            box.IsDocumentEnabled = true;
            box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            box.TextChanged += delegate { if (loading) return; dirty = true; if (Changed != null) Changed(); };
            box.SelectionChanged += delegate { UpdateHeadingFromCaret(); };
            DataObject.AddPastingHandler(box, OnPaste);
            box.PreviewKeyDown += OnPreviewKeyDown;
            Grid.SetRow(box, 1);
            root.Children.Add(box);
            Content = root;
            loading = true;
            try { box.Document = NewDocument(); }
            finally { loading = false; }
        }

        void AddTool(string label, string tip, Action action, Action<Button> style)
        {
            var button = Theme.Button(null, label, action);
            button.MinHeight = 28;
            button.Padding = new Thickness(8, 3, 8, 3);
            button.Margin = new Thickness(2, 2, 2, 2);
            button.FontSize = 12;
            button.ToolTip = tip;
            System.Windows.Automation.AutomationProperties.SetName(button, tip);
            if (style != null) style(button);
            tools.Children.Add(button);
        }

        static FlowDocument NewDocument()
        {
            var flow = new FlowDocument
            {
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
                FontSize = 16,
                LineHeight = 28,
                PagePadding = new Thickness(32, 24, 32, 32),
                ColumnWidth = Double.NaN,
                TextAlignment = TextAlignment.Left,
                Background = Brushes.Transparent
            };
            flow.SetResourceReference(W.TextElement.ForegroundProperty, "TextBrush");
            return flow;
        }

        internal bool Load(Document doc, out string reason)
        {
            reason = null;
            bound = doc;
            directory = doc.Path == null ? null : Path.GetDirectoryName(doc.Path);
            dirty = false;
            active = false;
            string text = doc.ReadText(NativeMarkdown.MaxCharacters + 1);
            if (text.Length > NativeMarkdown.MaxCharacters)
            {
                reason = "文档超过 512 KiB，原位编辑已关闭；请使用源码编辑。";
                return false;
            }
            MarkdownDocument tree;
            try { tree = Markdown.Parse(text, Pipeline); }
            catch (Exception error) { reason = "Markdown 解析失败：" + error.Message; return false; }
            string unsupported = Unsupported(tree);
            if (unsupported != null)
            {
                reason = "包含原位编辑暂不支持的语法：" + unsupported + "。可继续使用源码或阅读视图。";
                return false;
            }
            loading = true;
            try
            {
                var flow = NewDocument();
                Render(tree, flow.Blocks);
                if (flow.Blocks.Count == 0) flow.Blocks.Add(new W.Paragraph());
                box.Document = flow;
                box.CaretPosition = flow.ContentStart;
            }
            finally { loading = false; }
            active = true;
            dirty = false;
            UpdateHeadingFromCaret();
            return true;
        }

        internal void Unload()
        {
            active = false;
            dirty = false;
            bound = null;
            loading = true;
            try { box.Document = NewDocument(); }
            finally { loading = false; }
        }

        internal bool IsDirty { get { return dirty; } }
        internal bool IsActive { get { return active; } }
        internal string Serialize() { return MarkdownWriter.Write(box.Document, bound == null ? "\r\n" : bound.NewLine); }
        internal void MarkCommitted() { dirty = false; }
        internal void FocusEditor() { box.Focus(); }
        internal void AppendPlainParagraph(string text)
        {
            var paragraph = new W.Paragraph(new W.Run(text)) { Margin = new Thickness(0, 0, 0, 16) };
            box.Document.Blocks.Add(paragraph);
            box.CaretPosition = paragraph.ContentStart;
            box.Focus();
        }

        internal void ApplyTheme(Settings settings)
        {
            if (box.Document == null) return;
            box.Document.FontSize = settings.FontSize + 1;
            box.Document.LineHeight = (settings.FontSize + 1) * 1.75;
        }

        internal void ScrollToLine(int line)
        {
            var targets = Paragraphs(box.Document.Blocks).Select(paragraph => new { Paragraph = paragraph, Line = LineTag(paragraph.Tag) })
                .Where(entry => entry.Line > 0).ToArray();
            if (targets.Length == 0) return;
            var target = targets.Where(entry => entry.Line <= line).OrderByDescending(entry => entry.Line).FirstOrDefault() ?? targets[0];
            target.Paragraph.BringIntoView();
            box.CaretPosition = target.Paragraph.ContentStart;
            box.Focus();
        }

        static int LineTag(object tag)
        {
            string value = tag as string;
            if (value == null) return 0;
            int split = value.IndexOf('|');
            int line;
            return split >= 0 && Int32.TryParse(value.Substring(split + 1), out line) ? line : 0;
        }

        static IEnumerable<W.Paragraph> Paragraphs(W.BlockCollection blocks)
        {
            foreach (W.Block block in blocks)
            {
                var paragraph = block as W.Paragraph;
                if (paragraph != null) { yield return paragraph; continue; }
                var section = block as W.Section;
                if (section != null) { foreach (var child in Paragraphs(section.Blocks)) yield return child; continue; }
                var list = block as W.List;
                if (list != null) { foreach (var item in list.ListItems) foreach (var child in Paragraphs(item.Blocks)) yield return child; continue; }
                var table = block as W.Table;
                if (table != null) foreach (var group in table.RowGroups) foreach (var row in group.Rows) foreach (var cell in row.Cells) foreach (var child in Paragraphs(cell.Blocks)) yield return child;
            }
        }

        static string Unsupported(ContainerBlock root)
        {
            foreach (Markdig.Syntax.Block block in AllBlocks(root))
            {
                if (block is HeadingBlock || block is ParagraphBlock || block is ListBlock || block is ListItemBlock
                    || block is QuoteBlock || block is CodeBlock || block is ThematicBreakBlock) continue;
                if (block is Markdig.Extensions.Tables.Table)
                {
                    if (block.GetType() != typeof(Markdig.Extensions.Tables.Table)) return block.GetType().Name;
                    continue;
                }
                if (block is Markdig.Extensions.Tables.TableRow || block is Markdig.Extensions.Tables.TableCell) continue;
                return block.GetType().Name;
            }
            foreach (Markdig.Syntax.Inlines.Inline inline in AllInlines(root))
            {
                if (inline is LinkInline)
                {
                    var link = (LinkInline)inline;
                    if (String.IsNullOrEmpty(link.Url)) return "引用式链接";
                    continue;
                }
                if (inline is LiteralInline || inline is LineBreakInline || inline is EmphasisInline || inline is CodeInline
                    || inline is AutolinkInline || inline is HtmlEntityInline || inline is ContainerInline) continue;
                if (inline.GetType().Name.EndsWith("DelimiterInline", StringComparison.Ordinal)) continue;
                return inline.GetType().Name;
            }
            foreach (var table in AllBlocks(root).OfType<Markdig.Extensions.Tables.Table>())
                foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
                    foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                        foreach (var block in cell)
                            if (!(block is ParagraphBlock)) return "表格内嵌块";
            return null;
        }

        static IEnumerable<Markdig.Syntax.Block> AllBlocks(ContainerBlock source)
        {
            foreach (var block in source)
            {
                yield return block;
                var container = block as ContainerBlock;
                if (container != null) foreach (var child in AllBlocks(container)) yield return child;
            }
        }

        static IEnumerable<Markdig.Syntax.Inlines.Inline> AllInlines(ContainerBlock source)
        {
            foreach (var block in AllBlocks(source))
            {
                var leaf = block as LeafBlock;
                if (leaf != null && leaf.Inline != null) foreach (var inline in AllInlines(leaf.Inline)) yield return inline;
            }
        }

        static IEnumerable<Markdig.Syntax.Inlines.Inline> AllInlines(ContainerInline source)
        {
            for (var inline = source.FirstChild; inline != null; inline = inline.NextSibling)
            {
                yield return inline;
                var container = inline as ContainerInline;
                if (container != null) foreach (var child in AllInlines(container)) yield return child;
            }
        }

        void Render(ContainerBlock source, W.BlockCollection target)
        {
            foreach (var block in source)
            {
                var heading = block as HeadingBlock;
                if (heading != null) { target.Add(RenderHeading(heading)); continue; }
                var paragraph = block as ParagraphBlock;
                if (paragraph != null) { target.Add(RenderParagraph(paragraph)); continue; }
                var list = block as ListBlock;
                if (list != null) { target.Add(RenderList(list)); continue; }
                var quote = block as QuoteBlock;
                if (quote != null) { target.Add(RenderQuote(quote)); continue; }
                var code = block as CodeBlock;
                if (code != null) { target.Add(RenderCode(code)); continue; }
                if (block is ThematicBreakBlock) { target.Add(RenderRule()); continue; }
                var table = block as Markdig.Extensions.Tables.Table;
                if (table != null) { target.Add(RenderTable(table)); continue; }
            }
        }

        W.Block RenderHeading(HeadingBlock source)
        {
            int level = Math.Max(1, Math.Min(6, source.Level));
            var paragraph = new W.Paragraph
            {
                Tag = "h" + level + "|" + (source.Line + 1),
                FontSize = HeadingSizes[level - 1],
                FontWeight = FontWeights.SemiBold,
                LineHeight = HeadingSizes[level - 1] * 1.4,
                Margin = new Thickness(0, level == 1 ? 4 : 24, 0, 14)
            };
            if (source.Inline != null) AddInlines(source.Inline, paragraph.Inlines);
            else paragraph.Inlines.Add(new W.Run(source.Lines.ToString()));
            return paragraph;
        }

        W.Block RenderParagraph(ParagraphBlock source)
        {
            var paragraph = new W.Paragraph { Margin = new Thickness(0, 0, 0, 16) };
            if (source.Inline != null) AddInlines(source.Inline, paragraph.Inlines);
            else paragraph.Inlines.Add(new W.Run(source.Lines.ToString()));
            if (paragraph.Inlines.Count == 0) paragraph.Inlines.Add(new W.Run(""));
            return paragraph;
        }

        W.Block RenderList(ListBlock source)
        {
            var list = new W.List
            {
                MarkerStyle = source.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(0, 8, 0, 14),
                Padding = new Thickness(23, 0, 0, 0)
            };
            int start;
            if (source.IsOrdered && Int32.TryParse(source.OrderedStart, out start)) list.StartIndex = Math.Max(1, start);
            foreach (ListItemBlock child in source)
            {
                var item = new W.ListItem();
                Render(child, item.Blocks);
                if (item.Blocks.Count == 0) item.Blocks.Add(new W.Paragraph());
                list.ListItems.Add(item);
            }
            return list;
        }

        W.Block RenderQuote(QuoteBlock source)
        {
            var section = new W.Section
            {
                Tag = "quote",
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(16, 0, 0, 0),
                Margin = new Thickness(0, 10, 0, 18)
            };
            section.SetResourceReference(W.Block.BorderBrushProperty, "QuoteBorder");
            section.SetResourceReference(W.TextElement.ForegroundProperty, "QuoteText");
            Render(source, section.Blocks);
            if (section.Blocks.Count == 0) section.Blocks.Add(new W.Paragraph());
            return section;
        }

        W.Block RenderCode(CodeBlock source)
        {
            string language = "";
            var fenced = source as FencedCodeBlock;
            if (fenced != null && !String.IsNullOrEmpty(fenced.Info))
                language = fenced.Info.Split(new[] { ' ', '{', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var paragraph = new W.Paragraph
            {
                Tag = "code" + language,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 14,
                LineHeight = 22,
                Padding = new Thickness(16),
                Margin = new Thickness(0, 8, 0, 20)
            };
            paragraph.SetResourceReference(W.TextElement.BackgroundProperty, "CodeBackground");
            paragraph.Inlines.Add(new W.Run(source.Lines.ToString()));
            return paragraph;
        }

        W.Block RenderRule()
        {
            var paragraph = new W.Paragraph { Tag = "hr", BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 18, 0, 18) };
            paragraph.SetResourceReference(W.Block.BorderBrushProperty, "Rule");
            return paragraph;
        }

        W.Block RenderTable(Markdig.Extensions.Tables.Table source)
        {
            var table = new W.Table { CellSpacing = 0, FontSize = 14, Margin = new Thickness(0, 6, 0, 22) };
            var group = new W.TableRowGroup();
            table.RowGroups.Add(group);
            foreach (var row in source.OfType<Markdig.Extensions.Tables.TableRow>())
            {
                var output = new W.TableRow { FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal };
                if (row.IsHeader) output.Tag = "th";
                foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                {
                    var target = new W.TableCell { Padding = new Thickness(9), BorderThickness = new Thickness(0, 0, 0, 1) };
                    target.SetResourceReference(W.TableCell.BorderBrushProperty, "Rule");
                    Render(cell, target.Blocks);
                    if (target.Blocks.Count == 0) target.Blocks.Add(new W.Paragraph());
                    output.Cells.Add(target);
                }
                if (output.Cells.Count > 0) group.Rows.Add(output);
            }
            return table;
        }

        void AddInlines(ContainerInline source, W.InlineCollection target)
        {
            for (var inline = source.FirstChild; inline != null; inline = inline.NextSibling)
            {
                var literal = inline as LiteralInline;
                if (literal != null) { target.Add(new W.Run(literal.Content.ToString())); continue; }
                var entity = inline as HtmlEntityInline;
                if (entity != null) { target.Add(new W.Run(entity.Transcoded.ToString())); continue; }
                var automatic = inline as AutolinkInline;
                if (automatic != null) { target.Add(Link(automatic.Url, automatic.Url)); continue; }
                var code = inline as CodeInline;
                if (code != null) { target.Add(CodeRun(code.Content)); continue; }
                var line = inline as LineBreakInline;
                if (line != null) { if (line.IsHard) target.Add(new W.LineBreak()); else target.Add(new W.Run(" ")); continue; }
                var emphasis = inline as EmphasisInline;
                if (emphasis != null)
                {
                    var span = new W.Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else
                    {
                        span.FontWeight = emphasis.DelimiterCount >= 2 ? FontWeights.Bold : FontWeights.Normal;
                        span.FontStyle = emphasis.DelimiterCount == 1 || emphasis.DelimiterCount == 3 ? FontStyles.Italic : FontStyles.Normal;
                    }
                    AddInlines(emphasis, span.Inlines);
                    target.Add(span);
                    continue;
                }
                var link = inline as LinkInline;
                if (link != null)
                {
                    if (link.IsImage && TryImage(link, target)) continue;
                    var hyperlink = Link(link.Url, null);
                    AddInlines(link, hyperlink.Inlines);
                    if (hyperlink.Inlines.Count == 0) hyperlink.Inlines.Add(new W.Run(link.Url ?? "链接"));
                    target.Add(hyperlink);
                    continue;
                }
                var container = inline as ContainerInline;
                if (container != null)
                {
                    var span = new W.Span();
                    AddInlines(container, span.Inlines);
                    target.Add(span);
                }
            }
        }

        W.Hyperlink Link(string url, string text)
        {
            var hyperlink = new W.Hyperlink { Tag = url ?? "", ToolTip = url == null ? null : url + "（Ctrl+单击打开）" };
            hyperlink.SetResourceReference(W.TextElement.ForegroundProperty, "AccentBrush");
            if (text != null) hyperlink.Inlines.Add(new W.Run(text));
            string target = url;
            hyperlink.Click += delegate(object sender, RoutedEventArgs e)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) OpenLink(target);
            };
            return hyperlink;
        }

        W.Run CodeRun(string text)
        {
            var run = new W.Run(text) { Tag = "code", FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 14 };
            run.SetResourceReference(W.TextElement.BackgroundProperty, "CodeBackground");
            return run;
        }

        bool TryImage(LinkInline link, W.InlineCollection target)
        {
            string url = link.Url ?? "";
            string alt = InlineText(link);
            string path = LocalPath(url);
            if (path != null && File.Exists(path))
            {
                try
                {
                    if (new FileInfo(path).Length <= 16L * 1024 * 1024)
                    {
                        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                            int width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight;
                            if ((long)width * height <= 20000000)
                            {
                                stream.Position = 0;
                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.DecodePixelWidth = Math.Min(width, 900);
                                bitmap.StreamSource = stream;
                                bitmap.EndInit();
                                bitmap.Freeze();
                                var image = new Image { Source = bitmap, MaxWidth = 680, MaxHeight = 520, Stretch = Stretch.Uniform, Margin = new Thickness(0, 10, 0, 10) };
                                var container = new W.InlineUIContainer(image) { Tag = "image\t" + alt + "\t" + url };
                                target.Add(container);
                                return true;
                            }
                        }
                    }
                }
                catch { }
            }
            var fallback = new W.Run("![" + alt + "](" + url + ")") { Tag = "image\t" + alt + "\t" + url };
            fallback.SetResourceReference(W.TextElement.ForegroundProperty, "MutedBrush");
            target.Add(fallback);
            return true;
        }

        string LocalPath(string url)
        {
            if (directory == null || String.IsNullOrEmpty(url) || Uri.IsWellFormedUriString(url, UriKind.Absolute)) return null;
            try
            {
                string path = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(url.Split('#')[0]).Replace('/', Path.DirectorySeparatorChar)));
                return FileService.IsInsideRoot(directory, path) ? path : null;
            }
            catch { return null; }
        }

        static string InlineText(ContainerInline source)
        {
            var builder = new System.Text.StringBuilder();
            for (var inline = source.FirstChild; inline != null; inline = inline.NextSibling)
            {
                var literal = inline as LiteralInline;
                if (literal != null) { builder.Append(literal.Content); continue; }
                var container = inline as ContainerInline;
                if (container != null) builder.Append(InlineText(container));
            }
            return builder.ToString();
        }

        void OpenLink(string url)
        {
            if (String.IsNullOrEmpty(url)) return;
            Uri uri;
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "mailto"))
                { Process.Start(uri.AbsoluteUri); return; }
                string path = LocalPath(url);
                if (path != null && File.Exists(path)) owner.Observe(owner.OpenPathsAsync(new[] { path }, true));
            }
            catch { }
        }

        void ToggleStrike()
        {
            object current = box.Selection.GetPropertyValue(W.Inline.TextDecorationsProperty);
            var decorations = current as TextDecorationCollection;
            bool has = false;
            if (decorations != null) foreach (TextDecoration decoration in decorations) if (decoration.Location == TextDecorationLocation.Strikethrough) has = true;
            box.Selection.ApplyPropertyValue(W.Inline.TextDecorationsProperty, has ? null : TextDecorations.Strikethrough);
        }

        void ToggleInlineCode()
        {
            var start = box.Selection.Start;
            var end = box.Selection.End;
            string text = new TextRange(start, end).Text;
            if (text.Length == 0) return;
            var run = start.Parent as W.Run;
            var endRun = end.Parent as W.Run;
            if (run == null || endRun != run || !(run.Parent is W.Paragraph))
            {
                box.Selection.ApplyPropertyValue(W.TextElement.FontFamilyProperty, new FontFamily("Consolas, Microsoft YaHei UI"));
                return;
            }
            int from = new TextRange(run.ContentStart, start).Text.Length;
            int to = new TextRange(run.ContentStart, end).Text.Length;
            string whole = run.Text ?? "";
            from = Math.Max(0, Math.Min(from, whole.Length));
            to = Math.Max(from, Math.Min(to, whole.Length));
            if (from == to) return;
            bool wasCode = "code".Equals(run.Tag);
            run.Text = whole.Substring(0, from);
            var paragraph = (W.Paragraph)run.Parent;
            W.Inline replacement;
            if (wasCode) replacement = new W.Run(text);
            else
            {
                var code = new W.Run(text) { Tag = "code", FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 14 };
                code.SetResourceReference(W.TextElement.BackgroundProperty, "CodeBackground");
                replacement = code;
            }
            var after = new W.Run(whole.Substring(to));
            paragraph.Inlines.InsertAfter(run, after);
            paragraph.Inlines.InsertAfter(run, replacement);
            box.CaretPosition = replacement.ContentEnd;
            box.Focus();
        }

        void ApplyHeading(int index)
        {
            var paragraph = box.CaretPosition.Paragraph;
            if (paragraph == null) return;
            if (index <= 0)
            {
                paragraph.Tag = null;
                paragraph.FontSize = 16;
                paragraph.FontWeight = FontWeights.Normal;
                paragraph.LineHeight = 28;
                paragraph.Margin = new Thickness(0, 0, 0, 16);
                return;
            }
            paragraph.Tag = "h" + index;
            paragraph.FontSize = HeadingSizes[index - 1];
            paragraph.FontWeight = FontWeights.SemiBold;
            paragraph.LineHeight = HeadingSizes[index - 1] * 1.4;
            paragraph.Margin = new Thickness(0, index == 1 ? 4 : 24, 0, 14);
        }

        void UpdateHeadingFromCaret()
        {
            if (!active || box.CaretPosition == null) return;
            var paragraph = box.CaretPosition.Paragraph;
            string tag = paragraph == null ? "" : paragraph.Tag as string ?? "";
            int level = tag.Length >= 2 && tag[0] == 'h' && tag[1] >= '1' && tag[1] <= '6' ? tag[1] - '0' : 0;
            if (headingChoice.SelectedIndex == level) return;
            updatingHeading = true;
            try { headingChoice.SelectedIndex = level; }
            finally { updatingHeading = false; }
        }

        void ToggleQuote()
        {
            var paragraph = box.CaretPosition.Paragraph;
            if (paragraph == null) return;
            if ("quote".Equals(paragraph.Tag))
            {
                paragraph.Tag = null;
                paragraph.ClearValue(W.Block.BorderBrushProperty);
                paragraph.ClearValue(W.Block.BorderThicknessProperty);
                paragraph.ClearValue(W.TextElement.ForegroundProperty);
                paragraph.Padding = new Thickness(0);
                paragraph.Margin = new Thickness(0, 0, 0, 16);
                return;
            }
            paragraph.Tag = "quote";
            paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
            paragraph.SetResourceReference(W.Block.BorderBrushProperty, "QuoteBorder");
            paragraph.SetResourceReference(W.TextElement.ForegroundProperty, "QuoteText");
            paragraph.Padding = new Thickness(16, 0, 0, 0);
            paragraph.Margin = new Thickness(0, 10, 0, 18);
        }

        void ToggleCodeBlock()
        {
            var paragraph = box.CaretPosition.Paragraph;
            if (paragraph == null) return;
            string tag = paragraph.Tag as string ?? "";
            if (tag.StartsWith("code", StringComparison.Ordinal))
            {
                paragraph.Tag = null;
                paragraph.ClearValue(W.TextElement.FontFamilyProperty);
                paragraph.ClearValue(W.TextElement.FontSizeProperty);
                paragraph.ClearValue(W.TextElement.BackgroundProperty);
                paragraph.FontSize = 16;
                paragraph.Padding = new Thickness(0);
                paragraph.Margin = new Thickness(0, 0, 0, 16);
                return;
            }
            paragraph.Tag = "code";
            paragraph.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
            paragraph.FontSize = 14;
            paragraph.LineHeight = 22;
            paragraph.Padding = new Thickness(16);
            paragraph.Margin = new Thickness(0, 8, 0, 20);
            paragraph.SetResourceReference(W.TextElement.BackgroundProperty, "CodeBackground");
        }

        void InsertTable()
        {
            var table = new W.Table { CellSpacing = 0, FontSize = 14, Margin = new Thickness(0, 6, 0, 22) };
            var group = new W.TableRowGroup();
            table.RowGroups.Add(group);
            for (int row = 0; row < 2; row++)
            {
                var output = new W.TableRow();
                if (row == 0) output.Tag = "th";
                for (int column = 0; column < 2; column++)
                {
                    var cell = new W.TableCell(new W.Paragraph(new W.Run(""))) { Padding = new Thickness(9), BorderThickness = new Thickness(0, 0, 0, 1) };
                    cell.SetResourceReference(W.TableCell.BorderBrushProperty, "Rule");
                    output.Cells.Add(cell);
                }
                group.Rows.Add(output);
            }
            var current = box.CaretPosition.Paragraph;
            if (current != null && box.Document.Blocks.Contains(current)) box.Document.Blocks.InsertAfter(current, table);
            else box.Document.Blocks.Add(table);
            var first = (W.Paragraph)table.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock;
            box.CaretPosition = first.ContentStart;
            box.Focus();
        }

        void InsertLink()
        {
            string text = new TextRange(box.Selection.Start, box.Selection.End).Text;
            if (text.Length == 0)
            {
                text = PromptDialog.Ask(owner, "插入链接", "链接文字", "链接");
                if (String.IsNullOrEmpty(text)) return;
            }
            string url = PromptDialog.Ask(owner, "插入链接", "链接地址", "https://");
            if (String.IsNullOrEmpty(url)) return;
            var start = box.Selection.Start;
            var end = box.Selection.End;
            var run = start.Parent as W.Run;
            var endRun = end.Parent as W.Run;
            if (run == null || endRun != run || !(run.Parent is W.Paragraph))
            {
                new TextRange(start, end).Text = "[" + text + "](" + url + ")";
                return;
            }
            int from = new TextRange(run.ContentStart, start).Text.Length;
            int to = new TextRange(run.ContentStart, end).Text.Length;
            string whole = run.Text ?? "";
            from = Math.Max(0, Math.Min(from, whole.Length));
            to = Math.Max(from, Math.Min(to, whole.Length));
            run.Text = whole.Substring(0, from);
            var paragraph = (W.Paragraph)run.Parent;
            var link = Link(url, whole.Substring(from, to - from));
            var after = new W.Run(whole.Substring(to));
            paragraph.Inlines.InsertAfter(run, after);
            paragraph.Inlines.InsertAfter(run, link);
            box.CaretPosition = link.ContentEnd;
        }

        void InsertImage()
        {
            string path = PromptDialog.Ask(owner, "插入图片", "相对路径或绝对路径", "");
            if (String.IsNullOrEmpty(path)) return;
            string alt = Path.GetFileName(path);
            string url = path.Replace('\\', '/');
            var run = new W.Run("![" + alt + "](" + url + ")") { Tag = "image\t" + alt + "\t" + url };
            run.SetResourceReference(W.TextElement.ForegroundProperty, "MutedBrush");
            InsertInline(run);
        }

        void OnPaste(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Bitmap)) return;
            e.CancelCommand();
            try
            {
                var bitmap = Clipboard.GetImage();
                if (bitmap == null || bound == null || bound.Path == null) { owner.SetNotice("先保存 Markdown 文件，才能插入剪贴板图片。"); return; }
                string name = PromptDialog.Ask(owner, "插入图片", "文件名（保存到同目录 assets 文件夹）", "image-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                if (String.IsNullOrWhiteSpace(name)) return;
                name = Path.GetFileName(name.Trim());
                if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name += ".png";
                string folder = Path.Combine(Path.GetDirectoryName(bound.Path), "assets");
                Directory.CreateDirectory(folder);
                string full = Path.Combine(folder, name);
                using (var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(stream);
                }
                string url = "assets/" + name.Replace('\\', '/');
                var image = new Image { Source = bitmap, MaxWidth = 680, MaxHeight = 520, Stretch = Stretch.Uniform, Margin = new Thickness(0, 10, 0, 10) };
                var container = new W.InlineUIContainer(image) { Tag = "image\t" + Path.GetFileNameWithoutExtension(name) + "\t" + url };
                InsertInline(container);
                owner.SetNotice("已插入图片 " + name);
            }
            catch (Exception error) { owner.SetNotice("插入图片失败：" + error.Message); }
        }

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Tab) return;
            var paragraph = box.CaretPosition == null ? null : box.CaretPosition.Paragraph;
            var cell = paragraph == null ? null : FindParent<W.TableCell>(paragraph);
            if (cell == null) return;
            var table = FindParent<W.Table>(cell);
            if (table == null) return;
            var cells = table.RowGroups.SelectMany(group => group.Rows).SelectMany(row => row.Cells).ToArray();
            int index = Array.IndexOf(cells, cell);
            if (index < 0) return;
            int next = index + ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
            if (next < 0 || next >= cells.Length) return;
            var target = cells[next].Blocks.OfType<W.Paragraph>().FirstOrDefault();
            if (target == null) { target = new W.Paragraph(new W.Run("")); cells[next].Blocks.Add(target); }
            box.CaretPosition = target.ContentStart;
            e.Handled = true;
        }

        static T FindParent<T>(FrameworkContentElement element) where T : FrameworkContentElement
        {
            var current = element;
            while (current != null)
            {
                var match = current as T;
                if (match != null) return match;
                current = current.Parent as FrameworkContentElement;
            }
            return null;
        }

        void InsertInline(W.Inline inline)
        {
            var pointer = box.CaretPosition;
            var paragraph = pointer == null ? null : pointer.Paragraph;
            if (paragraph == null) return;
            var run = pointer.Parent as W.Run;
            if (run == null) { paragraph.Inlines.Add(inline); return; }
            string whole = run.Text ?? "";
            int offset = Math.Max(0, Math.Min(new TextRange(run.ContentStart, pointer).Text.Length, whole.Length));
            run.Text = whole.Substring(0, offset);
            paragraph.Inlines.InsertAfter(run, new W.Run(whole.Substring(offset)));
            paragraph.Inlines.InsertAfter(run, inline);
            box.CaretPosition = inline.ContentEnd;
            box.Focus();
        }
    }
}
