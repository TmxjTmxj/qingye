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
        sealed class UiAgentTransport : Qingye.Core.IStreamingAgentTransport
        {
            internal readonly System.Collections.Generic.Queue<Qingye.Core.AgentReply> Replies = new System.Collections.Generic.Queue<Qingye.Core.AgentReply>();
            internal Action<System.Collections.Generic.IList<System.Collections.Generic.Dictionary<string,object>>> Inspect;
            internal bool Hold;
            internal bool Fail;
            public Task<Qingye.Core.AgentReply> ChatAsync(System.Collections.Generic.IList<System.Collections.Generic.Dictionary<string,object>> messages,string tools,System.Threading.CancellationToken token)
            {
                if(Inspect!=null)Inspect(messages);
                if(Fail)throw new Qingye.Core.AgentException("test connection failed sk-ui-test-only");
                if(!Hold)return Task.FromResult(Replies.Dequeue());
                var completion=new TaskCompletionSource<Qingye.Core.AgentReply>();
                token.Register(()=>completion.TrySetCanceled());
                return completion.Task;
            }
            public Task<Qingye.Core.AgentReply> ChatStreamingAsync(System.Collections.Generic.IList<System.Collections.Generic.Dictionary<string,object>> messages,string tools,System.Threading.CancellationToken token,Action<string> delta)
            {
                if(Hold)delta("已经收到的实时回复");
                return ChatAsync(messages,tools,token);
            }
        }
        static Button AgentAction(AgentPanel agent,string label)
        {
            Button action=null;
            Until(()=>{action=Walk(agent).OfType<Button>().FirstOrDefault(button=>Walk(button).OfType<TextBlock>().Any(text=>text.Text==label));return action!=null;});
            return action;
        }
        static void Until(Func<bool> condition)
        {var deadline=DateTime.UtcNow.AddSeconds(5);while(!condition()){if(DateTime.UtcNow>deadline)throw new Exception("Agent state did not arrive");Pump();System.Threading.Thread.Sleep(10);}}
        static void AgentIntegrationTests()
        {
            var host=new MainWindow{ShowActivated=false,Left=-16000,Top=0};host.Show();Pump();
            string folder=Path.Combine(root,"agent-integration");Directory.CreateDirectory(folder);
            string file=Path.Combine(folder,"note.md");File.WriteAllText(file,"old");Await(host.OpenPathsAsync(new[]{file},false));host.ToggleAgent();
            var agent=host.TestHooks.Agent;var transport=new UiAgentTransport();
            var config=new Qingye.Core.ModelConfig{BaseUrl="https://example.com",ApiKey="sk-ui-test-only",ModelName="test-model"};
            string historyPath=Path.Combine(folder,"agent-history");agent.UseTestServices(historyPath,()=>config,value=>transport);
            try
            {
                Test("Agent UI continues earlier turns and restores saved history",delegate{
                    transport.Replies.Enqueue(new Qingye.Core.AgentReply{Content="# 第一轮\n\n**重点**内容"});Await(agent.SubmitAsync("先记住这一轮"));
                    string id=agent.CurrentSession.Id;
                    transport.Inspect=messages=>Check(messages.Any(message=>Convert.ToString(message["content"])=="先记住这一轮") && messages.Any(message=>Convert.ToString(message["content"]).Contains("第一轮")),"UI recreated an empty model context");
                    transport.Replies.Enqueue(new Qingye.Core.AgentReply{Content="第二轮回答"});Await(agent.SubmitAsync("接着说"));transport.Inspect=null;
                    Check(Walk(agent).OfType<RichTextBox>().Any(box=>new System.Windows.Documents.TextRange(box.Document.ContentStart,box.Document.ContentEnd).Text.Contains("重点")),"Assistant Markdown was not rendered");
                    agent.NewConversation();Check(agent.CurrentSession.Id!=id && agent.CurrentSession.History().Count==0,"New conversation kept the previous context");
                    agent.OpenConversation(id);Check(agent.CurrentSession.History().Count==4 && agent.CurrentSession.CurrentFile==file,"Opening history lost turns or file scope");
                    var saved=new Qingye.Core.AgentSessionStore(historyPath).Load(id);Check(saved.History().Count==4,"UI history was not durably saved");
                });
                Test("Agent UI streams, stops and protects a running conversation",delegate{
                    transport.Hold=true;string id=agent.CurrentSession.Id;var operation=agent.SubmitAsync("持续运行");
                    Until(()=>agent.IsBusy && agent.StatusText.Contains("等待模型"));
                    agent.NewConversation();Check(agent.CurrentSession.Id==id,"New conversation replaced an active run");
                    Until(()=>Walk(agent).OfType<TextBox>().Any(box=>box.Text.Contains("实时回复")));
                    agent.Stop();Await(operation);transport.Hold=false;
                    Check(agent.StatusText.Contains("已停止") && agent.CurrentSession.Entries.Any(entry=>entry.Kind=="attempt"),"Stop lost partial output or left a running state");
                    transport.Replies.Enqueue(new Qingye.Core.AgentReply{Content="继续完成"});Await(agent.SubmitAsync("继续"));Check(agent.StatusText.Contains("任务完成"),"Stopped conversation cannot continue");
                });
                Test("Agent inline approval applies only after confirmation and can be stopped",delegate{
                    agent.NewConversation();
                    transport.Replies.Enqueue(new Qingye.Core.AgentReply{ToolCalls=new[]{new Qingye.Core.AgentToolCall{Id="write-1",Name="write_file",Arguments="{\"path\":\"note.md\",\"content\":\"new\"}"}}});
                    transport.Replies.Enqueue(new Qingye.Core.AgentReply{Content="已完成修改"});
                    var operation=agent.SubmitAsync("修改文件");Until(()=>agent.StatusText.Contains("等待确认修改"));
                    Check(File.ReadAllText(file)=="old" && Walk(agent).OfType<RichTextBox>().Any(box=>new System.Windows.Documents.TextRange(box.Document.ContentStart,box.Document.ContentEnd).Text.Contains("+ new")),"Write happened before a visible diff");
                    AgentAction(agent,"应用修改").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Await(operation);
                    Check(File.ReadAllText(file)=="new" && agent.StatusText.Contains("任务完成"),"Approved inline write did not complete");
                    transport.Replies.Enqueue(new Qingye.Core.AgentReply{ToolCalls=new[]{new Qingye.Core.AgentToolCall{Id="write-2",Name="write_file",Arguments="{\"path\":\"note.md\",\"content\":\"bad\"}"}}});
                    operation=agent.SubmitAsync("再次修改");Until(()=>agent.StatusText.Contains("等待确认修改"));agent.Stop();Await(operation);
                    Check(File.ReadAllText(file)=="new" && agent.StatusText.Contains("已停止"),"Stopping an approval wrote the file or stuck");
                });
                Test("Agent failed requests can retry without saving model secrets",delegate{
                    transport.Fail=true;Await(agent.SubmitAsync("失败后重试"));transport.Fail=false;
                    Check(agent.StatusText.Contains("运行失败"),"Failure is not a visible settled state");
                    transport.Replies.Enqueue(new Qingye.Core.AgentReply{Content="重试完成"});
                    AgentAction(agent,"重试").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Until(()=>!agent.IsBusy);
                    Check(agent.StatusText.Contains("任务完成"),"Retry failed to recover");
                    string saved=File.ReadAllText(Path.Combine(historyPath,agent.CurrentSession.Id+".json"));
                    Check(!saved.Contains(config.ApiKey),"Model secret entered persistent conversation history");
                });
                Test("Agent keeps unsaved conversation when history save fails",delegate{
                    var current=agent.CurrentSession;current.Draft=new string('字',3*1024*1024);
                    try{agent.NewConversation();Check(Object.ReferenceEquals(agent.CurrentSession,current) && agent.CurrentSession.Draft.Length==3*1024*1024,"Failed save discarded the current draft");}
                    finally{agent.CurrentSession.Draft="";}
                });
                Test("Agent pins attachment scope before the first message",delegate{
                    string previous=agent.CurrentSession.Id;agent.NewConversation();host.ImportWorkspace(folder);
                    Check(agent.TryAttachFile(file),"Initial attachment was rejected");
                    string other=Path.Combine(folder,"switched");Directory.CreateDirectory(other);File.WriteAllText(Path.Combine(other,"note.md"),"other");host.ImportWorkspace(other);
                    try
                    {
                        transport.Inspect=messages=>Check(Convert.ToString(messages[0]["content"]).Contains(folder) && !Convert.ToString(messages[0]["content"]).Contains(other),"First send rebound the attachment to another directory");
                        transport.Replies.Enqueue(new Qingye.Core.AgentReply{Content="已检查附件范围"});Await(agent.SubmitAsync("检查附加文件"));
                        Check(agent.CurrentSession.Root==folder,"Initial attachment scope was not pinned");
                    }
                    finally{transport.Inspect=null;agent.OpenConversation(previous);}
                });
                Test("Agent attachments respect pinned scope after switching files",delegate{
                    string other=Path.Combine(folder,"other");Directory.CreateDirectory(other);string selected=Path.Combine(other,"note.md");File.WriteAllText(selected,"other file");
                    host.ImportWorkspace(other);host.UpdateStatus();
                    Check(!agent.TryAttachFile(selected),"Attachment from another scope was interpreted as the old session's file");
                });
                Test("Agent history binds scope and deletes without recreating the session",delegate{
                    string id=agent.CurrentSession.Id;host.ImportWorkspace(folder);host.UpdateStatus();
                    Check(agent.CurrentSession.Root==null && agent.ContextText.Contains("单文件模式"),"Changing workspace expanded an existing session's file access");
                    agent.DeleteConversation(id,false);Await(agent.ShutdownAsync());
                    Check(!new Qingye.Core.AgentSessionStore(historyPath).List().Any(info=>info.Id==id),"Deleted session was re-saved on shutdown");
                });
            }
            finally{foreach(var document in host.Session.Documents)document.Modified=false;host.Close();Pump();}
        }
        static Button AgentNamed(AgentPanel agent,string name)
        {
            Button action=null;
            Until(()=>{action=Walk(agent).OfType<Button>().FirstOrDefault(button=>((System.Windows.Automation.AutomationProperties.GetName(button)??"")).StartsWith(name));return action!=null;});
            return action;
        }
        static System.Collections.Generic.Dictionary<string,object> AgentMessage(string role,string text)
        {
            return new System.Collections.Generic.Dictionary<string,object>{{"role",role},{"content",text}};
        }
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
        static void NativeAgentTests()
        {
            Test("Native Agent history lists, opens, deletes and clears local sessions",delegate{
                var host=new MainWindow{ShowActivated=false,Left=-16000,Top=0};host.Show();Pump();
                string folder=Path.Combine(root,"native-agent-history");Directory.CreateDirectory(folder);
                string historyPath=Path.Combine(folder,"agent-history");
                var store=new Qingye.Core.AgentSessionStore(historyPath);
                File.WriteAllText(Path.Combine(folder,"one.md"),"one");File.WriteAllText(Path.Combine(folder,"two.md"),"two");
                var first=new Qingye.Core.AgentSession{Title="第一份历史",Root=folder,CurrentFile=Path.Combine(folder,"one.md"),HarnessSessionId="harness-first",State="completed"};
                first.Record(AgentMessage("user","历史问题一"));first.Record(AgentMessage("assistant","历史回答一"));store.Save(first);
                var second=new Qingye.Core.AgentSession{Title="第二份历史",Root=folder,CurrentFile=Path.Combine(folder,"two.md"),HarnessSessionId="harness-second",State="completed",UpdatedUtc=DateTime.UtcNow.AddMinutes(-5)};
                second.Record(AgentMessage("user","历史问题二"));store.Save(second);
                var agent=host.TestHooks.Agent;
                agent.UseTestServices(historyPath,()=>new Qingye.Core.ModelConfig{BaseUrl="https://example.com",ApiKey="sk-ui-test-only",ModelName="test-model"},value=>new UiAgentTransport());
                host.ToggleAgent();Pump();
                try
                {
                    var listed=new Qingye.Core.AgentSessionStore(historyPath).List();
                    Check(listed.Count==2 && listed.Any(info=>info.Title=="第一份历史") && listed.Any(info=>info.Title=="第二份历史"),"History list lost local sessions");
                    AgentNamed(agent,"历史会话").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                    Check(Walk(agent).OfType<TextBlock>().Any(text=>text.Text=="第一份历史"),"History drawer did not render stored titles");
                    agent.OpenConversation(first.Id);Pump();
                    Check(agent.CurrentSession.Id==first.Id && agent.CurrentSession.HarnessSessionId=="harness-first","Opening history lost identity or SDK session id");
                    Check(agent.CurrentSession.History().Count==2,"Opening history lost turns");
                    Check(Walk(agent).OfType<RichTextBox>().Any(box=>new System.Windows.Documents.TextRange(box.Document.ContentStart,box.Document.ContentEnd).Text.Contains("历史回答一")),"Assistant history message was not rendered");
                    agent.DeleteConversation(second.Id,false);
                    Check(!new Qingye.Core.AgentSessionStore(historyPath).List().Any(info=>info.Id==second.Id),"Delete left the session on disk");
                    var third=new Qingye.Core.AgentSession{Title="第三份历史",Root=folder,CurrentFile=Path.Combine(folder,"one.md"),HarnessSessionId="harness-third"};
                    third.Record(AgentMessage("user","历史问题三"));store.Save(third);
                    agent.ClearHistory(false);
                    Check(new Qingye.Core.AgentSessionStore(historyPath).List().Count==0,"Clear left history files behind");
                    Check(agent.CurrentSession.Id!=first.Id,"Clear kept the deleted conversation selected");
                }
                finally{Await(agent.ShutdownAsync());host.Close();Pump();}
            });
            Test("Agent writes update the open editor buffer in place instead of reloading the window",delegate{
                string file=Path.Combine(root,"agent-sync.md");string oldText="第一行\n第二行\n";File.WriteAllText(file,oldText);
                Await(window.OpenPathsAsync(new[]{file},false));Pump();
                var doc=window.Current;var editor=Editor();editor.CaretOffset=3;Pump();
                Check(Object.ReferenceEquals(doc,window.Current) && editor.Text==oldText,"Fixture did not open the sync file");
                string newText=oldText+"第三行\n";
                File.WriteAllText(file,newText);
                window.TestHooks.Agent.ApplyEditorWrite(new Qingye.Core.WriteProposal{Path=file,RelativePath="agent-sync.md",OldText=oldText,NewText=newText,IsNew=false});
                Pump();
                Check(Object.ReferenceEquals(doc,window.Current) && window.Session.Documents.Contains(doc),"Agent write rebuilt the document list");
                Check(editor.Text==newText,"Open buffer did not receive the agent text");
                Check(editor.CaretOffset==3,"Agent write moved the caret to the start");
                Check(editor.Document.UndoStack.CanUndo,"Agent write is not undoable");
                editor.Document.UndoStack.Undo();Pump();
                Check(editor.Text==oldText,"Undo did not restore the pre-write text");
                editor.Text=oldText+"本地修改";Pump();
                Check(window.Current.IsDirty,"Local edit did not mark the buffer dirty");
                File.WriteAllText(file,newText+"Agent 再次写入");
                window.TestHooks.Agent.ApplyEditorWrite(new Qingye.Core.WriteProposal{Path=file,RelativePath="agent-sync.md",OldText=newText,NewText=newText+"Agent 再次写入",IsNew=false});
                Pump();
                Check(editor.Text.EndsWith("本地修改"),"Agent overwrote an unsaved editor buffer");
                Check(Walk(window.TestHooks.Agent).OfType<TextBlock>().Any(text=>text.Text.Contains("没有保存的修改")),"Dirty-buffer keep was not explained");
                doc.Modified=false;
            });
            Test("Agent-created files appear in the workspace tree",delegate{
                string folder=Path.Combine(root,"agent-newfile");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"existing.md"),"# existing");
                window.ImportWorkspace(folder);Await(window.WorkspaceLoadTask);Pump();
                string created=Path.Combine(folder,"created.md");File.WriteAllText(created,"# 新建");
                window.TestHooks.Agent.ApplyEditorWrite(new Qingye.Core.WriteProposal{Path=created,RelativePath="created.md",OldText="",NewText="# 新建",IsNew=true});
                Await(window.WorkspaceLoadTask);Pump();
                var tree=(System.Windows.Controls.TreeView)window.TestHooks.Workspace.folderTree;
                var node=(System.Windows.Controls.TreeViewItem)tree.Items[0];
                Check(node.Items.OfType<System.Windows.Controls.TreeViewItem>().Any(item=>item.Tag is string && (string)item.Tag==created),"Workspace tree missed the agent-created file");
                window.ImportWorkspace(root);Await(window.WorkspaceLoadTask);
            });
            Test("Native Agent exposes read-only, confirm and automatic file permissions",delegate{
                var host=new MainWindow{ShowActivated=false,Left=-16000,Top=0};host.Show();Pump();
                try
                {
                    var agent=host.TestHooks.Agent;
                    host.ToggleAgent();Pump();
                    var picker=Walk(agent).OfType<ComboBox>().FirstOrDefault(box=>box.Items.Count==3);
                    Check(picker!=null,"File permission picker is missing from the native panel");
                    Check(picker.ToolTip is string && ((string)picker.ToolTip).Contains("单文件"),"Permission picker does not explain single-file confirmation");
                    agent.FilePermission=Qingye.Core.HarnessFilePermission.Automatic;
                    Check(picker.SelectedIndex==2 && agent.FilePermission==Qingye.Core.HarnessFilePermission.Automatic,"Automatic permission selection was not accepted");
                    agent.FilePermission=Qingye.Core.HarnessFilePermission.ReadOnly;
                    Check(picker.SelectedIndex==0 && agent.FilePermission==Qingye.Core.HarnessFilePermission.ReadOnly,"Read-only permission selection was not accepted");
                    agent.FilePermission=Qingye.Core.HarnessFilePermission.Confirm;
                }
                finally{host.Close();Pump();}
            });
            Test("Agent shortcuts open the panel, history and focus mode",delegate{
                var host=new MainWindow{ShowActivated=false,Left=-16000,Top=0};host.Show();Pump();
                try
                {
                    var agent=host.TestHooks.Agent;
                    Check(!agent.IsOpen,"Agent started open");
                    host.ToggleAgentHistory();Pump();
                    Check(agent.IsOpen && agent.HistoryVisible,"Ctrl+Alt+H did not reveal the history drawer");
                    host.ToggleAgentFocus();Pump();
                    Check(Walk(agent).OfType<TextBlock>().Any(text=>text.Text.Contains("专注")),"Ctrl+Alt+F did not switch to focus mode");
                    host.ToggleAgentFocus();Pump();
                    string before=agent.CurrentSession.Id;
                    host.NewAgentTask();Pump();
                    Check(agent.IsOpen && agent.CurrentSession.Id!=before,"Ctrl+Alt+N did not start a new task");
                    agent.HandleEscape();Pump();
                    Check(!agent.HistoryVisible,"Escape kept the history drawer open");
                    host.StopAgent();Pump();
                    host.FocusDocument();Pump();
                    Check(!agent.IsKeyboardFocusWithin,"Focus stayed inside the agent panel");
                    host.ToggleAgent();Pump();
                    Check(!agent.IsOpen,"Ctrl+Alt+A did not close the panel");
                }
                finally{host.Close();Pump();}
            });
            Test("Agent sends with Enter and keeps Shift+Enter for newlines",delegate{
                Check(Qingye.UI.AgentPanel.IsSendShortcut(System.Windows.Input.Key.Enter,System.Windows.Input.ModifierKeys.None),"Enter no longer sends");
                Check(Qingye.UI.AgentPanel.IsSendShortcut(System.Windows.Input.Key.Enter,System.Windows.Input.ModifierKeys.Control),"Ctrl+Enter no longer sends");
                Check(!Qingye.UI.AgentPanel.IsSendShortcut(System.Windows.Input.Key.Enter,System.Windows.Input.ModifierKeys.Shift),"Shift+Enter would send instead of inserting a newline");
                Check(!Qingye.UI.AgentPanel.IsSendShortcut(System.Windows.Input.Key.Enter,System.Windows.Input.ModifierKeys.Alt),"Alt+Enter would send");
                var host=new MainWindow{ShowActivated=false,Left=-16000,Top=0};host.Show();Pump();
                string folder=Path.Combine(root,"agent-enter");Directory.CreateDirectory(folder);
                string file=Path.Combine(folder,"note.md");File.WriteAllText(file,"old");
                Await(host.OpenPathsAsync(new[]{file},false));host.ToggleAgent();
                var agent=host.TestHooks.Agent;var transport=new UiAgentTransport();
                var config=new Qingye.Core.ModelConfig{BaseUrl="https://example.com",ApiKey="sk-ui-test-only",ModelName="test-model"};
                agent.UseTestServices(Path.Combine(folder,"agent-history"),()=>config,value=>transport);
                try
                {
                    transport.Replies.Enqueue(new Qingye.Core.AgentReply{Content="收到"});
                    agent.Input.Text="按 Enter 发送";
                    agent.Input.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(agent.Input),0,System.Windows.Input.Key.Enter){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent});
                    Until(()=>agent.CurrentSession.History().Any(message=>Convert.ToString(message["content"])=="按 Enter 发送"));
                    Check(agent.Input.Text.Length==0,"Enter left the prompt text in the composer");
                }
                finally{Await(agent.ShutdownAsync());host.Close();Pump();}
            });
            Test("Agent binds scope before the runtime starts for the first send",delegate{
                var host=new MainWindow{ShowActivated=false,Left=-16000,Top=0};host.Show();Pump();
                try
                {
                    var agent=host.TestHooks.Agent;
                    string history=Path.Combine(root,"scope-bind-history");Directory.CreateDirectory(history);
                    var scopeConfig=new Qingye.Core.ModelConfig{BaseUrl="https://example.com",ApiKey="sk-ui-test-only",ModelName="test-model"};
                    agent.UseTestEnvironment(history,()=>scopeConfig);
                    host.ToggleAgent();Pump();
                    Check(agent.CurrentSession.Root==null && agent.CurrentSession.CurrentFile==null,"Panel invented a scope before any file existed");
                    string file=Path.Combine(root,"scope-bind.md");File.WriteAllText(file,"# scope");
                    Await(host.OpenPathsAsync(new[]{file},false));Pump();
                    Await(agent.PrepareControllerAsync());Pump();
                    Check(agent.CurrentSession.CurrentFile==file,"Controller was created before the editor scope was bound");
                    Check(agent.Controller!=null && agent.RuntimeProcessId==0,"Preparing the controller started the node process");
                    Check(agent.ContextText.Contains("单文件模式"),"Bound scope is not reflected in the panel context");
                }
                finally{host.Close();Pump();}
            });
            Test("Agent opened without a file follows the scope opened later",delegate{
                var host=new MainWindow{ShowActivated=false,Left=-16000,Top=0};host.Show();Pump();
                try
                {
                    var agent=host.TestHooks.Agent;
                    host.ToggleAgent();Pump();
                    Check(agent.CurrentSession.Root==null && agent.CurrentSession.CurrentFile==null,"A blank window invented a file scope");
                    Check(agent.ContextText.Contains("打开文件"),"Blank window does not ask for a scope");
                    string file=Path.Combine(root,"late-open.md");File.WriteAllText(file,"# late scope");
                    Await(host.OpenPathsAsync(new[]{file},false));host.UpdateStatus();Pump();
                    Check(agent.ContextText.Contains("单文件模式"),"Panel context did not follow the file opened after reveal");
                    host.ToggleAgent();Pump();host.ToggleAgent();Pump();
                    Check(agent.IsOpen && agent.ContextText.Contains("单文件模式"),"Re-revealing lost the late scope");
                }
                finally{host.Close();Pump();}
            });
            Test("Native Agent screenshots cover four themes and narrow widths",delegate{
                string output=Path.Combine(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","..")),"artifacts","ui");
                var host=new MainWindow{ShowActivated=false,Left=0,Top=0};host.Show();Pump();
                try
                {
                    string file=Path.Combine(root,"agent-shot.md");File.WriteAllText(file,"# 截图\n\n用于检查原生 Agent 面板的主题与窄窗口表现。\n");
                    Await(host.OpenPathsAsync(new[]{file},false));host.ToggleAgent();Pump();
                    var agent=host.TestHooks.Agent;
                    var transport=new UiAgentTransport();
                    var config=new Qingye.Core.ModelConfig{BaseUrl="https://example.com",ApiKey="sk-ui-test-only",ModelName="test-model"};
                    agent.UseTestServices(Path.Combine(root,"agent-shot-history"),()=>config,value=>transport);
                    transport.Replies.Enqueue(new Qingye.Core.AgentReply{Content="# 已检查当前文件\n\n- 保留原有内容\n- 标题与结构清晰\n\n修改前会先展示差异。"});
                    Await(agent.SubmitAsync("检查这份文档并给出修改建议"));
                    foreach(string theme in new[]{"mint","warm","lilac","dark"})
                    {
                        host.Width=1240;host.Height=820;host.SetTheme(theme);Pump();host.UpdateLayout();Pump();
                        string path=Path.Combine(output,"agent-native-"+theme+".png");
                        CaptureElement(agent,path);
                        Check(File.Exists(path) && new FileInfo(path).Length>5000,"Agent theme screenshot missing: "+theme);
                    }
                    host.SetTheme("dark");host.Width=820;host.Height=640;Pump();host.UpdateLayout();Pump();
                    string wide=Path.Combine(output,"agent-native-820.png");CaptureElement(agent,wide);
                    host.Width=390;host.Height=640;Pump();host.UpdateLayout();Pump();
                    string narrow=Path.Combine(output,"agent-native-390.png");CaptureElement(agent,narrow);
                    Check(File.Exists(wide) && File.Exists(narrow) && new FileInfo(narrow).Length>5000,"Narrow Agent screenshots missing");
                    Check(agent.ActualWidth>200,"Agent collapsed at 390 pixels");
                }
                finally{host.Close();Pump();}
            });
        }
        [STAThread] public static int Main()
        {
            HarnessPanel.AllowAutomaticStart = false;
            AgentPanel.AllowAutomaticStart = false;
            AgentPanel.AllowPrewarm = false;
            AppDomain.CurrentDomain.UnhandledException+=delegate(object s,UnhandledExceptionEventArgs e){var error=e.ExceptionObject as Exception;if(error!=null){Console.WriteLine("UNHANDLED "+error.GetType().FullName+" "+error.Message);Console.WriteLine(error.StackTrace);}};
            var app=new Application();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-data-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            Console.WriteLine("UI: constructing native window");
            try{window=new MainWindow();Console.WriteLine("UI: showing native window");window.ShowActivated=false;window.Left=-16000;window.Top=0;window.Show();Pump();}
            catch(Exception error){for(int i=0;error!=null && i<6;i++,error=error.InnerException){Console.WriteLine(error.GetType().FullName+": "+error.Message);Console.WriteLine(error.StackTrace);}return 1;}
            try
            {
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
                Test("Agent panel follows the opened file or workspace",delegate{
                    string file=Path.Combine(root,"agent-panel.md");File.WriteAllText(file,"# agent");
                    Await(window.OpenPathsAsync(new[]{file},false));window.ToggleAgent();Pump();
                    var agent=window.TestHooks.Agent;Check(agent.IsOpen && agent.ContextText.Contains("单文件模式"),"Agent did not follow the single file");
                    window.ImportWorkspace(root);Pump();window.UpdateStatus();Check(agent.ContextText.Contains("工作区"),"Agent did not follow the workspace");
                    window.ToggleAgent();Pump();Check(!agent.IsOpen,"Agent panel did not close");
                });
                Test("Agent stays usable when the window becomes narrow",delegate{
                    var host=new MainWindow{ShowActivated=false,Left=-16000,Top=0};host.Show();Pump();host.ToggleAgent();
                    try{foreach(int width in new[]{820,640,390})
                    {
                        host.Width=width;Pump();var agent=host.TestHooks.Agent;
                        Check(agent.IsOpen && agent.ActualWidth>200,"Agent disappears below 1000 pixels");
                        Check(Bounds(agent,host).Right<=host.ActualWidth+1,"Agent exceeds the window");
                    }}finally{host.Close();Pump();}
                });
                Test("Agent exposes a new-task action and explicit ready state",delegate{
                    window.ToggleAgent();Pump();var agent=window.TestHooks.Agent;
                    // The native panel keeps a single conversation plus a new-task
                    // action; history lives in the local drawer instead of a page.
                    Check(Walk(agent).OfType<Button>().Any(button=>(System.Windows.Automation.AutomationProperties.GetName(button)??"").StartsWith("新任务")),"No new-task entry point");
                    Check(agent.StatusText.Contains("待命"),"Model name is used in place of task state");
                    Check(agent.TimingText.Contains("打开面板") && agent.LastPanelMilliseconds>=0,"Panel reveal was not measured");
                    agent.NewConversation();Pump();
                    Check(agent.TimingText.Contains("新任务") && agent.LastNewTaskMilliseconds>=0,"New task was not measured");
                    window.ToggleAgent();Pump();
                });
                Test("Agent diff uses live theme resources including dark mode",delegate{
                    var host=new Window{Width=420,Height=320,Left=-16000,Top=0,ShowActivated=false};Theme.Install(host);
                    var document=DiffDialog.BuildDocument(new Qingye.Core.WriteProposal{OldText="old",NewText="new"});host.Content=new RichTextBox{Document=document};host.Show();
                    try
                    {
                        var backgrounds=new System.Collections.Generic.List<System.Windows.Media.Color>();
                        foreach(string theme in new[]{"mint","warm","lilac","dark"})
                        {
                            Theme.Apply(host,new Qingye.Core.Settings{Theme=theme});Pump();
                            var added=document.Blocks.OfType<System.Windows.Documents.Paragraph>().First(paragraph=>new System.Windows.Documents.TextRange(paragraph.ContentStart,paragraph.ContentEnd).Text.StartsWith("+ "));
                            backgrounds.Add(((System.Windows.Media.SolidColorBrush)added.Background).Color);
                        }
                        Check(backgrounds.Distinct().Count()==4,"Diff ignores the selected content theme");
                    }
                    finally{host.Close();Pump();}
                });
                Test("Agent tool rows follow the dark text token",delegate{
                    var host=new Window{Width=400,Height=300,Left=-16000,Top=0,ShowActivated=false};Theme.Install(host);Theme.Apply(host,new Qingye.Core.Settings{Theme="dark"});
                    var view=new AgentConversationView();view.ToolStarted("tool-1","read_file note.md");host.Content=view;host.Show();Pump();
                    try{Check(Walk(view).OfType<Expander>().Single().Foreground==host.FindResource("TextBrush"),"Tool label becomes black on the dark surface");}finally{host.Close();Pump();}
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
                NativeAgentTests();
                HarnessUiTests();
                AgentIntegrationTests();
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
            }
            finally
            {
                foreach(var doc in window.Session.Documents)doc.Modified=false;
                window.Close();Pump();app.Shutdown();if(Qingye.Core.FileService.IsInsideRoot(AppDomain.CurrentDomain.BaseDirectory,root))Directory.Delete(root,true);
            }
            Console.WriteLine("UI: "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
        [StructLayout(LayoutKind.Sequential)] struct ProcessBasicInfo { public IntPtr Reserved1,Peb,Reserved2,Reserved3,Pid,Parent; }
        static void CheckBrowser(PreviewPane pane){Check(pane.Browser!=null && pane.Browser.CoreWebView2!=null,"Preview engine not created: "+String.Join(" | ",Walk(pane).OfType<TextBlock>().Select(t=>t.Text)));}
        static T FindChild<T>(DependencyObject parent) where T:DependencyObject{if(parent==null)return null;if(parent is T)return (T)parent;for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++){var found=FindChild<T>(System.Windows.Media.VisualTreeHelper.GetChild(parent,i));if(found!=null)return found;}return null;}
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
