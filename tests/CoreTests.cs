using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using Qingye.Core;
using System.IO.Compression;

namespace Qingye.Tests
{
    public static partial class CoreTests
    {
        static int passed, failed;
        static string root;
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
        static string Fixture(string name, byte[] bytes) { string path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path; }
        static string Archive(string name,IDictionary<string,string> entries)
        {
            string path=Path.Combine(root,name);using(var stream=File.Create(path))using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))foreach(var pair in entries){var entry=zip.CreateEntry(pair.Key);using(var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false)))writer.Write(pair.Value);}return path;
        }
        static void ZipText(ZipArchive zip,string name,string text){var entry=zip.CreateEntry(name);using(var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false)))writer.Write(text);}
        static void ZipBytes(ZipArchive zip,string name,byte[] bytes){var entry=zip.CreateEntry(name);using(var stream=entry.Open())stream.Write(bytes,0,bytes.Length);}
        static string EpubFixture(string name,bool encrypted)
        {
            string path=Path.Combine(root,name);
            using(var stream=File.Create(path))using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
            {
                ZipText(zip,"mimetype","application/epub+zip");
                ZipText(zip,"META-INF/container.xml","<?xml version='1.0'?><container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='OEBPS/content.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
                ZipText(zip,"OEBPS/content.opf","<?xml version='1.0'?><package xmlns='http://www.idpf.org/2007/opf' xmlns:dc='http://purl.org/dc/elements/1.1/' version='3.0'><metadata><dc:title>测试书</dc:title><dc:creator>作者</dc:creator></metadata><manifest><item id='c1' href='text/ch1.xhtml' media-type='application/xhtml+xml'/><item id='c2' href='text/ch2.xhtml' media-type='application/xhtml+xml'/><item id='img' href='img/p.png' media-type='image/png'/></manifest><spine><itemref idref='c1'/><itemref idref='c2'/></spine></package>");
                ZipText(zip,"OEBPS/text/ch1.xhtml","<html xmlns='http://www.w3.org/1999/xhtml'><head><title>第一章</title></head><body><h1>开始</h1><p>正文<strong>重点</strong></p><img src='../img/p.png'/></body></html>");
                ZipText(zip,"OEBPS/text/ch2.xhtml","<html xmlns='http://www.w3.org/1999/xhtml'><body><h1>第二章</h1><p>更多</p></body></html>");
                ZipBytes(zip,"OEBPS/img/p.png",Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
                if(encrypted)ZipText(zip,"META-INF/encryption.xml","<?xml version='1.0'?><encryption xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><EncryptedData xmlns='http://www.w3.org/2001/04/xmlenc#'><CipherData><CipherReference URI='OEBPS/text/ch1.xhtml'/></CipherData></EncryptedData></encryption>");
            }
            return path;
        }
        static void Roundtrip(string name, Encoding encoding, bool bom)
        {
            byte[] body = encoding.GetBytes("中文，轻页\r\nfirst\nlast\r終\t😀");
            byte[] prefix = bom ? encoding.GetPreamble() : new byte[0];
            byte[] original = prefix.Concat(body).ToArray();
            string path = Fixture(name, original);
            Document doc = FileService.Read(path);
            Check(doc.Text == "中文，轻页\r\nfirst\nlast\r終\t😀", "Decoded text changed");
            FileService.Save(doc, path, false);
            Check(original.SequenceEqual(File.ReadAllBytes(path)), "Encoding, BOM or newlines changed");
        }
        static Dictionary<string,string> PresentationEntries(string target="slides/slide1.xml",string mode="")
        {
            return new Dictionary<string,string>{
                {"ppt/presentation.xml","<p:presentation xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><p:sldIdLst><p:sldId id='256' r:id='r1'/></p:sldIdLst></p:presentation>"},
                {"ppt/_rels/presentation.xml.rels","<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide' Target='"+target+"' "+mode+"/></Relationships>"},
                {"ppt/slides/slide1.xml","<p:sld xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><p:cSld><p:spTree><p:sp><p:nvSpPr><p:nvPr><p:ph type='title'/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>演示标题</a:t></a:r></a:p></p:txBody></p:sp><p:sp><p:txBody><a:p><a:r><a:t>正文内容</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>"}
            };
        }
        static string PresentationText(PresentationContent content)
        { return String.Join("\n",content.Body.Descendants().Where(element=>element.Name.LocalName=="t").Select(element=>element.Value)); }
        static void RejectPresentation(string name,Dictionary<string,string> entries)
        {
            bool rejected=false;
            try { ContentReaders.ReadPptx(Archive(name,entries)); }
            catch(InvalidDataException) { rejected=true; }
            catch(System.Xml.XmlException) { rejected=true; }
            Check(rejected,"Unsafe or invalid PPTX was accepted: "+name);
        }
        static void BoundaryRoundtrip(string name,Encoding encoding,int position,string value,bool bom)
        {
            string path=Path.Combine(root,name);using(var file=File.Create(path)){long length=129L*1024*1024;char[] letters=new string('a',65536).ToCharArray();for(int i=127;i<letters.Length;i+=128)letters[i]='\n';byte[] fill=encoding.GetBytes(letters);while(file.Position<length)file.Write(fill,0,(int)Math.Min(fill.Length,length-file.Position));if(bom){file.Position=0;var prefix=encoding.GetPreamble();file.Write(prefix,0,prefix.Length);}file.Position=position;byte[] bytes=encoding.GetBytes(value);file.Write(bytes,0,bytes.Length);}
            Document first=FileService.Read(path,0,encoding.WebName),second=FileService.Read(path,1,encoding.WebName);Check(first.Kind==DocumentKind.PagedText && second.Kind==DocumentKind.PagedText,"Paging lost text mode");Check((first.Text+second.Text).Split(new[]{value},StringSplitOptions.None).Length==2,"Boundary character missing or duplicated");
        }
        public static int Main()
        {
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {

                Test("Failed GB18030 index builds do not accumulate metadata",delegate{
                    for(int attempt=0;attempt<12;attempt++)
                    {
                        string file=Fixture("locked-"+attempt+".txt",Encoding.GetEncoding("gb18030").GetBytes("中文\n"));
                        using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                        {
                            bool rejected=false;try{Gb18030PageIndex.Before(file,0,1024);}catch(IOException){rejected=true;}
                            Check(rejected,"Exclusive fixture did not fail index construction");
                        }
                    }
                    Check(Gb18030PageIndex.CachedEntryCount<=4,"Failed builds leaked unbounded cache metadata");
                });
                Test("PPTX opens as a readonly type with slide text",delegate{
                    string file=Archive("presentation.pptx",PresentationEntries());byte[] before=File.ReadAllBytes(file);
                    var doc=FileService.Read(file);
                    Check(doc.Kind.ToString()=="Pptx" && doc.IsReadOnly && FilePresentation.For(doc).PreferReader,"PPTX has no native readonly reader");
                    Check(doc.Text.Contains("演示标题") && doc.Text.Contains("正文内容") && before.SequenceEqual(File.ReadAllBytes(file)),"PPTX content missing or archive changed");
                });
                Test("PPTX slide order follows presentation relationships",delegate{
                    var entries=PresentationEntries();
                    entries["ppt/presentation.xml"]=entries["ppt/presentation.xml"].Replace("<p:sldId id='256' r:id='r1'/>","<p:sldId id='257' r:id='r2'/><p:sldId id='256' r:id='r1'/>");
                    entries["ppt/_rels/presentation.xml.rels"]=entries["ppt/_rels/presentation.xml.rels"].Replace("</Relationships>","<Relationship Id='r2' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide' Target='/ppt/slides/slide2.xml'/></Relationships>");
                    entries["ppt/slides/slide2.xml"]=entries["ppt/slides/slide1.xml"].Replace("演示标题","先显示的标题");
                    var result=ContentReaders.ReadPptx(Archive("order.pptx",entries));string text=PresentationText(result);
                    Check(result.SlideCount==2 && text.IndexOf("先显示的标题")<text.IndexOf("演示标题"),"ZIP filename order replaced presentation order");
                });
                Test("PPTX supports strict OOXML namespaces",delegate{
                    var entries=PresentationEntries();foreach(string key in entries.Keys.ToArray())entries[key]=entries[key].Replace("http://schemas.openxmlformats.org/presentationml/2006/main","http://purl.oclc.org/ooxml/presentationml/main").Replace("http://schemas.openxmlformats.org/drawingml/2006/main","http://purl.oclc.org/ooxml/drawingml/main").Replace("http://schemas.openxmlformats.org/officeDocument/2006/relationships","http://purl.oclc.org/ooxml/officeDocument/relationships");
                    Check(PresentationText(ContentReaders.ReadPptx(Archive("strict.pptx",entries))).Contains("正文内容"),"Strict slides opened blank");
                });
                Test("PPTX tables and run formatting retain reading semantics",delegate{
                    var entries=PresentationEntries();
                    entries["ppt/slides/slide1.xml"]=entries["ppt/slides/slide1.xml"].Replace("</p:spTree>","<p:graphicFrame><a:tbl><a:tr><a:tc><a:txBody><a:p><a:r><a:rPr b='1' i='1'/><a:t>表格单元</a:t></a:r><a:br/><a:r><a:t>第二行</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl></p:graphicFrame></p:spTree>");
                    var result=ContentReaders.ReadPptx(Archive("table.pptx",entries));
                    Check(result.Body.Descendants().Count(element=>element.Name.LocalName=="tbl")==1 && PresentationText(result).Contains("表格单元"),"Table structure or text flattened");
                    Check(result.Body.Descendants().Any(element=>element.Name.LocalName=="b") && result.Body.Descendants().Any(element=>element.Name.LocalName=="i") && result.Body.Descendants().Any(element=>element.Name.LocalName=="br"),"Run style or line break lost");
                });
                Test("PPTX rejects missing and duplicate slide relationships",delegate{
                    var entries=PresentationEntries();entries.Remove("ppt/_rels/presentation.xml.rels");RejectPresentation("missing-rel.pptx",entries);
                    entries=PresentationEntries();entries["ppt/_rels/presentation.xml.rels"]=entries["ppt/_rels/presentation.xml.rels"].Replace("</Relationships>","<Relationship Id='r1'/></Relationships>");RejectPresentation("duplicate-rel.pptx",entries);
                });
                Test("PPTX rejects traversal and external slide paths",delegate{
                    foreach(string target in new[]{"../outside.xml","%2e%2e/outside.xml","https://example.com/slide.xml","//example.com/slide.xml","slides\\slide1.xml","slides/slide1.xml#fragment"})RejectPresentation("bad-path.pptx",PresentationEntries(target));
                    RejectPresentation("external.pptx",PresentationEntries("slides/slide1.xml","TargetMode='External'"));
                });
                Test("PPTX prohibits DTD in slides",delegate{
                    var entries=PresentationEntries();entries["ppt/slides/slide1.xml"]="<!DOCTYPE sld [<!ENTITY text 'expanded'>]>"+entries["ppt/slides/slide1.xml"].Replace("正文内容","&text;");RejectPresentation("dtd.pptx",entries);
                });
                Test("PPTX preserves Office XML entry and package byte limits",delegate{
                    var entries=PresentationEntries();entries["ppt/slides/slide1.xml"]="<x>"+new string('a',16*1024*1024)+"</x>";RejectPresentation("oversize.pptx",entries);
                    entries=PresentationEntries();string list="",relations="",slide=entries["ppt/slides/slide1.xml"];
                    for(int page=1;page<=5;page++){list+="<p:sldId id='"+(255+page)+"' r:id='r"+page+"'/>";relations+="<Relationship Id='r"+page+"' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide' Target='slides/slide"+page+".xml'/>";entries["ppt/slides/slide"+page+".xml"]=slide.Replace("</p:cSld>","<!--"+new string('a',13*1024*1024)+"--></p:cSld>");}
                    entries["ppt/presentation.xml"]=entries["ppt/presentation.xml"].Replace("<p:sldId id='256' r:id='r1'/>",list);
                    entries["ppt/_rels/presentation.xml.rels"]="<Relationships>"+relations+"</Relationships>";
                    RejectPresentation("package-budget.pptx",entries);
                });
                Test("PPTX bounds characters across slide content",delegate{
                    var entries=PresentationEntries();entries["ppt/slides/slide1.xml"]=entries["ppt/slides/slide1.xml"].Replace("正文内容",new string('字',600*1024));
                    var result=ContentReaders.ReadPptx(Archive("characters.pptx",entries));
                    Check(result.Truncated && PresentationText(result).Length<513*1024 && result.Notice.Contains("预算"),"PPTX bypassed the shared text budget");
                });
                Test("PPTX bounds slide count without materializing all slides",delegate{
                    var entries=PresentationEntries();entries["ppt/presentation.xml"]=entries["ppt/presentation.xml"].Replace("<p:sldId id='256' r:id='r1'/>",String.Concat(Enumerable.Range(0,120).Select(index=>"<p:sldId id='"+(256+index)+"' r:id='r1'/>")));
                    var result=ContentReaders.ReadPptx(Archive("many-slides.pptx",entries));Check(result.SlideCount==100 && result.Truncated,"PPTX slide count is unbounded");
                });
                Test("PPTX bounds inlines and blocks before native rendering",delegate{
                    var entries=PresentationEntries();entries["ppt/slides/slide1.xml"]=entries["ppt/slides/slide1.xml"].Replace("<a:t>正文内容</a:t>","<a:t>正文内容</a:t>"+String.Concat(Enumerable.Repeat("<a:br/>",22000)));
                    var result=ContentReaders.ReadPptx(Archive("many-inlines.pptx",entries));Check(result.Truncated && result.Body.Descendants().Count(element=>element.Name.LocalName=="br")<20000,"Single paragraph bypassed inline budget");
                    entries=PresentationEntries();entries["ppt/slides/slide1.xml"]=entries["ppt/slides/slide1.xml"].Replace("<a:p><a:r><a:t>正文内容</a:t></a:r></a:p>",String.Concat(Enumerable.Repeat("<a:p><a:r><a:t>段落</a:t></a:r></a:p>",1600)));
                    result=ContentReaders.ReadPptx(Archive("many-blocks.pptx",entries));Check(result.Truncated && result.Body.Elements().Count()<=1501,"Slide bypassed block budget");
                });
                Test("Lazy text snapshots bound reads and save complete content",delegate{
                    var doc=new Document();doc.SetLoadedText("baseline");doc.Modified=true;
                    string snapshot="latest snapshot\nfull content";int largestRead=0;
                    long revision=doc.ContentRevision;
                    doc.SetTextSource(snapshot.Length,length=>{largestRead=Math.Max(largestRead,length);return snapshot.Substring(0,length);});
                    Check(doc.ReadText(6)=="latest" && largestRead==6 && doc.TextLength==snapshot.Length,"Bounded read materialized full text");
                    Check(doc.Modified && doc.ContentRevision>revision,"Snapshot lost dirty state or revision");
                    string file=Path.Combine(root,"snapshot.txt");FileService.Save(doc,file,false);
                    Check(File.ReadAllText(file)==snapshot && !doc.IsDirty,"Save omitted lazy snapshot content");
                    doc.SetLoadedText("reloaded");Check(doc.Text=="reloaded" && doc.ReadText(3)=="rel","Reload retained a stale source");
                });
                Test("UTF8 no BOM roundtrip", delegate { Roundtrip("utf8.txt", new UTF8Encoding(false,true), false); });
                Test("UTF8 BOM roundtrip", delegate { Roundtrip("bom.txt", new UTF8Encoding(true,true), true); });
                Test("UTF16 LE roundtrip", delegate { Roundtrip("le.txt", new UnicodeEncoding(false,true,true), true); });
                Test("UTF16 BE roundtrip", delegate { Roundtrip("be.txt", new UnicodeEncoding(true,true,true), true); });
                Test("GB18030 roundtrip", delegate { Roundtrip("gb.txt", Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), false); });
                Test("External write conflict retains content", delegate {
                    string path = Fixture("conflict.txt", Encoding.UTF8.GetBytes("initial")); var doc = FileService.Read(path); doc.Text = "mine"; File.WriteAllText(path,"theirs");
                    bool conflict = false; try { FileService.Save(doc,path,false); } catch (FileConflictException) { conflict = true; }
                    Check(conflict && File.ReadAllText(path)=="theirs" && doc.Text=="mine" && doc.IsDirty,"External content silently overwritten");
                });
                Test("Deleted file is not recreated by ordinary save", delegate {
                    string path = Fixture("deleted.txt", Encoding.UTF8.GetBytes("initial")); var doc = FileService.Read(path); doc.Text="edited"; File.Delete(path);
                    bool conflict=false; try { FileService.Save(doc,path,false); } catch (FileConflictException) { conflict=true; }
                    Check(conflict && !File.Exists(path) && doc.IsDirty,"Deleted file recreated silently");
                });
                Test("Explicit overwrite handles external conflict", delegate {
                    string path = Fixture("forced.txt",Encoding.UTF8.GetBytes("old"));var doc=FileService.Read(path);doc.Text="new";File.WriteAllText(path,"other");FileService.Save(doc,path,true);
                    Check(File.ReadAllText(path)=="new" && !doc.IsDirty,"Explicit save did not finish");
                });
                Test("Failed save preserves buffer and original", delegate {
                    string path=Fixture("failed.txt",Encoding.UTF8.GetBytes("old"));var doc=FileService.Read(path);doc.Text="new";bool error=false;
                    try { FileService.Save(doc,Path.Combine(root,"missing","target.txt"),false); } catch (IOException) { error=true; }
                    Check(error && File.ReadAllText(path)=="old" && doc.IsDirty && doc.Text=="new","Failed save lost content");
                });
                Test("Unknown text extension remains editable", delegate { var d=FileService.Read(Fixture("custom.weird",Encoding.UTF8.GetBytes("hello 中文")));Check(d.Kind==DocumentKind.Text && !d.IsReadOnly,"Unknown text blocked"); });
                Test("NUL binary is read only and cannot save", delegate {
                    var d=FileService.Read(Fixture("binary.dat",new byte[]{0,1,2,3,255,0}));Check(d.IsReadOnly && d.Kind==DocumentKind.Binary,"Binary exposed as editable");
                    bool blocked=false;try { FileService.Save(d,d.Path,true); }catch(InvalidOperationException){blocked=true;}Check(blocked,"Binary save allowed");
                });
                Test("Empty file remains editable", delegate { var d=FileService.Read(Fixture("empty",new byte[0]));Check(d.Text=="" && !d.IsReadOnly,"Empty file rejected"); });
                Test("Single-file open replaces and import appends", delegate {
                    var s=new DocumentSession();s.Open(new Document{Title="a"},false);s.Open(new Document{Title="b"},false);Check(s.Documents.Count==1 && s.Documents[0].Title=="b","Default open retained tabs");
                    s.Open(new Document{Title="c"},true);Check(s.Documents.Count==2,"Import replaced current file");
                });
                Test("Dirty replace requires a close decision", delegate {
                    var s=new DocumentSession();var d=new Document();d.Text="unsaved";s.Open(d,true);bool blocked=false;try{s.Open(new Document(),false);}catch(InvalidOperationException){blocked=true;}
                    Check(blocked && s.Documents.Count==1 && s.Documents[0]==d,"Dirty document silently removed");
                });
                Test("Cancel close retains all dirty documents", delegate {
                    var s=new DocumentSession();var a=new Document();a.Text="a";var b=new Document();b.Text="b";s.Open(a,true);s.Open(b,true);int prompts=0;
                    bool closed=s.TryClose(s.Documents.ToArray(),delegate(IList<Document> dirty){prompts++;Check(dirty.Count==2,"Prompt omitted dirty file");return CloseChoice.Cancel;});
                    Check(!closed && prompts==1 && s.Documents.Count==2,"Cancel lost session");
                });
                Test("Discard closes once and clears references", delegate {
                    var s=new DocumentSession();var d=new Document();d.Text="a";s.Open(d,true);Check(s.TryClose(s.Documents.ToArray(),delegate{return CloseChoice.Discard;}) && s.Documents.Count==0,"Discard retained closed docs");
                });
                Test("Clean close needs no dialog", delegate {
                    var s=new DocumentSession();s.Open(new Document(),true);Check(s.TryClose(s.Documents.ToArray(),delegate{throw new Exception("Unexpected prompt");}),"Clean close blocked");
                });
                Test("Saving decision cannot discard unsaved state", delegate {
                    var s=new DocumentSession();var d=new Document();d.Text="a";s.Open(d,true);Check(!s.TryClose(s.Documents.ToArray(),delegate{return CloseChoice.Save;}) && s.Documents.Count==1,"Unfinished save closed dirty buffer");
                });
                Test("Default settings write no file", delegate { var settings=new Settings();string p=Path.Combine(root,"preferences.xml");settings.Save(p);Check(!File.Exists(p),"Default settings left a file"); });
                Test("Opt-in settings contain appearance only", delegate {
                    var settings=new Settings();settings.Theme="lilac";settings.RememberAppearance=true;string p=Path.Combine(root,"appearance.xml");settings.Save(p);string saved=File.ReadAllText(p);
                    Check(!saved.Contains(root) && !saved.Contains("Documents") && !saved.Contains("Workspace"),"Session paths persisted");var loaded=Settings.Load(p);Check(loaded.Theme=="lilac" && loaded.RememberAppearance,"Appearance was not retained");
                });
                Test("Opt-out removes old preferences", delegate {
                    string p=Path.Combine(root,"optout.xml");File.WriteAllText(p,"old");new Settings().Save(p);Check(!File.Exists(p),"Opt-out retained preference file");
                });
                Test("Corrupt preferences safely reset", delegate { string p=Fixture("corrupt.xml",Encoding.UTF8.GetBytes("<invalid>"));Check(Settings.Load(p).Theme=="mint","Corrupt preference crash"); });
                Test("Root boundary rejects siblings and traversal", delegate {
                    string allowed=Path.Combine(root,"site");Check(FileService.IsInsideRoot(allowed,Path.Combine(allowed,"css","a.css")),"Valid asset rejected");
                    Check(!FileService.IsInsideRoot(allowed,Path.Combine(root,"site-secret","x")) && !FileService.IsInsideRoot(allowed,Path.Combine(allowed,"..","x")),"Directory traversal accepted");
                });
                Test("Explicit newline conversion leaves characters intact", delegate { Check(FileService.NormalizeNewLines("a\r\nb\rc\n", "\n")=="a\nb\nc\n","Newline conversion damaged text"); });
                Test("Huge text opens as bounded readonly page", delegate {
                    string p=Path.Combine(root,"huge.log");byte[] block=Enumerable.Repeat((byte)65,1024*1024).ToArray();using(var f=File.Create(p)){for(int i=0;i<129;i++)f.Write(block,0,block.Length);}
                    var d=FileService.Read(p,1);Check(d.Kind==DocumentKind.PagedText && d.IsReadOnly && d.Text.Length<=FileService.PageBytes && d.PageCount>1,"Huge text loaded unbounded");
                });
                Test("Moderate large text uses a smaller bounded reading page",delegate{string path=Path.Combine(root,"33mb.log");byte[] block=Encoding.UTF8.GetBytes(String.Concat(Enumerable.Repeat(new string('a',127)+"\n",8192)));using(var file=File.Create(path))for(int i=0;i<33;i++)file.Write(block,0,block.Length);var doc=FileService.Read(path);Check(doc.Kind==DocumentKind.PagedText && doc.Text.Length<=1024*1024 && doc.IsReadOnly,"Large text still loads and retains the entire file");});
                Test("Minified long lines are read in small segments with intact Unicode",delegate{byte[] text=Enumerable.Repeat((byte)'a',2*1024*1024).ToArray();byte[] chinese=Encoding.UTF8.GetBytes("中");Array.Copy(chinese,0,text,65535,chinese.Length);string path=Fixture("minified.json",text);var first=FileService.Read(path,0);var second=FileService.Read(path,1);Check(first.Kind==DocumentKind.PagedText && first.Text.Length<=65536,"Minified text still causes unbounded line layout");Check((first.Text+second.Text).Split(new[]{"中"},StringSplitOptions.None).Length==2,"Small segment dropped boundary Unicode");});
                Test("A long line after an ordinary header is still safely segmented",delegate{string header=String.Concat(Enumerable.Repeat("ordinary header\n",5000));string path=Fixture("late-line.js",Encoding.UTF8.GetBytes(header+new string('a',1024*1024)));var doc=FileService.Read(path);Check(doc.Kind==DocumentKind.PagedText && doc.PageByteSize==65536 && doc.Text.Length<=65536,"Later long line still reaches the UI as one large line");});
                Test("Paged UTF8 preserves a boundary character",delegate{BoundaryRoundtrip("utf8-boundary.log",new UTF8Encoding(false,true),FileService.PageBytes-1,"中",false);});
                Test("Paged UTF16 preserves a boundary surrogate pair",delegate{BoundaryRoundtrip("utf16-boundary.log",new UnicodeEncoding(false,true,true),FileService.PageBytes-2,"😀",true);});
                Test("Paged GB18030 preserves a boundary character",delegate{BoundaryRoundtrip("gb-boundary.log",Encoding.GetEncoding("gb18030"),FileService.PageBytes-1,"中",false);});
                Test("Paged UTF32 preserves scalars in both byte orders",delegate{
                    BoundaryRoundtrip("utf32-le.log",new UTF32Encoding(false,true,true),FileService.PageBytes-4,"😀",true);
                    BoundaryRoundtrip("utf32-be.log",new UTF32Encoding(true,true,true),FileService.PageBytes-4,"😀",true);
                });
                Test("GB18030 last-page latency stays below twice first-page latency",delegate{
                    string path=Path.Combine(root,"gb18030-129mb.log");
                    byte[] block=Encoding.GetEncoding("gb18030").GetBytes(String.Concat(Enumerable.Repeat("日志 中文😀 123456789\n",2048)));
                    const long target=129L*1024*1024;
                    using(var file=File.Create(path))
                    {
                        while(file.Position+block.Length<=target)file.Write(block,0,block.Length);
                        while(file.Position<target)file.WriteByte((file.Position%128)==127?(byte)'\n':(byte)'a');
                    }
                    var initialWatch=System.Diagnostics.Stopwatch.StartNew();
                    var first=FileService.Read(path,0,"gb18030");
                    Console.WriteLine("MEASURE GB18030 initial open/index="+initialWatch.Elapsed.TotalMilliseconds.ToString("F2")+" ms");
                    var firstTimes=new List<double>();
                    var lastTimes=new List<double>();
                    for(int sample=0;sample<7;sample++)
                    {
                        var watch=System.Diagnostics.Stopwatch.StartNew();
                        FileService.Read(path,0,"gb18030");
                        firstTimes.Add(watch.Elapsed.TotalMilliseconds);
                        watch.Restart();
                        FileService.Read(path,first.PageCount-1,"gb18030");
                        lastTimes.Add(watch.Elapsed.TotalMilliseconds);
                    }
                    double firstMedian=firstTimes.OrderBy(value=>value).ElementAt(3);
                    double lastMedian=lastTimes.OrderBy(value=>value).ElementAt(3);
                    Console.WriteLine("MEASURE GB18030 129 MiB first="+firstMedian.ToString("F2")+" ms last="+lastMedian.ToString("F2")+" ms ratio="+(lastMedian/firstMedian).ToString("F2"));
                    Check(lastMedian<2*firstMedian,"Last page still replays the file prefix");
                });
                Test("CSV quotes and embedded newlines remain in cells",delegate{var t=ContentReaders.ParseTable("name,note\r\n\"张三\",\"hello,\"\"world\"\"\nnext\"\r\n",',');Check(t.Rows.Count==2 && t.Rows[1][0]=="张三" && t.Rows[1][1]=="hello,\"world\"\nnext","Quoted CSV broken");});
                Test("CSV viewer truncates without reading all rows",delegate{var t=ContentReaders.ParseTable(String.Join("\n",Enumerable.Repeat("a,b",700)),',');Check(t.Rows.Count==500 && t.Truncated,"Table preview unbounded");});
                Test("DOCX extracts text without changing archive",delegate{string p=Archive("sample.docx",new Dictionary<string,string>{{"word/document.xml","<w:document xmlns:w='urn:word'><w:body><w:p><w:r><w:t>中文</w:t></w:r><w:r><w:t> reader</w:t></w:r></w:p><w:p><w:r><w:t>next</w:t></w:r></w:p></w:body></w:document>"}});byte[] before=File.ReadAllBytes(p);string result=ContentReaders.ReadDocx(p);Check(result.Contains("中文 reader") && result.Contains("next") && before.SequenceEqual(File.ReadAllBytes(p)),"Office text lost or file changed");});
                Test("DOCX refuses external entity expansion",delegate{string p=Archive("xxe.docx",new Dictionary<string,string>{{"word/document.xml","<!DOCTYPE document [<!ENTITY stolen SYSTEM 'file:///C:/Windows/win.ini'>]><document><t>&stolen;</t></document>"}});bool blocked=false;try{ContentReaders.ReadDocx(p);}catch(System.Xml.XmlException){blocked=true;}Check(blocked,"External entity expanded");});
                Test("DOCX refuses oversized XML entry",delegate{string p=Archive("large.docx",new Dictionary<string,string>{{"word/document.xml","<document>"+new string('a',16*1024*1024)+"</document>"}});bool blocked=false;try{ContentReaders.ReadDocx(p);}catch(InvalidDataException){blocked=true;}Check(blocked,"Oversized document parsed");});
                Test("XLSX resolves shared strings and inline strings",delegate{
                    string p=Archive("sheet.xlsx",new Dictionary<string,string>{{"xl/workbook.xml","<workbook xmlns:r='urn:rels'><sheets><sheet name='数据' r:id='r1'/></sheets></workbook>"},{"xl/_rels/workbook.xml.rels","<Relationships><Relationship Id='r1' Target='worksheets/sheet1.xml'/></Relationships>"},{"xl/sharedStrings.xml","<sst><si><t>中文</t></si></sst>"},{"xl/worksheets/sheet1.xml","<worksheet><sheetData><row r='1'><c r='A1' t='s'><v>0</v></c><c r='C1' t='inlineStr'><is><t>hi</t></is></c></row><row r='2'><c r='B2'><v>42</v></c></row></sheetData></worksheet>"}});
                    var t=ContentReaders.ReadXlsx(p);Check(ContentReaders.SheetNames(p)[0]=="数据" && t.Rows[0][0]=="中文" && t.Rows[0][2]=="hi" && t.Rows[1][1]=="42","Sheet cell positions changed");
                });
                Test("XLSX displays built-in dates, percentages, booleans and missing formula caches",delegate{
                    string p=Archive("formatted.xlsx",new Dictionary<string,string>{{"xl/workbook.xml","<workbook xmlns:r='urn:rels'><sheets><sheet name='格式' r:id='r1'/></sheets></workbook>"},{"xl/_rels/workbook.xml.rels","<Relationships><Relationship Id='r1' Target='worksheets/sheet1.xml'/></Relationships>"},{"xl/styles.xml","<styleSheet><cellXfs><xf numFmtId='0'/><xf numFmtId='14'/><xf numFmtId='10'/></cellXfs></styleSheet>"},{"xl/worksheets/sheet1.xml","<worksheet><sheetData><row r='1'><c r='A1' s='1'><v>45292</v></c><c r='B1' s='2'><v>0.125</v></c><c r='C1' t='b'><v>1</v></c><c r='D1'><f>A2+B2</f></c><c r='E1'><v>9007199254740993</v></c></row></sheetData></worksheet>"}});var t=ContentReaders.ReadXlsx(p);Check(t.Rows[0].SequenceEqual(new[]{"2024-01-01","12.50%","TRUE","[公式无缓存值]","9007199254740993"}),"Cell formats, cache availability or precision lost");
                });
                Test("Outline follows Markdown headings and setext lines",delegate{
                    var doc=new Document{Title="outline.md",Path=Path.Combine(root,"outline.md")};doc.SetLoadedText("# 标题\n\n正文\n\n## 小节\n\n另一个\n===\n");
                    var items=Outline.Build(doc);Check(items.Count==3 && items[0].Title=="标题" && items[0].Level==1 && items[1].Title=="小节" && items[1].Level==2 && items[2].Title=="另一个" && items[2].Level==1,"Markdown outline lost headings");
                });
                Test("Outline lists JSON and XML structure",delegate{
                    var json=new Document{Title="data.json",Path=Path.Combine(root,"data.json")};json.SetLoadedText("{\n  \"alpha\": 1,\n  \"nested\": {\n    \"beta\": 2\n  }\n}\n");
                    var items=Outline.Build(json);Check(items.Count==3 && items[0].Title=="alpha" && items[0].Level==1 && items[1].Title=="nested" && items[2].Title=="beta" && items[2].Level==2,"JSON outline lost keys");
                    var xml=new Document{Title="data.xml",Path=Path.Combine(root,"data.xml")};xml.SetLoadedText("<root><item id=\"7\"><name>x</name></item></root>");
                    var nodes=Outline.Build(xml);Check(nodes.Count==3 && nodes[0].Title=="<root>" && nodes[1].Title=="<item> #7" && nodes[1].Level==2,"XML outline lost elements");
                });
                Test("Outline recognizes prose chapters and configuration keys",delegate{
                    var prose=new Document{Title="book.txt",Path=Path.Combine(root,"book.txt")};prose.SetLoadedText("前言\n\n第一章 开始\n正文\n第一节 细节\n更多\n");
                    var items=Outline.Build(prose);Check(items.Count==3 && items[0].Title=="前言" && items[1].Title=="第一章 开始" && items[2].Level==2,"Prose outline missed chapters");
                    var config=new Document{Title="app.yaml",Path=Path.Combine(root,"app.yaml")};config.SetLoadedText("name: qingye\nserver:\n  port: 8080\n");
                    var keys=Outline.Build(config);Check(keys.Count==3 && keys[0].Title=="name" && keys[1].Title=="server" && keys[2].Title=="port" && keys[2].Level==2,"Configuration outline missed keys");
                });
                Test("EPUB reads chapters, metadata and bounded images",delegate{
                    var book=EpubBook.Load(EpubFixture("book.epub",false));
                    Check(book.Title=="测试书" && book.Author=="作者" && book.Chapters.Count==2,"EPUB metadata or spine was lost");
                    Check(book.Chapters[0].Title=="第一章" && book.Chapters[1].Title=="第二章","EPUB chapter titles were lost");
                    Check(book.Chapters[0].Xhtml.Contains("正文") && book.Chapters[0].Xhtml.Contains("重点"),"EPUB chapter content was lost");
                    Check(book.Resource(book.Chapters[0].Href,"../img/p.png")!=null,"EPUB image resource was lost");
                    Check(book.ChapterIndex("ch2.xhtml",book.Chapters[0].Href)==1,"EPUB internal chapter link was not resolved");
                });
                Test("EPUB refuses encrypted content",delegate{
                    bool blocked=false;try{EpubBook.Load(EpubFixture("encrypted.epub",true));}catch(InvalidDataException){blocked=true;}Check(blocked,"Encrypted EPUB was accepted");
                });
                Test("Workspace search finds text across files and skips binary",delegate{
                    string searchRoot=Path.Combine(root,"search-root");Directory.CreateDirectory(searchRoot);Directory.CreateDirectory(Path.Combine(searchRoot,"sub"));
                    File.WriteAllText(Path.Combine(searchRoot,"a.txt"),"hello\nworld",Encoding.UTF8);File.WriteAllText(Path.Combine(searchRoot,"sub","b.md"),"HELLO again",Encoding.UTF8);File.WriteAllBytes(Path.Combine(searchRoot,"binary.bin"),new byte[]{0,1,2,3});
                    var hits=WorkspaceSearch.Search(searchRoot,"hello",50,10,CancellationToken.None);
                    Check(hits.Count==2 && hits.Any(hit=>hit.RelativePath=="a.txt" && hit.Line==1) && hits.Any(hit=>hit.RelativePath.EndsWith("b.md",StringComparison.OrdinalIgnoreCase)),"Workspace search missed a text hit");
                    Check(WorkspaceSearch.Search(searchRoot,"hello",50,1,CancellationToken.None).Count==1,"Workspace search ignored the result limit");
                    Check(!WorkspaceSearch.Search(searchRoot,"hello",50,10,CancellationToken.None).Any(hit=>hit.RelativePath=="binary.bin"),"Workspace search read a binary file");
                });
                Test("Quick open scan stays bounded and supports fuzzy names",delegate{
                    string folder=Path.Combine(root,"quick-open");Directory.CreateDirectory(folder);
                    Directory.CreateDirectory(Path.Combine(folder,"sub"));Directory.CreateDirectory(Path.Combine(folder,"node_modules"));
                    File.WriteAllText(Path.Combine(folder,"root.txt"),"root");File.WriteAllText(Path.Combine(folder,"sub","ProjectNotes.md"),"notes");File.WriteAllText(Path.Combine(folder,"node_modules","hidden.txt"),"ignored");
                    var files=QuickOpenFiles.Collect(folder,20,CancellationToken.None);
                    Check(files.Count==2 && files.Any(path=>path.EndsWith("ProjectNotes.md")) && !files.Any(path=>path.EndsWith("hidden.txt")),"Quick open scan missed files or visited excluded directories");
                    Check(QuickOpenFiles.Collect(folder,1,CancellationToken.None).Count==1,"Quick open ignored its file limit");
                    Check(QuickOpenFiles.Matches("ProjectNotes.md","x","pnt") && QuickOpenFiles.Matches("ProjectNotes.md","x\\notes","NOTES") && !QuickOpenFiles.Matches("ProjectNotes.md","x","zz"),"Quick open matching failed");
                    var cancellation=new CancellationTokenSource();cancellation.Cancel();bool canceled=false;try{QuickOpenFiles.Collect(folder,20,cancellation.Token);}catch(OperationCanceledException){canceled=true;}Check(canceled,"Quick open ignored cancellation");
                });
            }
            finally { if(FileService.IsInsideRoot(AppDomain.CurrentDomain.BaseDirectory,root))Directory.Delete(root,true); }
            Console.WriteLine("Core: " + passed + " passed, " + failed + " failed");
            return failed==0 ? 0:1;
        }
    }
}
