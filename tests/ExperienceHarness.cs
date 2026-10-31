using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Qingye.UI;
namespace Qingye.Tests
{
    public static partial class ExperienceHarness
    {
        static string project,output;
        static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate{frame.Continue=false;}));Dispatcher.PushFrame(frame);}
        static void Await(Task task){var until=DateTime.UtcNow.AddSeconds(20);while(!task.IsCompleted){if(DateTime.UtcNow>until)throw new Exception("Timed out");Pump();System.Threading.Thread.Sleep(5);}task.GetAwaiter().GetResult();Pump();}
        static void WaitWindow(Process process,string expected)
        {
            var until=DateTime.UtcNow.AddSeconds(10);while(DateTime.UtcNow<until){process.Refresh();if(process.HasExited)throw new Exception("Application exited before readiness");if(process.MainWindowHandle!=IntPtr.Zero && process.MainWindowTitle.Contains(expected))return;System.Threading.Thread.Sleep(5);}throw new Exception("Window did not open "+expected);
        }
        static double Median(IEnumerable<double> values){var list=values.OrderBy(x=>x).ToArray();return list[list.Length/2];}
        static void Measure(string executableOverride)
        {
            string exe=executableOverride??Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"轻页.exe");var report=new List<string>();var startup=new List<double>();var closing=new List<double>();long working=0,privateBytes=0;double idleCpu=0;
            for(int i=0;i<6;i++)using(var process=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false}))
            {
                var watch=Stopwatch.StartNew();WaitWindow(process,"未命名");startup.Add(watch.Elapsed.TotalMilliseconds);System.Threading.Thread.Sleep(250);process.Refresh();working=process.WorkingSet64;privateBytes=process.PrivateMemorySize64;
                if(i==0){System.Threading.Thread.Sleep(1750);var samples=new List<double>();for(int sample=0;sample<3;sample++){var cpu=process.TotalProcessorTime;System.Threading.Thread.Sleep(1000);process.Refresh();samples.Add((process.TotalProcessorTime-cpu).TotalMilliseconds/10);}idleCpu=Median(samples);report.Add("Stable idle CPU sample range: "+samples.Min().ToString("0.00")+"–"+samples.Max().ToString("0.00")+"% (3 x 1 second, after 2 seconds settling)");}
                watch.Restart();process.CloseMainWindow();if(!process.WaitForExit(5000))throw new Exception("Text editor stayed running after close");closing.Add(watch.Elapsed.TotalMilliseconds);
            }
            report.Add("First observed startup to editable window: "+startup[0].ToString("0")+" ms (OS cache not flushed)");report.Add("Warm startup median (5 runs): "+Median(startup.Skip(1)).ToString("0")+" ms");report.Add("Clean text close median (6 runs): "+Median(closing).ToString("0")+" ms");report.Add("Blank editor working set: "+(working/1048576.0).ToString("0.0")+" MiB; private bytes: "+(privateBytes/1048576.0).ToString("0.0")+" MiB");report.Add("Stable idle CPU median: "+idleCpu.ToString("0.00")+"% of one logical core");
            foreach(int mb in new[]{1,10})
            {
                string file=Path.Combine(output,"text-"+mb+"MB.txt");byte[] block=System.Text.Encoding.UTF8.GetBytes(new string('a',126)+"\r\n");using(var stream=File.Create(file))for(int i=0;i<mb*1048576/block.Length;i++)stream.Write(block,0,block.Length);
                var times=new List<double>();for(int i=0;i<3;i++){var watch=Stopwatch.StartNew();using(var process=Process.Start(new ProcessStartInfo(exe,"\""+file+"\""){UseShellExecute=false})){WaitWindow(process,Path.GetFileName(file));times.Add(watch.Elapsed.TotalMilliseconds);process.CloseMainWindow();if(!process.WaitForExit(5000))throw new Exception("File editor did not close");}}report.Add(mb+" MiB file launch to content-ready title median (3): "+Median(times).ToString("0")+" ms");File.Delete(file);
            }
            string first=Path.Combine(project,"examples","阅读示例.md"),second=Path.Combine(project,"examples","server.py");using(var process=Process.Start(new ProcessStartInfo(exe,"\""+first+"\""){UseShellExecute=false})){WaitWindow(process,Path.GetFileName(first));using(var forwarded=Process.Start(new ProcessStartInfo(exe,"\""+second+"\""){UseShellExecute=false})){if(!forwarded.WaitForExit(5000))throw new Exception("Second instance stayed open");}WaitWindow(process,Path.GetFileName(second));report.Add("Single instance forwarding: PASS, existing window opened second file");process.CloseMainWindow();if(!process.WaitForExit(5000))throw new Exception("Forwarded instance did not close");}
            File.WriteAllLines(Path.Combine(project,"artifacts","metrics","performance.txt"),report);foreach(string line in report)Console.WriteLine(line);
        }
        static void Capture(MainWindow window,string name,PreviewPane preview)
        {
            window.UpdateLayout();Pump();int width=(int)window.ActualWidth,height=(int)window.ActualHeight;var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
            if(preview!=null && preview.Browser!=null){using(var page=new MemoryStream()){Await(preview.CapturePageAsync(page));page.Position=0;var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=page;image.EndInit();var offset=preview.Browser.TranslatePoint(new Point(),window);var visual=new DrawingVisual();using(var drawing=visual.RenderOpen()){drawing.DrawImage(bitmap,new Rect(0,0,width,height));drawing.DrawImage(image,new Rect(offset.X,offset.Y,preview.Browser.ActualWidth,preview.Browser.ActualHeight));}bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);}}
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,name)))encoder.Save(file);
        }
        static void NativeQa()
        {
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));var watch=Stopwatch.StartNew();var window=new MainWindow{Width=1440,Height=960,Left=-16000,Top=0,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false};window.Show();Pump();Console.WriteLine("Harness construct + first layout: "+watch.ElapsedMilliseconds+" ms");
            var preview=(PreviewPane)window.TestHooks.Preview;
            try
            {
                Await(window.OpenPathsAsync(new[]{Path.Combine(project,"examples","阅读示例.md")},false));Capture(window,"native-reading.png",null);
                Await(window.OpenPathsAsync(new[]{Path.Combine(project,"examples","index.html")},false));Await(window.OpenPathsAsync(new[]{Path.Combine(project,"examples","server.py")},true));window.ImportWorkspace(Path.Combine(project,"examples"));Await(window.WorkspaceLoadTask);window.SetLayout("preview");var until=DateTime.UtcNow.AddSeconds(15);while(preview.Browser==null || preview.Browser.CoreWebView2==null || preview.Browser.CoreWebView2.Source!="https://qingye.local/index.html"){if(DateTime.UtcNow>until)throw new Exception("Integrated preview timed out");Pump();System.Threading.Thread.Sleep(10);}Await(preview.ShowAsync(window.Current,window.Session.Documents));
                var processes=preview.Browser.CoreWebView2.Environment.GetProcessInfos().Select(p=>p.ProcessId).Distinct().ToArray();long browserMemory=0;foreach(int id in processes)try{using(var p=Process.GetProcessById(id))browserMemory+=p.WorkingSet64;}catch{}
                Capture(window,"native-integrated.png",preview);window.SetTheme("lilac");Capture(window,"native-lilac.png",preview);window.SetTheme("warm");window.Appearance.PreviewPosition="bottom";window.SetLayout("preview");Pump();Capture(window,"native-bottom.png",preview);
                var close=Stopwatch.StartNew();Await(preview.CloseAsync());Console.WriteLine("Web preview shutdown + cleanup: "+close.ElapsedMilliseconds+" ms; owned browser working-set sum: "+(browserMemory/1048576.0).ToString("0.0")+" MiB (shared pages counted more than once)");
                window.SetLayout("single");Pump();string markdown=Path.Combine(project,"examples","阅读示例.md");var doc=Qingye.Core.FileService.Read(markdown);var reader=new PreviewPane();var savedContent=window.Content;window.Content=new Grid{Children={reader}};Pump();try{Await(reader.ShowAsync(doc,new[]{doc}));var native=FindChild<FlowDocumentScrollViewer>(reader);if(reader.Browser!=null || native==null || !new System.Windows.Documents.TextRange(native.Document.ContentStart,native.Document.ContentEnd).Text.Contains("一页"))throw new Exception("Actual native Markdown reading failed");Capture(window,"native-markdown-reader.png",null);Console.WriteLine("Native Markdown / no browser: PASS");Await(reader.CloseAsync());string pdf=Path.Combine(output,"sample.pdf");CreatePdf(pdf);var pdfDoc=Qingye.Core.FileService.Read(pdf);Await(reader.ShowAsync(pdfDoc,new[]{pdfDoc}));var check=reader.Browser.CoreWebView2.ExecuteScriptAsync("!!document.querySelector('embed[type=\"application/pdf\"]')");Await(check);if(check.Result!="true")throw new Exception("PDF viewer did not open");System.Threading.Thread.Sleep(300);Pump();Capture(window,"native-pdf.png",reader);Console.WriteLine("Actual PDF viewer: PASS");Await(reader.CloseAsync());File.Delete(pdf);}finally{Await(reader.CloseAsync());window.Content=savedContent;}
                window.SetLayout("single");window.Width=900;window.Height=600;Pump();Capture(window,"native-compact.png",null);
                string folder=Path.Combine(output,"folder-fixture");Directory.CreateDirectory(folder);try{for(int i=0;i<1000;i++)File.WriteAllText(Path.Combine(folder,"file-"+i+".txt"),"");watch.Restart();window.ImportWorkspace(folder);Await(window.WorkspaceLoadTask);Pump();Console.WriteLine("1000-file folder first-level bounded load: "+watch.ElapsedMilliseconds+" ms");}finally{window.ImportWorkspace(Path.Combine(project,"examples"));if(Qingye.Core.FileService.IsInsideRoot(output,folder))Directory.Delete(folder,true);}
            }
            finally{foreach(var doc in window.Session.Documents){doc.Modified=false;doc.MetadataModified=false;}window.Close();Pump();app.Shutdown();}
        }
        static void NativeReadingMeasurement()
        {
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));var window=new MainWindow{Width=1240,Height=820,Left=-16000,Top=0,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false};window.Show();Pump();
            try
            {
                Await(window.OpenPathsAsync(new[]{Path.Combine(project,"examples","阅读示例.md")},false));var watch=Stopwatch.StartNew();window.SetLayout("preview");var preview=(PreviewPane)window.TestHooks.Preview;Await(preview.ShowAsync(window.Current,window.Session.Documents));Pump();double elapsed=watch.Elapsed.TotalMilliseconds;CheckNative(preview);System.Threading.Thread.Sleep(250);Pump();long memory;using(var process=Process.GetCurrentProcess()){process.Refresh();memory=process.WorkingSet64;}
                var lines=new[]{"Native Markdown first reading layout: "+elapsed.ToString("0")+" ms","Native Markdown complete window working set: "+(memory/1048576.0).ToString("0.0")+" MiB (includes test host)","Browser process group initialized: no"};File.WriteAllLines(Path.Combine(project,"artifacts","metrics","reading-performance.txt"),lines);foreach(string line in lines)Console.WriteLine(line);Capture(window,"native-markdown.png",null);
            }
            finally{foreach(var doc in window.Session.Documents){doc.Modified=false;doc.MetadataModified=false;}window.Close();Pump();app.Shutdown();}
        }
        static void CheckNative(PreviewPane preview){var reader=FindChild<FlowDocumentScrollViewer>(preview);if(preview.Browser!=null || reader==null || !new System.Windows.Documents.TextRange(reader.Document.ContentStart,reader.Document.ContentEnd).Text.Contains("一页"))throw new Exception("Native reading window missing content or started a browser");}
        static void DesignQa()
        {
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));var window=new MainWindow{Width=1487,Height=1058,Left=0,Top=0,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false};window.Show();Pump();var preview=(PreviewPane)window.TestHooks.Preview;
            try
            {
                Await(window.OpenPathsAsync(new[]{Path.Combine(project,"examples","index.html")},false));Await(window.OpenPathsAsync(new[]{Path.Combine(project,"examples","server.py"),Path.Combine(project,"examples","阅读示例.md")},true));window.ImportWorkspace(Path.Combine(project,"examples"));Await(window.WorkspaceLoadTask);window.SetLayout("preview");window.SelectDocument(window.Session.Documents.First(d=>d.Title=="index.html"),"primary");window.SelectDocument(window.Session.Documents.First(d=>d.Title=="server.py"),"secondary");Await(preview.ShowAsync(window.Current,window.Session.Documents));Capture(window,"design-integrated.png",preview);
                ((Button)window.FindName("AppearanceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();var popup=(System.Windows.Controls.Primitives.Popup)window.TestHooks.AppearancePopup;var child=(FrameworkElement)popup.Child;child.UpdateLayout();Pump();Capture(window,"design-base.png",preview);
                var baseImage=new BitmapImage(new Uri(Path.Combine(output,"design-base.png")));var panelImage=new RenderTargetBitmap((int)Math.Ceiling(child.ActualWidth),(int)Math.Ceiling(child.ActualHeight),96,96,PixelFormats.Pbgra32);panelImage.Render(child);var point=child.PointToScreen(new Point());var origin=window.PointToScreen(new Point());var matrix=PresentationSource.FromVisual(window).CompositionTarget.TransformFromDevice;var offset=matrix.Transform(new Point(point.X-origin.X,point.Y-origin.Y));var visual=new DrawingVisual();using(var drawing=visual.RenderOpen()){drawing.DrawImage(baseImage,new Rect(0,0,window.ActualWidth,window.ActualHeight));drawing.DrawImage(panelImage,new Rect(offset.X,offset.Y,child.ActualWidth,child.ActualHeight));}SaveVisual(visual,"design-appearance.png",1487,1058,1);popup.IsOpen=false;
                window.Width=820;window.Height=540;Pump();Capture(window,"design-minimum.png",preview);window.Appearance.PreviewPosition="bottom";window.SetLayout("preview");Pump();Capture(window,"design-bottom-minimum.png",preview);
                window.SetLayout("single");window.Width=1180;window.Height=760;Pump();SaveVisual(window,"design-150-percent.png",1180,760,1.5);Console.WriteLine("Design QA captured: matching 1487x1058 viewport, popup, 820x540 minimum, 150% raster scale");
            }
            finally{foreach(var doc in window.Session.Documents){doc.Modified=false;doc.MetadataModified=false;}window.Close();Pump();app.Shutdown();}
        }
        static void SaveVisual(Visual visual,string name,int width,int height,double scale){var bitmap=new RenderTargetBitmap((int)(width*scale),(int)(height*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(visual);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,name)))png.Save(file);}
        static string FixtureArchive(string name,IDictionary<string,string> parts)
        {
            string path=Path.Combine(output,"fixtures",name);using(var stream=File.Create(path))using(var zip=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Create))foreach(var part in parts){using(var writer=new StreamWriter(zip.CreateEntry(part.Key).Open()))writer.Write(part.Value);}return path;
        }
        static void TypeQa()
        {
            Directory.CreateDirectory(Path.Combine(output,"fixtures"));var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));var window=new MainWindow{Width=1240,Height=820,Left=-16000,Top=0,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false};window.Show();Pump();var preview=(PreviewPane)window.TestHooks.Preview;
            Action<string,string> open=delegate(string path,string name){Await(window.OpenPathsAsync(new[]{path},false));if(window.Appearance.Layout=="reader")Await(preview.ShowAsync(window.Current,window.Session.Documents));if(window.Current.Kind==Qingye.Core.DocumentKind.Pdf){Await(Task.Delay(800));var ready=preview.Browser.CoreWebView2.ExecuteScriptAsync("!!document.querySelector('embed[type=\"application/pdf\"]')");Await(ready);if(ready.Result!="true")throw new Exception("PDF viewer did not open");}Capture(window,name,preview);Console.WriteLine("Captured "+name+": "+Qingye.Core.FilePresentation.For(window.Current).Name+" / "+window.Appearance.Layout);};
            try
            {
                open(Path.Combine(project,"examples","阅读示例.md"),"type-markdown.png");window.Width=1440;window.Height=900;Pump();Capture(window,"reading-wide.png",null);window.Width=390;window.Height=640;Pump();Capture(window,"reading-narrow.png",null);window.Width=240;window.Height=320;Pump();Capture(window,"reading-small.png",null);window.Width=1240;window.Height=820;Pump();window.SetViewMode("preview");Await(preview.ShowAsync(window.Current,window.Session.Documents));Capture(window,"type-markdown-split.png",preview);window.Width=820;window.Height=540;Pump();Capture(window,"type-minimum.png",preview);window.Width=1240;window.Height=820;
                open(Path.Combine(project,"examples","server.py"),"type-code.png");open(Path.Combine(project,"examples","表格示例.csv"),"type-table.png");
                string json=Path.Combine(output,"fixtures","阅读设置.json");File.WriteAllText(json,"{\n  \"应用\": \"轻页\",\n  \"阅读\": {\"字号\": 16, \"自动换行\": true},\n  \"支持格式\": [\"Markdown\", \"JSON\", \"XML\", \"CSV\"],\n  \"保留原值\": 9007199254740993,\n  \"空列表\": [],\n  \"未设置\": null\n}");open(json,"type-json.png");var tree=FindChild<TreeView>(preview);var root=(TreeViewItem)tree.Items[0];foreach(var item in root.Items.OfType<TreeViewItem>())item.IsExpanded=true;Pump();Capture(window,"type-json.png",preview);window.SetViewMode("single");Capture(window,"type-json-source.png",null);
                string xml=Path.Combine(output,"fixtures","阅读设置.xml");File.WriteAllText(xml,"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<preferences version=\"0.4\"><reading fontSize=\"16\">自然阅读</reading><files><format>Markdown</format><format>CSV</format></files></preferences>");open(xml,"type-xml.png");
                string yaml=Path.Combine(output,"fixtures","阅读设置.yaml");File.WriteAllText(yaml,"# 本地阅读偏好\napp: qingye\nfont_size: 16\nremember: false\ntheme: mint\n");open(yaml,"type-config.png");
                string text=Path.Combine(output,"fixtures","纯文本.txt");File.WriteAllText(text,"每一种文件，都有适合自己的阅读方式。\n\n纯文本保留自然的文字流；代码保留缩进与行号；表格按原始行列呈现。切换展示方式，不改变文件内容。\n\n这是用于界面验证的示例文字。");open(text,"type-text.png");
                string rtf=Path.Combine(output,"fixtures","富文本.rtf");File.WriteAllText(rtf,"{\\rtf1\\ansi\\deff0 {\\fonttbl {\\f0 Segoe UI;}}\\f0\\fs36 Qingye rich text\\par\\fs24 Native reading with \\b bold\\b0  and \\i italic\\i0 .}");open(rtf,"type-rtf.png");
                string docx=FixtureArchive("文档排版.docx",new Dictionary<string,string>{{"word/document.xml","<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:pPr><w:pStyle w:val='Heading1'/></w:pPr><w:r><w:t>让内容成为主角</w:t></w:r></w:p><w:p><w:r><w:t>这是基础 Word 排版的验证文档，保留标题、段落、强调与表格。完整分页与图片请用系统关联软件查看。</w:t></w:r></w:p><w:p><w:r><w:rPr><w:b/></w:rPr><w:t>本地读取，原文件只读。</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>文档</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>原生阅读</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>"}});open(docx,"type-docx.png");
                string xlsx=FixtureArchive("工作表.xlsx",new Dictionary<string,string>{{"xl/workbook.xml","<workbook xmlns:r='urn:rels'><sheets><sheet name='阅读计划' r:id='s1'/><sheet name='空工作表' r:id='s2'/></sheets></workbook>"},{"xl/_rels/workbook.xml.rels","<Relationships><Relationship Id='s1' Target='worksheets/sheet1.xml'/><Relationship Id='s2' Target='worksheets/sheet2.xml'/></Relationships>"},{"xl/styles.xml","<styleSheet><cellXfs><xf numFmtId='0'/><xf numFmtId='10'/></cellXfs></styleSheet>"},{"xl/worksheets/sheet1.xml","<worksheet><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>文件类型</t></is></c><c r='B1' t='inlineStr'><is><t>完成比例</t></is></c></row><row r='2'><c r='A2' t='inlineStr'><is><t>Markdown</t></is></c><c r='B2' s='1'><v>0.75</v></c></row><row r='3'><c r='A3' t='inlineStr'><is><t>JSON</t></is></c><c r='B3' s='1'><v>0.5</v></c></row></sheetData></worksheet>"},{"xl/worksheets/sheet2.xml","<worksheet><sheetData/></worksheet>"}});open(xlsx,"type-xlsx.png");
                open(Path.Combine(project,"src","Assets","qingye.png"),"type-image.png");string pdf=Path.Combine(output,"fixtures","页面.pdf");CreatePdf(pdf);open(pdf,"type-pdf.png");string binary=Path.Combine(output,"fixtures","字节.bin");File.WriteAllBytes(binary,new byte[]{0,1,2,3,255,254,48,49,50,51,0,13,10});open(binary,"type-binary.png");
                window.Width=820;window.Height=540;open(json,"type-json-minimum.png");
                Console.WriteLine("Type QA complete: native 1240x820 and 820x540, source/reader/split, all supported families.");
            }
            finally{foreach(var doc in window.Session.Documents){doc.Modified=false;doc.MetadataModified=false;}window.Close();Pump();app.Shutdown();}
        }
        static void ThemeQa()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var window = new MainWindow { Width = 1240, Height = 820, Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
            window.Show();
            var reader = NativeMarkdown.Create(NativeMarkdown.Parse("# 同一文档，三种主题\n\n> 引用的边框与文字跟随主题。\n\n```cs\n// code background\nvar value = 42;\n```\n\n| 内容 | 数值 |\n| --- | --- |\n| 阅读 | 42 |\n\n正文中的 `inline code`。\n\n---"), null, null);
            window.Content = new Grid { Children = { reader } };
            try
            {
                foreach (string theme in new[] { "mint", "warm", "lilac" })
                {
                    window.SetTheme(theme);
                    Pump();
                    Capture(window, "content-theme-" + theme + ".png", null);
                }
            }
            finally { window.Close(); Pump(); app.Shutdown(); }
        }
        static T FindChild<T>(DependencyObject parent) where T:DependencyObject{if(parent==null)return null;if(parent is T)return (T)parent;for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var found=FindChild<T>(VisualTreeHelper.GetChild(parent,i));if(found!=null)return found;}return null;}
        static void CreatePdf(string path)
        {
            string stream="BT /F1 24 Tf 70 700 Td (HELLO PDF - QINGYE) Tj ET";string[] objects={"<< /Type /Catalog /Pages 2 0 R >>","<< /Type /Pages /Kids [3 0 R] /Count 1 >>","<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>","<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>","<< /Length "+stream.Length+" >>\nstream\n"+stream+"\nendstream"};var text=new System.Text.StringBuilder("%PDF-1.4\n");var offsets=new List<int>();for(int i=0;i<objects.Length;i++){offsets.Add(text.Length);text.Append(i+1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");}int xref=text.Length;text.Append("xref\n0 6\n0000000000 65535 f \n");foreach(int offset in offsets)text.Append(offset.ToString("D10")).Append(" 00000 n \n");text.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");File.WriteAllText(path,text.ToString(),System.Text.Encoding.ASCII);
        }
        static void ShellQa()
        {
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var window=new MainWindow{Width=1240,Height=820,Left=-16000,Top=0,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false};window.Show();Pump();
            try
            {
                Await(window.OpenPathsAsync(new[]{Path.Combine(project,"examples","阅读示例.md")},false));
                window.ToggleSidePanel();Pump();
                var side=(SidePanel)window.TestHooks.Side;side.SetMode(true);Pump();
                Capture(window,"sidebar-outline.png",null);
                window.SetViewMode("wysiwyg");Pump();
                Capture(window,"inplace-editing.png",null);
                window.SetViewMode("single");Pump();
                window.ToggleAgent();Pump();
                Capture(window,"agent-panel.png",null);
                window.ToggleAgent();Pump();
                Console.WriteLine("Shell captures written: sidebar-outline.png, inplace-editing.png, agent-panel.png");
            }
            finally{foreach(var doc in window.Session.Documents){doc.Modified=false;doc.MetadataModified=false;}window.Close();Pump();app.Shutdown();}
        }
        sealed class AgentDemoTransport : Qingye.Core.IStreamingAgentTransport
        {
            internal readonly Queue<Qingye.Core.AgentReply> Replies = new Queue<Qingye.Core.AgentReply>();
            internal bool Hold;
            public Task<Qingye.Core.AgentReply> ChatAsync(IList<Dictionary<string,object>> messages,string tools,System.Threading.CancellationToken token)
            {
                if(!Hold)return Task.FromResult(Replies.Dequeue());
                var completion=new TaskCompletionSource<Qingye.Core.AgentReply>();token.Register(()=>completion.TrySetCanceled());return completion.Task;
            }
            public Task<Qingye.Core.AgentReply> ChatStreamingAsync(IList<Dictionary<string,object>> messages,string tools,System.Threading.CancellationToken token,Action<string> delta)
            {
                if(Hold)delta("正在阅读当前文件，并整理需要修改的部分。回复会在收到时逐步显示。\n");
                return ChatAsync(messages,tools,token);
            }
        }
        static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
        {
            if(parent is T)yield return (T)parent;
            for(int index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++)foreach(var child in Descendants<T>(VisualTreeHelper.GetChild(parent,index)))yield return child;
        }
        static void AgentQa(bool interactive)
        {
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var window=new MainWindow{Width=1280,Height=860,Left=interactive?100:-16000,Top=interactive?80:0,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=interactive};window.Show();Pump();
            string fixture=Path.Combine(project,"build","agent-qa-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
            string file=Path.Combine(fixture,"阅读计划.md");File.WriteAllText(file,"# 阅读计划\n\n这一页用于检查 Agent 与文档并排工作的体验。\n\n## 本周安排\n\n- 阅读资料\n- 整理笔记\n- 检查未完成事项\n\n## 修改要求\n\n保留现有内容，只调整用户要求的部分。\n");
            try
            {
                Await(window.OpenPathsAsync(new[]{file},false));window.SetViewMode("reader");window.ToggleAgent();
                var agent=window.TestHooks.Agent;var transport=new AgentDemoTransport();
                agent.UseTestServices(Path.Combine(fixture,"agent-history"),()=>new Qingye.Core.ModelConfig{BaseUrl="https://example.com",ApiKey="sk-demo-placeholder",ModelName="本地交互演示"},config=>transport);
                transport.Replies.Enqueue(new Qingye.Core.AgentReply{Content="我先读取当前文件，核对章节和待办事项。",ToolCalls=new[]{new Qingye.Core.AgentToolCall{Id="demo-read",Name="read_file",Arguments="{\"path\":\"阅读计划.md\"}"}}});
                transport.Replies.Enqueue(new Qingye.Core.AgentReply{Content="## 已检查当前文件\n\n文档包含本周安排与修改要求，内容已保留。\n\n- **阅读资料**：保留原有待办\n- **整理笔记**：可以补充完成标准\n- **检查未完成事项**：建议逐项确认\n\n本轮只读取了文件，尚未写入修改。"});
                Await(agent.SubmitAsync("检查这份阅读计划，先给出修改建议。"));Pump();
                foreach(string theme in new[]{"mint","warm","lilac","dark"}){window.SetTheme(theme);Pump();Capture(window,"agent-workbench-"+theme+".png",null);}
                window.SetTheme("mint");
                Descendants<Button>(agent).First(button=>(button.ToolTip as string)=="历史会话").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();Capture(window,"agent-history.png",null);
                Descendants<Button>(agent).First(button=>(button.ToolTip as string)=="历史会话").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.ToggleAgentFocus();Pump();Capture(window,"agent-focus.png",null);
                window.Width=390;window.Height=720;Pump();Capture(window,"agent-narrow.png",null);
                window.Width=1280;window.Height=860;window.ToggleAgentFocus();Pump();
                agent.NewConversation();transport.Hold=true;var operation=agent.SubmitAsync("阅读当前文件，并帮我整理一个清晰的任务列表。");System.Threading.Thread.Sleep(150);Pump();Capture(window,"agent-running.png",null);agent.Stop();Await(operation);transport.Hold=false;
                agent.NewConversation();transport.Replies.Enqueue(new Qingye.Core.AgentReply{ToolCalls=new[]{new Qingye.Core.AgentToolCall{Id="demo-write",Name="write_file",Arguments=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{path="阅读计划.md",content=File.ReadAllText(file).Replace("# 阅读计划","# 本周阅读计划")})}}});
                transport.Replies.Enqueue(new Qingye.Core.AgentReply{Content="标题已按确认后的差异修改，其余内容保留。"});
                operation=agent.SubmitAsync("把标题改为本周阅读计划，其余内容不要改。");Pump();Capture(window,"agent-approval.png",null);
                if(interactive){Console.WriteLine("AGENT_QA_READY: synthetic local data; no model requests");while(window.IsVisible){Pump();System.Threading.Thread.Sleep(20);}}
                else{agent.Stop();Await(operation);}
            }
            finally{foreach(var document in window.Session.Documents)document.Modified=false;window.Close();Pump();app.Shutdown();}
        }
        [STAThread] public static int Main(string[] args){project=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..",".."));output=Path.Combine(project,"artifacts","ui");Directory.CreateDirectory(output);Directory.CreateDirectory(Path.Combine(project,"artifacts","metrics"));try{if(args.Length>0 && args[0]=="harness-memory")HarnessMemoryQa();else if(args.Length>0 && args[0]=="harness")HarnessQa(args);else if(args.Length>0 && (args[0]=="agent" || args[0]=="agent-live"))AgentQa(args[0]=="agent-live");else if(args.Length>0 && args[0]=="measure")Measure(args.Length>1?Path.GetFullPath(args[1]):null);else if(args.Length>0 && args[0]=="reading")NativeReadingMeasurement();else if(args.Length>0 && args[0]=="design")DesignQa();else if(args.Length>0 && args[0]=="types")TypeQa();else if(args.Length>0 && args[0]=="themes")ThemeQa();else if(args.Length>0 && args[0]=="shell")ShellQa();else NativeQa();return 0;}catch(Exception e){Console.WriteLine("QA FAILED: "+e);return 1;}}
    }
}
