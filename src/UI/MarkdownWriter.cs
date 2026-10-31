using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using W = System.Windows.Documents;

namespace Qingye.UI
{
    // Serializes the editable FlowDocument back into the supported Markdown subset.
    // Formatting is normalized deliberately; the source text is never assumed to be
    // byte-identical after an in-place editing session.
    public static class MarkdownWriter
    {
        public static string Write(FlowDocument document, string newLine)
        {
            var builder = new StringBuilder();
            WriteBlocks(document.Blocks, builder, "");
            string text = builder.ToString().TrimEnd('\n', ' ');
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            return newLine == "\n" ? text : text.Replace("\n", newLine);
        }

        static void WriteBlocks(BlockCollection blocks, StringBuilder builder, string indent)
        {
            foreach (W.Block block in blocks)
            {
                if (block is W.Paragraph) { WriteParagraph((W.Paragraph)block, builder, indent); continue; }
                if (block is W.Section) { WriteQuote((W.Section)block, builder, indent); continue; }
                if (block is W.List) { WriteList((W.List)block, builder, indent); continue; }
                if (block is W.Table) { WriteTable((W.Table)block, builder, indent); continue; }
            }
        }

        static void WriteParagraph(W.Paragraph paragraph, StringBuilder builder, string indent)
        {
            string tag = paragraph.Tag as string ?? "";
            if (tag == "hr") { builder.Append(indent).Append("---\n\n"); return; }
            if (tag.StartsWith("code", StringComparison.Ordinal))
            {
                string language = tag.Length > 4 ? tag.Substring(4) : "";
                string body = Plain(paragraph).Trim('\n');
                if (body.Length == 0) return;
                string fence = body.Contains("```") ? "````" : "```";
                builder.Append(indent).Append(fence).Append(language).Append('\n');
                builder.Append(body);
                if (!body.EndsWith("\n", StringComparison.Ordinal)) builder.Append('\n');
                builder.Append(indent).Append(fence).Append("\n\n");
                return;
            }
            if (tag.Length >= 2 && tag[0] == 'h' && tag[1] >= '1' && tag[1] <= '6')
            {
                string value = Inlines(paragraph.Inlines).Trim();
                if (value.Length == 0) return;
                builder.Append(indent).Append('#', tag[1] - '0').Append(' ').Append(value).Append("\n\n");
                return;
            }
            string text = Inlines(paragraph.Inlines).TrimEnd();
            if (text.Length == 0) return;
            if (tag == "quote")
            {
                foreach (string line in text.Split('\n')) builder.Append(indent).Append("> ").Append(line).Append('\n');
                builder.Append('\n');
                return;
            }
            builder.Append(indent).Append(ProtectLineStart(text)).Append("\n\n");
        }

        static void WriteQuote(W.Section section, StringBuilder builder, string indent)
        {
            var body = new StringBuilder();
            WriteBlocks(section.Blocks, body, "");
            string text = body.ToString().TrimEnd('\n');
            if (text.Length == 0) return;
            foreach (string line in text.Split('\n')) builder.Append(indent).Append("> ").Append(line).Append('\n');
            builder.Append('\n');
        }

        static void WriteList(W.List list, StringBuilder builder, string indent)
        {
            bool ordered = list.MarkerStyle != TextMarkerStyle.Disc && list.MarkerStyle != TextMarkerStyle.Circle
                && list.MarkerStyle != TextMarkerStyle.Square && list.MarkerStyle != TextMarkerStyle.Box;
            int number = Math.Max(1, list.StartIndex);
            foreach (W.ListItem item in list.ListItems)
            {
                var body = new StringBuilder();
                WriteBlocks(item.Blocks, body, "");
                string text = body.ToString().TrimEnd('\n', ' ');
                if (text.Length == 0) continue;
                string marker = ordered ? (number++).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " : "- ";
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                builder.Append(indent).Append(marker).Append(ProtectLineStart(lines[0])).Append('\n');
                for (int index = 1; index < lines.Length; index++)
                    builder.Append(indent).Append("    ").Append(lines[index]).Append('\n');
            }
            builder.Append('\n');
        }

        static void WriteTable(W.Table table, StringBuilder builder, string indent)
        {
            var rows = new List<string[]>();
            foreach (W.TableRowGroup group in table.RowGroups)
                foreach (W.TableRow row in group.Rows)
                {
                    var cells = new List<string>();
                    foreach (W.TableCell cell in row.Cells) cells.Add(Cell(cell));
                    rows.Add(cells.ToArray());
                }
            if (rows.Count == 0) return;
            int columns = Math.Max(1, rows.Max(row => row.Length));
            for (int index = 0; index < rows.Count; index++)
            {
                string line = "| " + String.Join(" | ", Pad(rows[index], columns)) + " |";
                builder.Append(indent).Append(line).Append('\n');
                if (index == 0) builder.Append(indent).Append('|').Append(String.Join("|", Enumerable.Repeat(" --- ", columns))).Append("|\n");
            }
            builder.Append('\n');
        }

        static string[] Pad(string[] row, int columns)
        {
            var result = new string[columns];
            for (int index = 0; index < columns; index++) result[index] = index < row.Length ? row[index] : "";
            return result;
        }

        static string Cell(W.TableCell cell)
        {
            var value = new StringBuilder();
            foreach (W.Block block in cell.Blocks)
            {
                var paragraph = block as W.Paragraph;
                if (paragraph == null) continue;
                if (value.Length > 0) value.Append(' ');
                value.Append(Inlines(paragraph.Inlines));
            }
            string text = value.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Replace("\\", "\\\\").Replace("|", "\\|");
        }

        static string Plain(W.Paragraph paragraph)
        {
            return new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
        }

        static string Inlines(InlineCollection inlines)
        {
            var builder = new StringBuilder();
            foreach (W.Inline inline in inlines) AppendInline(inline, builder);
            return builder.ToString();
        }

        static void AppendInline(W.Inline inline, StringBuilder builder)
        {
            var run = inline as W.Run;
            if (run != null)
            {
                string tag = run.Tag as string ?? "";
                if (tag.StartsWith("image\t", StringComparison.Ordinal))
                {
                    builder.Append(ImageSyntax(tag.Substring(6)));
                    return;
                }
                string text = run.Text ?? "";
                if (tag == "code")
                {
                    builder.Append(CodeSpan(text));
                    return;
                }
                text = Escape(text);
                if (text.Length == 0) return;
                bool bold = LocalBold(run);
                bool italic = LocalItalic(run);
                bool strike = LocalStrike(run);
                if (bold && italic) text = Wrap(text, "***");
                else if (bold) text = Wrap(text, "**");
                else if (italic) text = Wrap(text, "*");
                if (strike) text = Wrap(text, "~~");
                builder.Append(text);
                return;
            }
            var span = inline as W.Span;
            if (span != null)
            {
                string inner = Inlines(span.Inlines);
                bool bold = LocalBold(span);
                bool italic = LocalItalic(span);
                bool strike = LocalStrike(span);
                if (bold && italic) inner = Wrap(inner, "***");
                else if (bold) inner = Wrap(inner, "**");
                else if (italic) inner = Wrap(inner, "*");
                if (strike) inner = Wrap(inner, "~~");
                builder.Append(inner);
                return;
            }
            var hyperlink = inline as W.Hyperlink;
            if (hyperlink != null)
            {
                string url = hyperlink.Tag as string;
                if (String.IsNullOrEmpty(url) && hyperlink.NavigateUri != null) url = hyperlink.NavigateUri.ToString();
                builder.Append('[').Append(Inlines(hyperlink.Inlines)).Append("](").Append(url ?? "").Append(')');
                return;
            }
            if (inline is W.LineBreak) { builder.Append("  \n"); return; }
            var image = inline as W.InlineUIContainer;
            if (image != null)
            {
                string tag = image.Tag as string ?? "";
                if (tag.StartsWith("image\t", StringComparison.Ordinal)) builder.Append(ImageSyntax(tag.Substring(6)));
                return;
            }
        }

        static string ImageSyntax(string payload)
        {
            int split = payload.IndexOf('\t');
            string alt = split < 0 ? "" : payload.Substring(0, split);
            string url = split < 0 ? payload : payload.Substring(split + 1);
            return "![" + Escape(alt) + "](" + url + ")";
        }

        static bool HasStrike(TextDecorationCollection decorations)
        {
            if (decorations == null) return false;
            foreach (TextDecoration decoration in decorations)
                if (decoration.Location == TextDecorationLocation.Strikethrough) return true;
            return false;
        }

        static bool LocalBold(W.Inline inline)
        {
            object value = inline.ReadLocalValue(W.TextElement.FontWeightProperty);
            return value is FontWeight && ((FontWeight)value).ToOpenTypeWeight() >= 600;
        }

        static bool LocalItalic(W.Inline inline)
        {
            object value = inline.ReadLocalValue(W.TextElement.FontStyleProperty);
            return value is FontStyle && ((FontStyle)value).Equals(FontStyles.Italic);
        }

        static bool LocalStrike(W.Inline inline)
        {
            object value = inline.ReadLocalValue(W.Inline.TextDecorationsProperty);
            return value is TextDecorationCollection && HasStrike((TextDecorationCollection)value);
        }

        static string Wrap(string text, string marker)
        {
            if (text.Length == 0) return text;
            int start = 0, end = text.Length;
            while (start < end && Char.IsWhiteSpace(text[start])) start++;
            while (end > start && Char.IsWhiteSpace(text[end - 1])) end--;
            if (start == end) return text;
            return text.Substring(0, start) + marker + text.Substring(start, end - start) + marker + text.Substring(end);
        }

        static string CodeSpan(string text)
        {
            if (text.Length == 0) return "``";
            string fence = text.Contains("`") ? "``" : "`";
            return fence + text + fence;
        }

        static string Escape(string text)
        {
            var builder = new StringBuilder(text.Length + 8);
            foreach (char value in text)
            {
                if ("\\`*_{}[]<>~".IndexOf(value) >= 0) builder.Append('\\');
                builder.Append(value);
            }
            return builder.ToString();
        }

        static string ProtectLineStart(string text)
        {
            Match match = Regex.Match(text, "^(\\s*)(#{1,6}|>|[-+*]|\\d+[.)])(\\s)");
            if (!match.Success) return text;
            int position = match.Groups[1].Length;
            return text.Substring(0, position) + "\\" + text.Substring(position);
        }
    }
}
