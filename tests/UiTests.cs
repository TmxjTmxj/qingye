using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using ICSharpCode.AvalonEdit;
using Qingye.UI;
namespace Qingye.Tests
{
    public static partial class UiTests
    {
        static int passed,failed;static MainWindow window;static string root;
        static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
        static void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
        public static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate{frame.Continue=false;}));Dispatcher.PushFrame(frame);}
        static void Await(Task task){var deadline=DateTime.UtcNow.AddSeconds(20);while(!task.IsCompleted){if(DateTime.UtcNow>deadline)throw new Exception("Native operation timed out");Pump();System.Threading.Thread.Sleep(5);}task.GetAwaiter().GetResult();Pump();}
        static TextEditor Editor(){return (TextEditor)window.FindName("PrimaryEditor");}
        static void CaptureElement(FrameworkElement element,string path)
        {
            if(element==null)throw new Exception("Nothing to capture");
            element.UpdateLayout();
            var window=Window.GetWindow(element);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if(window==null){throw new Exception("Capture needs a live window");}
            window.UpdateLayout();
            var full=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Max(1,Math.Ceiling(window.ActualWidth)),(int)Math.Max(1,Math.Ceiling(window.ActualHeight)),96,96,System.Windows.Media.PixelFormats.Pbgra32);
            full.Render(window);
            System.Windows.Media.Imaging.BitmapSource source=full;
            try
            {
                Point origin=element.TranslatePoint(new Point(0,0),window);
                int x=Math.Max(0,(int)Math.Round(origin.X)),y=Math.Max(0,(int)Math.Round(origin.Y));
                int width=Math.Min(full.PixelWidth-x,(int)Math.Round(element.ActualWidth));
                int height=Math.Min(full.PixelHeight-y,(int)Math.Round(element.ActualHeight));
                if(width>10 && height>10)source=new System.Windows.Media.Imaging.CroppedBitmap(full,new Int32Rect(x,y,width,height));
            }
            catch(Exception){/* Crop is cosmetic; keep the full window capture. */}
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using(var file=File.Create(path))encoder.Save(file);
        }
        [STAThread] public static int Main()
        {
            AppDomain.CurrentDomain.UnhandledException+=delegate(object s,UnhandledExceptionEventArgs e){var error=e.ExceptionObject as Exception;if(error!=null){Console.WriteLine("UNHANDLED "+error.GetType().FullName+" "+error.Message);Console.WriteLine(error.StackTrace);}};
            var app=new Application();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-data-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            Console.WriteLine("UI: constructing native window");
            try{window=new MainWindow();Console.WriteLine("UI: showing native window");window.ShowActivated=false;window.Left=-16000;window.Top=0;window.Show();Pump();}
            catch(Exception error){for(int i=0;error!=null && i<6;i++,error=error.InnerException){Console.WriteLine(error.GetType().FullName+": "+error.Message);Console.WriteLine(error.StackTrace);}return 1;}
            try
            {
                Test("Unused rich editing and second text editor stay unloaded",delegate{Check(!window.richEdit.IsCreated&&!window.secondary.IsEditorCreated,"Startup created unused editors");window.SetTheme("warm");Check(!window.richEdit.IsCreated&&!window.secondary.IsEditorCreated,"Theme change initialized unused editors");window.SetTheme("mint");});
                Test("Caret status reuses header controls and preserves explicit notices",delegate{var icon=window.TestHooks.DocumentBar.FileIcon;for(int i=0;i<100;i++)window.UpdateStatus();Check(Object.ReferenceEquals(icon,window.TestHooks.DocumentBar.FileIcon),"Caret updates rebuilt the file icon");window.TestHooks.Activate(window.TestHooks.Primary);window.SetNotice("PERFORMANCE_NOTICE");Pump();Check(window.TestHooks.Footer.notice.Text=="PERFORMANCE_NOTICE","Queued status erased an explicit notice");window.UpdateStatus();});
                Test("Startup is an editable blank document",delegate{Check(Editor()!=null && !Editor().IsReadOnly && Editor().Text=="" && window.Session.Documents.Count==1,"Startup needs selection or lacks editor");});
                Test("Quick open includes unsaved documents and shows empty results",delegate{
                    Exception failure=null;
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(delegate{
                        var dialog=Application.Current.Windows.OfType<Window>().FirstOrDefault(item=>item.Title=="快速打开");
                        try{
                            Check(dialog!=null,"Quick open did not appear");
                            var search=Walk(dialog).OfType<TextBox>().First();var list=Walk(dialog).OfType<ListBox>().First();
                            Check(list.Items.Count>0 && Walk(dialog).OfType<TextBlock>().Any(text=>text.Text=="未保存文档"),"Unsaved document was missing");
                            string output=Path.GetFullPath(Path.Combine(root,"..","..","..","artifacts","ui"));Directory.CreateDirectory(output);
                            CaptureElement(dialog,Path.Combine(output,"quick-open-light.png"));
                            dialog.Width=366;dialog.Height=360;Theme.Apply(dialog,new Qingye.Core.Settings{Theme="dark"});Pump();
                            CaptureElement(dialog,Path.Combine(output,"quick-open-dark-narrow.png"));
                            search.Text="no-such-file-987654";
                            Check(list.Items.Count==0 && Walk(dialog).OfType<TextBlock>().Any(text=>text.Text.Contains("没有匹配")),"Empty result was not explained");
                        }catch(Exception error){failure=error;}finally{if(dialog!=null)dialog.Close();}
                    }));
                    QuickOpen.Show(window);if(failure!=null)throw failure;
                });
                Test("Window uses the embedded application icon",delegate{var icon=window.Icon as System.Windows.Media.Imaging.BitmapSource;Check(icon!=null && icon.PixelWidth>=128,"Taskbar/window icon was not configured");});
                Test("Window regions are separate UserControls",delegate{
                    foreach(string name in new[]{"Toolbar","DocumentBar","AppearancePopup","Footer","TabStrip"})
                    {
                        var type=typeof(MainWindow).Assembly.GetType("Qingye.UI."+name);
                        Check(type!=null && typeof(UserControl).IsAssignableFrom(type),name+" is not an extracted UserControl");
                    }
                });
                Test("MainWindow orchestration stays within 250 lines",delegate{
                    string project=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..",".."));
                    Check(File.ReadAllLines(Path.Combine(project,"src","UI","MainWindow.cs")).Length<=250,"MainWindow still contains region implementations");
                });
                Test("Preview edits avoid repeated full text allocation",delegate{
                    string file=Path.Combine(root,"allocation.md");
                    string line=new string('a',1023)+"\n";
                    using(var output=new StreamWriter(file)){output.WriteLine("```");for(int index=0;index<10240;index++)output.Write(line);output.WriteLine("```");}
                    Await(window.OpenPathsAsync(new[]{file},false));
                    window.SetLayout("reader");Await(window.UpdatePreviewAsync());
                    AppDomain.MonitoringIsEnabled=true;
                    long before=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                    for(int edit=0;edit<20;edit++)
                    {
                        Editor().Document.Insert(0,"edit-"+edit+"\n");
                        Await(window.UpdatePreviewAsync());
                    }
                    long allocated=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize-before;
                    Console.WriteLine("METRIC preview 10 MiB / 20 edits allocated "+(allocated/1048576.0).ToString("F2")+" MiB");
                    var view=FindChild<FlowDocumentScrollViewer>(window.TestHooks.Preview);
                    Check(view!=null && new System.Windows.Documents.TextRange(view.Document.ContentStart,view.Document.ContentEnd).Text.Contains("edit-19"),"Preview missed the latest edit");
                    window.Current.Modified=false;window.SetLayout("single");
                    Check(allocated<200L*1024*1024,"Preview still materializes the complete buffer for each edit");
                });
                Test("Open defaults to one file",delegate{string a=Path.Combine(root,"a.txt"),b=Path.Combine(root,"b.txt");File.WriteAllText(a,"alpha 中文");File.WriteAllText(b,"beta");Await(window.OpenPathsAsync(new[]{a},false));Await(window.OpenPathsAsync(new[]{b},false));Check(window.Session.Documents.Count==1 && Editor().Text=="beta","Open kept previous file");});
                Test("Manual import adds another file",delegate{Await(window.OpenPathsAsync(new[]{Path.Combine(root,"a.txt")},true));Check(window.Session.Documents.Count==2,"Import lost existing file");});
                Test("Consecutive open requests are processed in order",delegate{
                    var first=window.OpenPathsAsync(new[]{Path.Combine(root,"a.txt")},false);
                    var second=window.OpenPathsAsync(new[]{Path.Combine(root,"b.txt")},false);
                    Await(Task.WhenAll(first,second));
                    Check(window.Current.Title=="b.txt" && Editor().Text=="beta","A request was silently dropped while busy");
                });
                Test("Open queue drops oldest beyond four and recovers after errors",delegate{
                    var active=window.OpenPathsAsync(new[]{Path.Combine(root,"a.txt")},false);
                    var pending=new System.Collections.Generic.List<Task>();
                    for(int index=1;index<=6;index++)
                    {
                        string file=Path.Combine(root,"queued-"+index+".txt");
                        File.WriteAllText(file,"queue "+index);
                        pending.Add(window.OpenPathsAsync(new[]{file},true));
                    }
                    Await(Task.WhenAll(pending.Concat(new[]{active})));
                    Check(window.Session.Documents.Count==5 && !window.Session.Documents.Any(d=>d.Title=="queued-1.txt" || d.Title=="queued-2.txt"),"Queue is unbounded or dropped the wrong requests");
                    Check(Walk(window).OfType<TextBlock>().Any(text=>text.Text.Contains("排队") && text.Text.Contains("4")),"Dropped requests have no visible notice");
                    var missing=window.OpenPathsAsync(new[]{Path.Combine(root,"missing.txt")},false);
                    var recovery=window.OpenPathsAsync(new[]{Path.Combine(root,"b.txt")},false);
                    Await(Task.WhenAll(missing,recovery));
                    Check(window.Current.Title=="b.txt","An open failure stuck the queue");
                });
                Test("Layout and theme preserve live edits",delegate{Editor().Text="changed 中文";window.SetTheme("lilac");window.SetLayout("compare");Pump();Check(Editor().Text=="changed 中文" && window.Session.Documents.Any(d=>d.IsDirty),"Customization lost edits");Check(window.FindName("SecondaryEditor")!=null,"Second pane missing");});
                Test("Minimum window keeps toolbar icons inside separate buttons",delegate{window.Width=820;window.Height=540;Pump();var toolbar=(System.Collections.Generic.List<Button>)window.TestHooks.Toolbar.toolButtons;var buttons=toolbar.Concat(Walk(window).OfType<Button>().Where(b=>new[]{"最小化","最大化","关闭"}.Contains(b.ToolTip as string))).ToArray();foreach(var button in buttons){Rect rect=Bounds(button,window);Check(rect.Left>=0 && rect.Right<=window.ActualWidth+1,"Toolbar button outside window");foreach(var icon in Walk(button).OfType<IconView>()){Rect local=Bounds(icon,button);Check(local.Left>=-1 && local.Top>=-1 && local.Right<=button.ActualWidth+1 && local.Bottom<=button.ActualHeight+1,"Icon clips inside button");}}for(int i=0;i<buttons.Length;i++)for(int j=i+1;j<buttons.Length;j++){var overlap=Rect.Intersect(Bounds(buttons[i],window),Bounds(buttons[j],window));Check(overlap.IsEmpty || overlap.Width<.5 || overlap.Height<.5,"Toolbar buttons overlap");}window.Width=1240;window.Height=820;Pump();});
                Test("Appearance color cards retain edits and switch theme",delegate{string before=Editor().Text;var button=(Button)window.FindName("AppearanceButton");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();var popup=(System.Windows.Controls.Primitives.Popup)window.TestHooks.AppearancePopup;var cards=Walk(popup.Child).OfType<System.Windows.Controls.Primitives.UniformGrid>().First();((Button)cards.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(window.Appearance.Theme=="warm" && Editor().Text==before,"Appearance cards lost content or did not apply theme");popup.IsOpen=false;});
                Test("Window resizes below the former minimum without overlapping visible controls",delegate{
                    Check(window.MinWidth==0 && window.MinHeight==0,"Artificial window minimum remains");string before=Editor().Text;
                    foreach(int width in new[]{640,480,360,240,1240}){window.Width=width;window.Height=420;Pump();Check(Math.Abs(window.ActualWidth-width)<2,"Window width was constrained");var buttons=Walk(window).OfType<Button>().Where(b=>b.IsVisible && (b==window.FindName("MoreToolsButton") || b==window.FindName("ReadViewButton") || b==window.FindName("SourceViewButton") || b==window.FindName("SplitViewButton") || new[]{"打开","保存","最小化","最大化","关闭"}.Contains(b.ToolTip as string))).ToArray();foreach(var button in buttons){var bounds=Bounds(button,window);Check(bounds.Left>=-1 && bounds.Right<=window.ActualWidth+1,"A narrow-window control clips");}for(int i=0;i<buttons.Length;i++)for(int j=i+1;j<buttons.Length;j++){var overlap=Rect.Intersect(Bounds(buttons[i],window),Bounds(buttons[j],window));Check(overlap.IsEmpty || overlap.Width<1 || overlap.Height<1,"Narrow-window controls overlap");}}Check(Editor().Text==before,"Resize lost editor content");window.Height=820;Pump();
                });
                Test("Narrow-window overflow retains tools and search wraps",delegate{window.Width=360;window.Height=500;Pump();var more=(Button)window.FindName("MoreToolsButton");Check(more.IsVisible,"Narrow window has no overflow control");more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();var menu=more.ContextMenu;Check(menu!=null && menu.Items.OfType<MenuItem>().Any(i=>(string)i.Header=="外观与布局"),"Hidden tools are inaccessible");menu.IsOpen=false;menu.Items.OfType<MenuItem>().First(i=>(string)i.Header=="搜索").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();var strip=(WrapPanel)window.TestHooks.Search.searchStrip;Check(strip.IsVisible && strip.ActualHeight>40,"Search did not wrap at narrow width");window.TestHooks.ToggleSearch();window.Width=1240;window.Height=820;Pump();});
                Test("Reader mode keeps tabs and can return to editable text",delegate{var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(2,2,96,96,System.Windows.Media.PixelFormats.Bgra32,null,new byte[16],8);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));string path=Path.Combine(root,"reader.png");using(var file=File.Create(path))png.Save(file);Await(window.OpenPathsAsync(new[]{path},true));Pump();var border=(Border)window.TestHooks.TabStrip.tabsBorder;Check(border.IsVisible,"Reader hid all file tabs");var text=window.Session.Documents.First(d=>d.Kind==Qingye.Core.DocumentKind.Text);window.SelectDocument(text,"primary");Pump();Check(Editor().IsVisible && !Editor().IsReadOnly,"Returning from reader did not restore editor");window.SetLayout("compare");});
                Test("Overflow tabs have a visible horizontal navigation control",delegate{var extra=new[]{"alpha-long-name.md","beta-long-name.json","gamma-long-name.cs"}.Select(name=>Path.Combine(root,name)).ToArray();foreach(var file in extra)File.WriteAllText(file,"overflow");Await(window.OpenPathsAsync(extra,true));window.SetLayout("preview");window.Width=820;window.Height=540;Pump();var border=(Border)window.TestHooks.TabStrip.tabsBorder;var scroll=(ScrollViewer)border.Child;Check(scroll.ScrollableWidth>0 && scroll.HorizontalScrollBarVisibility==ScrollBarVisibility.Auto,"Overflow files are inaccessible by pointer");double before=scroll.HorizontalOffset;scroll.ScrollToRightEnd();Pump();Check(scroll.HorizontalOffset>before,"Tab strip does not scroll");window.SetLayout("compare");window.Width=1240;window.Height=820;Pump();});
                Test("Tab switches the active pane without replacing the other",delegate{var primary=(EditorPane)window.TestHooks.Primary;var secondary=(EditorPane)window.TestHooks.Secondary;var before=primary.BoundDocument;window.TestHooks.Activate(secondary);var tabs=(StackPanel)window.TestHooks.TabStrip.tabs;int target=window.Session.Documents.IndexOf(window.Session.Documents.First(d=>d!=before));var button=Walk(tabs.Children[target]).OfType<Button>().First();button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(primary.BoundDocument==before && window.Current==secondary.BoundDocument,"Tab replaced the other pane");window.TestHooks.Activate(primary);});
                Test("Tab selection reuses controls and context menu targets the second pane",delegate{
                    var tabs=window.TestHooks.TabStrip;var original=tabs.tabs.Children[0];
                    var primary=window.TestHooks.Primary;var before=primary.BoundDocument;
                    var target=window.Session.Documents.First(doc=>doc!=before);
                    var button=Walk(tabs.tabs.Children[window.Session.Documents.IndexOf(target)]).OfType<Button>().First();
                    ((MenuItem)button.ContextMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Check(window.TestHooks.Secondary.BoundDocument==target && primary.BoundDocument==before,"Tab menu did not target the second pane");
                    Check(Object.ReferenceEquals(original,tabs.tabs.Children[0]),"Switching files rebuilt the tab controls");
                    string output=Path.Combine(root,"..","..","..","artifacts","ui");Directory.CreateDirectory(output);
                    CaptureElement(window,Path.Combine(output,"lightweight-tabs.png"));window.TestHooks.Activate(primary);
                });
                Test("Replace is applied to real editor buffer",delegate{Editor().Text="hello hello\n你好";int count=window.ReplaceAll("hello","world",false,true);Check(count==2 && Editor().Text=="world world\n你好","Replacement did not modify buffer");});
                Test("Text mode has not loaded webpage engine",delegate{Check(!System.Diagnostics.Process.GetProcessesByName("msedgewebview2").Any(p=>p.ParentId()==System.Diagnostics.Process.GetCurrentProcess().Id),"Text mode created browser process");});
                Test("Preferences remain opt-in",delegate{Check(!window.Appearance.RememberAppearance && window.WorkspaceRoot==null,"Startup persisted workspace or preferences");});
                Test("Workspace loads bounded pages and can reveal further files",delegate{string folder=Path.Combine(root,"many");Directory.CreateDirectory(folder);for(int i=0;i<310;i++)File.WriteAllText(Path.Combine(folder,"file-"+i+".txt"),"");window.ImportWorkspace(folder);Pump();var tree=(TreeView)window.TestHooks.Workspace.folderTree;var node=(TreeViewItem)tree.Items[0];Func<Task> taskProperty=()=>window.WorkspaceLoadTask;Check(taskProperty!=null,"Workspace loading still blocks UI");Await((Task)taskProperty());int files=node.Items.OfType<TreeViewItem>().Count(x=>x.Tag is string);Check(files<=120 && files>0,"First folder page unbounded");var more=node.Items.OfType<Button>().FirstOrDefault();Check(more!=null,"Further files inaccessible");more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Await((Task)taskProperty());Check(node.Items.OfType<TreeViewItem>().Count(x=>x.Tag is string)>files,"Load more did not append files");window.ImportWorkspace(root);Await((Task)taskProperty());});
                Test("A failed directory page can be retried without losing earlier items",delegate{string folder=Path.Combine(root,"retry"),moved=Path.Combine(root,"retry-moved");Check(Qingye.Core.FileService.IsInsideRoot(root,folder) && Qingye.Core.FileService.IsInsideRoot(root,moved),"Unsafe directory test path");Directory.CreateDirectory(folder);for(int i=0;i<135;i++)File.WriteAllText(Path.Combine(folder,"f"+i+".txt"),"");window.ImportWorkspace(folder);Await(window.WorkspaceLoadTask);var tree=(TreeView)window.TestHooks.Workspace.folderTree;var node=(TreeViewItem)tree.Items[0];int before=node.Items.OfType<TreeViewItem>().Count(x=>x.Tag is string);try{Directory.Move(folder,moved);node.Items.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Await(window.WorkspaceLoadTask);Directory.Move(moved,folder);var retry=node.Items.OfType<Button>().FirstOrDefault();Check(retry!=null,"Directory cannot retry after a transient error");retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Await(window.WorkspaceLoadTask);Check(node.Items.OfType<TreeViewItem>().Count(x=>x.Tag is string)>before,"Retry lost or failed to append items");}finally{if(Directory.Exists(moved))Directory.Move(moved,folder);window.ImportWorkspace(root);Await(window.WorkspaceLoadTask);}});
                Test("Single pane directs commands to visible document",delegate{window.SetLayout("compare");var pane=(EditorPane)window.TestHooks.Secondary;window.TestHooks.Activate(pane);window.SetLayout("single");var visible=(EditorPane)window.TestHooks.Primary;Check(window.Current==visible.BoundDocument,"Commands target hidden file");});
                Test("Replacing session releases hidden document references",delegate{foreach(var d in window.Session.Documents){d.Modified=false;d.MetadataModified=false;}window.SetLayout("compare");var pane=(EditorPane)window.TestHooks.Secondary;var old=pane.BoundDocument;window.SetLayout("single");window.NewDocument(false);Check(!window.Session.Documents.Contains(old) && pane.BoundDocument!=old,"Hidden pane retains closed content");Await(window.OpenPathsAsync(new[]{Path.Combine(root,"a.txt")},false));});
                Test("Paged large files can navigate without accumulating documents",delegate{foreach(var d in window.Session.Documents)d.Modified=false;string path=Path.Combine(root,"large.log");using(var file=new FileStream(path,FileMode.Create)){file.SetLength(129L*1024*1024);byte[] line=System.Text.Encoding.UTF8.GetBytes("page one\n");file.Write(line,0,line.Length);file.Position=Qingye.Core.FileService.PageBytes;file.Write(line,0,line.Length);} /* ASCII fixture avoids binary NUL detection. */ using(var file=new FileStream(path,FileMode.Open,FileAccess.Write)){byte[] block=Enumerable.Repeat((byte)'x',65536).ToArray();for(int j=255;j<block.Length;j+=256)block[j]=(byte)'\n';for(long pos=0;pos<file.Length;pos+=block.Length){file.Position=pos;file.Write(block,0,(int)Math.Min(block.Length,file.Length-pos));}}Await(window.OpenPathsAsync(new[]{path},false));Func<int,Task> method=window.ChangePageAsync;Check(method!=null,"Page navigation missing");Await((Task)method(1));Check(window.Current.Page==1 && window.Session.Documents.Count==1 && Editor().IsReadOnly,"Paging replaced mode or retained old pages");Await(window.OpenPathsAsync(new[]{Path.Combine(root,"a.txt")},false));});
                Test("Multi-file native picker preserves every selected filename",delegate{var files=Dialogs.ParseSelection("D:\\folder name\0first.txt\0second file.md\0\0");Check(files.SequenceEqual(new[]{"D:\\folder name\\first.txt","D:\\folder name\\second file.md"}),"Native selection lost file names");});
                Test("Native file chooser structure can cross the Windows API boundary",delegate{var type=typeof(Dialogs).GetNestedType("OpenFileName",System.Reflection.BindingFlags.NonPublic);var data=Activator.CreateInstance(type,true);Check(Marshal.SizeOf(data)==152,"Incorrect x64 file dialog structure size");});
                Test("Mixed file and directory import preserves files and opens workspace",delegate{foreach(var doc in window.Session.Documents){doc.Modified=false;doc.MetadataModified=false;}string folder=Path.Combine(root,"mixed workspace");Directory.CreateDirectory(folder);string file=Path.Combine(root,"mixed 中文.txt");File.WriteAllText(file,"mixed import");int before=window.Session.Documents.Count;Await(window.OpenPathsAsync(new[]{folder,file},true));Await(window.WorkspaceLoadTask);Check(window.WorkspaceRoot==Path.GetFullPath(folder) && window.Session.Documents.Count==before+1 && Editor().Text=="mixed import","Mixed import failed or lost documents");});
                Test("File views follow type and retain per-document choice and unsaved edits",delegate{
                    foreach(var d in window.Session.Documents){d.Modified=false;d.MetadataModified=false;}
                    string md=Path.Combine(root,"adaptive.MARKDOWN"),code=Path.Combine(root,"adaptive.py");File.WriteAllText(md,"# 阅读标题\n\n正文");File.WriteAllText(code,"print('code')");
                    Await(window.OpenPathsAsync(new[]{md},false));Check(window.Appearance.Layout=="reader","Markdown did not default to reading");var doc=window.Current;window.SetViewMode("single");Editor().AppendText("\n\n未保存段落");
                    Await(window.OpenPathsAsync(new[]{code},true));Check(window.Appearance.Layout=="single" && Editor().ShowLineNumbers && !Editor().WordWrap,"Code typography did not follow type");
                    window.SelectDocument(doc,"primary");Check(window.Appearance.Layout=="single" && Editor().Text.Contains("未保存段落"),"Per-file source choice or edits were lost");window.SetViewMode("reader");var preview=(PreviewPane)window.TestHooks.Preview;Await(preview.ShowAsync(doc,window.Session.Documents));var flow=FindChild<FlowDocumentScrollViewer>(preview);Check(flow!=null && new System.Windows.Documents.TextRange(flow.Document.ContentStart,flow.Document.ContentEnd).Text.Contains("未保存段落"),"Reading ignored live buffer");doc.Modified=false;window.SetViewMode("single");
                });
                Test("Plain text, logs and configuration use distinct editor conventions",delegate{
                    string txt=Path.Combine(root,"typography.txt"),log=Path.Combine(root,"typography.log"),yaml=Path.Combine(root,"typography.yaml");File.WriteAllText(txt,"自然文本");File.WriteAllText(log,"2026-10-29 INFO test");File.WriteAllText(yaml,"name: qingye\n# comment\nactive: true");
                    Await(window.OpenPathsAsync(new[]{txt},false));Check(Editor().WordWrap && !Editor().ShowLineNumbers,"Text did not use prose conventions");Await(window.OpenPathsAsync(new[]{log},false));Check(!Editor().WordWrap && Editor().ShowLineNumbers,"Log lost raw lines");Await(window.OpenPathsAsync(new[]{yaml},false));Check(Editor().SyntaxHighlighting!=null && Editor().SyntaxHighlighting.Name=="Configuration","Configuration highlighting missing");
                });
                Test("Per-file split view does not add a different document editor",delegate{string a=Path.Combine(root,"split.md"),b=Path.Combine(root,"other.txt");File.WriteAllText(a,"# Split current file");File.WriteAllText(b,"other");Await(window.OpenPathsAsync(new[]{a},false));var selected=window.Current;Await(window.OpenPathsAsync(new[]{b},true));window.SelectDocument(selected,"primary");window.SetViewMode("preview");Pump();Check(window.Appearance.Layout=="preview" && window.Current==selected && !((TextEditor)window.FindName("SecondaryEditor")).IsVisible,"View button unexpectedly opened another document");window.SetViewMode("single");});
                Test("JSON tree preserves exact numeric text, unusual keys and empty container types",delegate{
                    string file=Path.Combine(root,"structure.json"),source="{\"large\":9007199254740993,\"中文 key\":[],\"empty\":{},\"flag\":false,\"value\":null}";File.WriteAllText(file,source);Await(window.OpenPathsAsync(new[]{file},false));Check(window.Appearance.Layout=="reader" && window.Current.Text==source,"JSON view rewrote source");var xml=StructuredReader.Parse(source,true);Check(xml.Elements().First().Value=="9007199254740993","Number precision changed");var tree=StructuredReader.Create(xml,true);window.Content=new Grid{Children={tree}};Pump();var item=(TreeViewItem)tree.Items[0];Check(item.Items.Count==5,"Tree lost properties");var labels=item.Items.OfType<TreeViewItem>().Select(x=>((TextBlock)x.Header).Text).ToArray();Check(labels.Any(x=>x.Contains("中文 key") && x.Contains("0 项")) && labels.Any(x=>x.Contains("empty") && x.Contains("0 个字段")),"Keys or empty container type lost");
                });
                Test("XML structure refuses DTD and keeps attributes and mixed text",delegate{
                    bool refused=false;try{StructuredReader.Parse("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><root>&x;</root>",false);}catch(System.Xml.XmlException){refused=true;}Check(refused,"XML reader accepted external entities");var xml=StructuredReader.Parse("<root id='42'>before<child>内容</child>after</root>",false);Check((string)xml.Attribute("id")=="42" && xml.Value=="before内容after","XML data changed");bool invalid=false;try{StructuredReader.Parse("{broken}",true);}catch{invalid=true;}Check(invalid,"Invalid JSON accepted silently");
                });
                Test("CSV grid preserves quoted multiline values and has row headers",delegate{
                    var doc=new Qingye.Core.Document{Path=Path.Combine(root,"grid.csv")};doc.SetLoadedText("name,value\r\n\"quoted, name\",42\r\n\"line1\nline2\",0\r\n");var pane=new PreviewPane();window.Content=new Grid{Children={pane}};Pump();try{Await(pane.ShowAsync(doc,new[]{doc}));var grid=FindChild<DataGrid>(pane);Check(grid!=null && grid.Items.Count==3 && grid.HeadersVisibility==DataGridHeadersVisibility.All,"Table convention missing");var row=(string[])grid.Items[2];Check(row[0]=="line1\nline2" && row[1]=="0" && pane.Browser==null,"Table altered raw values or started browser");}finally{Await(pane.CloseAsync());}
                });
                Test("Invalid structured preview cannot poison another document cache",delegate{var pane=new PreviewPane();window.Content=new Grid{Children={pane}};Pump();var invalid=new Qingye.Core.Document{Path=Path.Combine(root,"invalid.json")};invalid.SetLoadedText("{broken}");try{foreach(string ext in new[]{".json",".md"}){var valid=new Qingye.Core.Document{Path=Path.Combine(root,"valid"+ext)};valid.SetLoadedText(ext==".json"?"{\"valid\":true}":"# Valid document");Await(pane.ShowAsync(valid,new[]{valid,invalid}));Await(pane.ShowAsync(invalid,new[]{valid,invalid}));Await(pane.ShowAsync(valid,new[]{valid,invalid}));Check(ext==".json"?FindChild<TreeView>(pane)!=null:FindChild<FlowDocumentScrollViewer>(pane)!=null,"Other document's error remained in cached preview");}}finally{Await(pane.CloseAsync());}});
                Test("PPTX opens in native reading with headings and tables",delegate{
                    string file=Path.Combine(root,"slides.pptx");
                    var entries=new System.Collections.Generic.Dictionary<string,string>{
                        {"ppt/presentation.xml","<p:presentation xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><p:sldIdLst><p:sldId id='256' r:id='r1'/></p:sldIdLst></p:presentation>"},
                        {"ppt/_rels/presentation.xml.rels","<Relationships><Relationship Id='r1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide' Target='slides/slide1.xml'/></Relationships>"},
                        {"ppt/slides/slide1.xml","<p:sld xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><p:cSld><p:spTree><p:sp><p:nvSpPr><p:nvPr><p:ph type='title'/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>季度报告</a:t></a:r></a:p></p:txBody></p:sp><p:graphicFrame><a:tbl><a:tr><a:tc><a:txBody><a:p><a:r><a:t>收入</a:t></a:r></a:p></a:txBody></a:tc><a:tc><a:txBody><a:p><a:r><a:t>42</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl></p:graphicFrame></p:spTree></p:cSld></p:sld>"}
                    };
                    using(var output=File.Create(file))using(var zip=new System.IO.Compression.ZipArchive(output,System.IO.Compression.ZipArchiveMode.Create))foreach(var entry in entries)using(var writer=new StreamWriter(zip.CreateEntry(entry.Key).Open()))writer.Write(entry.Value);
                    byte[] before=File.ReadAllBytes(file);Await(window.OpenPathsAsync(new[]{file},false));
                    Check(window.Appearance.Layout=="reader" && window.Current.IsReadOnly,"PPTX did not default to readonly reading");
                    var pane=window.TestHooks.Preview;Await(pane.ShowAsync(window.Current,window.Session.Documents));
                    var flow=FindChild<FlowDocumentScrollViewer>(pane);Check(flow!=null && pane.Browser==null,"PPTX started a webpage engine");
                    string text=new System.Windows.Documents.TextRange(flow.Document.ContentStart,flow.Document.ContentEnd).Text;
                    Check(text.Contains("第 1 页 · 季度报告") && text.Contains("收入") && flow.Document.Blocks.OfType<System.Windows.Documents.Table>().Count()==1,"Slide headings or tables missing");
                    Check(before.SequenceEqual(File.ReadAllBytes(file)),"Reader changed the archive");
                });
                Test("PPTX malformed content leaves a readable error",delegate{
                    string file=Path.Combine(root,"invalid.pptx");File.WriteAllText(file,"invalid archive");
                    var pane=new PreviewPane();window.Content=new Grid{Children={pane}};
                    var doc=new Qingye.Core.Document{Path=file,Kind=Qingye.Core.DocumentKind.Pptx,IsReadOnly=true};
                    try{Await(pane.ShowAsync(doc,new[]{doc}));Check(pane.Browser==null && Walk(pane).OfType<TextBlock>().Any(text=>text.Text.Contains("演示文稿无法显示")),"PPTX failure is blank or started a browser");}finally{Await(pane.CloseAsync());}
                });
                Test("EPUB opens in native reading with chapter text and no browser",delegate{
                    string file=Path.Combine(root,"native.epub");CreateEpub(file);
                    var doc=Qingye.Core.FileService.Read(file);Check(doc.Kind==Qingye.Core.DocumentKind.Epub && doc.IsReadOnly,"EPUB was not recognized as a readonly book");
                    var pane=new PreviewPane();window.Content=new Grid{Children={pane}};Pump();
                    try{Await(pane.ShowAsync(doc,new[]{doc}));var flow=FindChild<FlowDocumentScrollViewer>(pane);Check(flow!=null && new System.Windows.Documents.TextRange(flow.Document.ContentStart,flow.Document.ContentEnd).Text.Contains("开始") && pane.Browser==null,"EPUB native reading failed");}
                    finally{Await(pane.CloseAsync());}
                });
                Test("Word basic layout preserves headings, bold runs and tables",delegate{
                    var body=System.Xml.Linq.XElement.Parse("<w:body xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:p><w:pPr><w:pStyle w:val='Heading1'/></w:pPr><w:r><w:t>文档标题</w:t></w:r></w:p><w:p><w:r><w:rPr><w:b/></w:rPr><w:t>重点</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>表格</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body>");var view=OfficeReader.Create(body);var blocks=view.Document.Blocks.ToArray();Check(((System.Windows.Documents.Paragraph)blocks[0]).FontSize==30 && blocks.OfType<System.Windows.Documents.Table>().Count()==1,"Word layout flattened");Check(new System.Windows.Documents.TextRange(view.Document.ContentStart,view.Document.ContentEnd).Text.Contains("表格"),"Word content lost");
                });
                Test("RTF defaults to native rich text without a browser",delegate{
                    string file=Path.Combine(root,"rich.rtf");File.WriteAllText(file,"{\\rtf1\\ansi Plain \\b bold\\b0}");var doc=Qingye.Core.FileService.Read(file);Check(doc.Kind==Qingye.Core.DocumentKind.Rtf && doc.IsReadOnly && Qingye.Core.FilePresentation.For(doc).PreferReader,"RTF was treated as editable code");var pane=new PreviewPane();window.Content=new Grid{Children={pane}};Pump();try{Await(pane.ShowAsync(doc,new[]{doc}));var flow=FindChild<FlowDocumentScrollViewer>(pane);Check(flow!=null && new System.Windows.Documents.TextRange(flow.Document.ContentStart,flow.Document.ContentEnd).Text.Contains("bold") && pane.Browser==null,"RTF native view failed");}finally{Await(pane.CloseAsync());}
                });
                Test("ISO Strict WordprocessingML has the same readable content",delegate{var body=System.Xml.Linq.XElement.Parse("<w:body xmlns:w='http://purl.oclc.org/ooxml/wordprocessingml/main'><w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Strict 正文</w:t></w:r></w:p></w:body>");var reader=OfficeReader.Create(body);Check(new System.Windows.Documents.TextRange(reader.Document.ContentStart,reader.Document.ContentEnd).Text.Contains("Strict 正文"),"Strict DOCX opened blank");});
                Test("Word bounds inline objects inside a single run",delegate{System.Xml.Linq.XNamespace ns="http://schemas.openxmlformats.org/wordprocessingml/2006/main";var body=new System.Xml.Linq.XElement(ns+"body",new System.Xml.Linq.XElement(ns+"p",new System.Xml.Linq.XElement(ns+"r",Enumerable.Range(0,22000).Select(i=>new System.Xml.Linq.XElement(ns+"br")))));var flow=OfficeReader.Create(body).Document;var p=flow.Blocks.OfType<System.Windows.Documents.Paragraph>().First();var span=(System.Windows.Documents.Span)p.Inlines.FirstInline;Check(span.Inlines.Count<=20000 && new System.Windows.Documents.TextRange(flow.ContentStart,flow.ContentEnd).Text.Contains("超出基础预览范围"),"Single run bypassed Office node budget");});
                Test("Word content controls retain text and oversized layout is bounded",delegate{string ns="http://schemas.openxmlformats.org/wordprocessingml/2006/main";var body=System.Xml.Linq.XElement.Parse("<w:body xmlns:w='"+ns+"'><w:sdt><w:sdtContent><w:p><w:r><w:t>内容控件正文</w:t></w:r></w:p></w:sdtContent></w:sdt></w:body>");var view=OfficeReader.Create(body);Check(new System.Windows.Documents.TextRange(view.Document.ContentStart,view.Document.ContentEnd).Text.Contains("内容控件正文"),"Content control text disappeared");var large=new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("body",ns),Enumerable.Range(0,1600).Select(i=>new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("p",ns))));Check(OfficeReader.Create(large).Document.Blocks.Count<=1501,"Word preview layout unbounded");});
                Test("Markdown disables raw executable markup",delegate{string html=WebContent.MarkdownHtml("# 中文\n\n<script>alert(1)</script>");Check(html.Contains("<h1") && html.Contains("中文") && !html.Contains("<script>") && html.Contains("script-src 'none'"),"Markdown can run raw scripts");});
                Test("Markdown reading does not start a browser process",delegate{var pane=new PreviewPane();window.Content=new Grid{Children={pane}};Pump();string file=Path.Combine(root,"read.md");File.WriteAllText(file,"# 原生阅读\n\n**重点**与中文\n\n| 名称 | 值 |\n| --- | --- |\n| 轻页 | 42 |\n\n```python\nprint('你好')\n```\n<script>evil()</script>");var doc=Qingye.Core.FileService.Read(file);try{Await(pane.ShowAsync(doc,new[]{doc}));Check(pane.Browser==null,"Markdown started webpage engine");var reader=pane.Child as FrameworkElement;var flow=FindChild<System.Windows.Controls.FlowDocumentScrollViewer>(reader);Check(flow!=null && new System.Windows.Documents.TextRange(flow.Document.ContentStart,flow.Document.ContentEnd).Text.Contains("原生阅读"),"Native reading contents missing");Check(flow.Document.Blocks.OfType<System.Windows.Documents.Table>().Any(),"Markdown table missing");}finally{Await(pane.CloseAsync());}});
                Test("Content brushes change live with all three themes",delegate{
                    var reader=NativeMarkdown.Create(NativeMarkdown.Parse("# Theme\n\n> Quote\n\n```cs\nvar value = 42;\n```\n\n| Name | Value |\n| --- | --- |\n| Item | 42 |"),null,null);
                    window.Content=new Grid{Children={reader}};
                    var code=reader.Document.Blocks.OfType<System.Windows.Documents.Paragraph>().First(p=>p.Background!=null);
                    var backgrounds=new System.Collections.Generic.List<System.Windows.Media.Color>();
                    foreach(string name in new[]{"mint","warm","lilac","dark"}){window.SetTheme(name);Pump();backgrounds.Add(((System.Windows.Media.SolidColorBrush)code.Background).Color);}
                    Check(backgrounds.Distinct().Count()==4,"Code block background stayed fixed across themes");
                    var quote=reader.Document.Blocks.OfType<System.Windows.Documents.Section>().First();
                    Check(quote.BorderBrush==window.FindResource("QuoteBorder"),"Quote border does not use its token");
                    var table=reader.Document.Blocks.OfType<System.Windows.Documents.Table>().First();
                    Check(table.RowGroups[0].Rows[0].Background==window.FindResource("TableHeader"),"Table header does not use its token");
                });
                Test("Native Markdown preserves entities and automatic links",delegate{var reader=NativeMarkdown.Create(NativeMarkdown.Parse("Before &amp; &copy; after <https://example.com>"),null,null);string text=new System.Windows.Documents.TextRange(reader.Document.ContentStart,reader.Document.ContentEnd).Text;Check(text.Contains("&") && text.Contains("©") && text.Contains("https://example.com"),"Standard Markdown text or links silently disappeared");});
                Test("Native reading fills wide and narrow windows with compact side padding",delegate{var reader=NativeMarkdown.Create(NativeMarkdown.Parse("# 随窗口排版\n\n"+String.Join(" ",Enumerable.Repeat("Reading follows the window.",30))),null,null);window.Content=new Grid{Children={reader}};window.Width=1600;window.Height=700;Pump();Check(reader.ActualWidth>=window.ActualWidth-2 && Double.IsPositiveInfinity(reader.MaxWidth),"Reading is still capped or centered");Check(reader.Document.PagePadding.Left<=24,"Wide reading padding is excessive");window.Width=360;window.Height=480;Pump();Check(Math.Abs(reader.ActualWidth-window.ActualWidth)<2 && reader.Document.PagePadding.Left==16,"Narrow reading does not reflow");Check(new System.Windows.Documents.TextRange(reader.Document.ContentStart,reader.Document.ContentEnd).Text.Contains("随窗口排版"),"Resize lost reader content");window.Width=1240;window.Height=820;Pump();});
                Test("Live HTML preview renders unsaved text",delegate{
                    var pane=new PreviewPane();string profile=null;int browserId=0;
                    try{var doc=new Qingye.Core.Document{Title="index.html",Path=Path.Combine(root,"index.html")};doc.Text="<html><body><h1>UNSAVED 中文</h1></body></html>";window.Content=new Grid{Children={pane}};Pump();Await(pane.ShowAsync(doc,new[]{doc}));CheckBrowser(pane);profile=pane.ProfileDirectory;browserId=(int)pane.Browser.CoreWebView2.BrowserProcessId;var task=pane.Browser.CoreWebView2.ExecuteScriptAsync("document.body.innerText.includes('UNSAVED 中文')");Await(task);if(task.Result!="true"){var diagnostic=pane.Browser.CoreWebView2.ExecuteScriptAsync("document.body.innerText");Await(diagnostic);Console.WriteLine("Preview source: "+pane.Browser.CoreWebView2.Source);Console.WriteLine("Preview fixture DOM: "+diagnostic.Result);}Check(task.Result=="true","Preview did not use the edited buffer");}
                    finally{Await(pane.CloseAsync());}
                    bool exited=false;try{using(var browser=System.Diagnostics.Process.GetProcessById(browserId))exited=browser.HasExited;}catch(ArgumentException){exited=true;}Check(exited,"Preview browser survived close");Check(!Directory.Exists(profile),"Preview cache retained after close");
                });
                Test("Unchanged HTML stays loaded and edited CSS refreshes it",delegate{var pane=new PreviewPane();window.Content=new Grid{Children={pane}};Pump();var html=new Qingye.Core.Document{Title="cached.html",Path=Path.Combine(root,"cached.html")};html.Text="<link rel='stylesheet' href='style.css'><body><h1>cached</h1></body>";var css=new Qingye.Core.Document{Title="style.css",Path=Path.Combine(root,"style.css")};css.Text="body{background:rgb(1,2,3)}";try{Await(pane.ShowAsync(html,new[]{html,css}));CheckBrowser(pane);int starts=0;pane.Browser.CoreWebView2.NavigationStarting+=delegate{starts++;};Await(pane.ShowAsync(html,new[]{html,css}));Check(starts==0,"Unchanged layout reloads page");css.Text="body{background:rgb(4,5,6)}";Await(pane.ShowAsync(html,new[]{html,css}));Check(starts>0,"Edited CSS did not refresh");var color=pane.Browser.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor");Await(color);Check(color.Result.Contains("4, 5, 6"),"Page ignored unsaved CSS");}finally{Await(pane.CloseAsync());}});
                Test("Completed resource requests do not exhaust preview quota",delegate{var pane=new PreviewPane();window.Content=new Grid{Children={pane}};Pump();var html=new Qingye.Core.Document{Title="fetch.html",Path=Path.Combine(root,"fetch.html")};html.Text="<h1>fetch test</h1>";File.WriteAllText(Path.Combine(root,"response.txt"),"response");try{Await(pane.ShowAsync(html,new[]{html}));CheckBrowser(pane);var start=pane.Browser.CoreWebView2.ExecuteScriptAsync("window.completed=0;window.lastStatus=0;(async()=>{for(let i=0;i<530;i++){let r=await fetch('response.txt?'+i);await r.text();window.lastStatus=r.status;window.completed++;}})()");Await(start);var deadline=DateTime.UtcNow.AddSeconds(15);int count=0;while(count<530 && DateTime.UtcNow<deadline){var poll=pane.Browser.CoreWebView2.ExecuteScriptAsync("window.completed");Await(poll);Int32.TryParse(poll.Result,out count);System.Threading.Thread.Sleep(5);}Check(count==530,"Sequential fetches did not finish");var status=pane.Browser.CoreWebView2.ExecuteScriptAsync("window.lastStatus");Await(status);Check(status.Result=="200","Finished requests still consume quota");}finally{Await(pane.CloseAsync());}});
                Test("Close waits for a preview being initialized and replaced",delegate{var pane=new PreviewPane();window.Content=new Grid{Children={pane}};Pump();var html=new Qingye.Core.Document{Title="race.html",Path=Path.Combine(root,"race.html")};html.Text="<h1>race</h1>";var text=new Qingye.Core.Document{Title="plain.txt"};text.Text="plain";var first=pane.ShowAsync(html,new[]{html});string profile=pane.ProfileDirectory;var replacement=pane.ShowAsync(text,new[]{text});try{Await(pane.CloseAsync());Check(!Directory.Exists(profile),"Close finished before initialization cleanup");Check(first.IsCompleted && replacement.IsCompleted,"Preview work still active after close");}finally{Await(Task.WhenAll(first,replacement));Await(pane.CloseAsync());}});
                Test("Workspace search shows results from imported files",delegate{
                    string folder=Path.Combine(root,"search-ws");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"hit.txt"),"needle in workspace",System.Text.Encoding.UTF8);
                    window.ImportWorkspace(folder);Pump();window.UpdateToolbar();
                    var search=window.TestHooks.Search;Check(search.WorkspaceOptionVisible,"Workspace search option missing");
                    search.RunWorkspaceSearchForTest("needle");
                    var deadline=DateTime.UtcNow.AddSeconds(10);while(search.WorkspaceResultCount==0 && DateTime.UtcNow<deadline){Pump();System.Threading.Thread.Sleep(10);}
                    Check(search.WorkspaceResultCount>0,"Workspace search returned no results");
                });





                Test("ComboBox closed state follows the dark palette",delegate{
                    // The Aero2 platform template paints a fixed light toggle button,
                    // so the app supplies its own ComboBox template; this keeps the
                    // permission picker readable in the dark theme.
                    var host=new Window{Width=420,Height=320,Left=-16000,Top=0,ShowActivated=false};Theme.Install(host);Theme.Apply(host,new Qingye.Core.Settings{Theme="dark"});
                    var box=new ComboBox{ItemsSource=new[]{"只读","每次确认","自动编辑工作区"},SelectedIndex=1};
                    host.Content=new StackPanel{Children={box}};host.Show();Pump();box.ApplyTemplate();host.UpdateLayout();Pump();
                    try
                    {
                        var border=(Border)box.Template.FindName("root",box);
                        Check(border!=null && border.Background is System.Windows.Media.SolidColorBrush && ((System.Windows.Media.SolidColorBrush)border.Background).Color==((System.Windows.Media.SolidColorBrush)host.FindResource("SurfaceBrush")).Color,"Dark ComboBox keeps the light platform background");
                        Check(((System.Windows.Media.SolidColorBrush)box.Foreground).Color==((System.Windows.Media.SolidColorBrush)host.FindResource("TextBrush")).Color,"Dark ComboBox keeps the light platform text");
                    }
                    finally{host.Close();Pump();}
                });
                Test("Sidebar outline is type-aware and navigates to source",delegate{
                    string file=Path.Combine(root,"outline-ui.md");File.WriteAllText(file,"# 第一节\n\n正文\n\n## 第二节\n\n更多");
                    Await(window.OpenPathsAsync(new[]{file},false));window.SetViewMode("single");window.ToggleSidePanel();Pump();
                    var side=window.TestHooks.Side;side.SetMode(true);Pump();Check(side.Outline.Count==2,"Sidebar outline is missing headings");
                    side.Outline.list.SelectedIndex=1;Pump();Pump();
                    Check(Editor().TextArea.Caret.Line>=5,"Outline navigation did not move the source caret");
                });
                Test("In-place Markdown editing commits to the source buffer",delegate{
                    string file=Path.Combine(root,"inplace.md");File.WriteAllText(file,"# 原位\n\n原文");
                    Await(window.OpenPathsAsync(new[]{file},false));window.SetViewMode("single");Editor().AppendText("\n\n源码修改");window.SetViewMode("wysiwyg");Pump();
                    Check(window.Appearance.Layout=="wysiwyg" && window.RichEditor.IsActive,"In-place editing did not open");
                    window.RichEditor.AppendPlainParagraph("新增段落");
                    window.SetViewMode("single");Pump();
                    Check(Editor().Text.Contains("新增段落") && Editor().Text.Contains("# 原位") && Editor().Text.Contains("源码修改"),"In-place edits were not committed");
                });
                Test("Markdown writer round-trips supported constructs",delegate{
                    string file=Path.Combine(root,"roundtrip.md");File.WriteAllText(file,"# 标题\n\n**重点**与*斜体*\n\n- 一\n- 二\n\n```python\nprint(1)\n```\n\n| 名称 | 值 |\n| --- | --- |\n| 轻页 | 42 |\n");
                    var doc=Qingye.Core.FileService.Read(file);var editor=new MarkdownRichEditor(window);string reason;Check(editor.Load(doc,out reason),reason??"Rich Markdown failed to load");
                    string markdown=editor.Serialize();Check(markdown.Contains("# 标题") && markdown.Contains("**重点**") && markdown.Contains("*斜体*") && markdown.Contains("```python") && markdown.Contains("| 名称 |"),"Markdown constructs lost: "+markdown);
                });
                Test("Export filter is a native null-separated list",delegate{
                    string file=Path.Combine(root,"filter.md");File.WriteAllText(file,"# filter");var doc=Qingye.Core.FileService.Read(file);var formats=FormatExport.Formats(doc);
                    string filter=FormatExport.Filter(formats);Check(!filter.Contains("|") && filter.Contains("\0") && filter.Split('\0').Length==formats.Count*2,"Export filter is malformed");
                });
                Test("Export converts without changing the source",delegate{
                    foreach(var sessionDoc in window.Session.Documents){sessionDoc.Modified=false;sessionDoc.MetadataModified=false;}
                    string file=Path.Combine(root,"export.md");File.WriteAllText(file,"# 导出\n\n正文");var doc=Qingye.Core.FileService.Read(file);
                    string html=Path.Combine(root,"export.html");Check(window.Exports.ExportTo(doc,html,ExportFormat.Html) && File.ReadAllText(html).Contains("<h1"),"HTML export failed");
                    string docx=Path.Combine(root,"export.docx");Check(window.Exports.ExportTo(doc,docx,ExportFormat.Docx),"DOCX export failed");
                    using(var zip=new System.IO.Compression.ZipArchive(File.OpenRead(docx))){var entry=zip.GetEntry("word/document.xml");Check(entry!=null && new StreamReader(entry.Open()).ReadToEnd().Contains("导出"),"DOCX content missing");}
                    var csv=new Qingye.Core.Document{Title="data.csv",Path=Path.Combine(root,"data.csv")};csv.SetLoadedText("a,b\r\n1,2");
                    string xlsx=Path.Combine(root,"generated.xlsx");Check(window.Exports.ExportTo(csv,xlsx,ExportFormat.Xlsx),"XLSX export failed");
                    var table=Qingye.Core.ContentReaders.ReadXlsx(xlsx);Check(table.Rows[0][0]=="a" && table.Rows[1][1]=="2","XLSX roundtrip failed");
                    var gb=new Qingye.Core.Document{Title="gb.txt",Path=Path.Combine(root,"gb.txt")};gb.SetLoadedText("中文");gb.EncodingName="gb18030";
                    Check(System.Text.Encoding.GetEncoding("gb18030").GetString(FormatExport.Bytes(gb,ExportFormat.Text))=="中文","Text export lost the source encoding");
                    Check(File.ReadAllText(file)=="# 导出\n\n正文","Export changed the source");
                    string live=Path.Combine(root,"live.md");File.WriteAllText(live,"# 旧");Await(window.OpenPathsAsync(new[]{live},false));window.SetViewMode("single");Editor().Text="# 新";
                    string liveHtml=Path.Combine(root,"live.html");Check(window.Exports.ExportTo(window.Current,liveHtml,ExportFormat.Html) && File.ReadAllText(liveHtml).Contains("新"),"Export ignored unsaved source edits");
                });
                Test("PDF export writes a PDF file",delegate{
                    var pane=new PreviewPane();window.Content=new Grid{Children={pane}};Pump();string file=Path.Combine(root,"export.pdf");
                    try{Await(pane.SaveHtmlAsPdfAsync("<html><body><h1>PDF 导出</h1></body></html>",file));Check(File.Exists(file) && new FileInfo(file).Length>100,"PDF export produced no file");Check(System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file).Take(5).ToArray())=="%PDF-","PDF header missing");}finally{Await(pane.CloseAsync());}
                });
                Test("Print moves source views to reading and explains non-printable files",delegate{
                    foreach(var doc in window.Session.Documents){doc.Modified=false;doc.MetadataModified=false;}
                    string md=Path.Combine(root,"print-guide.md");File.WriteAllText(md,"# 打印\n\n正文段落");
                    Await(window.OpenPathsAsync(new[]{md},false));window.SetViewMode("single");Pump();
                    var prepared=window.PreparePrintAsync();Await(prepared);
                    Check(prepared.Result && window.Appearance.Layout=="reader","Print did not move the source view to a printable reading view");
                    var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(2,2,96,96,System.Windows.Media.PixelFormats.Bgra32,null,new byte[16],8);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));string image=Path.Combine(root,"print-image.png");using(var file=File.Create(image))png.Save(file);
                    Await(window.OpenPathsAsync(new[]{image},false));Pump();
                    var rejected=window.PreparePrintAsync();Await(rejected);
                    Check(!rejected.Result,"Non-printable content was treated as printable");
                    Check(window.TestHooks.Footer.notice.Text.Contains("没有可打印内容"),"Non-printable content produced no visible explanation");
                });
                Test("Markdown HTML is announced instead of silently escaped",delegate{
                    var content=Qingye.UI.NativeMarkdown.Parse("# 标题\n\n<div align=\"center\">块</div>\n\n正文");
                    Check(Qingye.UI.NativeMarkdown.HasHtmlBlocks(content.Source),"HTML block was not detected");
                    var viewer=Qingye.UI.NativeMarkdown.Create(content,null,null);
                    string text=new System.Windows.Documents.TextRange(viewer.Document.ContentStart,viewer.Document.ContentEnd).Text;
                    Check(text.Contains("网页代码") && text.Contains("纯文本显示"),"Rendered markdown has no HTML explanation");
                    var fenced=Qingye.UI.NativeMarkdown.Parse("# 标题\n\n```\n<div>示例代码</div>\n```");
                    Check(!Qingye.UI.NativeMarkdown.HasHtmlBlocks(fenced.Source),"Fenced code was misreported as an HTML block");
                    string output=Path.GetFullPath(Path.Combine(root,"..","..","..","artifacts","ui"));Directory.CreateDirectory(output);
                    var host=new Grid{Children={Qingye.UI.NativeMarkdown.Create(Qingye.UI.NativeMarkdown.Parse("# 带网页代码的文档\n\n正常段落。\n\n<div align=\"center\">块内容</div>\n\n## 小节\n\n- 条目"),null,null)}};
                    var previewWindow=new Window{ShowActivated=false,Left=-16000,Top=0,Width=900,Height=700,Content=host};previewWindow.Show();Pump();
                    try{CaptureElement(host,Path.Combine(output,"0.15-markdown-html-notice.png"));}finally{previewWindow.Close();Pump();}
                });
                Test("View button accessible names follow their visible labels",delegate{
                    foreach(var doc in window.Session.Documents){doc.Modified=false;doc.MetadataModified=false;}
                    string csv=Path.Combine(root,"accessible.csv");File.WriteAllText(csv,"a,b\r\n1,2");
                    Await(window.OpenPathsAsync(new[]{csv},false));Pump();
                    Check((string)window.TestHooks.DocumentBar.readViewButton.Content=="表格","CSV reader label missing");
                    Check(System.Windows.Automation.AutomationProperties.GetName(window.TestHooks.DocumentBar.readViewButton)=="表格","Table button exposes a stale accessible name");
                    Check(System.Windows.Automation.AutomationProperties.GetName(window.TestHooks.DocumentBar.sourceViewButton)==(string)window.TestHooks.DocumentBar.sourceViewButton.Content,"Source button accessible name does not match its label");
                });
                Test("Wheel scrolling is proportional and accelerates during a spin",delegate{
                    foreach(var doc in window.Session.Documents){doc.Modified=false;doc.MetadataModified=false;}
                    string file=Path.Combine(root,"wheel.txt");var sb=new System.Text.StringBuilder();for(int i=0;i<2000;i++)sb.Append("wheel line ").Append(i).Append('\n');File.WriteAllText(file,sb.ToString());
                    Await(window.OpenPathsAsync(new[]{file},false));window.SetViewMode("single");Pump();
                    var editor=window.ActiveEditor;editor.ScrollToVerticalOffset(1000);Pump();
                    double start=editor.VerticalOffset;
                    RaiseWheel(editor.TextArea,-120);Pump();
                    double one=editor.VerticalOffset-start;
                    Check(one>40 && one<120,"A wheel notch no longer moves a few lines");
                    editor.ScrollToVerticalOffset(1000);Pump();start=editor.VerticalOffset;
                    for(int i=0;i<4;i++)RaiseWheel(editor.TextArea,-120);
                    Pump();
                    Check(editor.VerticalOffset-start>one*1.5,"Fast spinning does not cover more ground per notch");
                    editor.ScrollToVerticalOffset(1000);Pump();double before=editor.VerticalOffset;
                    RaiseWheel(editor.TextArea,-30);Pump();
                    Check(editor.VerticalOffset>before+5,"High-resolution wheel deltas are dropped instead of accumulated");
                });
                Test("Reading view uses the same proportional wheel handling",delegate{
                    var sb=new System.Text.StringBuilder();for(int i=0;i<400;i++)sb.Append("## 小节 ").Append(i).Append("\n\n这一节用来验证阅读视图的滚轮距离，内容重复以便形成足够的滚动范围。\n\n");
                    var viewer=Qingye.UI.NativeMarkdown.Create(Qingye.UI.NativeMarkdown.Parse(sb.ToString()),null,null);
                    var host=new Grid{Children={viewer}};
                    var probeWindow=new Window{ShowActivated=false,Left=-16000,Top=0,Width=900,Height=700,Content=host};probeWindow.Show();
                    try
                    {
                        probeWindow.UpdateLayout();Pump();
                        var scroll=FindChild<ScrollViewer>(viewer);
                        Check(scroll!=null,"Reading viewer has no scroll surface");
                        Check(scroll.ScrollableHeight>200,"Reading view is not scrollable: "+scroll.ScrollableHeight);
                        scroll.ScrollToVerticalOffset(0);Pump();
                        double start=scroll.VerticalOffset;
                        RaiseWheel(viewer,-120);Pump();
                        double one=scroll.VerticalOffset-start;
                        Check(one>40,"Reading view wheel scroll is too small: "+one);
                        scroll.ScrollToVerticalOffset(0);Pump();start=scroll.VerticalOffset;
                        for(int i=0;i<4;i++)RaiseWheel(viewer,-120);
                        Pump();
                        Check(scroll.VerticalOffset-start>one*1.5,"Reading view ignores fast wheel spinning");
                    }
                    finally{probeWindow.Close();Pump();}
                });
                RunEditorConvenienceTests(window,root);
                RunZoomAndColorTests(window,root);
            }
            finally
            {
                foreach(var doc in window.Session.Documents)doc.Modified=false;
                window.Close();Pump();app.Shutdown();if(Qingye.Core.FileService.IsInsideRoot(AppDomain.CurrentDomain.BaseDirectory,root))Directory.Delete(root,true);
            }
            Console.WriteLine("UI: "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
        // 0.16.0 code-editing conveniences: sentence-level document/control tests
        // for indentation, pairs, comment toggling and line operations.
        static void RunEditorConvenienceTests(MainWindow window,string root)
        {
            Test("Python indentation follows blocks and dedents keywords",delegate{
                var editor=CreateCodeEditor("def f():\n",".py");
                editor.Document.Insert(editor.Document.TextLength,"x");
                ApplyIndent(editor,2);
                Check(editor.Document.Text=="def f():\n    x","Python block did not open with four spaces: "+editor.Document.Text);
                editor.Document.Text="if x:\n    pass\nelse:";
                ApplyIndent(editor,3);
                Check(editor.Document.Text=="if x:\n    pass\nelse:","else was indented instead of aligned with its block: "+editor.Document.Text);
                editor.Document.Text="if x:\n    pass\n        else:";
                ApplyIndent(editor,3);
                Check(editor.Document.Text=="if x:\n    pass\nelse:","else did not dedent one level: "+editor.Document.Text);
            });
            Test("Language profiles choose tab and indentation conventions",delegate{
                var editor=CreateCodeEditor("","");
                CodeEditing.Configure(editor,".py");
                Check(editor.Options.ConvertTabsToSpaces && editor.Options.IndentationSize==4,"Python should type spaces with four-column indentation");
                Check(editor.TextArea.IndentationStrategy!=null,"Python indentation strategy missing");
                CodeEditing.Configure(editor,".cs");
                Check(editor.Options.ConvertTabsToSpaces,"Brace languages should use spaces");
                CodeEditing.Configure(editor,".bat");
                Check(!editor.Options.ConvertTabsToSpaces,"Batch files should keep tabs");
                CodeEditing.Configure(editor,".mk");
                Check(!editor.Options.ConvertTabsToSpaces,"Makefiles should keep tabs");
                CodeEditing.Configure(editor,".txt");
                Check(editor.TextArea.IndentationStrategy!=null,"Plain text should keep a default indentation strategy");
            });
            Test("Brace language indentation copies columns and opens blocks",delegate{
                var editor=CreateCodeEditor("class A {\n",".cs");
                ApplyIndent(editor,2);
                Check(editor.Document.Text=="class A {\n    ","An opening brace did not add one indentation level: "+editor.Document.Text);
                editor.Document.Text="    foo();\nbar";
                ApplyIndent(editor,2);
                Check(editor.Document.Text=="    foo();\n    bar","Brace language did not copy the previous indentation: "+editor.Document.Text);
            });
            Test("Indent and unindent selected lines undo in one step",delegate{
                var editor=CreateCodeEditor("alpha\nbeta\n",".py");
                editor.Select(0,10);
                CodeEditing.IndentLines(editor,false);
                Check(editor.Document.Text=="    alpha\n    beta\n","Selected lines were not indented: "+editor.Document.Text);
                editor.Document.UndoStack.Undo();
                Check(editor.Document.Text=="alpha\nbeta\n","One undo did not restore the indented block");
                var unindented=CreateCodeEditor("    alpha\n    beta\n",".py");
                unindented.Select(0,18);
                CodeEditing.IndentLines(unindented,true);
                Check(unindented.Document.Text=="alpha\nbeta\n","Selected lines were not unindented: "+unindented.Document.Text);
            });
            Test("Duplicate and move lines keep text and caret",delegate{
                var editor=CreateCodeEditor("one\ntwo\n",".py");
                editor.CaretOffset=5;
                CodeEditing.DuplicateLine(editor);
                Check(editor.Document.Text=="one\ntwo\ntwo\n","Duplicate line did not copy below: "+editor.Document.Text);
                Check(editor.SelectedText=="two","Duplicate line did not select the copied text");
                editor.Document.UndoStack.Undo();
                Check(editor.Document.Text=="one\ntwo\n","Duplicate line needs one undo step");
                editor.Select(5,0);
                CodeEditing.MoveLines(editor,-1);
                Check(editor.Document.Text=="two\none\n","Move up did not swap the lines: "+editor.Document.Text);
                Check(editor.CaretOffset==1,"Move up lost the caret column: "+editor.CaretOffset);
                CodeEditing.MoveLines(editor,1);
                Check(editor.Document.Text=="one\ntwo\n","Move down did not restore the order: "+editor.Document.Text);
                Check(editor.CaretOffset==5,"Move down lost the caret column: "+editor.CaretOffset);
                CodeEditing.MoveLines(editor,-1);
                CodeEditing.MoveLines(editor,-1);
                Check(editor.Document.Text=="two\none\n","Moving above the first line changed nothing instead of stopping");
            });
            Test("Line comments toggle on Python and stay inert for Markdown",delegate{
                var editor=CreateCodeEditor("x = 1\ny = 2\n",".py");
                editor.Select(0,12);
                CodeEditing.ToggleComment(editor);
                Check(editor.Document.Text=="# x = 1\n# y = 2\n","Python lines were not commented: "+editor.Document.Text);
                CodeEditing.ToggleComment(editor);
                Check(editor.Document.Text=="x = 1\ny = 2\n","Second toggle did not remove the comment prefix: "+editor.Document.Text);
                editor.Document.UndoStack.Undo();
                Check(editor.Document.Text=="# x = 1\n# y = 2\n","Comment toggle should undo as one step per toggle");
                var prose=CreateCodeEditor("plain text",".md");
                prose.Select(0,10);
                CodeEditing.ToggleComment(prose);
                Check(prose.Document.Text=="plain text","Markdown source must not gain a line comment");
            });
            Test("HTML comment toggle wraps and unwraps selections and lines",delegate{
                var editor=CreateCodeEditor("<div>x</div>",".html");
                editor.Select(0,12);
                CodeEditing.ToggleComment(editor);
                Check(editor.Document.Text=="<!--<div>x</div>-->","HTML selection was not wrapped: "+editor.Document.Text);
                Check(editor.SelectedText=="<div>x</div>","Wrapped HTML selection was not kept on the content");
                CodeEditing.ToggleComment(editor);
                Check(editor.Document.Text=="<div>x</div>","Second HTML toggle did not remove the wrapper: "+editor.Document.Text);
                editor.CaretOffset=5;
                CodeEditing.ToggleComment(editor);
                Check(editor.Document.Text=="<!--<div>x</div>-->","HTML line was not wrapped: "+editor.Document.Text);
                CodeEditing.ToggleComment(editor);
                Check(editor.Document.Text=="<div>x</div>","HTML line wrapper was not removed: "+editor.Document.Text);
            });
            Test("Auto pairs insert, wrap selections and skip closing characters",delegate{
                var editor=CreateCodeEditor("",".py");
                Check(CodeEditing.CompletePair(editor,"("),"Opening bracket was not handled");
                Check(editor.Document.Text=="()","Opening bracket did not insert its pair: "+editor.Document.Text);
                Check(editor.CaretOffset==1,"Caret is not between the inserted pair");
                Check(CodeEditing.CompletePair(editor,")"),"Typed closing bracket was not handled");
                Check(editor.Document.Text=="()" && editor.CaretOffset==2,"Typed closing bracket duplicated or did not move the caret");
                editor.Document.Text="abc";
                editor.Select(0,3);
                Check(CodeEditing.CompletePair(editor,"("),"Opening bracket around a selection was not handled");
                Check(editor.Document.Text=="(abc)","Selection was not wrapped: "+editor.Document.Text);
                Check(editor.SelectedText=="abc","Wrapped selection was not kept on the content");
                var prose=CreateCodeEditor("",".md");
                Check(!CodeEditing.CompletePair(prose,"("),"Markdown should not auto-pair");
                Check(prose.Document.Text=="","Markdown document was modified by pairing");
            });
            Test("Auto pairs handle quotes without duplicate closers",delegate{
                var editor=CreateCodeEditor("",".py");
                Check(CodeEditing.CompletePair(editor,"\""),"Double quote was not paired");
                Check(editor.Document.Text=="\"\"" && editor.CaretOffset==1,"Double quote pair is wrong: "+editor.Document.Text);
                editor.Document.Text="\"\"";
                editor.CaretOffset=1;
                Check(CodeEditing.CompletePair(editor,"\""),"Typing over an existing closing quote was not handled");
                Check(editor.Document.Text=="\"\"" && editor.CaretOffset==2,"Typed quote duplicated or did not move the caret");
                Check(CodeEditing.CompletePair(editor,"'"),"Single quote was not paired");
                Check(editor.Document.Text=="\"\"''","Single quote pair was not inserted: "+editor.Document.Text);
                Check(!CodeEditing.CompletePair(editor,")"),"A closing bracket with no matching next character should fall through");
            });
            Test("Editor text input wires pairs once and respects prose documents",delegate{
                var editor=new TextEditor();
                editor.Document=new ICSharpCode.AvalonEdit.Document.TextDocument("");
                CodeEditing.Attach(editor);
                CodeEditing.Attach(editor);
                CodeEditing.Configure(editor,".py");
                CodeEditing.Configure(editor,".py");
                editor.TextArea.PerformTextInput("(");
                Check(editor.Document.Text=="()","TextEntering pairing did not run through typed input: "+editor.Document.Text);
                Check(editor.CaretOffset==1,"Typed pair put the caret in the wrong place");
                editor.TextArea.PerformTextInput("abc");
                Check(editor.Document.Text=="(abc)","Typed content did not land inside the pair: "+editor.Document.Text);
                editor.TextArea.PerformTextInput(")");
                Check(editor.Document.Text=="(abc)" && editor.CaretOffset==5,"Typed closing bracket duplicated through input: "+editor.Document.Text);
                var prose=new TextEditor();
                prose.Document=new ICSharpCode.AvalonEdit.Document.TextDocument("");
                CodeEditing.Attach(prose);
                CodeEditing.Configure(prose,".md");
                prose.TextArea.PerformTextInput("(");
                Check(prose.Document.Text=="(","Prose documents should receive plain text input");
            });
        }
        static TextEditor CreateCodeEditor(string text,string extension)
        {
            var editor=new TextEditor();
            editor.Document=new ICSharpCode.AvalonEdit.Document.TextDocument(text);
            CodeEditing.Attach(editor);
            CodeEditing.Configure(editor,extension);
            return editor;
        }
        static void ApplyIndent(TextEditor editor,int lineNumber)
        {
            var strategy=editor.TextArea.IndentationStrategy;
            Check(strategy!=null,"No indentation strategy configured");
            strategy.IndentLine(editor.Document,editor.Document.GetLineByNumber(lineNumber));
        }
        [StructLayout(LayoutKind.Sequential)] struct ProcessBasicInfo { public IntPtr Reserved1,Peb,Reserved2,Reserved3,Pid,Parent; }
        static void CheckBrowser(PreviewPane pane){Check(pane.Browser!=null && pane.Browser.CoreWebView2!=null,"Preview engine not created: "+String.Join(" | ",Walk(pane).OfType<TextBlock>().Select(t=>t.Text)));}
        static T FindChild<T>(DependencyObject parent) where T:DependencyObject{if(parent==null)return null;if(parent is T)return (T)parent;for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++){var found=FindChild<T>(System.Windows.Media.VisualTreeHelper.GetChild(parent,i));if(found!=null)return found;}return null;}
        static void RaiseWheel(UIElement target,int delta){var args=new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,delta);args.RoutedEvent=UIElement.PreviewMouseWheelEvent;target.RaiseEvent(args);}
        static System.Collections.Generic.IEnumerable<DependencyObject> Walk(DependencyObject parent){yield return parent;for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++)foreach(var child in Walk(System.Windows.Media.VisualTreeHelper.GetChild(parent,i)))yield return child;}
        static Rect Bounds(FrameworkElement element,System.Windows.Media.Visual ancestor){return element.TransformToAncestor(ancestor).TransformBounds(new Rect(0,0,element.ActualWidth,element.ActualHeight));}
        static void CreateEpub(string path)
        {
            using(var stream=File.Create(path))using(var zip=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Create))
            {
                ZipText(zip,"mimetype","application/epub+zip");
                ZipText(zip,"META-INF/container.xml","<?xml version='1.0'?><container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='OEBPS/content.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
                ZipText(zip,"OEBPS/content.opf","<?xml version='1.0'?><package xmlns='http://www.idpf.org/2007/opf' xmlns:dc='http://purl.org/dc/elements/1.1/' version='3.0'><metadata><dc:title>阅读测试</dc:title></metadata><manifest><item id='c1' href='text/ch1.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='c1'/></spine></package>");
                ZipText(zip,"OEBPS/text/ch1.xhtml","<html xmlns='http://www.w3.org/1999/xhtml'><head><title>第一章</title></head><body><h1>开始</h1><p>EPUB 正文</p></body></html>");
                var entry=zip.CreateEntry("OEBPS/img/p.png");using(var image=entry.Open()){byte[] bytes=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");image.Write(bytes,0,bytes.Length);}
            }
        }
        static void ZipText(System.IO.Compression.ZipArchive zip,string name,string text){var entry=zip.CreateEntry(name);using(var writer=new StreamWriter(entry.Open(),new System.Text.UTF8Encoding(false)))writer.Write(text);}
        [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process,int type,ref ProcessBasicInfo information,int size,out int returned);
        static int ParentId(this System.Diagnostics.Process p){try{var info=new ProcessBasicInfo();int length;return NtQueryInformationProcess(p.Handle,0,ref info,Marshal.SizeOf(info),out length)==0?info.Parent.ToInt32():-1;}catch{return -1;}}
    }
}
