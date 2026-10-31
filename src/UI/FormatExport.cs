using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Qingye.Core;

namespace Qingye.UI
{
    public enum ExportFormat { Text, Markdown, Html, Docx, Csv, Xlsx, Pdf }

    // Converts readable document content into another format. The source file is
    // never touched: export always writes a new file and leaves the session as-is.
    public static class FormatExport
    {
        static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();
        const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        public static IList<ExportFormat> Formats(Document doc)
        {
            if (doc == null) return new ExportFormat[0];
            if (doc.Kind == DocumentKind.Image || doc.Kind == DocumentKind.Pdf || doc.Kind == DocumentKind.Binary || doc.Kind == DocumentKind.PagedText || doc.Kind == DocumentKind.Epub) return new ExportFormat[0];
            if (doc.Kind == DocumentKind.Xlsx) return new[] { ExportFormat.Csv, ExportFormat.Html, ExportFormat.Text, ExportFormat.Pdf };
            if (doc.Kind == DocumentKind.Docx || doc.Kind == DocumentKind.Pptx || doc.Kind == DocumentKind.Rtf)
                return new[] { ExportFormat.Text, ExportFormat.Markdown, ExportFormat.Html, ExportFormat.Docx, ExportFormat.Pdf };
            string extension = FilePresentation.Extension(doc);
            if (extension == ".csv" || extension == ".tsv")
                return new[] { ExportFormat.Csv, ExportFormat.Xlsx, ExportFormat.Html, ExportFormat.Text, ExportFormat.Pdf };
            if (new[] { ".md", ".markdown", ".mdown", ".mkd" }.Contains(extension))
                return new[] { ExportFormat.Markdown, ExportFormat.Html, ExportFormat.Docx, ExportFormat.Text, ExportFormat.Pdf };
            return new[] { ExportFormat.Text, ExportFormat.Markdown, ExportFormat.Html, ExportFormat.Docx, ExportFormat.Pdf };
        }

        public static string Name(ExportFormat format)
        {
            switch (format)
            {
                case ExportFormat.Text: return "纯文本";
                case ExportFormat.Markdown: return "Markdown";
                case ExportFormat.Html: return "网页 HTML";
                case ExportFormat.Docx: return "Word 文档";
                case ExportFormat.Csv: return "CSV 表格";
                case ExportFormat.Xlsx: return "Excel 工作簿";
                default: return "PDF";
            }
        }

        public static string Extension(ExportFormat format)
        {
            switch (format)
            {
                case ExportFormat.Text: return ".txt";
                case ExportFormat.Markdown: return ".md";
                case ExportFormat.Html: return ".html";
                case ExportFormat.Docx: return ".docx";
                case ExportFormat.Csv: return ".csv";
                case ExportFormat.Xlsx: return ".xlsx";
                default: return ".pdf";
            }
        }

        // Native OPENFILENAME filters are null-separated pairs, never pipe-separated.
        public static string Filter(IList<ExportFormat> formats)
        {
            return String.Join("\0", formats.Select(format => Name(format) + " (*" + Extension(format) + ")\0*" + Extension(format)));
        }

        public static ExportFormat FormatForExtension(string path)
        {
            switch ((Path.GetExtension(path ?? "") ?? "").ToLowerInvariant())
            {
                case ".md": return ExportFormat.Markdown;
                case ".html": case ".htm": return ExportFormat.Html;
                case ".docx": return ExportFormat.Docx;
                case ".csv": case ".tsv": return ExportFormat.Csv;
                case ".xlsx": return ExportFormat.Xlsx;
                case ".pdf": return ExportFormat.Pdf;
                default: return ExportFormat.Text;
            }
        }

        public static string Text(Document doc, ExportFormat format)
        {
            switch (format)
            {
                case ExportFormat.Text: return PlainText(doc);
                case ExportFormat.Markdown: return MarkdownText(doc);
                case ExportFormat.Html: return HtmlText(doc);
                case ExportFormat.Csv: return CsvText(doc);
                default: throw new NotSupportedException("此格式需要二进制写入。");
            }
        }

        public static byte[] Bytes(Document doc, ExportFormat format)
        {
            if (format == ExportFormat.Docx) return DocxBytes(doc);
            if (format == ExportFormat.Xlsx) return XlsxBytes(doc);
            string text = Text(doc, format);
            if (format == ExportFormat.Text && (doc.Kind == DocumentKind.Text || doc.Kind == DocumentKind.PagedText) && !String.IsNullOrEmpty(doc.EncodingName))
            {
                var encoding = FileService.GetEncoding(doc.EncodingName, doc.HasBom);
                byte[] body = encoding.GetBytes(text);
                byte[] prefix = doc.HasBom ? encoding.GetPreamble() : new byte[0];
                var result = new byte[prefix.Length + body.Length];
                Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length);
                Buffer.BlockCopy(body, 0, result, prefix.Length, body.Length);
                return result;
            }
            return new UTF8Encoding(false).GetBytes(text);
        }

        public static bool IsText(ExportFormat format)
        {
            return format == ExportFormat.Text || format == ExportFormat.Markdown || format == ExportFormat.Html || format == ExportFormat.Csv;
        }

        static string PlainText(Document doc)
        {
            if (doc.Kind == DocumentKind.Xlsx)
            {
                var table = ContentReaders.ReadXlsx(doc.Path);
                return String.Join("\r\n", table.Rows.Select(row => String.Join("\t", row)));
            }
            return Normalize(doc.Text);
        }

        static string MarkdownText(Document doc)
        {
            if (FilePresentation.For(doc).CanWysiwyg) return Normalize(doc.Text);
            if (doc.Kind == DocumentKind.Xlsx || FilePresentation.Extension(doc) == ".csv" || FilePresentation.Extension(doc) == ".tsv")
                return TableMarkdown(Rows(doc));
            string text = Normalize(doc.Text).Replace("\r\n", "\n");
            if (doc.Kind == DocumentKind.Pptx)
            {
                var lines = text.Split('\n');
                for (int index = 0; index < lines.Length; index++)
                    if (lines[index].StartsWith("第 ", StringComparison.Ordinal) && lines[index].Contains(" 页")) lines[index] = "## " + lines[index];
                text = String.Join("\n", lines);
            }
            return text;
        }

        static string HtmlText(Document doc)
        {
            string extension = FilePresentation.Extension(doc);
            string body;
            if (new[] { ".md", ".markdown", ".mdown", ".mkd" }.Contains(extension)) body = WebContent.MarkdownHtml(doc.Text);
            else if (extension == ".html" || extension == ".htm") body = doc.Text;
            else if (doc.Kind == DocumentKind.Xlsx || extension == ".csv" || extension == ".tsv") body = TableHtml(Rows(doc));
            else body = "<pre>" + SecurityElement.Escape(Normalize(doc.Text)) + "</pre>";
            return "<!doctype html><html><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline'; img-src * data:; script-src 'none'\"><style>body{font:16px/1.8 'Segoe UI','Microsoft YaHei UI',sans-serif;color:#263d38;margin:32px auto;padding:0 32px;max-width:860px;background:#fff}pre{white-space:pre-wrap;word-break:break-word;background:#f4f7f5;padding:18px;border-radius:6px}table{border-collapse:collapse;width:100%}th,td{border-bottom:1px solid #dfe9e4;text-align:left;padding:8px}h1,h2,h3{line-height:1.35}</style></head><body>" + body + "</body></html>";
        }

        static string CsvText(Document doc)
        {
            return String.Join("\r\n", Rows(doc).Select(row => String.Join(",", row.Select(Quote))));
        }

        static string Quote(string value)
        {
            value = value ?? "";
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        static IList<string[]> Rows(Document doc)
        {
            if (doc.Kind == DocumentKind.Xlsx) return ContentReaders.ReadXlsx(doc.Path).Rows;
            string extension = FilePresentation.Extension(doc);
            if (extension == ".csv" || extension == ".tsv") return ContentReaders.ParseTable(doc.Text, extension == ".tsv" ? '\t' : ',').Rows;
            if (new[] { ".md", ".markdown", ".mdown", ".mkd" }.Contains(extension)) return MarkdownTable(doc.Text);
            return new List<string[]>();
        }

        static IList<string[]> MarkdownTable(string text)
        {
            var rows = new List<string[]>();
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("|", StringComparison.Ordinal) || !line.EndsWith("|", StringComparison.Ordinal)) continue;
                string[] cells = line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
                if (cells.All(cell => cell.Length > 0 && cell.All(value => value == '-' || value == ':'))) continue;
                rows.Add(cells);
                if (rows.Count >= 500) break;
            }
            return rows;
        }

        static string TableMarkdown(IList<string[]> rows)
        {
            if (rows.Count == 0) return "";
            int columns = Math.Max(1, rows.Max(row => row.Length));
            var builder = new StringBuilder();
            for (int index = 0; index < rows.Count; index++)
            {
                builder.Append("| ").Append(String.Join(" | ", Pad(rows[index], columns).Select(cell => cell.Replace("|", "\\|")))).Append(" |\n");
                if (index == 0) builder.Append('|').Append(String.Join("|", Enumerable.Repeat(" --- ", columns))).Append("|\n");
            }
            return builder.ToString();
        }

        static string TableHtml(IList<string[]> rows)
        {
            if (rows.Count == 0) return "<p>没有可导出的表格内容。</p>";
            var builder = new StringBuilder("<table>");
            for (int index = 0; index < rows.Count; index++)
            {
                string tag = index == 0 ? "th" : "td";
                builder.Append("<tr>");
                foreach (string cell in rows[index]) builder.Append('<').Append(tag).Append('>').Append(SecurityElement.Escape(cell ?? "")).Append("</").Append(tag).Append('>');
                builder.Append("</tr>");
            }
            builder.Append("</table>");
            return builder.ToString();
        }

        static string[] Pad(string[] row, int columns)
        {
            var result = new string[columns];
            for (int index = 0; index < columns; index++) result[index] = index < row.Length ? row[index] : "";
            return result;
        }

        static string Normalize(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        }

        static byte[] DocxBytes(Document doc)
        {
            string body = WordBody(doc);
            string document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:document xmlns:w=\"" + WordNamespace + "\"><w:body>" + body +
                "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/></w:sectPr></w:body></w:document>";
            using (var stream = new MemoryStream())
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    WriteEntry(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
                    WriteEntry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
                    WriteEntry(zip, "word/document.xml", document);
                }
                return stream.ToArray();
            }
        }

        static string WordBody(Document doc)
        {
            var builder = new StringBuilder();
            if (FilePresentation.For(doc).CanWysiwyg)
            {
                var tree = Markdown.Parse(doc.Text, MarkdownPipeline);
                foreach (var block in tree)
                {
                    var heading = block as HeadingBlock;
                    if (heading != null)
                    {
                        int level = Math.Max(1, Math.Min(6, heading.Level));
                        builder.Append(WordParagraph(InlineText(heading.Inline), true, level == 1 ? 32 : level == 2 ? 24 : 19, level));
                        continue;
                    }
                    var paragraph = block as ParagraphBlock;
                    if (paragraph != null) { builder.Append(WordParagraph(InlineText(paragraph.Inline), false, 0, 0)); continue; }
                    var list = block as ListBlock;
                    if (list != null)
                    {
                        int number = 0;
                        foreach (var item in list.OfType<ListItemBlock>())
                            foreach (var child in item.OfType<ParagraphBlock>())
                                builder.Append(WordParagraph((list.IsOrdered ? (++number).ToString(CultureInfo.InvariantCulture) + ". " : "• ") + InlineText(child.Inline), false, 0, 0));
                        continue;
                    }
                    var code = block as CodeBlock;
                    if (code != null) { builder.Append(WordParagraph(code.Lines.ToString(), false, 0, 0, true)); continue; }
                    var quote = block as QuoteBlock;
                    if (quote != null)
                        foreach (var child in quote.OfType<ParagraphBlock>()) builder.Append(WordParagraph(InlineText(child.Inline), false, 0, 0));
                    var table = block as Markdig.Extensions.Tables.Table;
                    if (table != null) builder.Append(WordTable(table));
                }
            }
            else
            {
                foreach (string line in Normalize(doc.Text).Split('\n'))
                    if (line.Trim().Length > 0) builder.Append(WordParagraph(line, false, 0, 0));
            }
            if (builder.Length == 0) builder.Append(WordParagraph("", false, 0, 0));
            return builder.ToString();
        }

        static string WordTable(Markdig.Extensions.Tables.Table source)
        {
            var builder = new StringBuilder("<w:tbl><w:tblPr><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/><w:insideH w:val=\"single\" w:sz=\"4\"/></w:tblBorders></w:tblPr>");
            foreach (var row in source.OfType<Markdig.Extensions.Tables.TableRow>())
            {
                builder.Append("<w:tr>");
                foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                {
                    var paragraph = cell.OfType<ParagraphBlock>().FirstOrDefault();
                    builder.Append("<w:tc><w:tcPr><w:tcW w:w=\"2400\" w:type=\"dxa\"/></w:tcPr>");
                    builder.Append(WordParagraph(InlineText(paragraph == null ? null : paragraph.Inline), row.IsHeader, 0, 0));
                    builder.Append("</w:tc>");
                }
                builder.Append("</w:tr>");
            }
            return builder.Append("</w:tbl>").ToString();
        }

        static string WordParagraph(string text, bool bold, int size, int outline, bool code = false)
        {
            string properties = "<w:pPr>";
            if (outline > 0) properties += "<w:pStyle w:val=\"Heading" + outline + "\"/>";
            properties += "</w:pPr>";
            string runProperties = "<w:rPr>";
            if (bold) runProperties += "<w:b/>";
            if (size > 0) runProperties += "<w:sz w:val=\"" + (size * 2) + "\"/>";
            if (code) runProperties += "<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\"/>";
            runProperties += "</w:rPr>";
            return "<w:p>" + properties + "<w:r>" + runProperties + "<w:t xml:space=\"preserve\">" + SecurityElement.Escape(text ?? "") + "</w:t></w:r></w:p>";
        }

        static string InlineText(ContainerInline source)
        {
            if (source == null) return "";
            var builder = new StringBuilder();
            for (var inline = source.FirstChild; inline != null; inline = inline.NextSibling)
            {
                var literal = inline as LiteralInline;
                if (literal != null) { builder.Append(literal.Content); continue; }
                var code = inline as CodeInline;
                if (code != null) { builder.Append(code.Content); continue; }
                var line = inline as LineBreakInline;
                if (line != null) { builder.Append(' '); continue; }
                var container = inline as ContainerInline;
                if (container != null) builder.Append(InlineText(container));
            }
            return builder.ToString();
        }

        static byte[] XlsxBytes(Document doc)
        {
            var rows = Rows(doc);
            var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            for (int row = 0; row < rows.Count && row < 500; row++)
            {
                sheet.Append("<row r=\"").Append(row + 1).Append("\">");
                string[] values = rows[row];
                for (int column = 0; column < values.Length && column < 50; column++)
                {
                    string value = values[column] ?? "";
                    if (value.Length == 0) continue;
                    string reference = ColumnName(column) + (row + 1);
                    decimal number;
                    if (Decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out number))
                        sheet.Append("<c r=\"").Append(reference).Append("\"><v>").Append(number.ToString(CultureInfo.InvariantCulture)).Append("</v></c>");
                    else
                        sheet.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(SecurityElement.Escape(value)).Append("</t></is></c>");
                }
                sheet.Append("</row>");
            }
            sheet.Append("</sheetData></worksheet>");
            using (var stream = new MemoryStream())
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    WriteEntry(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
                    WriteEntry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                    WriteEntry(zip, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                    WriteEntry(zip, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                    WriteEntry(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
                }
                return stream.ToArray();
            }
        }

        static string ColumnName(int column)
        {
            string result = "";
            for (int value = column + 1; value > 0; value = (value - 1) / 26) result = (char)('A' + (value - 1) % 26) + result;
            return result;
        }

        static void WriteEntry(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(content);
        }
    }
}
