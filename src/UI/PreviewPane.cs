using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.IO;
using System.Linq;
using System.Text;
using System.Net;
using System.Diagnostics;
using System.Windows.Data;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Qingye.Core;
namespace Qingye.UI
{
    public class PreviewPane : Border
    {
        public event Action<string> PlaceRequested;
        public event Action<string> OpenFileRequested;
        public WebView2 Browser { get; private set; }
        public string ProfileDirectory { get; private set; }
        readonly Grid content=new Grid();
        readonly TextBox address=new TextBox{MinWidth=60,FontSize=12,Padding=new Thickness(8,5,8,5)};
        readonly TextBlock title=new TextBlock{Text="阅读预览",VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold};
        readonly StackPanel extra=new StackPanel{Orientation=Orientation.Horizontal};
        readonly DockPanel urlRow;
        readonly Button viewportButton;
        readonly TextBlock information=new TextBlock{FontSize=12,Margin=new Thickness(16,0,16,12),TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed};
        Task initialization=Task.FromResult(0);
        readonly List<Task> cleanups=new List<Task>();
        readonly Dictionary<WebView2,List<Stream>> responseStreams=new Dictionary<WebView2,List<Stream>>();
        Document rendered;
        EpubBook epubBook;
        string epubPath;
        ComboBox epubChapterChoice;
        long renderedRevision;
        string renderedResources;
        string renderedPath;
        TaskCompletionSource<bool> navigation;
        ulong navigationId;
        string expectedUri;
        Document shown;
        Document[] openDocuments=new Document[0];
        string mappedRoot;
        bool localUrl,narrow;
        int request;
        Point dragStart;bool dragging;
        public PreviewPane()
        {
            BorderThickness=new Thickness(0);SetResourceReference(BorderBrushProperty,"LineBrush");var root=new Grid();root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(40)});root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());Child=root;
            var header=new DockPanel{LastChildFill=true,Margin=new Thickness(10,0,6,0),Tag="preview"};root.Children.Add(header);
            var menu=Theme.Button("\uE712","",delegate{});menu.Padding=new Thickness(6,4,6,4);DockPanel.SetDock(menu,Dock.Right);header.Children.Add(menu);
            var placement=new ContextMenu();foreach(string side in new[]{"left","right","bottom"}){string s=side;placement.Items.Add(Theme.Menu(s=="left"?"预览放在左侧":s=="right"?"预览放在右侧":"预览放在下方",delegate{if(PlaceRequested!=null)PlaceRequested(s);}));}placement.Items.Add(Theme.Menu("收起预览",delegate{if(PlaceRequested!=null)PlaceRequested("single");}));menu.Click+=delegate{placement.PlacementTarget=menu;placement.IsOpen=true;};
            var refresh=Theme.Button("\uE72C","",delegate{if(Browser!=null && Browser.CoreWebView2!=null)Browser.Reload();else if(shown!=null)Observe(ShowAsync(shown,openDocuments,true));});refresh.ToolTip="刷新预览";DockPanel.SetDock(refresh,Dock.Right);header.Children.Add(refresh);
            var viewport=Theme.Button("\uE8EA","",delegate{narrow=!narrow;content.Width=narrow?390:Double.NaN;content.HorizontalAlignment=narrow?HorizontalAlignment.Center:HorizontalAlignment.Stretch;});viewport.ToolTip="切换窄屏 / 适应区域";DockPanel.SetDock(viewport,Dock.Right);header.Children.Add(viewport);
            viewportButton=viewport;
            DockPanel.SetDock(extra,Dock.Right);header.Children.Add(extra);header.Children.Add(title);
            urlRow=new DockPanel{Margin=new Thickness(12,0,12,10)};Grid.SetRow(urlRow,1);root.Children.Add(urlRow);var go=Theme.Button("\uE8A7","连接",NavigateUrl);go.Padding=new Thickness(8,5,8,5);DockPanel.SetDock(go,Dock.Right);urlRow.Children.Add(go);urlRow.Children.Add(address);address.ToolTip="输入本机开发服务地址，例 http://localhost:3000；不会自动启动后端";address.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Enter){NavigateUrl();e.Handled=true;}};
            information.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");Grid.SetRow(information,1);root.Children.Add(information);title.TextTrimming=TextTrimming.CharacterEllipsis;
            Grid.SetRow(content,2);root.Children.Add(content);SetResourceReference(BackgroundProperty,"SurfaceBrush");
            PreviewMouseWheel+=delegate(object sender,MouseWheelEventArgs e){var reader=content.Children.OfType<FlowDocumentScrollViewer>().FirstOrDefault();if(reader!=null && (Keyboard.Modifiers&ModifierKeys.Control)!=0){reader.Zoom=Math.Max(70,Math.Min(200,reader.Zoom+e.Delta/12.0));e.Handled=true;}};
            header.MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e){dragStart=e.GetPosition(header);dragging=true;};header.MouseLeftButtonUp+=delegate{dragging=false;};header.MouseMove+=delegate(object sender,MouseEventArgs e){if(dragging && e.LeftButton==MouseButtonState.Pressed && (e.GetPosition(header)-dragStart).Length>7){dragging=false;DragDrop.DoDragDrop(header,new DataObject("QingyePane","preview"),DragDropEffects.Move);}};
        }
        void Observe(Task task){task.ContinueWith(t=>Dispatcher.BeginInvoke(new Action(delegate{Message(t.Exception.GetBaseException().Message);})),TaskContinuationOptions.OnlyOnFaulted);}
        void Message(string text){rendered=null;renderedPath=null;renderedResources=null;content.Children.Clear();content.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(24),FontSize=15});}
        public async Task ShowAsync(Document doc,IEnumerable<Document> documents,bool force=false)
        {
            bool same=shown==doc;int mine=++request;if(navigation!=null)navigation.TrySetResult(false);shown=doc;openDocuments=documents.ToArray();extra.Children.Clear();
            if(localUrl && Browser!=null && same)return;if(!same)localUrl=false;
            information.Visibility=Visibility.Collapsed;
            string extension=doc.Path==null?"":Path.GetExtension(doc.Path).ToLowerInvariant();
            bool markdown=new[]{".md",".markdown",".mdown",".mkd"}.Contains(extension);
            bool html=extension==".html" || extension==".htm",svg=extension==".svg",pdf=doc.Kind==DocumentKind.Pdf;
            bool structure=doc.Kind==DocumentKind.Text && new[]{".json",".xml",".xaml",".config",".csproj"}.Contains(extension);
            bool webTools=html || svg || doc.Kind==DocumentKind.Text && !FilePresentation.For(doc).CanRead;
            urlRow.Visibility=viewportButton.Visibility=webTools?Visibility.Visible:Visibility.Collapsed;
            if(!webTools){narrow=false;content.Width=Double.NaN;content.HorizontalAlignment=HorizontalAlignment.Stretch;}
            Document entry=doc;
            if(!html && !markdown && !svg && !pdf && !structure && doc.Kind==DocumentKind.Text && extension!=".txt" && extension!=".log" && extension!=".csv" && extension!=".tsv" && extension!="")
            {
                entry=openDocuments.FirstOrDefault(d=>d.Path!=null && new[]{".html",".htm"}.Contains(Path.GetExtension(d.Path).ToLowerInvariant()));
                if(entry==null && doc.Path!=null){string index=Path.Combine(Path.GetDirectoryName(doc.Path),"index.html");if(File.Exists(index))entry=await Task.Run(delegate{return FileService.Read(index);});}
                if(entry!=null)html=true;else entry=doc;
            }
            if(mine!=request)return;
            if(structure)
            {
                if(!force && rendered==doc && renderedPath==doc.Path && renderedRevision==doc.ContentRevision && Browser==null)return;
                await CloseBrowserAsync();if(mine!=request)return;title.Text=extension==".json"?"JSON 结构":"XML 结构";
                try{string source=doc.ReadText(512*1024+1);var parsed=await Task.Run(delegate{return StructuredReader.Parse(source,extension==".json");});if(mine!=request)return;content.Children.Clear();content.Children.Add(StructuredReader.Create(parsed,extension==".json"));rendered=doc;renderedPath=doc.Path;renderedRevision=doc.ContentRevision;}
                catch(Exception ex){if(mine==request)Message("结构暂时无法显示："+ex.Message+"\n\n切换到源码可检查或继续编辑，原始内容保留。");}return;
            }
            if(doc.Kind==DocumentKind.Epub)
            {
                if(!force && rendered==doc && renderedPath==doc.Path && Browser==null && content.Children.OfType<FlowDocumentScrollViewer>().Any())return;
                await CloseBrowserAsync();if(mine!=request)return;title.Text="EPUB 阅读";address.Text="本地电子书 · 不启动网页进程";
                try
                {
                    if(epubBook==null || !String.Equals(epubPath,doc.Path,StringComparison.OrdinalIgnoreCase)){epubBook=await Task.Run(delegate{return EpubBook.Load(doc.Path);});epubPath=doc.Path;}
                    if(mine!=request)return;
                    extra.Children.Clear();
                    epubChapterChoice=new ComboBox{ItemsSource=epubBook.Chapters.Select(chapter=>chapter.Title).ToArray(),SelectedIndex=0,MinWidth=110,MaxWidth=250,FontSize=12};
                    extra.Children.Add(epubChapterChoice);
                    epubChapterChoice.SelectionChanged+=delegate{if(epubChapterChoice.SelectedIndex>=0 && shown==doc)RenderEpubChapter(epubBook,epubChapterChoice.SelectedIndex);};
                    RenderEpubChapter(epubBook,0);
                    rendered=doc;renderedPath=doc.Path;renderedRevision=doc.ContentRevision;renderedResources=null;
                }
                catch(Exception ex){if(mine==request)Message("EPUB 无法显示："+ex.Message+"\n\n请确认文件未加密且结构完整。");}
                return;
            }
            if(markdown)
            {
                if(!force && rendered==entry && renderedPath==entry.Path && renderedRevision==entry.ContentRevision && Browser==null)return;
                await CloseBrowserAsync();if(mine!=request)return;title.Text="排版阅读";address.Text="原生阅读 · 不启动网页进程";string source=entry.ReadText(NativeMarkdown.MaxCharacters+1);var parsed=await Task.Run(delegate{return NativeMarkdown.Parse(source);});if(mine!=request)return;content.Children.Clear();content.Children.Add(NativeMarkdown.Create(parsed,entry.Path,delegate(string path){if(OpenFileRequested!=null)OpenFileRequested(path);}));rendered=entry;renderedPath=entry.Path;renderedRevision=entry.ContentRevision;renderedResources=null;return;
            }
            if(html || svg || pdf)
            {
                string resources=String.Join("|",openDocuments.Where(d=>d.Kind==DocumentKind.Text && d.Path!=null && new[]{".html",".htm",".css",".js",".mjs",".svg",".json"}.Contains(Path.GetExtension(d.Path).ToLowerInvariant())).OrderBy(d=>d.Path).Select(d=>d.Path+":"+d.ContentRevision));
                if(!force && Browser!=null && rendered==entry && renderedRevision==entry.ContentRevision && renderedResources==resources)return;
                if((entry.TextLength>4*1024*1024 || entry.ByteLength>4*1024*1024) && !pdf){await CloseBrowserAsync();Message("文件较大，源码可继续阅读。网页预览可连接本机服务地址。");return;}
                title.Text=markdown?"Markdown 预览":pdf?"PDF 预览":svg?"SVG 预览":"实时预览";
                Exception previewError=null;
                try
                {
                    await EnsureBrowserAsync();if(mine!=request)return;
                    Browser.CoreWebView2.Settings.IsScriptEnabled=html || pdf;
                    if(entry.Path!=null)
                    {
                        mappedRoot=Path.GetDirectoryName(entry.Path);
                    }
                    shown=entry;var available=openDocuments.ToList();if(!available.Contains(entry))available.Add(entry);openDocuments=available.ToArray();
                    navigation=new TaskCompletionSource<bool>();
                    navigationId=0;
                    if(markdown){expectedUri="about:blank";address.Text="Markdown · 本地内容";Browser.NavigateToString(WebContent.MarkdownHtml(entry.Text));}
                    else if(entry.Path!=null){string uri="https://qingye.local/"+Uri.EscapeDataString(Path.GetFileName(entry.Path));expectedUri=uri;address.Text=uri;Browser.CoreWebView2.Navigate(uri);}
                    else{expectedUri="about:blank";address.Text="未保存页面";Browser.NavigateToString(entry.Text);}
                    var pending=navigation.Task;if(await Task.WhenAny(pending,Task.Delay(10000))!=pending)throw new IOException("页面加载超时，可重试或连接本机服务。");if(!await pending && mine==request)throw new IOException("页面未能加载。");
                    if(mine==request){rendered=entry;renderedRevision=entry.ContentRevision;renderedResources=resources;}
                }
                catch(Exception ex){previewError=ex;}
                if(previewError!=null && mine==request){await CloseBrowserAsync();if(mine==request)Message("预览无法开启："+previewError.Message+"\n\n文本阅读和编辑仍可使用。网页/PDF 预览需要系统 WebView2 Runtime。");}
                return;
            }
            await CloseBrowserAsync();if(mine!=request)return;rendered=null;content.Children.Clear();address.Text="本地内容 · 本次查看";
            if(doc.Kind==DocumentKind.Image){ShowImage(doc);return;}
            if(doc.Kind==DocumentKind.Rtf)
            {
                title.Text="RTF 富文本";try{if(doc.ByteLength>4*1024*1024)throw new InvalidDataException("RTF 预览限 4 MiB，请使用系统软件打开。");var flow=NativeMarkdown.EmptyDocument();using(var stream=new FileStream(doc.Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))new System.Windows.Documents.TextRange(flow.ContentStart,flow.ContentEnd).Load(stream,DataFormats.Rtf);content.Children.Add(NativeMarkdown.Viewer(flow));}catch(Exception ex){Message("RTF 无法显示："+ex.Message);}return;
            }
            if(doc.Kind==DocumentKind.Docx){title.Text="Word 基础排版 · 不含图片、页眉及完整分页";try{var body=await Task.Run(delegate{return ContentReaders.ReadDocxBody(doc.Path);});if(mine==request)content.Children.Add(OfficeReader.Create(body));}catch(Exception ex){if(mine==request)Message("文档无法显示："+ex.Message);}return;}
            if(doc.Kind==DocumentKind.Pptx)
            {
                title.Text="PowerPoint 内容阅读";
                try
                {
                    var presentation=await Task.Run(delegate{return ContentReaders.ReadPptx(doc.Path);});
                    if(mine!=request)return;
                    information.Text=presentation.Notice;information.Visibility=Visibility.Visible;
                    content.Children.Add(OfficeReader.Create(presentation.Body));
                }
                catch(Exception error){if(mine==request)Message("演示文稿无法显示："+error.Message);}
                return;
            }
            if(extension==".csv" || extension==".tsv"){title.Text="表格内容预览";ShowTable(ContentReaders.ParseTable(doc.Text,extension==".tsv"?'\t':','));return;}
            if(doc.Kind==DocumentKind.Xlsx)
            {
                title.Text="工作表内容预览";var names=await Task.Run(delegate{return ContentReaders.SheetNames(doc.Path);});if(mine!=request)return;
                var sheets=new ComboBox{ItemsSource=names,SelectedIndex=0,MinWidth=90,MaxWidth=160,FontSize=12};extra.Children.Add(sheets);sheets.SelectionChanged+=async delegate{try{int index=sheets.SelectedIndex;var table=await Task.Run(delegate{return ContentReaders.ReadXlsx(doc.Path,index);});if(shown==doc)ShowTable(table);}catch(Exception ex){Message(ex.Message);}};
                var initial=await Task.Run(delegate{return ContentReaders.ReadXlsx(doc.Path);});if(mine==request)ShowTable(initial);return;
            }
            title.Text=doc.IsReadOnly?"只读内容":"文本阅读";var textView=new TextBox{Text=doc.Text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI"),FontSize=16,Padding=new Thickness(32),BorderThickness=new Thickness(0),Background=Brushes.Transparent};textView.SetResourceReference(Control.ForegroundProperty,"TextBrush");content.Children.Add(textView);
        }
        async Task EnsureBrowserAsync()
        {
            if(Browser==null)
            {
                ProfileDirectory=Path.Combine(Path.GetTempPath(),"QingyePreview",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(ProfileDirectory);
                var view=new WebView2();Browser=view;responseStreams[view]=new List<Stream>();content.Children.Clear();content.Children.Add(view);initialization=InitializeAsync(view,ProfileDirectory);
            }
            await initialization;
        }
        async Task InitializeAsync(WebView2 view,string profile)
        {
            var options=new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-sync --disk-cache-size=1048576 --media-cache-size=1048576");
            var environment=await CoreWebView2Environment.CreateAsync(null,profile,options);await view.EnsureCoreWebView2Async(environment);
            var streams=responseStreams[view];
            var settings=view.CoreWebView2.Settings;settings.AreDevToolsEnabled=false;settings.AreDefaultScriptDialogsEnabled=false;settings.IsWebMessageEnabled=false;settings.IsStatusBarEnabled=false;
            view.CoreWebView2.AddWebResourceRequestedFilter("https://qingye.local/*",CoreWebView2WebResourceContext.All);
            view.CoreWebView2.WebResourceRequested+=delegate(object sender,CoreWebView2WebResourceRequestedEventArgs e)
            {
                var uri=new Uri(e.Request.Uri);string relative=Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/',Path.DirectorySeparatorChar);string path=mappedRoot==null?null:Path.Combine(mappedRoot,relative);
                if(Browser!=view || path==null || !FileService.IsInsideRoot(mappedRoot,path)){e.Response=environment.CreateWebResourceResponse(new MemoryStream(new byte[0]),403,"Forbidden","Cache-Control: no-store");return;}
                if(streams.Count>=512){e.Response=environment.CreateWebResourceResponse(new MemoryStream(new byte[0]),429,"Too Many Resources","Cache-Control: no-store");return;}
                var edited=openDocuments.FirstOrDefault(d=>d.Path!=null && d.Kind==DocumentKind.Text && String.Equals(Path.GetFullPath(d.Path),Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase));
                if(edited!=null){byte[] bytes=Encoding.UTF8.GetBytes(edited.Text);var body=TrackResponse(new MemoryStream(bytes,false),streams);e.Response=environment.CreateWebResourceResponse(body,200,"OK","Content-Type: "+Mime(path)+"; charset=utf-8\r\nCache-Control: no-store");}
                else if(File.Exists(path))
                {
                    var file=TrackResponse(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete),streams);
                    e.Response=environment.CreateWebResourceResponse(file,200,"OK","Content-Type: "+Mime(path)+"\r\nContent-Length: "+file.Length+"\r\nCache-Control: no-store\r\nAccept-Ranges: none");
                }
                else e.Response=environment.CreateWebResourceResponse(new MemoryStream(new byte[0]),404,"Not Found","Cache-Control: no-store");
            };
            view.CoreWebView2.NavigationCompleted+=delegate(object sender,CoreWebView2NavigationCompletedEventArgs e){if(Browser==view && navigation!=null && e.NavigationId==navigationId){navigation.TrySetResult(e.IsSuccess);if(shown==null || shown.Kind!=DocumentKind.Pdf){foreach(var stream in streams)stream.Dispose();streams.Clear();}}};
            view.CoreWebView2.NavigationStarting+=delegate(object sender,CoreWebView2NavigationStartingEventArgs e){if(Browser!=view)return;if(e.Uri==expectedUri || expectedUri=="about:blank" && e.Uri.StartsWith("data:text/html",StringComparison.OrdinalIgnoreCase))navigationId=e.NavigationId;Uri uri;if(Uri.TryCreate(e.Uri,UriKind.Absolute,out uri) && uri.Scheme!="http" && uri.Scheme!="https" && uri.Scheme!="about" && uri.Scheme!="data"){e.Cancel=true;if(navigation!=null)navigation.TrySetResult(false);}};
            view.CoreWebView2.ProcessFailed+=delegate{if(Browser==view && navigation!=null)navigation.TrySetResult(false);};
            view.CoreWebView2.NewWindowRequested+=delegate(object sender,CoreWebView2NewWindowRequestedEventArgs e){e.Handled=true;};
            view.CoreWebView2.DownloadStarting+=delegate(object sender,CoreWebView2DownloadStartingEventArgs e){e.Cancel=true;};
        }
        Stream TrackResponse(Stream body,List<Stream> streams)
        {
            var response=new ResponseStream(body,delegate(ResponseStream completed){try{if(Dispatcher.HasShutdownStarted){completed.Dispose();return;}Dispatcher.BeginInvoke(new Action(delegate{streams.Remove(completed);completed.Dispose();}));}catch{completed.Dispose();}});streams.Add(response);return response;
        }
        static string Mime(string path){switch(Path.GetExtension(path).ToLowerInvariant()){case ".html":case ".htm":return "text/html";case ".css":return "text/css";case ".js":case ".mjs":return "text/javascript";case ".svg":return "image/svg+xml";case ".json":return "application/json";case ".pdf":return "application/pdf";case ".png":return "image/png";case ".jpg":case ".jpeg":return "image/jpeg";case ".gif":return "image/gif";case ".woff":return "font/woff";case ".woff2":return "font/woff2";default:return "application/octet-stream";}}
        async void NavigateUrl()
        {
            Uri uri;if(!Uri.TryCreate(address.Text,UriKind.Absolute,out uri) || (uri.Scheme!="http" && uri.Scheme!="https") || !uri.IsLoopback){Message("请输入本机 http/https 地址，例如 http://localhost:3000。编辑器不会启动后端服务。");return;}
            try{await EnsureBrowserAsync();localUrl=true;title.Text="本机服务预览";Browser.CoreWebView2.Settings.IsScriptEnabled=true;Browser.CoreWebView2.Navigate(uri.AbsoluteUri);}catch(Exception ex){Message(ex.Message);}
        }
        void ShowImage(Document doc)
        {
            title.Text="图片查看";
            try
            {
                if(doc.ByteLength>128L*1024*1024)throw new InvalidDataException("图片文件较大，请用系统图片查看器打开。");
                BitmapImage bitmap;int width,height;using(var stream=new FileStream(doc.Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);width=decoder.Frames[0].PixelWidth;height=decoder.Frames[0].PixelHeight;if((long)width*height>50000000)throw new InvalidDataException("图片像素过大，已限制预览。");stream.Seek(0,SeekOrigin.Begin);bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=Math.Min(width,4096);bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();}
                var image=new Image{Source=bitmap,Stretch=Stretch.Uniform,Margin=new Thickness(18)};content.Children.Add(image);address.Text=width+" × "+height+" · "+FileService.FormatSize(doc.ByteLength)+(width>4096?" · 预览限 4K":"");
                information.Text=address.Text+(FilePresentation.Extension(doc)==".gif"?" · GIF 显示首帧":"");information.Visibility=Visibility.Visible;
                extra.Children.Add(Theme.Button(null,"100%",delegate{image.Stretch=Stretch.None;}));extra.Children.Add(Theme.Button(null,"适应",delegate{image.Stretch=Stretch.Uniform;}));
            }
            catch(Exception ex){Message("图片无法显示："+ex.Message);}
        }
        void ShowTable(TableContent table)
        {
            content.Children.Clear();var grid=new DataGrid{IsReadOnly=true,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,CanUserSortColumns=false,CanUserReorderColumns=false,EnableRowVirtualization=true,EnableColumnVirtualization=true,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,HeadersVisibility=DataGridHeadersVisibility.All,BorderThickness=new Thickness(0),FontSize=14,RowHeight=36,ColumnHeaderHeight=34,RowHeaderWidth=48,Background=Brushes.Transparent};
            grid.SetResourceReference(Control.ForegroundProperty,"TextBrush");grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty,"LineBrush");grid.Resources[SystemColors.HighlightBrushKey]=TryFindResource("HoverBrush")??Theme.Brush("#E3ECE6");grid.Resources[SystemColors.HighlightTextBrushKey]=TryFindResource("TextBrush")??Brushes.Black;
            var headers=new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));headers.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));headers.Setters.Add(new Setter(Control.ForegroundProperty,TryFindResource("MutedBrush")));headers.Setters.Add(new Setter(Control.FontSizeProperty,12.0));headers.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(12,6,12,6)));headers.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0,0,0,1)));headers.Setters.Add(new Setter(Control.BorderBrushProperty,TryFindResource("LineBrush")));grid.ColumnHeaderStyle=headers;
            var rowHeaders=new Style(typeof(System.Windows.Controls.Primitives.DataGridRowHeader));rowHeaders.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));rowHeaders.Setters.Add(new Setter(Control.ForegroundProperty,TryFindResource("MutedBrush")));rowHeaders.Setters.Add(new Setter(Control.FontSizeProperty,12.0));rowHeaders.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Center));grid.RowHeaderStyle=rowHeaders;grid.LoadingRow+=delegate(object sender,DataGridRowEventArgs e){e.Row.Header=(e.Row.GetIndex()+1).ToString();};
            int columns=table.Rows.Count==0?0:table.Rows.Max(r=>r.Length);for(int i=0;i<columns;i++){int index=i;double number;var values=table.Rows.Skip(1).Where(r=>r.Length>index && !String.IsNullOrEmpty(r[index])).Select(r=>r[index]).ToArray();bool numeric=values.Length>0 && values.All(v=>Double.TryParse(v.TrimEnd('%'),System.Globalization.NumberStyles.Float|System.Globalization.NumberStyles.AllowThousands,System.Globalization.CultureInfo.InvariantCulture,out number));var style=new Style(typeof(TextBlock));style.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(12,0,12,0)));style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center));style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty,numeric?TextAlignment.Right:TextAlignment.Left));grid.Columns.Add(new DataGridTextColumn{Header=ColumnName(i),Binding=new Binding("["+i+"]"),MinWidth=120,MaxWidth=480,ElementStyle=style,Width=new DataGridLength(1,DataGridLengthUnitType.SizeToCells)});}
            grid.ItemsSource=table.Rows.Select(row=>{var padded=new string[columns];Array.Copy(row,padded,row.Length);return padded;}).ToArray();content.Children.Add(grid);address.Text=table.Notice;
            information.Text=table.Rows.Count+" 行 / "+columns+" 列 · "+table.Notice;information.Visibility=Visibility.Visible;
        }
        static string ColumnName(int column){string result="";for(int n=column+1;n>0;n=(n-1)/26)result=(char)('A'+(n-1)%26)+result;return result;}
        public bool ScrollToSourceLine(int line)
        {
            var reader=content.Children.OfType<FlowDocumentScrollViewer>().FirstOrDefault();
            if(reader==null)return false;
            var headings=WalkBlocks(reader.Document.Blocks).OfType<System.Windows.Documents.Paragraph>()
                .Select(paragraph=>new{Paragraph=paragraph,Line=LineOf(paragraph.Tag)}).Where(entry=>entry.Line>0).ToArray();
            if(headings.Length==0)return false;
            var target=headings.Where(entry=>entry.Line<=line).OrderByDescending(entry=>entry.Line).FirstOrDefault()??headings[0];
            target.Paragraph.BringIntoView();return true;
        }
        static int LineOf(object tag){string value=tag as string;int line;return value!=null && value.StartsWith("line:",StringComparison.Ordinal) && Int32.TryParse(value.Substring(5),out line)?line:0;}
        static System.Collections.Generic.IEnumerable<System.Windows.Documents.Block> WalkBlocks(System.Windows.Documents.BlockCollection blocks)
        {
            foreach(System.Windows.Documents.Block block in blocks)
            {
                yield return block;
                var section=block as System.Windows.Documents.Section;if(section!=null)foreach(var child in WalkBlocks(section.Blocks))yield return child;
                var list=block as System.Windows.Documents.List;if(list!=null)foreach(var item in list.ListItems)foreach(var child in WalkBlocks(item.Blocks))yield return child;
                var table=block as System.Windows.Documents.Table;if(table!=null)foreach(var group in table.RowGroups)foreach(var row in group.Rows)foreach(var cell in row.Cells)foreach(var child in WalkBlocks(cell.Blocks))yield return child;
            }
        }
        public async Task<bool> SaveHtmlAsPdfAsync(string html,string path)
        {
            await EnsureBrowserAsync();
            var web=Browser==null?null:Browser.CoreWebView2;if(web==null)return false;
            var done=new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> handler=null;
            handler=delegate(object sender,CoreWebView2NavigationCompletedEventArgs e){done.TrySetResult(e.IsSuccess);};
            web.NavigationCompleted+=handler;
            try
            {
                web.NavigateToString(html);
                if(await Task.WhenAny(done.Task,Task.Delay(15000))!=done.Task || !done.Task.Result)return false;
            }
            finally{web.NavigationCompleted-=handler;}
            var settings=web.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds=true;
            settings.ShouldPrintHeaderAndFooter=false;
            settings.MarginTop=0.4;settings.MarginBottom=0.4;settings.MarginLeft=0.4;settings.MarginRight=0.4;
            return await web.PrintToPdfAsync(path,settings);
        }
        public async Task CapturePageAsync(Stream output){if(Browser==null || Browser.CoreWebView2==null)throw new InvalidOperationException("当前不是网页预览。");await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,output);}
        Task CloseBrowserAsync()
        {
            var view=Browser;if(view==null)return Task.FromResult(0);var start=initialization;string profile=ProfileDirectory;Browser=null;ProfileDirectory=null;initialization=Task.FromResult(0);localUrl=false;if(navigation!=null)navigation.TrySetResult(false);
            cleanups.RemoveAll(t=>t.Status==TaskStatus.RanToCompletion);var entireClose=CleanupBrowserAsync(view,start,profile);cleanups.Add(entireClose);return entireClose;
        }
        async Task CleanupBrowserAsync(WebView2 view,Task start,string profile)
        {
            try{await start;}catch{}int[] ids=new int[0];var owned=new Dictionary<int,DateTime>();try{if(view.CoreWebView2!=null)ids=view.CoreWebView2.Environment.GetProcessInfos().Select(p=>p.ProcessId).Concat(new[]{(int)view.CoreWebView2.BrowserProcessId}).Distinct().ToArray();}catch{}
            foreach(int id in ids)try{using(var process=Process.GetProcessById(id)){if(process.ProcessName=="msedgewebview2")owned[id]=process.StartTime.ToUniversalTime();}}catch{}
            try{view.Dispose();}catch{}content.Children.Remove(view);List<Stream> streams;if(responseStreams.TryGetValue(view,out streams)){foreach(var stream in streams)stream.Dispose();responseStreams.Remove(view);}view=null;start=null;
            var cleanup=Task.Run(delegate{
                // This environment has a unique profile and cannot share its processes with another app.
                // Dispose first; bound shutdown of only the SDK-reported, identity-checked processes.
                var deadline=DateTime.UtcNow.AddMilliseconds(250);while(DateTime.UtcNow<deadline){bool alive=false;foreach(int id in ids)try{using(var process=Process.GetProcessById(id)){if(!process.HasExited)alive=true;}}catch{}if(!alive)break;System.Threading.Thread.Sleep(25);}
                foreach(var identity in owned)try{using(var process=Process.GetProcessById(identity.Key)){if(!process.HasExited && process.ProcessName=="msedgewebview2" && process.StartTime.ToUniversalTime()==identity.Value){process.Kill();process.WaitForExit(500);}}}catch{}
                string permitted=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"QingyePreview"))+Path.DirectorySeparatorChar;string resolved=Path.GetFullPath(profile);Guid idValue;if(!resolved.StartsWith(permitted,StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(Path.GetFileName(resolved),out idValue))return;
                Exception last=null;for(int attempt=0;attempt<16;attempt++){try{if(Directory.Exists(resolved))Directory.Delete(resolved,true);string parent=Path.GetDirectoryName(resolved);if(Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())Directory.Delete(parent);break;}catch(Exception error){last=error;System.Threading.Thread.Sleep(80);}}
                if(Directory.Exists(resolved))throw new IOException("临时网页缓存未能清理："+resolved,last);
            });await cleanup;
        }
        public bool Print(Window owner)
        {
            var dialog=new PrintDialog();
            if(dialog.ShowDialog()!=true)return false;
            var viewer=content.Children.OfType<FlowDocumentScrollViewer>().FirstOrDefault();
            if(viewer!=null && viewer.Document!=null)
            {
                dialog.PrintDocument(((System.Windows.Documents.IDocumentPaginatorSource)viewer.Document).DocumentPaginator,"轻页");
                return true;
            }
            var text=content.Children.OfType<TextBox>().FirstOrDefault();
            if(text==null)return false;
            var flow=new FlowDocument{FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI"),FontSize=12,PagePadding=new Thickness(48)};
            foreach(string line in text.Text.Replace("\r\n","\n").Replace('\r','\n').Split('\n'))flow.Blocks.Add(new Paragraph(new Run(line)));
            dialog.PrintDocument(((System.Windows.Documents.IDocumentPaginatorSource)flow).DocumentPaginator,"轻页");
            return true;
        }
        void RenderEpubChapter(EpubBook book,int index)
        {
            if(book==null || index<0 || index>=book.Chapters.Count)return;
            content.Children.Clear();
            content.Children.Add(EpubRenderer.Create(book,book.Chapters[index],delegate(string href){NavigateEpub(book,href);}));
            title.Text="EPUB 阅读";
            address.Text=book.Title+" · "+(index+1)+" / "+book.Chapters.Count+(String.IsNullOrEmpty(book.Author)?"":" · "+book.Author);
            information.Text=book.Title+(String.IsNullOrEmpty(book.Author)?"":" · "+book.Author);information.Visibility=Visibility.Visible;
        }
        void NavigateEpub(EpubBook book,string href)
        {
            if(String.IsNullOrEmpty(href))return;
            Uri uri;
            try{if(Uri.TryCreate(href,UriKind.Absolute,out uri) && (uri.Scheme=="http" || uri.Scheme=="https" || uri.Scheme=="mailto")){Process.Start(uri.AbsoluteUri);return;}}catch{}
            int current=epubChapterChoice==null?0:epubChapterChoice.SelectedIndex;
            if(current<0 || current>=book.Chapters.Count)return;
            int index=book.ChapterIndex(href,book.Chapters[current].Href);
            if(index>=0 && epubChapterChoice!=null)epubChapterChoice.SelectedIndex=index;
        }
        public async Task CloseAsync(){request++;shown=null;rendered=null;renderedResources=null;epubBook=null;epubPath=null;epubChapterChoice=null;openDocuments=new Document[0];await CloseBrowserAsync();await Task.WhenAll(cleanups.ToArray());cleanups.Clear();content.Children.Clear();}
    }
}
