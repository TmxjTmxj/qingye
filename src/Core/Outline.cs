using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Qingye.Core
{
    public sealed class OutlineItem
    {
        public string Title;
        public int Level;
        public int Line;
        public int Offset;
    }

    // Builds a bounded, type-aware outline without holding a second copy of the document.
    public static class Outline
    {
        const int MaxItems = 500;
        const int MaxScanChars = 512 * 1024;

        public static IList<OutlineItem> Build(Document doc)
        {
            if (doc == null) return new List<OutlineItem>();
            string extension = FilePresentation.Extension(doc);
            string text = doc.ReadText(MaxScanChars + 1);
            if (text.Length > MaxScanChars) text = text.Substring(0, MaxScanChars);
            if (new[] { ".md", ".markdown", ".mdown", ".mkd" }.Contains(extension)) return Markdown(text);
            if (extension == ".json" || extension == ".jsonc") return Json(text);
            if (new[] { ".xml", ".xaml", ".config", ".csproj", ".svg" }.Contains(extension)) return Xml(text);
            if (new[] { ".yaml", ".yml", ".ini", ".toml", ".cfg", ".conf", ".env", ".properties" }.Contains(extension) || (doc.Title ?? "") == ".env") return Keys(text);
            if (extension == ".csv" || extension == ".tsv") return Columns(text, extension == ".tsv" ? '\t' : ',');
            if (String.IsNullOrEmpty(extension) || new[] { ".txt", ".text", ".log" }.Contains(extension)) return Prose(text);
            return new List<OutlineItem>();
        }

        sealed class Line
        {
            public int Number, Start;
            public string Text;
        }

        static IEnumerable<Line> Lines(string text)
        {
            int start = 0, number = 1;
            while (start <= text.Length)
            {
                int end = text.IndexOf('\n', start);
                if (end < 0) end = text.Length;
                string value = text.Substring(start, end - start).TrimEnd('\r');
                yield return new Line { Number = number, Start = start, Text = value };
                if (end == text.Length) yield break;
                start = end + 1;
                number++;
            }
        }

        static OutlineItem Item(string title, int level, int line, int offset)
        {
            title = (title ?? "").Trim();
            if (title.Length > 120) title = title.Substring(0, 120) + "…";
            return new OutlineItem { Title = title, Level = Math.Max(1, Math.Min(6, level)), Line = line, Offset = offset };
        }

        static IList<OutlineItem> Markdown(string text)
        {
            var all = Lines(text).ToArray();
            var result = new List<OutlineItem>();
            for (int index = 0; index < all.Length && result.Count < MaxItems; index++)
            {
                Match heading = Regex.Match(all[index].Text, "^ {0,3}(#{1,6})[ \\t]+(.+?)[ \\t#]*$");
                if (heading.Success)
                {
                    result.Add(Item(heading.Groups[2].Value, heading.Groups[1].Value.Length, all[index].Number, all[index].Start + heading.Groups[2].Index));
                    continue;
                }
                // Setext headings: a non-empty line underlined by === or ---.
                if (index + 1 < all.Length && all[index].Text.Trim().Length > 0)
                {
                    string next = all[index + 1].Text.Trim();
                    if (Regex.IsMatch(next, "^=+$") || Regex.IsMatch(next, "^-{2,}$"))
                    {
                        result.Add(Item(all[index].Text, next[0] == '=' ? 1 : 2, all[index].Number, all[index].Start + all[index].Text.IndexOf(all[index].Text.Trim(), StringComparison.Ordinal)));
                        index++;
                    }
                }
            }
            return result;
        }

        static IList<OutlineItem> Json(string text)
        {
            var result = new List<OutlineItem>();
            int depth = 0;
            foreach (Line line in Lines(text))
            {
                string trimmed = line.Text.TrimStart();
                for (int i = 0; i < trimmed.Length && (trimmed[i] == '}' || trimmed[i] == ']'); i++) depth = Math.Max(0, depth - 1);
                Match key = Regex.Match(line.Text, "^\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:");
                if (key.Success && depth >= 1 && depth <= 3 && result.Count < MaxItems)
                    result.Add(Item(Unescape(key.Groups[1].Value), depth, line.Number, line.Start + key.Groups[1].Index));
                bool quoted = false, escaped = false;
                for (int i = 0; i < line.Text.Length; i++)
                {
                    char value = line.Text[i];
                    if (quoted) { if (escaped) escaped = false; else if (value == '\\') escaped = true; else if (value == '"') quoted = false; continue; }
                    if (value == '"') quoted = true;
                    else if (value == '{' || value == '[') depth++;
                    else if (value == '}' || value == ']') depth = Math.Max(0, depth - 1);
                }
            }
            return result;
        }

        static string Unescape(string value)
        {
            try { return Regex.Unescape(value); } catch { return value; }
        }

        static IList<OutlineItem> Xml(string text)
        {
            try
            {
                var lineStarts = new List<int> { 0 };
                for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lineStarts.Add(i + 1);

                XDocument document;
                using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxScanChars }))
                    document = XDocument.Load(reader, LoadOptions.SetLineInfo);
                var result = new List<OutlineItem>();
                if (document.Root == null) return result;
                Walk(document.Root, 1, result, lineStarts);
                return result;
            }
            catch
            {
                return new List<OutlineItem>();
            }
        }

        static void Walk(XElement element, int level, List<OutlineItem> result, List<int> lineStarts)
        {
            if (result.Count >= MaxItems || level > 4) return;
            IXmlLineInfo info = element;
            int line = info.HasLineInfo() ? info.LineNumber : 1;
            int column = info.HasLineInfo() ? info.LinePosition : 1;
            int offset = lineStarts.Count >= line ? lineStarts[line - 1] + Math.Max(0, column - 1) : 0;
            string id = (string)element.Attribute("id") ?? (string)element.Attribute("name");
            result.Add(Item("<" + element.Name.LocalName + ">" + (String.IsNullOrEmpty(id) ? "" : " #" + id), level, line, offset));
            foreach (var child in element.Elements()) Walk(child, level + 1, result, lineStarts);
        }

        static IList<OutlineItem> Keys(string text)
        {
            var result = new List<OutlineItem>();
            foreach (Line line in Lines(text))
            {
                if (result.Count >= MaxItems) break;
                string trimmed = line.Text.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';') continue;
                Match section = Regex.Match(trimmed, "^\\[([^\\]]+)\\]");
                if (section.Success) { result.Add(Item(section.Groups[1].Value, 1, line.Number, line.Start + line.Text.IndexOf('['))); continue; }
                Match key = Regex.Match(line.Text, "^(\\s*)([A-Za-z_][A-Za-z0-9_.-]*|\"[^\"]+\"|'[^']+')\\s*[:=]");
                if (key.Success) result.Add(Item(key.Groups[2].Value.Trim('"', '\''), key.Groups[1].Value.Length / 2 + 1, line.Number, line.Start + key.Groups[2].Index));
            }
            return result;
        }

        static IList<OutlineItem> Columns(string text, char delimiter)
        {
            var result = new List<OutlineItem>();
            var table = ContentReaders.ParseTable(text, delimiter);
            if (table.Rows.Count == 0) return result;
            string[] header = table.Rows[0];
            for (int index = 0; index < header.Length && result.Count < MaxItems; index++)
                if (!String.IsNullOrWhiteSpace(header[index])) result.Add(Item(header[index], 1, 1, 0));
            return result;
        }

        static IList<OutlineItem> Prose(string text)
        {
            var result = new List<OutlineItem>();
            var chapter = new Regex("^\\s*(第[0-9一二三四五六七八九十百千]+[章卷部篇回]|Chapter\\s+\\d+|Part\\s+\\d+|序章|前言|后记|附录[0-9A-Za-z一二三四五六七八九十]*)\\b", RegexOptions.IgnoreCase);
            var section = new Regex("^\\s*(第[0-9一二三四五六七八九十百千]+节|[0-9]+\\.[0-9]+\\s+\\S)");
            foreach (Line line in Lines(text))
            {
                if (result.Count >= MaxItems) break;
                string value = line.Text.Trim();
                if (value.Length == 0 || value.Length > 100) continue;
                if (chapter.IsMatch(value)) result.Add(Item(value, 1, line.Number, line.Start + line.Text.IndexOf(value, StringComparison.Ordinal)));
                else if (section.IsMatch(value)) result.Add(Item(value, 2, line.Number, line.Start + line.Text.IndexOf(value, StringComparison.Ordinal)));
            }
            return result;
        }
    }
}
