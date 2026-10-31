using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using Qingye.Core;
using W = System.Windows.Documents;

namespace Qingye.UI
{
    internal static class EpubRenderer
    {
        public static FlowDocumentScrollViewer Create(EpubBook book, EpubChapter chapter, Action<string> navigate)
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
            var budget = new Budget();
            XElement body = Body(chapter.Xhtml);
            if (body != null) AddBlocks(body, flow.Blocks, book, chapter, navigate, budget);
            else
            {
                string text = System.Net.WebUtility.HtmlDecode(Regex.Replace(chapter.Xhtml ?? "", "<[^>]+>", " "));
                text = Regex.Replace(text, "\\s+", " ").Trim();
                if (text.Length > 0) flow.Blocks.Add(new W.Paragraph(new W.Run(text)));
            }
            if (flow.Blocks.Count == 0) flow.Blocks.Add(new W.Paragraph(new W.Run("这一章没有可显示的正文。")));
            if (budget.Limited) flow.Blocks.Add(new W.Paragraph(new W.Run("本章内容较多，已限量显示。")) { FontSize = 12 });
            var viewer = new FlowDocumentScrollViewer
            {
                Document = flow,
                IsToolBarVisible = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            viewer.SizeChanged += delegate { double inset = viewer.ActualWidth < 480 ? 16 : 24; flow.PagePadding = new Thickness(inset, 24, inset, 32); };
            return viewer;
        }

        sealed class Budget { public int Blocks, Inlines, Images; public bool Limited; }

        static XElement Body(string xhtml)
        {
            try
            {
                string xml = Prepare(xhtml);
                using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 }))
                {
                    var document = XDocument.Load(reader);
                    var body = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "body");
                    return body ?? document.Root;
                }
            }
            catch { return null; }
        }

        static string Prepare(string xhtml)
        {
            string value = (xhtml ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            value = Regex.Replace(value, "<!DOCTYPE[\\s\\S]*?>", "", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, "[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]", "");
            value = Regex.Replace(value, "&([A-Za-z][A-Za-z0-9]+);", delegate(Match match)
            {
                string name = match.Groups[1].Value;
                if (name == "amp" || name == "lt" || name == "gt" || name == "quot" || name == "apos") return match.Value;
                string decoded = System.Net.WebUtility.HtmlDecode(match.Value);
                return decoded == match.Value ? "&#xFFFD;" : decoded;
            });
            return value;
        }

        static void AddBlocks(XElement source, W.BlockCollection target, EpubBook book, EpubChapter chapter, Action<string> navigate, Budget budget)
        {
            foreach (var element in source.Elements())
            {
                if (++budget.Blocks > 4000) { budget.Limited = true; return; }
                string name = element.Name.LocalName;
                if (name == "h1" || name == "h2" || name == "h3" || name == "h4" || name == "h5" || name == "h6")
                {
                    int level = name[1] - '0';
                    var heading = new W.Paragraph { FontSize = new[] { 30, 24, 20, 18, 16, 14 }[level - 1], FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, level == 1 ? 4 : 20, 0, 12) };
                    AddInlines(element, heading.Inlines, book, chapter, navigate, budget);
                    target.Add(heading);
                    continue;
                }
                if (name == "p" || name == "figcaption")
                {
                    var paragraph = new W.Paragraph { Margin = new Thickness(0, 0, 0, 14) };
                    AddInlines(element, paragraph.Inlines, book, chapter, navigate, budget);
                    target.Add(paragraph);
                    continue;
                }
                if (name == "div" || name == "section" || name == "article" || name == "header" || name == "footer" || name == "main" || name == "aside" || name == "figure")
                {
                    if (element.Elements().Any(child => IsBlock(child.Name.LocalName))) AddBlocks(element, target, book, chapter, navigate, budget);
                    else
                    {
                        var paragraph = new W.Paragraph { Margin = new Thickness(0, 0, 0, 14) };
                        AddInlines(element, paragraph.Inlines, book, chapter, navigate, budget);
                        target.Add(paragraph);
                    }
                    continue;
                }
                if (name == "blockquote")
                {
                    var section = new W.Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(16, 0, 0, 0), Margin = new Thickness(0, 10, 0, 18) };
                    section.SetResourceReference(W.Block.BorderBrushProperty, "QuoteBorder");
                    section.SetResourceReference(W.TextElement.ForegroundProperty, "QuoteText");
                    AddBlocks(element, section.Blocks, book, chapter, navigate, budget);
                    target.Add(section);
                    continue;
                }
                if (name == "pre")
                {
                    var paragraph = new W.Paragraph { FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 14, Padding = new Thickness(16), Margin = new Thickness(0, 8, 0, 16) };
                    paragraph.SetResourceReference(W.TextElement.BackgroundProperty, "CodeBackground");
                    paragraph.Inlines.Add(new W.Run(element.Value));
                    target.Add(paragraph);
                    continue;
                }
                if (name == "ul" || name == "ol")
                {
                    var list = new W.List { MarkerStyle = name == "ol" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 8, 0, 14), Padding = new Thickness(23, 0, 0, 0) };
                    foreach (var item in element.Elements().Where(child => child.Name.LocalName == "li"))
                    {
                        var output = new W.ListItem();
                        AddBlocks(item, output.Blocks, book, chapter, navigate, budget);
                        if (output.Blocks.Count == 0) output.Blocks.Add(new W.Paragraph());
                        list.ListItems.Add(output);
                    }
                    target.Add(list);
                    continue;
                }
                if (name == "table")
                {
                    var table = new W.Table { CellSpacing = 0, FontSize = 14, Margin = new Thickness(0, 6, 0, 20) };
                    var group = new W.TableRowGroup();
                    table.RowGroups.Add(group);
                    foreach (var row in element.Descendants().Where(child => child.Name.LocalName == "tr"))
                    {
                        var output = new W.TableRow();
                        foreach (var cell in row.Elements().Where(child => child.Name.LocalName == "td" || child.Name.LocalName == "th"))
                        {
                            var targetCell = new W.TableCell { Padding = new Thickness(9), BorderThickness = new Thickness(0, 0, 0, 1) };
                            targetCell.SetResourceReference(W.TableCell.BorderBrushProperty, "Rule");
                            AddBlocks(cell, targetCell.Blocks, book, chapter, navigate, budget);
                            if (targetCell.Blocks.Count == 0) targetCell.Blocks.Add(new W.Paragraph());
                            output.Cells.Add(targetCell);
                        }
                        if (output.Cells.Count > 0) group.Rows.Add(output);
                    }
                    target.Add(table);
                    continue;
                }
                if (name == "hr")
                {
                    var rule = new W.Paragraph { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 18, 0, 18) };
                    rule.SetResourceReference(W.Block.BorderBrushProperty, "Rule");
                    target.Add(rule);
                    continue;
                }
                if (IsBlock(name)) continue;
                var fallback = new W.Paragraph { Margin = new Thickness(0, 0, 0, 14) };
                AddInlines(element, fallback.Inlines, book, chapter, navigate, budget);
                target.Add(fallback);
            }
        }

        static bool IsBlock(string name)
        {
            return name == "p" || name == "div" || name == "section" || name == "article" || name == "h1" || name == "h2" || name == "h3"
                || name == "h4" || name == "h5" || name == "h6" || name == "ul" || name == "ol" || name == "li" || name == "blockquote"
                || name == "pre" || name == "table" || name == "tr" || name == "td" || name == "th" || name == "hr";
        }

        static void AddInlines(XElement source, W.InlineCollection target, EpubBook book, EpubChapter chapter, Action<string> navigate, Budget budget)
        {
            if (source.Value == null && !source.HasElements) return;
            if (!source.HasElements)
            {
                string text = Regex.Replace(source.Value ?? "", "\\s+", " ");
                if (text.Length > 0)
                {
                    if (++budget.Inlines > 40000) { budget.Limited = true; return; }
                    target.Add(new W.Run(text));
                }
                return;
            }
            foreach (var node in source.Nodes())
            {
                var textNode = node as XText;
                if (textNode != null)
                {
                    string text = Regex.Replace(textNode.Value ?? "", "\\s+", " ");
                    if (text.Length > 0)
                    {
                        if (++budget.Inlines > 40000) { budget.Limited = true; return; }
                        target.Add(new W.Run(text));
                    }
                    continue;
                }
                var element = node as XElement;
                if (element == null) continue;
                string name = element.Name.LocalName;
                if (name == "br") { target.Add(new W.LineBreak()); continue; }
                if (name == "img")
                {
                    string src = (string)element.Attribute("src");
                    var image = LoadImage(book, chapter, src, element, budget);
                    if (image != null) target.Add(new W.InlineUIContainer(image));
                    continue;
                }
                if (name == "a")
                {
                    string href = (string)element.Attribute("href");
                    var link = new W.Hyperlink { Tag = href };
                    link.SetResourceReference(W.TextElement.ForegroundProperty, "AccentBrush");
                    AddInlines(element, link.Inlines, book, chapter, navigate, budget);
                    if (link.Inlines.Count == 0) link.Inlines.Add(new W.Run(href ?? "链接"));
                    string targetHref = href;
                    link.Click += delegate { if (navigate != null && !String.IsNullOrEmpty(targetHref)) navigate(targetHref); };
                    target.Add(link);
                    continue;
                }
                if (name == "strong" || name == "b")
                {
                    var span = new W.Span { FontWeight = FontWeights.Bold };
                    AddInlines(element, span.Inlines, book, chapter, navigate, budget);
                    target.Add(span);
                    continue;
                }
                if (name == "em" || name == "i")
                {
                    var span = new W.Span { FontStyle = FontStyles.Italic };
                    AddInlines(element, span.Inlines, book, chapter, navigate, budget);
                    target.Add(span);
                    continue;
                }
                if (name == "code" || name == "kbd" || name == "samp")
                {
                    var run = new W.Run(element.Value) { FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 14 };
                    run.SetResourceReference(W.TextElement.BackgroundProperty, "CodeBackground");
                    target.Add(run);
                    continue;
                }
                if (name == "sup" || name == "sub")
                {
                    var run = new W.Run(element.Value) { FontSize = 11, BaselineAlignment = name == "sup" ? BaselineAlignment.Superscript : BaselineAlignment.Subscript };
                    target.Add(run);
                    continue;
                }
                var container = new W.Span();
                AddInlines(element, container.Inlines, book, chapter, navigate, budget);
                target.Add(container);
            }
        }

        static Image LoadImage(EpubBook book, EpubChapter chapter, string src, XElement element, Budget budget)
        {
            if (String.IsNullOrEmpty(src) || ++budget.Images > 40) { budget.Limited = true; return null; }
            byte[] bytes = book.Resource(chapter.Href, src);
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                using (var stream = new MemoryStream(bytes))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    int width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight;
                    if ((long)width * height > 20000000) { budget.Limited = true; return null; }
                    stream.Position = 0;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = Math.Min(width, 900);
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    string alt = (string)element.Attribute("alt");
                    var image = new Image { Source = bitmap, MaxWidth = 680, MaxHeight = 520, Stretch = Stretch.Uniform, Margin = new Thickness(0, 10, 0, 10), ToolTip = alt };
                    return image;
                }
            }
            catch { return null; }
        }
    }
}
