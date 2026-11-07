using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Diagnostics;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit;
using Qingye.Core;
namespace Qingye.UI
{
    public partial class MainWindow : Window
    {
        public DocumentSession Session { get; private set; }
        public Settings Appearance { get; private set; }
        public string WorkspaceRoot { get { return workspace.Root; } }
        public Task WorkspaceLoadTask { get { return workspace.LoadTask; } }
        internal readonly Dictionary<Document,TextDocument> buffers=new Dictionary<Document,TextDocument>();
        internal readonly Dictionary<Document,ITextSourceVersion> copiedVersions=new Dictionary<Document,ITextSourceVersion>();
        readonly Dictionary<Document,bool> displayedDirty=new Dictionary<Document,bool>();
        internal readonly Dictionary<Document,string> viewModes=new Dictionary<Document,string>();
        internal readonly EditorPane primary,secondary;
        internal EditorPane active;
        internal readonly PreviewPane preview;
        internal readonly RichEditSession richEdit;
        readonly PaneLayout layoutHost;
        readonly Grid shellWork=new Grid();
        readonly Toolbar toolbar;
        internal readonly DocumentBar documentBar;
        readonly Footer footer;
        readonly TabStrip tabStrip;
        readonly AppearancePopup appearancePopup;
        internal readonly SearchBar search;
        readonly WorkspaceTree workspace;
        readonly SidePanel sidePanel;
        internal readonly TerminalPanel terminalPanel;
        readonly GridSplitter terminalSplitter;
        readonly TerminalDockLayout terminalDock;
        internal bool terminalVisible;
        readonly System.Windows.Threading.DispatcherTimer refreshTimer=new System.Windows.Threading.DispatcherTimer();
        readonly string preferencesPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preferences.xml");
        internal readonly BoundedOpenQueue openQueue;
        readonly DocumentOpenHandler openRequests;
        readonly DocumentSaver saver;
        readonly ExportController exports;
        internal bool closing,busy,showSecondary,showPreview,narrowLayout;
        internal Task previewClose=Task.FromResult(0);
        string openQueueNotice;
        internal Button SaveButton {get{return toolbar.saveButton;}}
        internal Button SourceViewButton {get{return documentBar.sourceViewButton;}}
        internal TextEditor ActiveEditor {get{return active.Editor;}}
        internal bool IsClosing {get{return closing;}}
        internal MarkdownRichEditor RichEditor {get{return richEdit.Editor;}}
        internal ExportController Exports {get{return exports;}}
        internal string PreferencesPath {get{return preferencesPath;}}
        internal IDictionary<Document,bool> DisplayedDirty {get{return displayedDirty;}}
        internal WindowTestHooks TestHooks {get;private set;}
        internal void RefreshTabs(){tabStrip.Refresh();if(sidePanel!=null)sidePanel.RefreshFiles();}
        internal Task RefreshWorkspaceAsync(){return workspace.RefreshAsync();} internal void RefreshWorkspace(){Observe(workspace.RefreshAsync());}
        internal void ShowAppearance(Button anchor){appearancePopup.Show(anchor);}
        internal void ToggleSearch(){search.ToggleSearch();}
        public int ReplaceAll(string text,string value,bool useRegex,bool matchCase){return search.ReplaceAll(text,value,useRegex,matchCase);}
        internal void ShowGoToLine(){search.ShowGoToLine();}
        public void ImportWorkspace(string path){workspace.ImportWorkspace(path);sidePanel.Reveal();sidePanel.SetMode(false);sidePanel.RefreshFiles();footer.sessionHint.Text="临时工作区 · 退出即清空";UpdateToolbar();}
        internal void ClearWorkspace(){workspace.ClearWorkspace();UpdateUsageHint();sidePanel.RefreshFiles();UpdateToolbar();}
        public MainWindow()
        {
            Session=new DocumentSession();Appearance=Settings.Load(preferencesPath);Appearance.Layout="single";
            NameScope.SetNameScope(this,new NameScope());
            Width=1240;Height=820;Title="轻页";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResize;WindowStartupLocation=WindowStartupLocation.CenterScreen;
            using(var iconStream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Qingye.Application.ico")){var decoder=System.Windows.Media.Imaging.BitmapDecoder.Create(iconStream,System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);Icon=decoder.Frames.Last();Icon.Freeze();}
            Theme.Install(this);Theme.Apply(this,Appearance);
            WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=56,ResizeBorderThickness=new Thickness(6),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(0),UseAeroCaptionButtons=false});
            var root=new Grid();root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(56)});root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1)});root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(32)});Content=root;

            toolbar=new Toolbar(this);root.Children.Add(toolbar);
            var topLine=Theme.Line();Grid.SetRow(topLine,1);root.Children.Add(topLine);
            documentBar=new DocumentBar(this);Grid.SetRow(documentBar,2);root.Children.Add(documentBar);
            search=new SearchBar(this);Grid.SetRow(search,3);root.Children.Add(search);
            footer=new Footer(this);Grid.SetRow(footer,5);root.Children.Add(footer);
            appearancePopup=new AppearancePopup(this);workspace=new WorkspaceTree(this);sidePanel=new SidePanel(this,workspace);terminalPanel=new TerminalPanel(this);
            tabStrip=new TabStrip(this);layoutHost=new PaneLayout(this,tabStrip);layoutHost.SetResourceReference(Panel.BackgroundProperty,"SurfaceBrush");
            Grid.SetRow(shellWork,4);root.Children.Add(shellWork);shellWork.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(0)});shellWork.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(0)});shellWork.ColumnDefinitions.Add(new ColumnDefinition());shellWork.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(0)});shellWork.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(0)});shellWork.Children.Add(sidePanel);Grid.SetColumn(layoutHost,2);shellWork.Children.Add(layoutHost);shellWork.Children.Add(terminalPanel);Grid.SetColumn(terminalPanel,4);
            terminalSplitter=new GridSplitter{HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,ResizeDirection=GridResizeDirection.Columns,ResizeBehavior=GridResizeBehavior.PreviousAndNext,ShowsPreview=false};terminalSplitter.SetResourceReference(Control.BackgroundProperty,"LineBrush");Grid.SetColumn(terminalSplitter,3);shellWork.Children.Add(terminalSplitter);terminalSplitter.DragCompleted+=delegate{Appearance.TerminalSize=Math.Max(140,Math.Min(620,terminalDock.Right?terminalPanel.ActualWidth:terminalPanel.ActualHeight));};
            terminalDock=new TerminalDockLayout(shellWork,layoutHost,sidePanel,terminalPanel,terminalSplitter);
            primary=new EditorPane("primary",buffers,OnBufferChanged,Placement);secondary=new EditorPane("secondary",buffers,OnBufferChanged,Placement,true);active=primary;
            RegisterName("PrimaryEditor",primary.Editor);secondary.EditorCreated+=delegate{RegisterName("SecondaryEditor",secondary.Editor);};primary.Activated+=ActivatePane;secondary.Activated+=ActivatePane;primary.ZoomRequested+=AdjustTextZoom;secondary.ZoomRequested+=AdjustTextZoom;
            preview=new PreviewPane();preview.PlaceRequested+=Placement;preview.OpenFileRequested+=path=>Observe(OpenPathsAsync(new[]{path},true));preview.ZoomChanged+=delegate(int percent){SetNotice("阅读缩放 "+percent+"%");};
            richEdit=new RichEditSession(this);
            openRequests=new DocumentOpenHandler(this);saver=new DocumentSaver(this);exports=new ExportController(this);
            InitializeUsage();
            openQueue=new BoundedOpenQueue(()=>!busy&&!closing,()=>{openQueueNotice="打开请求最多排队 4 个，已取消最早等待的请求。";SetNotice(openQueueNotice);},error=>SetNotice("无法打开："+error.Message));
            refreshTimer.Interval=TimeSpan.FromMilliseconds(300);refreshTimer.Tick+=async delegate{refreshTimer.Stop();await UpdatePreviewAsync();};
            AllowDrop=true;PreviewDragOver+=OnDragOver;PreviewDrop+=OnDrop;PreviewKeyDown+=(sender,args)=>WindowShortcuts.Handle(this,args);Closing+=OnClosing;
            SizeChanged+=delegate{bool small=ActualWidth<720;if(small!=narrowLayout){narrowLayout=small;if(showSecondary||showPreview&&Appearance.Layout!="reader")ApplyLayout(Appearance.Layout,!showSecondary);}UpdateToolbar();};
            Loaded+=delegate{primary.Editor.Focus();UpdateStatus();Usage.Startup();};Activated+=delegate{CheckExternalFiles();if(Usage!=null)Usage.RefreshOptions();};
            TestHooks=new WindowTestHooks(this,toolbar,documentBar,footer,tabStrip,appearancePopup,workspace,search,sidePanel,terminalPanel);
            NewDocument(true);SetLayout("single");
        }
        internal void UpdateToolbar()
        {
            double width=ActualWidth;toolbar.Resize(width);documentBar.Resize(width);footer.Resize(width);search.RefreshContext();
            shellWork.Margin=width<620?new Thickness(6,0,6,6):new Thickness(12,0,12,8);
            terminalDock.Apply(width,ActualHeight,terminalVisible,sidePanel.ResolveVisible(WorkspaceRoot!=null,width),Appearance);
        }

        public Document Current {get{return active==null?null:active.BoundDocument;}}
        internal void ActivatePane(EditorPane pane){if(active!=pane){active=pane;UpdateStatus();}else QueueFooterStatus();}
        internal void Observe(Task task){task.ContinueWith(t=>Dispatcher.BeginInvoke(new Action(delegate{SetNotice(t.Exception.GetBaseException().Message);})),TaskContinuationOptions.OnlyOnFaulted);}
        public Task OpenPathsAsync(string[] paths,bool append)
        {
            if (paths == null || paths.Length == 0 || closing) return Task.FromResult(0);
            if (!openQueue.IsRunning && openQueue.Count == 0 && !busy) openQueueNotice = null;
            string[] requestedPaths = paths.ToArray();
            return openQueue.Enqueue(() => openRequests.ExecuteAsync(requestedPaths, append));
        }
        internal async Task OpenSpecialViewerAsync()
        {
            if(Current==null)return;
            if(!showSecondary || Current.Kind==DocumentKind.Image || Current.Kind==DocumentKind.Pdf){ApplyLayout(DocumentView(Current),true);if(showPreview)await UpdatePreviewAsync();}
        }
        string DocumentView(Document doc){string mode;if(viewModes.TryGetValue(doc,out mode)){if(mode=="wysiwyg" && richEdit.IsEditing(doc))return "wysiwyg";if(mode!="wysiwyg")return mode;}return FilePresentation.For(doc).PreferReader?"reader":"single";}
        public void SetViewMode(string mode)
        {
            if(Current==null)return;var doc=Current;var profile=FilePresentation.For(doc);
            if(mode=="wysiwyg")
            {
                if(!profile.CanWysiwyg)return;
                if(active!=primary){primary.Bind(doc);active=primary;}
                if(!richEdit.Enter(doc))return;
                viewModes[doc]="wysiwyg";ApplyLayout("wysiwyg",true);RefreshTabs();UpdateStatus();richEdit.Editor.FocusEditor();return;
            }
            if(richEdit.Active)richEdit.Leave();
            if(mode!="single" && !profile.CanRead || mode=="single" && !documentBar.sourceViewButton.IsEnabled || mode=="preview" && !documentBar.splitViewButton.IsEnabled)return;
            if(active!=primary){primary.Bind(doc);active=primary;}viewModes[doc]=mode;ApplyLayout(mode,true);RefreshTabs();if(mode=="single")primary.Editor.Focus();
        }
        public void NewDocument(bool append){openRequests.NewBlank(append);}
        public bool SaveDocument(Document doc,bool saveAs){return saver.Save(doc,saveAs);}
        internal bool CloseDocuments(IEnumerable<Document> requested)
        {
            Usage.Capture();var docs=requested.ToArray();if(richEdit.Active && docs.Contains(richEdit.Document))richEdit.Commit();
            if(!Session.TryClose(docs,delegate(IList<Document> dirty){return Dialogs.ResolveClose(this,dirty,d=>SaveDocument(d,false));}))return false;
            Usage.Closed(docs);foreach(var doc in docs){if(primary.BoundDocument==doc)primary.Unbind();if(secondary.BoundDocument==doc)secondary.Unbind();buffers.Remove(doc);copiedVersions.Remove(doc);displayedDirty.Remove(doc);viewModes.Remove(doc);if(doc==richEdit.Document)richEdit.Detach(doc);}
            if(docs.Length>0){previewClose=preview.CloseAsync();Dispatcher.BeginInvoke(new Action(delegate{if(showPreview && !closing)Observe(UpdatePreviewAsync());}));}
            return true;
        }
        internal void CloseTab(Document doc)
        {
            if(!CloseDocuments(new[]{doc}))return;if(Session.Documents.Count==0)NewDocument(true);
            else {if(primary.BoundDocument==null || !Session.Documents.Contains(primary.BoundDocument))primary.Bind(Session.Documents[0]);if(secondary.BoundDocument==null || !Session.Documents.Contains(secondary.BoundDocument))secondary.Bind(Session.Documents.FirstOrDefault(d=>d!=primary.BoundDocument)??primary.BoundDocument);}
            active=primary;if(!showSecondary)ApplyLayout(DocumentView(Current),true);RefreshTabs();UpdateStatus();if(showPreview)Observe(UpdatePreviewAsync());
        }
        public void SelectDocument(Document doc,string pane)
        {
            if(!Session.Documents.Contains(doc) || closing)return;Usage.Capture();
            if(richEdit.Active && doc!=richEdit.Document)richEdit.Leave();
            if(pane=="secondary"){if(!showSecondary)SetLayout(showPreview && Session.Documents.Count>1?"preview":"compare");active=secondary;}else if(pane=="primary" || !showSecondary)active=primary;
            active.Bind(doc);active.Apply(Appearance);Usage.Restore(doc);if(!showSecondary){string view=DocumentView(doc);if(view!=Appearance.Layout)ApplyLayout(view,true);}else if(doc.Kind==DocumentKind.Image || doc.Kind==DocumentKind.Pdf){primary.Bind(doc);active=primary;SetLayout("reader");}RefreshTabs();UpdateStatus();if(Appearance.Layout!="reader")active.Editor.Focus();if(showPreview)Observe(UpdatePreviewAsync());
        }
        internal void OnBufferChanged()
        {
            if(primary==null)return;primary.UpdateEmptyHint();secondary.UpdateEmptyHint();search.RefreshMatches(true);if(Usage!=null)Usage.Changed();if(sidePanel!=null)sidePanel.RequestOutlineRefresh();
            // Rebuild tabs only at the clean/dirty transition, not for every typed character.
            if(Session.Documents.Any(d=>!displayedDirty.ContainsKey(d) || displayedDirty[d]!=d.IsDirty)){RefreshTabs();UpdateStatus();}else QueueFooterStatus();
            if(showPreview){refreshTimer.Stop();refreshTimer.Start();}
        }
        internal void UpdateStatus()
        {
            CancelFooterStatus();if(active==null||Current==null)return;var doc=Current;
            search.RefreshMatches();Title=doc.Title+(doc.IsDirty?" •":"")+" — 轻页";documentBar.Update(doc);footer.Update(doc,active,openQueueNotice);
            if(sidePanel!=null)sidePanel.RequestOutlineRefresh();
        }
        internal void SetNotice(string message){CancelFooterStatus();footer.SetNotice(message);}
        public void SetTheme(string theme){Appearance.Theme=theme;Appearance.Accent="";ApplyAppearance();}
        internal void ApplyAppearance(){Theme.Apply(this,Appearance);primary.Apply(Appearance);secondary.Apply(Appearance);richEdit.ApplyTheme(Appearance);RefreshTabs();if(terminalPanel!=null)terminalPanel.RefreshAppearance();}
        public void SetLayout(string layout){ApplyLayout(layout,false);}
        internal void ApplyLayout(string layout,bool currentFileOnly)
        {
            if(richEdit.Active && layout!="wysiwyg")richEdit.Leave();
            Appearance.Layout=layout;showSecondary=layout=="compare"||layout=="preview"&&Session.Documents.Count>1&&!currentFileOnly;
            if(!showSecondary)active=primary;bool oldPreview=showPreview;showPreview=layout=="preview"||layout=="reader";
            layoutHost.Apply(layout,showSecondary,showPreview,oldPreview);
        }
        internal void ToggleSidePanel(){sidePanel.Toggle();}
        internal void PrintCurrent(){Observe(PrintCurrentAsync());}

        internal void ToggleTerminal(){terminalVisible=!terminalVisible;UpdateToolbar();if(terminalVisible)terminalPanel.Reveal();else active.Editor.Focus();}
        internal void NewWindow(){Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location){UseShellExecute=false});}
        internal void NavigateToOutline(OutlineItem item){sidePanel.Navigate(item);}
        void Placement(string command)
        {
            if(command=="swap"){var doc=primary.BoundDocument;if(secondary.BoundDocument!=null){primary.Bind(secondary.BoundDocument);secondary.Bind(doc);RefreshTabs();UpdateStatus();if(showPreview)Observe(UpdatePreviewAsync());}return;}
            if(command=="vertical" || command=="horizontal"){Appearance.EditorsVertical=command=="vertical";SetLayout(showPreview?"preview":"compare");return;}
            if(command=="left" || command=="right" || command=="bottom"){Appearance.PreviewPosition=command;SetLayout("preview");return;}
            SetLayout(command);
        }
        internal async Task UpdatePreviewAsync()
        {
            if(!showPreview || Current==null || closing)return;
            await previewClose;if(!showPreview || closing)return;
            foreach(var doc in Session.Documents)
            {
                TextDocument buffer;if(!buffers.TryGetValue(doc,out buffer) || doc.Kind!=DocumentKind.Text)continue;ITextSourceVersion previous;bool known=copiedVersions.TryGetValue(doc,out previous);
                if(known && previous.CompareAge(buffer.Version)==0)continue;
                if(known || doc.Modified){var snapshot=buffer.CreateSnapshot();doc.SetTextSource(snapshot.TextLength,length=>snapshot.GetText(0,length));}copiedVersions[doc]=buffer.Version;
            }
            var target=Current;await preview.ShowAsync(target,Session.Documents.ToArray());if(Current==target)Usage.RestoreReader(target);
        }
        internal async Task ReloadEncodingAsync(string encoding)
        {
            var old=Current;if(old==null || old.Path==null)return;Document loaded;
            try{loaded=await Task.Run(delegate{return FileService.Read(old.Path,old.Page,encoding);});}catch(Exception ex){SetNotice(ex.Message);return;}
            if(!CloseDocuments(new[]{old}))return;Session.Open(loaded,true);primary.Bind(loaded);if(secondary.BoundDocument==old)secondary.Bind(loaded);active=primary;RefreshTabs();UpdateStatus();
        }
        public async Task ChangePageAsync(int offset)
        {
            var old=Current;if(old==null || old.Kind!=DocumentKind.PagedText || busy || closing)return;int page=Math.Max(0,Math.Min(old.PageCount-1,old.Page+offset));if(page==old.Page)return;
            busy=true;try{var next=await Task.Run(delegate{return FileService.Read(old.Path,page,old.EncodingName,old.PageByteSize);});if(closing)return;int index=Session.Documents.IndexOf(old);if(index<0)return;Session.Documents[index]=next;buffers.Remove(old);copiedVersions.Remove(old);displayedDirty.Remove(old);if(primary.BoundDocument==old)primary.Bind(next);if(secondary.BoundDocument==old)secondary.Bind(next);RefreshTabs();UpdateStatus();}catch(Exception ex){SetNotice(ex.Message);}finally{busy=false;openQueue.Resume();}
        }
        void OnDragOver(object sender,DragEventArgs e){if(e.Data.GetDataPresent("QingyePane")){e.Effects=DragDropEffects.Move;e.Handled=true;}else if(e.Data.GetDataPresent(DataFormats.FileDrop)){e.Effects=DragDropEffects.Copy;e.Handled=true;}}
        void OnDrop(object sender,DragEventArgs e)
        {
            if(e.Data.GetDataPresent("QingyePane")){string pane=(string)e.Data.GetData("QingyePane");Point point=e.GetPosition(layoutHost);if(pane=="preview"){Placement(point.Y>layoutHost.ActualHeight*.7?"bottom":point.X<layoutHost.ActualWidth*.5?"left":"right");}else{if(showSecondary && point.Y>layoutHost.ActualHeight*.4)Placement("swap");else Placement(point.X>layoutHost.ActualWidth*.5?"horizontal":"vertical");}e.Handled=true;}
            else if(e.Data.GetDataPresent(DataFormats.FileDrop)){var paths=(string[])e.Data.GetData(DataFormats.FileDrop);Observe(OpenPathsAsync(paths,paths.Length>1));e.Handled=true;}
        }
        void CheckExternalFiles()
        {
            if(busy || closing)return;bool removed=false;
            if(richEdit.Active && (richEdit.Document==null || !Session.Documents.Contains(richEdit.Document)))richEdit.Detach(richEdit.Document);
            foreach(var doc in Session.Documents.ToArray())if(doc.Path!=null && !File.Exists(doc.Path)){if(doc.IsDirty){doc.Notice="原文件已被删除 · 内容仍在本次内存中";}else{Session.Documents.Remove(doc);buffers.Remove(doc);copiedVersions.Remove(doc);displayedDirty.Remove(doc);removed=true;}}
            if(Session.Documents.Count==0)NewDocument(true);else if(primary.BoundDocument!=null && !Session.Documents.Contains(primary.BoundDocument))primary.Bind(Session.Documents[0]);if(secondary.BoundDocument!=null && !Session.Documents.Contains(secondary.BoundDocument))secondary.Bind(primary.BoundDocument);RefreshTabs();UpdateStatus();
            if(removed){previewClose=preview.CloseAsync();if(showPreview)Observe(UpdatePreviewAsync());}
        }
        async void OnClosing(object sender,System.ComponentModel.CancelEventArgs e)
        {
            if(closing)return;e.Cancel=true;if(!CloseDocuments(Session.Documents.ToArray()))return;closing=true;openQueue.CancelWaiting();refreshTimer.Stop();appearancePopup.Close();Hide();
            try{await Usage.ShutdownAsync();await terminalPanel.ShutdownAsync();await previewClose;await preview.CloseAsync();}catch(Exception error){Show();MessageBox.Show(this,error.Message,"预览清理提示",MessageBoxButton.OK,MessageBoxImage.Warning);}
            try{Appearance.Save(preferencesPath);}catch(Exception){/* Optional appearance settings cannot prevent exit. */}
            buffers.Clear();copiedVersions.Clear();ClearWorkspace();Close();
        }
    }
}
