using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Qingye.Core
{
    public sealed class PresentationContent
    {
        public XElement Body;
        public int SlideCount;
        public bool Truncated;
        public string Notice;
    }

    public static partial class ContentReaders
    {
        const string PresentationNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string StrictPresentationNamespace = "http://purl.oclc.org/ooxml/presentationml/main";
        static readonly XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        sealed class PresentationBudget
        {
            public int Blocks, Inlines, Characters;
            public bool Limited;
            public bool Full { get { return Blocks >= 1500 || Inlines >= 20000 || Characters >= 512 * 1024; } }
        }

        public static PresentationContent ReadPptx(string path)
        {
            var result = new PresentationContent { Body = new XElement(word + "body") };
            var budget = new PresentationBudget();
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
            {
                long total = 0;
                var presentation = Xml(zip, "ppt/presentation.xml", ref total);
                if (presentation == null || presentation.Root == null) throw new InvalidDataException("未找到 PPTX 演示文稿。");
                XNamespace presentationNs = presentation.Root.Name.Namespace;
                if (presentation.Root.Name.LocalName != "presentation" ||
                    presentationNs != PresentationNamespace && presentationNs != StrictPresentationNamespace)
                    throw new InvalidDataException("PPTX 命名空间无效。");
                XNamespace relationshipNs = presentationNs == PresentationNamespace
                    ? "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                    : "http://purl.oclc.org/ooxml/officeDocument/relationships";
                var relationships = Xml(zip, "ppt/_rels/presentation.xml.rels", ref total);
                if (relationships == null) throw new InvalidDataException("PPTX 页面引用缺失。");
                var slides = presentation.Root.Element(presentationNs + "sldIdLst");
                if (slides == null) throw new InvalidDataException("PPTX 页面列表缺失。");
                foreach (var reference in slides.Elements(presentationNs + "sldId"))
                {
                    if (result.SlideCount >= 100 || budget.Full) { budget.Limited = true; break; }
                    string id = (string)reference.Attribute(relationshipNs + "id");
                    var matches = relationships.Root.Elements().Where(element => element.Name.LocalName == "Relationship" && (string)element.Attribute("Id") == id).Take(2).ToArray();
                    if (String.IsNullOrEmpty(id) || matches.Length != 1 ||
                        (string)matches[0].Attribute("Type") != relationshipNs.NamespaceName + "/slide" ||
                        String.Equals((string)matches[0].Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("PPTX 页面引用无效或指向外部资源。");
                    string target = PresentationPart((string)matches[0].Attribute("Target"));
                    var slide = Xml(zip, target, ref total);
                    if (slide == null || slide.Root == null || slide.Root.Name != presentationNs + "sld")
                        throw new InvalidDataException("PPTX 页面内容缺失或无效。");
                    result.SlideCount++;
                    AddPresentationSlide(slide.Root, presentationNs, result, budget);
                }
            }
            result.Truncated = budget.Limited;
            result.Notice = "PPTX 只读提取 · 标题、正文与表格；不含图片、动画、图表、讲者备注及原始版式";
            if (result.Truncated)
            {
                result.Notice += " · 部分内容超出预览预算";
                result.Body.Add(new XElement(word + "p", new XElement(word + "r", new XElement(word + "t", "部分内容超出基础预览范围，请用系统软件查看完整文档。"))));
            }
            return result;
        }

        static string PresentationPart(string target)
        {
            if (String.IsNullOrEmpty(target)) throw new InvalidDataException("PPTX 页面路径缺失。");
            target = Uri.UnescapeDataString(target);
            if (target.Contains("\\") || target.Contains(":") || target.Contains("?") || target.Contains("#") || target.StartsWith("//") ||
                target.Split('/').Any(segment => segment == "." || segment == "..") || !target.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PPTX 页面路径不受支持。");
            return target.StartsWith("/") ? target.Substring(1) : "ppt/" + target;
        }

        static void AddPresentationSlide(XElement slide, XNamespace presentationNs, PresentationContent result, PresentationBudget budget)
        {
            XNamespace drawing = presentationNs == PresentationNamespace
                ? "http://schemas.openxmlformats.org/drawingml/2006/main"
                : "http://purl.oclc.org/ooxml/drawingml/main";
            var tree = slide.Element(presentationNs + "cSld");
            var title = tree == null ? null : tree.Descendants(presentationNs + "sp").FirstOrDefault(shape =>
                shape.Descendants(presentationNs + "ph").Any(placeholder => new[] { "title", "ctrTitle" }.Contains((string)placeholder.Attribute("type"))));
            var heading = new XElement(word + "p", new XElement(word + "pPr", new XElement(word + "pStyle", new XAttribute(word + "val", "Heading1"))),
                new XElement(word + "r", new XElement(word + "t", "第 " + result.SlideCount + " 页" + (title == null ? "" : " · "))));
            result.Body.Add(heading);
            budget.Blocks++;
            if (title != null)
                foreach (var paragraph in title.Descendants(drawing + "p")) heading.Add(PresentationParagraph(paragraph, drawing, budget).Elements(word + "r"));
            if (tree == null) return;
            foreach (var block in tree.Descendants().Where(element => element.Name == presentationNs + "sp" || element.Name == drawing + "tbl"))
            {
                if (budget.Full) { budget.Limited = true; break; }
                if (block == title) continue;
                if (block.Name == drawing + "tbl") result.Body.Add(PresentationTable(block, drawing, budget));
                else foreach (var paragraph in block.Descendants(drawing + "p"))
                {
                    if (budget.Full) { budget.Limited = true; break; }
                    result.Body.Add(PresentationParagraph(paragraph, drawing, budget));
                }
            }
        }

        static XElement PresentationParagraph(XElement source, XNamespace drawing, PresentationBudget budget)
        {
            var paragraph = new XElement(word + "p");
            budget.Blocks++;
            foreach (var node in source.Descendants().Where(element => element.Name == drawing + "t" || element.Name == drawing + "br" || element.Name == drawing + "tab"))
            {
                if (budget.Inlines >= 20000 || budget.Characters >= 512 * 1024) { budget.Limited = true; break; }
                budget.Inlines++;
                var run = new XElement(word + "r");
                if (node.Name == drawing + "t")
                {
                    string value = node.Value;
                    int length = Math.Min(value.Length, 512 * 1024 - budget.Characters);
                    if (length < value.Length) { if (length > 0 && Char.IsHighSurrogate(value[length - 1])) length--; budget.Limited = true; }
                    run.Add(new XElement(word + "t", value.Substring(0, length)));
                    budget.Characters += length;
                    var properties = node.Parent.Element(drawing + "rPr");
                    if (properties != null)
                    {
                        var format = new XElement(word + "rPr");
                        if (new[] { "1", "true" }.Contains((string)properties.Attribute("b"))) format.Add(new XElement(word + "b"));
                        if (new[] { "1", "true" }.Contains((string)properties.Attribute("i"))) format.Add(new XElement(word + "i"));
                        run.AddFirst(format);
                    }
                }
                else run.Add(new XElement(word + (node.Name.LocalName == "br" ? "br" : "tab")));
                paragraph.Add(run);
            }
            return paragraph;
        }

        static XElement PresentationTable(XElement source, XNamespace drawing, PresentationBudget budget)
        {
            var table = new XElement(word + "tbl");
            budget.Blocks++;
            int rows = 0;
            foreach (var row in source.Elements(drawing + "tr"))
            {
                if (++rows > 500 || budget.Full) { budget.Limited = true; break; }
                var output = new XElement(word + "tr");
                int columns = 0;
                foreach (var cell in row.Elements(drawing + "tc"))
                {
                    if (++columns > 50 || budget.Full) { budget.Limited = true; break; }
                    budget.Blocks++;
                    var outputCell = new XElement(word + "tc");
                    foreach (var paragraph in cell.Descendants(drawing + "p"))
                    {
                        if (budget.Full) { budget.Limited = true; break; }
                        outputCell.Add(PresentationParagraph(paragraph, drawing, budget));
                    }
                    output.Add(outputCell);
                }
                table.Add(output);
            }
            return table;
        }
    }
}
