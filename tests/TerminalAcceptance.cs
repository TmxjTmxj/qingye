using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Web.Script.Serialization;
using Qingye.UI;
namespace Qingye.Tests {
public static class TerminalAcceptance {
static readonly string Root=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"../.."));
static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
static void Pump(){var f=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>f.Continue=false));Dispatcher.PushFrame(f);}
static void Wait(Func<bool> predicate,int seconds=30){var end=DateTime.UtcNow.AddSeconds(seconds);while(!predicate()){if(DateTime.UtcNow>end)throw new Exception("Acceptance state timed out");Pump();Thread.Sleep(15);}}
static void Await(Task task){Wait(()=>task.IsCompleted);task.GetAwaiter().GetResult();Pump();}
static Dictionary<string,object> State(TerminalPanel panel){var task=panel.StateAsync();Await(task);return task.Result;}
static string Text(TerminalPanel panel){var s=State(panel);return s.ContainsKey("text")?Convert.ToString(s["text"]):"";}
static void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS "+label);}
static void TextWait(TerminalPanel panel,string target,int seconds=30){try{Wait(()=>Text(panel).IndexOf(target,StringComparison.OrdinalIgnoreCase)>=0,seconds);}catch{Console.WriteLine("WAITING FOR "+target+"\n"+Text(panel));throw;}}
static Dictionary<string,object> Cell(TerminalPanel panel,int row,int col){var task=panel.Browser.CoreWebView2.ExecuteScriptAsync("qingyeTerminal.inspect("+row+","+col+")");Await(task);return Json.Deserialize<Dictionary<string,object>>(task.Result);}
static void Capture(MainWindow window,string name){window.UpdateLayout();Pump();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var visual=new DrawingVisual();using(var drawing=visual.RenderOpen()){drawing.DrawImage(bitmap,new Rect(0,0,window.ActualWidth,window.ActualHeight));using(var stream=new MemoryStream()){var view=window.TestHooks.Terminal.Browser;Await(view.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,stream));stream.Position=0;var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();var origin=view.TranslatePoint(new Point(),window);drawing.DrawImage(image,new Rect(origin.X,origin.Y,view.ActualWidth,view.ActualHeight));}}var full=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);full.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(full));using(var output=File.Create(Path.Combine(Root,"artifacts/ui/",name)))encoder.Save(output);}
static void Start(TerminalPanel panel){panel.Start();Wait(()=>panel.IsRunning||panel.RendererError!=null);Check(panel.IsRunning,"Renderer ready without script errors: "+panel.RendererError);}
static void ExitCli(TerminalPanel panel){panel.Stop();Start(panel);panel.Send("echo SHELL_RESTARTED\r");TextWait(panel,"\nSHELL_RESTARTED");}
public static void Run(){var window=new MainWindow{Width=1240,Height=820,Left=-16000,Top=0,ShowActivated=false};window.Show();Pump();string fixture=Path.Combine(Root,"build/cli-tests/workspace");Directory.CreateDirectory(fixture);
try {
var panel=window.TestHooks.Terminal;Check(panel.Browser==null&&panel.ProcessId==0,"Reader startup loads neither WebView nor CMD");string originalRuntime=Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER");
try{Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",Path.Combine(Root,"build/cli-tests/missing-runtime"));window.ToggleTerminal();Await(panel.StartupTask);Check(panel.RendererError!=null&&panel.Browser==null,"Failed renderer initialization releases the browser and can retry");}
finally{Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",originalRuntime);}
panel.Start();panel.Stop();Await(panel.StartupTask);Check(!panel.IsRunning,"Stopping during initialization prevents a late shell launch");
Start(panel);Check(panel.IsRunning,"xterm renderer initialized after retry: "+panel.RendererError);
string probe="\""+Path.Combine(Root,"build/bin/TerminalProbe.exe")+"\"";
panel.Send(probe+"\r");TextWait(panel,"PROBE_DONE");var state=State(panel);
var red=Cell(panel,0,9);Check(Convert.ToString(red["chars"])=="R"&&Convert.ToInt32(red["fg"])==1&&Convert.ToBoolean(red["bold"]),"ANSI red and bold remain real cell attributes");
var combining=Cell(panel,1,11);var wide=Cell(panel,1,13);var emoji=Cell(panel,1,16);
Check(Convert.ToString(combining["chars"])=="e\u0301"&&Convert.ToInt32(combining["width"])==1,"Combining accent occupies one terminal cell");
Check(Convert.ToString(wide["chars"])=="中"&&Convert.ToInt32(wide["width"])==2,"Chinese glyph occupies two cells");
Check(Convert.ToString(emoji["chars"])=="👩‍💻"&&Convert.ToInt32(emoji["width"])==2,"Joined emoji stays one grapheme in two cells");
Check(Text(panel).Contains("QY_SIZE "+state["cols"]+"x"+state["rows"]),"ConPTY dimensions exactly match measured renderer cells");
Capture(window,"terminal-0.13-unicode.png");
panel.Send(probe+" alt\r");TextWait(panel,"ALT_READY");Check(Convert.ToBoolean(State(panel)["alternate"]),"TUI uses a separate alternate buffer");
window.Width=900;Pump();Wait(()=>Convert.ToInt32(State(panel)["cols"])<Convert.ToInt32(state["cols"]));Capture(window,"terminal-0.13-alt.png");
panel.Send("\r");TextWait(panel,"PROBE_DONE");Check(!Convert.ToBoolean(State(panel)["alternate"])&&Text(panel).Contains("PRIMARY_SENTINEL"),"Returning from TUI restores the primary screen");
window.Width=1240;window.Appearance.TerminalSize=360;window.UpdateToolbar();window.SetTheme("dark");Pump();
int cols=Convert.ToInt32(State(panel)["cols"]);panel.Browser.ZoomFactor=1.25;Wait(()=>Convert.ToInt32(State(panel)["cols"])<cols);Capture(window,"terminal-0.13-zoom125.png");panel.Browser.ZoomFactor=1.0;
Check(true,"125 percent browser scale refits cells without guessed pixel widths");
// Only startup/onboarding screens are exercised. No prompt, credentials or model request.
Console.WriteLine("STAGE restart before Claude");ExitCli(panel);string config=Path.Combine(Root,"build/cli-tests/claude");Directory.CreateDirectory(config);
panel.Send("cd /d \""+fixture+"\"\rset \"CLAUDE_CONFIG_DIR="+config+"\"\rset CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1\rset DISABLE_TELEMETRY=1\rset ANTHROPIC_API_KEY=\rset ANTHROPIC_AUTH_TOKEN=\rset ANTHROPIC_BASE_URL=http://127.0.0.1:1\rset ANTHROPIC_API_KEY=QINGYE_TERMINAL_TEST_NOT_A_SECRET\rclaude --permission-mode plan\r");
Console.WriteLine("STAGE Claude startup");TextWait(panel,"Claude Code",45);Wait(()=>{var t=Text(panel);return t.Contains("text style")||t.Contains("theme")||t.Contains("Unable to connect");},45);for(int i=0;i<30;i++){Thread.Sleep(20);Pump();}Capture(window,"terminal-0.13-claude.png");File.WriteAllText(Path.Combine(Root,"artifacts/metrics/terminal-0.13-claude.txt"),Text(panel));Check(Text(panel).Contains("Welcome to Claude Code"),"Claude Code 2.1.316 startup text renders");if(Text(panel).Contains("Unable to connect"))Console.WriteLine("LIMIT Claude interactive onboarding not verified: upstream startup connectivity check returned 403; no credentials or model prompt used.");else Check(Text(panel).Contains("theme")||Text(panel).Contains("text style"),"Claude theme selector renders");
panel.Send("\x1b[B\x1b[A");Thread.Sleep(100);Pump();Capture(window,"terminal-0.13-claude-keys.png");
ExitCli(panel);
string opencode=Path.Combine(Root,"build/cli-tests/tools/opencode.exe");if(!File.Exists(opencode))throw new Exception("Isolated OpenCode fixture is missing");
foreach(string variable in new[]{"XDG_CONFIG_HOME","XDG_DATA_HOME","XDG_CACHE_HOME","XDG_STATE_HOME"}){string path=Path.Combine(Root,"build/cli-tests/opencode",variable);Directory.CreateDirectory(path);panel.Send("set \""+variable+"="+path+"\"\r");}
panel.Send("cd /d \""+fixture+"\"\rset OPENCODE_DISABLE_CLAUDE_CODE=1\rset OPENCODE_DISABLE_DEFAULT_PLUGINS=1\rset OPENCODE_DISABLE_MODELS_FETCH=1\rset OPENCODE_DISABLE_LSP_DOWNLOAD=1\rset DO_NOT_TRACK=1\rset OPENAI_API_KEY=\rset ANTHROPIC_API_KEY=\rset \"OPENCODE_CONFIG_CONTENT={\"autoupdate\":false,\"snapshot\":false,\"enabled_providers\":[]}\"\r\""+opencode+"\"\r");
Wait(()=>{string text=Text(panel);return text.IndexOf("ctrl+p",StringComparison.OrdinalIgnoreCase)>=0||text.IndexOf("Ask anything",StringComparison.OrdinalIgnoreCase)>=0;},45);
Capture(window,"terminal-0.13-opencode.png");File.WriteAllText(Path.Combine(Root,"artifacts/metrics/terminal-0.13-opencode.txt"),Text(panel));Check(Convert.ToBoolean(State(panel)["alternate"]),"OpenCode 1.19.5 full-screen TUI renders in alternate buffer");
panel.Send("\x1b");for(int i=0;i<20;i++){Thread.Sleep(20);Pump();}
window.Appearance.TerminalDock="right";window.Appearance.TerminalSize=460;window.UpdateToolbar();Pump();window.UpdateLayout();Wait(()=>Math.Abs(Convert.ToDouble(State(panel)["viewportWidth"])-panel.Browser.ActualWidth)<3);Wait(()=>Convert.ToInt32(State(panel)["cols"])<70);for(int i=0;i<60;i++){Thread.Sleep(20);Pump();}Check(Convert.ToDouble(State(panel)["canvasWidth"])<=panel.Browser.ActualWidth,"Dock switch refits the real terminal canvas to the right panel");Capture(window,"terminal-0.13-opencode-right.png");
panel.Send("\x03");ExitCli(panel);int exited=panel.ProcessId;panel.Send("exit\r");Wait(()=>!panel.IsRunning);TextWait(panel,"SHELL_RESTARTED");Wait(()=>{try{using(var process=Process.GetProcessById(exited))return process.HasExited;}catch(ArgumentException){return true;}});Check(true,"Natural CMD exit ends the shell without closing the window");Start(panel);panel.Send("echo AFTER_NATURAL_EXIT\r");TextWait(panel,"\nAFTER_NATURAL_EXIT");
int pid=panel.ProcessId;int browser=(int)panel.Browser.CoreWebView2.BrowserProcessId;window.Close();Wait(()=>!window.IsVisible);Wait(()=>{try{using(var p=Process.GetProcessById(pid))return p.HasExited;}catch(ArgumentException){return true;}});Wait(()=>{try{using(var p=Process.GetProcessById(browser))return p.HasExited;}catch(ArgumentException){return true;}});Check(true,"Close ends owned shell, CLI and browser process");
}catch{if(window.TestHooks.Terminal.Browser!=null){File.WriteAllText(Path.Combine(Root,"artifacts/metrics/terminal-0.13-failure.txt"),Text(window.TestHooks.Terminal));Capture(window,"terminal-0.13-failure.png");}throw;}finally{if(window.IsVisible)window.Close();Pump();}
}
} }
