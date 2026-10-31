using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Qingye.Core
{
    public sealed class EpubChapter
    {
        public string Title;
        public string Href;
        public string Xhtml;
    }

    // Reads the EPUB package without extracting it to disk. Only spine chapters
    // and bounded image resources are kept in memory.
    public sealed class EpubBook
    {
        public string Title = "";
        public string Author = "";
        public string SourcePath;
        public readonly List<EpubChapter> Chapters = new List<EpubChapter>();
        readonly Dictionary<string, byte[]> resources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        const int MaxChapters = 200;
        const int MaxChapterBytes = 2 * 1024 * 1024;
        const int MaxResourceBytes = 5 * 1024 * 1024;
        const long MaxResourcesTotal = 24L * 1024 * 1024;

        public static EpubBook Load(string path)
        {
            var book = new EpubBook { SourcePath = path };
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var encryption = zip.GetEntry("META-INF/encryption.xml");
                if (encryption != null && encryption.Length > 0)
                {
                    var encrypted = Xml(zip, "META-INF/encryption.xml", 1024 * 1024);
                    if (encrypted.Descendants().Any(element => element.Name.LocalName == "EncryptedData"))
                        throw new InvalidDataException("EPUB 受 DRM/加密保护，轻页不读取加密内容。");
                }
                string opfPath = ContainerRoot(zip);
                string baseDir = DirectoryOf(opfPath);
                var opf = Xml(zip, opfPath, 4 * 1024 * 1024);
                var opfNames = opf.Root.Name.Namespace;
                string title = Text(opf.Descendants().FirstOrDefault(element => element.Name.LocalName == "title"));
                string creator = Text(opf.Descendants().FirstOrDefault(element => element.Name.LocalName == "creator"));
                book.Title = String.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title.Trim();
                book.Author = creator == null ? "" : creator.Trim();

                var manifest = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
                var manifestHrefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in opf.Descendants().Where(element => element.Name.LocalName == "item"))
                {
                    string id = (string)item.Attribute("id");
                    string href = (string)item.Attribute("href");
                    if (String.IsNullOrEmpty(id) || String.IsNullOrEmpty(href)) continue;
                    manifest[id] = item;
                    manifestHrefs[id] = Combine(baseDir, href);
                }
                var spine = opf.Descendants().FirstOrDefault(element => element.Name.LocalName == "spine");
                if (spine == null) throw new InvalidDataException("EPUB 缺少章节顺序（spine）。");
                var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                LoadNavigation(zip, opf, baseDir, titles);

                long chapterBytes = 0;

                foreach (var reference in spine.Elements().Where(element => element.Name.LocalName == "itemref"))
                {
                    if (book.Chapters.Count >= MaxChapters) break;
                    string idref = (string)reference.Attribute("idref");
                    if (String.IsNullOrEmpty(idref) || !manifestHrefs.ContainsKey(idref)) continue;
                    XElement item;
                    if (!manifest.TryGetValue(idref, out item)) continue;
                    string href = manifestHrefs[idref];
                    var entry = zip.GetEntry(href);
                    if (entry == null || entry.Length > MaxChapterBytes) continue;
                    if (chapterBytes + entry.Length > 32L * 1024 * 1024) break;
                    chapterBytes += entry.Length;
                    string xhtml = Decode(zip, entry);
                    string chapterTitle;
                    if (!titles.TryGetValue(href, out chapterTitle)) chapterTitle = ExtractTitle(xhtml);
                    book.Chapters.Add(new EpubChapter { Title = String.IsNullOrWhiteSpace(chapterTitle) ? "第 " + (book.Chapters.Count + 1) + " 章" : chapterTitle, Href = href, Xhtml = xhtml });
                }
                if (book.Chapters.Count == 0) throw new InvalidDataException("EPUB 没有可阅读的章节。");

                long total = 0;
                foreach (var pair in manifestHrefs)
                {
                    XElement item;
                    if (!manifest.TryGetValue(pair.Key, out item)) continue;
                    string media = (string)item.Attribute("media-type") ?? "";
                    if (!media.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) continue;
                    var entry = zip.GetEntry(pair.Value);
                    if (entry == null || entry.Length == 0 || entry.Length > MaxResourceBytes) continue;
                    if (total + entry.Length > MaxResourcesTotal) break;
                    total += entry.Length;
                    book.resources[pair.Value] = Read(zip, entry);
                }
            }
            return book;
        }

        public byte[] Resource(string chapterHref, string href)
        {
            if (String.IsNullOrWhiteSpace(href) || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return null;
            string full = Combine(DirectoryOf(chapterHref), href.Split('#')[0]);
            byte[] value;
            return resources.TryGetValue(full, out value) ? value : null;
        }

        public int ChapterIndex(string href, string currentHref)
        {
            if (String.IsNullOrWhiteSpace(href)) return -1;
            string full = Combine(DirectoryOf(currentHref ?? ""), href.Split('#')[0]);
            for (int index = 0; index < Chapters.Count; index++)
                if (String.Equals(Chapters[index].Href, full, StringComparison.OrdinalIgnoreCase)) return index;
            return -1;
        }

        static string ContainerRoot(ZipArchive zip)
        {
            var container = Xml(zip, "META-INF/container.xml", 1024 * 1024);
            var rootfile = container.Descendants().FirstOrDefault(element => element.Name.LocalName == "rootfile");
            string path = rootfile == null ? null : (string)rootfile.Attribute("full-path");
            if (String.IsNullOrEmpty(path) || zip.GetEntry(path) == null) throw new InvalidDataException("EPUB 缺少有效的 OPF 包文件。");
            return path;
        }

        static void LoadNavigation(ZipArchive zip, XDocument opf, string baseDir, Dictionary<string, string> titles)
        {
            var items = opf.Descendants().Where(element => element.Name.LocalName == "item").ToArray();
            var nav = items.FirstOrDefault(item => ((string)item.Attribute("properties") ?? "").Split(' ').Contains("nav"));
            var ncx = items.FirstOrDefault(item => String.Equals((string)item.Attribute("media-type"), "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase));
            XElement source = nav ?? ncx;
            if (source == null) return;
            string href = Combine(baseDir, (string)source.Attribute("href"));
            var entry = zip.GetEntry(href);
            if (entry == null || entry.Length > 2 * 1024 * 1024) return;
            try
            {
                var xml = Xml(zip, href, 2 * 1024 * 1024);
                string navBase = DirectoryOf(href);
                foreach (var link in xml.Descendants().Where(element => element.Name.LocalName == "a" || element.Name.LocalName == "content"))
                {
                    string target = (string)link.Attribute("href") ?? (string)link.Attribute("src");
                    if (String.IsNullOrEmpty(target)) continue;
                    string text = link.Name.LocalName == "a" ? link.Value : "";
                    if (link.Name.LocalName != "a" && link.Parent != null)
                    {
                        var label = link.Parent.Elements().FirstOrDefault(element => element.Name.LocalName == "navLabel" || element.Name.LocalName == "text");
                        text = label == null ? "" : label.Value;
                    }
                    string full = Combine(navBase, target);
                    if (!titles.ContainsKey(full) && !String.IsNullOrWhiteSpace(text)) titles[full] = Regex.Replace(text, "\\s+", " ").Trim();
                }
            }
            catch { }
        }

        static string ExtractTitle(string xhtml)
        {
            Match title = Regex.Match(xhtml ?? "", "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (title.Success) return Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(title.Groups[1].Value, "<[^>]+>", "")), "\\s+", " ").Trim();
            Match heading = Regex.Match(xhtml ?? "", "<h[1-2][^>]*>(.*?)</h[1-2]>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return heading.Success ? Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(heading.Groups[1].Value, "<[^>]+>", "")), "\\s+", " ").Trim() : "";
        }

        static XDocument Xml(ZipArchive zip, string name, long limit)
        {
            var entry = zip.GetEntry(name);
            if (entry == null) throw new InvalidDataException("EPUB 缺少文件：" + name);
            if (entry.Length > limit) throw new InvalidDataException("EPUB 文件超过安全大小：" + name);
            using (var stream = entry.Open())
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = limit }))
                return XDocument.Load(reader);
        }

        static string Decode(ZipArchive zip, ZipArchiveEntry entry)
        {
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                return reader.ReadToEnd();
        }

        static byte[] Read(ZipArchive zip, ZipArchiveEntry entry)
        {
            using (var stream = entry.Open())
            using (var memory = new MemoryStream())
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) memory.Write(buffer, 0, read);
                return memory.ToArray();
            }
        }

        static string Text(XElement element)
        {
            return element == null ? "" : Regex.Replace(element.Value ?? "", "\\s+", " ").Trim();
        }

        static string DirectoryOf(string path)
        {
            string value = (path ?? "").Replace('\\', '/');
            int split = value.LastIndexOf('/');
            return split < 0 ? "" : value.Substring(0, split);
        }

        static string Combine(string baseDir, string href)
        {
            string value = Uri.UnescapeDataString((href ?? "").Replace('\\', '/')).Trim();
            if (value.Length == 0) return baseDir ?? "";
            if (value.StartsWith("/", StringComparison.Ordinal)) value = value.TrimStart('/');
            var parts = new List<string>();
            foreach (string part in ((baseDir ?? "") + "/" + value).Split('/'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else parts.Add(part);
            }
            return String.Join("/", parts);
        }
    }
}
