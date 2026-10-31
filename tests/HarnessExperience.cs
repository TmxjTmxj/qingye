using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Qingye.Core;
using Qingye.UI;

namespace Qingye.Tests
{
    public static partial class ExperienceHarness
    {
        // 0.10 acceptance: the right-hand panel is native WPF and the agent runs on
        // a persistent DSH SDK process (stdio JSON-RPC + loopback TCP file bridge).
        // No WebView2 is allowed to appear anywhere on this path.
        //
        // The flow drives the real MainWindow / AgentPanel through the internal test
        // hooks (UseTestEnvironment + EnsureRuntimeAsync) and a local synthetic model
        // served over OpenAI-compatible SSE. Timing is recorded, never tuned to the
        // machine: hard failure thresholds are generous (a hot send over one second
        // is a product regression, a startup over thirty seconds is a hang).
        const string OriginalTitle = "# 阅读计划";
        const string UpdatedTitle = "# 本周阅读计划";
        const string OriginalBody = "周一：阅读设计资料。\n周三：整理重点。\n";
        

        // ---------------------------------------------------------------- helpers

        static string ArchiveMetadata(string directory)
        {
            if (!Directory.Exists(directory)) return "absent";
            var pending = new Stack<string>();
            var rows = new List<string>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                var attributes = File.GetAttributes(current);
                rows.Add(current + "|" + attributes);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                foreach (string child in Directory.EnumerateFileSystemEntries(current))
                {
                    var childAttributes = File.GetAttributes(child);
                    if ((childAttributes & FileAttributes.Directory) != 0) pending.Push(child);
                    else
                    {
                        var info = new FileInfo(child);
                        rows.Add(child + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
                    }
                    if (rows.Count + pending.Count > 100000) throw new Exception("Existing archive metadata exceeds the fixture budget");
                }
            }
            rows.Sort(StringComparer.Ordinal);
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(String.Join("\n", rows))));
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
        }

        static void PumpUntil(Func<bool> condition, string message, int seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new Exception("超时：" + message);
                Pump();
                Thread.Sleep(20);
            }
            Pump();
        }

        static T WaitResult<T>(Task<T> task, string label, int seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) throw new Exception("超时：" + label);
                Pump();
                Thread.Sleep(5);
            }
            T value = task.GetAwaiter().GetResult();
            Pump();
            return value;
        }

        static void WaitDone(Task task, string label, int seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) throw new Exception("超时：" + label);
                Pump();
                Thread.Sleep(5);
            }
            task.GetAwaiter().GetResult();
            Pump();
        }

        static string ReadString(object target, string member)
        {
            if (target == null) return null;
            var type = target.GetType();
            var property = type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                object value = property.GetValue(target, null);
                return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            var field = type.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                object value = field.GetValue(target);
                return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            return null;
        }

        static object ReadValue(object target, string member)
        {
            if (target == null) return null;
            var type = target.GetType();
            var property = type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(target, null);
            var field = type.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(target);
        }
        static double ReadDouble(object target, string member)
        {
            string text = ReadString(target, member);
            double value;
            return text != null && Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        static IEnumerable<FrameworkElement> Elements(DependencyObject root)
        {
            return Descendants<FrameworkElement>(root);
        }

        static string VisualText(DependencyObject root)
        {
            var text = new StringBuilder();
            foreach (var block in Descendants<TextBlock>(root)) text.AppendLine(block.Text);
            foreach (var box in Descendants<TextBox>(root)) text.AppendLine(box.Text);
            foreach (var rich in Descendants<RichTextBox>(root))
            {
                try { text.AppendLine(new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text); }
                catch { }
            }
            foreach (var expander in Descendants<Expander>(root)) text.AppendLine(Convert.ToString(expander.Header));
            return text.ToString();
        }

        static string ButtonLabel(Button button)
        {
            if (button == null) return "";
            var direct = button.Content as string;
            if (!String.IsNullOrEmpty(direct)) return direct;
            var panel = button.Content as StackPanel;
            if (panel != null)
            {
                foreach (var child in panel.Children)
                {
                    var block = child as TextBlock;
                    if (block != null && Convert.ToString(block.Tag) == "ButtonLabel") return block.Text;
                }
            }
            return System.Windows.Automation.AutomationProperties.GetName(button) ?? "";
        }

        static Button FindButton(DependencyObject root, string label)
        {
            foreach (var button in Descendants<Button>(root))
            {
                // Shortcut hints are appended to tooltips, so match the label as
                // a prefix rather than requiring the bare text.
                if (ButtonLabel(button).StartsWith(label, StringComparison.Ordinal) ||
                    Convert.ToString(button.ToolTip).StartsWith(label, StringComparison.Ordinal)) return button;
            }
            return null;
        }

        static Button FindVisibleButton(DependencyObject root, string label)
        {
            // Completed approval cards remain in the live timeline. A disabled
            // old action must not hide the enabled action in the current card.
            return Descendants<Button>(root).FirstOrDefault(button => button.IsVisible && button.IsEnabled &&
                (ButtonLabel(button).StartsWith(label, StringComparison.Ordinal) || Convert.ToString(button.ToolTip).StartsWith(label, StringComparison.Ordinal)));
        }

        static Button FindApprovalButton(DependencyObject root, string label)
        {
            var button = FindVisibleButton(root, label);
            if (button != null) return button;
            // Buttons inside an approval card are only materialised when the card is
            // on screen; a second pass after a layout pump catches late insertions.
            Pump();
            return FindVisibleButton(root, label);
        }

        static bool ApprovalCardVisible(DependencyObject root)
        {
            return FindVisibleButton(root, "拒绝") != null && FindVisibleButton(root, "应用修改") != null;
        }

        static bool HistoryVisible(DependencyObject root)
        {
            foreach (var view in Descendants<AgentHistoryView>(root)) if (view.IsVisible) return true;
            return false;
        }

        static string ApprovalDiffText(DependencyObject root)
        {
            // DiffDialog renders each diff row as "<marker> <text>" where marker is
            // '-' or '+'; the card is the only RichTextBox carrying both markers.
            foreach (var rich in Descendants<RichTextBox>(root))
            {
                string text;
                try { text = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text; }
                catch { continue; }
                if (text == null) continue;
                if (text.Contains("+ ") && text.Contains("- ") && text.Contains("阅读计划")) return text;
            }
            return null;
        }

        static string SessionText(AgentSession session)
        {
            if (session == null) return "";
            var text = new StringBuilder();
            foreach (var entry in session.Entries)
            {
                text.Append(entry.Kind).Append(' ').Append(entry.Text).Append(' ').Append(entry.ToolName).AppendLine();
                if (entry.Message != null)
                {
                    object content;
                    if (entry.Message.TryGetValue("content", out content)) text.AppendLine(Convert.ToString(content));
                }
            }
            return text.ToString();
        }

        static void Click(Button button, string label)
        {
            Check(button != null, "找不到按钮「" + label + "」");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Thread.Sleep(60);
            Pump();
        }

        static void CaptureWindow(MainWindow window, string name)
        {
            window.UpdateLayout();
            Pump();
            int width = (int)window.ActualWidth;
            int height = (int)window.ActualHeight;
            Check(width > 0 && height > 0, "窗口尺寸无效，无法截图 " + name);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, name))) encoder.Save(file);
        }

        static bool ProcessExists(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (var process = Process.GetProcessById(pid)) return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        // Enumerates the direct children of the test process. ParentProcessId is the
        // only reliable way to distinguish "our node runtime" from another node.exe
        // a developer or a parallel teammate started. Returns null when the query is
        // unavailable (for example PowerShell blocked), so callers can degrade.
        sealed class ChildProcessInfo
        {
            internal int ProcessId;
            internal string Name;
            internal string CommandLine;
        }

        static List<ChildProcessInfo> OurChildProcesses()
        {
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                string command = "Get-CimInstance Win32_Process -Filter 'ParentProcessId=" + pid + "' | Select-Object ProcessId,Name,CommandLine | ConvertTo-Json -Compress";
                var info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"" + command + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var shell = Process.Start(info))
                {
                    string body = shell.StandardOutput.ReadToEnd();
                    shell.StandardError.ReadToEnd();
                    if (!shell.WaitForExit(20000))
                    {
                        try { shell.Kill(); } catch { }
                        return null;
                    }
                    if (String.IsNullOrWhiteSpace(body)) return new List<ChildProcessInfo>();
                    var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                    object parsed = serializer.DeserializeObject(body.Trim());
                    var list = new List<ChildProcessInfo>();
                    AddChild(list, parsed as Dictionary<string, object>);
                    var array = parsed as object[];
                    if (array != null) foreach (var item in array) AddChild(list, item as Dictionary<string, object>);
                    return list;
                }
            }
            catch
            {
                return null;
            }
        }

        static void AddChild(List<ChildProcessInfo> list, Dictionary<string, object> row)
        {
            if (row == null) return;
            var child = new ChildProcessInfo();
            object value;
            if (row.TryGetValue("ProcessId", out value)) child.ProcessId = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (row.TryGetValue("Name", out value)) child.Name = Convert.ToString(value);
            if (row.TryGetValue("CommandLine", out value)) child.CommandLine = Convert.ToString(value);
            list.Add(child);
        }

        static List<int> OwnedNodePids()
        {
            var children = OurChildProcesses();
            if (children == null) return null;
            return children.Where(child => String.Equals(child.Name, "node.exe", StringComparison.OrdinalIgnoreCase))
                .Select(child => child.ProcessId).Distinct().ToList();
        }

        static bool HasOwnedWebView2()
        {
            var children = OurChildProcesses();
            if (children == null) return false;
            return children.Any(child => child.Name != null &&
                child.Name.IndexOf("msedgewebview2", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static void CheckNoWebView2InPanel(DependencyObject root)
        {
            foreach (var element in Elements(root))
            {
                if (element == null) continue;
                string type = element.GetType().Name;
                if (type.IndexOf("WebView", StringComparison.OrdinalIgnoreCase) >= 0) throw new Exception("FAIL: 原生面板出现了 WebView 控件：" + type);
                if (type.IndexOf("HarnessPanel", StringComparison.OrdinalIgnoreCase) >= 0) throw new Exception("FAIL: 生产面板仍然挂载了 HarnessPanel（WebView 方案）");
            }
            Check(!HasOwnedWebView2(), "面板运行期间出现了本进程拥有的 WebView2 子进程");
        }

        // ------------------------------------------------------------- mock model

        sealed class HarnessMockServer : IDisposable
        {
            readonly HttpListener listener = new HttpListener();
            internal readonly string BaseUrl;
            internal int Requests;
            internal string LastRequest;
            internal string LastPlan = "";
            readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };

            internal HarnessMockServer()
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int number = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                BaseUrl = "http://127.0.0.1:" + number + "/v1";
                listener.Prefixes.Add("http://127.0.0.1:" + number + "/");
                listener.Start();
                Task.Run(new Func<Task>(ServeAsync));
            }

            async Task ServeAsync()
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); } catch { return; }
                    try { await HandleAsync(context); }
                    catch (Exception error) { Console.WriteLine("MOCK ERROR " + error.Message); try { context.Response.Abort(); } catch { } }
                }
            }

            async Task HandleAsync(HttpListenerContext context)
            {
                string body = "";
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
                Interlocked.Increment(ref Requests);
                LastRequest = body;
                var request = String.IsNullOrWhiteSpace(body) ? new Dictionary<string, object>() :
                    (json.DeserializeObject(body) as Dictionary<string, object>) ?? new Dictionary<string, object>();
                var plan = ChoosePlan(request);
                LastPlan = plan.ToolName != null ? plan.ToolName : "text";
                bool stream = true;
                object streamValue;
                if (request.TryGetValue("stream", out streamValue) && streamValue is bool) stream = (bool)streamValue;
                if (!stream)
                {
                    context.Response.ContentType = "application/json; charset=utf-8";
                    using (var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false)))
                        writer.Write(json.Serialize(new
                        {
                            id = "qa", model = "本地验收模型", created = Now(),
                            choices = new[] { new { index = 0, message = new { role = "assistant", content = plan.Content }, finish_reason = "stop" } }
                        }));
                    context.Response.Close();
                    return;
                }
                context.Response.ContentType = "text/event-stream; charset=utf-8";
                context.Response.SendChunked = true;
                using (var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false)))
                {
                    object delta;
                    string finish;
                    int delay;
                    if (plan.ToolName != null)
                    {
                        delta = new
                        {
                            role = "assistant",
                            tool_calls = new[] { new { index = 0, id = "qa-" + Requests, type = "function", function = new { name = plan.ToolName, arguments = plan.Arguments } } }
                        };
                        finish = "tool_calls";
                        delay = 140;
                    }
                    else
                    {
                        delta = new { role = "assistant", content = plan.Content };
                        finish = "stop";
                        delay = 25;
                    }
                    await writer.WriteAsync("data: " + json.Serialize(new { id = "qa", model = "本地验收模型", created = Now(), choices = new[] { new { index = 0, delta = delta, finish_reason = (string)null } } }) + "\n\n");
                    await writer.FlushAsync();
                    await Task.Delay(delay);
                    await writer.WriteAsync("data: " + json.Serialize(new { id = "qa", model = "本地验收模型", created = Now(), choices = new[] { new { index = 0, delta = new { }, finish_reason = finish } } }) + "\n\ndata: [DONE]\n\n");
                }
                context.Response.Close();
            }

            static int Now()
            {
                return (int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            }

            sealed class MockPlan
            {
                internal string ToolName;
                internal string Arguments;
                internal string Content;
            }

            MockPlan ChoosePlan(Dictionary<string, object> request)
            {
                string user = LastUserText(request);
                int toolCount = ToolCount(request);
                if (user.IndexOf("越界", StringComparison.Ordinal) >= 0)
                {
                    if (toolCount == 0)
                        return Tool("write_file", json.Serialize(new { path = "../越界-验收.md", content = "不应写入工作区之外。" }));
                    return Final("已确认不能写入工作区之外的路径。");
                }
                if (user.IndexOf("后绑定", StringComparison.Ordinal) >= 0)
                {
                    if (toolCount == 0) return Tool("read_file", json.Serialize(new { path = "阅读计划.md" }));
                    return Final("后绑定测试完成：运行时会话已切换到编辑器当前工作区，文件工具可用。");
                }
                if (user.IndexOf("继续", StringComparison.Ordinal) >= 0)
                    return Final("已恢复历史会话并继续完成。\n\n这份回复来自本机验收模型。");
                if (user.IndexOf("先读取", StringComparison.Ordinal) >= 0)
                {
                    if (toolCount == 0) return Tool("read_file", json.Serialize(new { path = "阅读计划.md" }));
                    if (toolCount == 1) return Tool("write_file", json.Serialize(new { path = "阅读计划.md", content = UpdatedTitle + "\n\n" + OriginalBody }));
                    return Final("已取消，未修改文件。");
                }
                if (user.IndexOf("直接修改", StringComparison.Ordinal) >= 0)
                {
                    if (toolCount == 0) return Tool("write_file", json.Serialize(new { path = "阅读计划.md", content = UpdatedTitle + "\n\n" + OriginalBody }));
                    return Final("## 已完成\n\n标题已按确认的差异更新，其余内容保持不变。\n\n这份回复来自本机验收模型，用于验证 SDK 原生面板的完整工作流程。");
                }
                return Final("## 已完成\n\n已读取当前范围，并给出结论。\n\n这份回复来自本机验收模型。");
            }

            static MockPlan Tool(string name, string arguments)
            {
                return new MockPlan { ToolName = name, Arguments = arguments };
            }

            static MockPlan Final(string content)
            {
                return new MockPlan { Content = content };
            }

            static int ToolCount(Dictionary<string, object> request)
            {
                object value;
                if (!request.TryGetValue("messages", out value)) return 0;
                var messages = value as System.Collections.IEnumerable;
                if (messages == null) return 0;
                int count = 0;
                foreach (var item in messages)
                {
                    var message = item as Dictionary<string, object>;
                    if (message == null) continue;
                    object role;
                    if (!message.TryGetValue("role", out role)) continue;
                    if (Convert.ToString(role) == "tool") count++;
                    else if (Convert.ToString(role) == "user")
                    {
                        object content;
                        if (message.TryGetValue("content", out content) && !IsRuntimeContext(ContentText(content))) count = 0;
                    }
                }
                return count;
            }

            static string ContentText(object content)
            {
                if (content == null) return "";
                var direct = content as string;
                if (direct != null) return direct;
                var parts = content as System.Collections.IEnumerable;
                if (parts == null) return Convert.ToString(content) ?? "";
                var text = new StringBuilder();
                foreach (var part in parts)
                {
                    var map = part as Dictionary<string, object>;
                    if (map == null) { text.Append(Convert.ToString(part)); continue; }
                    object value;
                    if (map.TryGetValue("text", out value)) text.Append(Convert.ToString(value));
                    else if (map.TryGetValue("content", out value)) text.Append(Convert.ToString(value));
                }
                return text.ToString();
            }

            static bool IsRuntimeContext(string text)
            {
                return text.StartsWith("Current runtime context.", StringComparison.Ordinal) ||
                    text.StartsWith("Current runtime context:", StringComparison.Ordinal);
            }

            // Follow the latest real request. A warm session intentionally keeps
            // older turns, and the SDK also injects its own runtime-context row.
            static string LastUserText(Dictionary<string, object> request)
            {
                object value;
                if (!request.TryGetValue("messages", out value)) return "";
                var messages = value as System.Collections.IEnumerable;
                if (messages == null) return "";
                string text = "";
                foreach (var item in messages)
                {
                    var message = item as Dictionary<string, object>;
                    if (message == null) continue;
                    object role;
                    if (!message.TryGetValue("role", out role) || Convert.ToString(role) != "user") continue;
                    object content;
                    if (!message.TryGetValue("content", out content)) continue;
                    string candidate = ContentText(content);
                    if (!IsRuntimeContext(candidate)) text = candidate;
                }
                return text;
            }

            public void Dispose()
            {
                try { listener.Stop(); listener.Close(); } catch { }
            }
        }

        // ------------------------------------------------------------- the QA flow

        static void HarnessQa(string[] args)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            Directory.CreateDirectory(Path.Combine(project, "artifacts", "metrics"));
            Directory.CreateDirectory(Path.Combine(project, "artifacts", "ui"));

            string folder = Path.Combine(project, "build", "harness-qa-" + Guid.NewGuid().ToString("N"));
            string workspace = Path.Combine(folder, "设计资料");
            Directory.CreateDirectory(workspace);
            string file = Path.Combine(workspace, "阅读计划.md");
            File.WriteAllText(file, OriginalTitle + "\n\n" + OriginalBody, new UTF8Encoding(false));
            string historyPath = Path.Combine(folder, "agent-history");
            // Own the installation fixture and preserve any existing archives.
            string installation = Path.Combine(folder, "installation");
            string applicationData = Path.Combine(installation, "agent-data");
            string runtimeData = Path.Combine(installation, "harness-data", Guid.NewGuid().ToString("N"));
            string existingAppData = Path.Combine(project, "app", "agent-data");
            string existingBuildData = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent-data");
            string appDataBefore = ArchiveMetadata(existingAppData);
            string buildDataBefore = ArchiveMetadata(existingBuildData);

            var metrics = new List<string>();
            var passes = new List<string>();
            var mock = new HarnessMockServer();
            var total = Stopwatch.StartNew();
            MainWindow window = null;
            AgentPanel panel = null;
            bool completed = false;
            try
            {
                Console.WriteLine("SDK_QA start: workspace=" + workspace);
                metrics.Add("轻页 0.10 原生 SDK 端到端验收");
                metrics.Add("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                metrics.Add("临时工作区：" + workspace);
                metrics.Add("本机合成模型：" + mock.BaseUrl);

                Func<ModelConfig> config = delegate
                {
                    return new ModelConfig { BaseUrl = mock.BaseUrl, ApiKey = "qa-local-only", ModelName = "本地验收模型" };
                };

                window = new MainWindow { Width = 1280, Height = 860, Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
                panel = window.TestHooks.Agent;
                Check(panel != null, "TestHooks.Agent 为空，无法访问 AgentPanel");
                panel.UseTestEnvironment(historyPath, runtimeData, config);
                string suppliedData = ReadString(panel, "RuntimeDataDirectory") ?? ReadString(panel, "DataDirectory");
                if (!String.IsNullOrEmpty(suppliedData)) runtimeData = suppliedData;
                Console.WriteLine("SDK_QA panel configured: history=" + historyPath);
                window.Show();
                Pump();
                Await(window.OpenPathsAsync(new[] { file }, false));
                Check(window.Current != null && window.Current.Text.Contains("阅读计划"), "临时 Markdown 没有打开");
                Pump();

                // 1. First open: progress text -> ready, measured against the wall clock.
                window.ToggleAgent();
                Pump();
                Check(panel.IsOpen, "Agent 面板没有打开");
                Check(!panel.RuntimeStarted, "运行时在显式启动前就已运行（自动预热未关闭，计时会失真）");
                var startup = Stopwatch.StartNew();
                var starting = panel.EnsureRuntimeAsync();
                bool sawStarting = false;
                var observed = new List<string>();
                while (!starting.IsCompleted)
                {
                    string status = panel.StatusText ?? "";
                    observed.Add(status);
                    if (status.Contains("正在启动")) sawStarting = true;
                    Pump();
                    Thread.Sleep(5);
                }
                bool starter = WaitResult(starting, "启动内置 SDK 运行时", 60);
                double startupMs = startup.Elapsed.TotalMilliseconds;
                Check(starter, "EnsureRuntimeAsync 返回 false，SDK 运行时未连接");
                Check(panel.RuntimeStarted, "运行时启动后 RuntimeStarted 仍为 false");
                PumpUntil(delegate { return !(panel.StatusText ?? "").Contains("正在启动"); }, "启动完成后状态文本没有离开「正在启动」", 30);
                Check(sawStarting || observed.Count == 0, "首次打开面板没有出现「正在启动内置运行时…」进度状态（观察到：" + String.Join(" / ", observed.Distinct().Take(5)) + "）");
                Check((panel.StatusText ?? "").Contains("待命") || (panel.StatusText ?? "").Contains("就绪"),
                    "启动完成后状态文本不是待命/就绪：" + panel.StatusText);
                Check(startupMs < 30000, "首次启动耗时过长：" + startupMs.ToString("0") + " ms");
                int runtimePid = panel.RuntimeProcessId;
                Check(runtimePid > 0, "SdkAgentController.ProcessId 没有暴露（应 > 0）");
                Check(ProcessExists(runtimePid), "运行时进程不存在：" + runtimePid);
                CheckNoWebView2InPanel(window);
                CheckNoWebView2InPanel(panel);
                CaptureWindow(window, "agent-native-empty.png");
                passes.Add("PASS 原生面板首次打开显示「正在启动内置运行时…」→「待命」，耗时 " + startupMs.ToString("0") + " ms（node PID " + runtimePid + "）");
                passes.Add("PASS 原生面板运行期间无 WebView2 / HarnessPanel（无浏览器子进程）");

                // 2. Reject path: read_file -> write_file -> approval card -> reject.
                string rejectPrompt = "先读取阅读计划.md，再把标题改为本周阅读计划。";
                var rejectTask = panel.SubmitAsync(rejectPrompt);
                PumpUntil(delegate { return VisualText(panel).Contains("读取文件") || VisualText(panel).Contains("read_file") || SessionText(panel.CurrentSession).Contains("read_file"); },
                    "发送 prompt 后没有出现工具步骤（read_file）", 90);
                PumpUntil(delegate { return ApprovalCardVisible(panel); }, "写文件前没有出现审批卡（拒绝/应用修改）", 90);
                string diff = ApprovalDiffText(panel);
                Check(diff != null, "审批卡没有可读的 +/- diff 文本");
                Check(diff.Contains("+") && diff.Contains("-"), "审批 diff 没有包含 +/- 行：" + diff);
                Check(diff.Contains("阅读计划"), "审批 diff 没有显示目标文件内容：" + diff);
                Check(File.ReadAllText(file).Contains(OriginalTitle), "审批前文件已被写入（应先显示审批卡）");
                Console.WriteLine("SDK_QA approval diff:\n" + diff);
                Pump();
                Thread.Sleep(120);
                CaptureWindow(window, "agent-native-approval.png");
                Click(FindApprovalButton(panel, "拒绝"), "拒绝");
                WaitDone(rejectTask, "拒绝后的整轮对话", 120);
                Check(File.ReadAllText(file).Contains(OriginalTitle), "拒绝审批后文件仍被修改");
                string rejectText = VisualText(panel) + SessionText(panel.CurrentSession);
                Check(rejectText.Contains("未批准") || rejectText.Contains("未修改") || rejectText.Contains("rejected"),
                    "拒绝后界面没有显示未修改的结论：" + Trim(rejectText, 400));
                passes.Add("PASS 发送 prompt 后出现工具步骤（read_file）");
                passes.Add("PASS 写文件先出现审批卡，diff 含 +/- 行（" + diff.Split('\n')[0].Trim() + "）");
                passes.Add("PASS 审批拒绝时不落盘（文件内容保持不变）");

                // 3. Four themes on the settled rejected conversation.
                foreach (string theme in new[] { "mint", "warm", "lilac", "dark" })
                {
                    window.SetTheme(theme);
                    Pump();
                    Thread.Sleep(120);
                    Pump();
                    string name = theme == "mint" ? "agent-native-light.png" : "agent-native-" + theme + ".png";
                    CaptureWindow(window, name);
                }
                window.SetTheme("mint");
                Pump();

                // 4. New task in the same persistent process: approval path + editor buffer sync.
                var documentBefore = window.Current;
                var bufferBefore = window.buffers[documentBefore];
                int caretBefore = window.ActiveEditor.CaretOffset;
                int documentsBefore = window.Session.Documents.Count;
                string sessionBefore = panel.CurrentSession.Id;
                var newTask = Stopwatch.StartNew();
                WaitDone(panel.NewConversationAsync(), "新任务创建内置会话", 30);
                PumpUntil(delegate { return !panel.IsBusy && panel.CurrentSession.Id != sessionBefore; }, "新任务没有在常驻进程内建立新会话", 30);
                double newTaskMs = newTask.Elapsed.TotalMilliseconds;
                double panelReportedNewTask = ReadDouble(panel, "LastNewTaskMilliseconds");
                Check(newTaskMs < 1000, "新任务耗时超过 1s（可能重启了运行时）：" + newTaskMs.ToString("0") + " ms");
                Check(panel.RuntimeProcessId == runtimePid, "新任务导致运行时重启（PID 变化：" + runtimePid + " → " + panel.RuntimeProcessId + "）");
                Check(ProcessExists(runtimePid), "新任务后常驻运行时进程消失");

                string approvePrompt = "直接修改：把标题改为本周阅读计划。";
                var approveTask = panel.SubmitAsync(approvePrompt);
                PumpUntil(delegate { return ApprovalCardVisible(panel); }, "新任务的写文件审批卡没有出现", 90);
                Check(File.ReadAllText(file).Contains(OriginalTitle), "新任务审批前文件已被写入");
                Click(FindApprovalButton(panel, "应用修改"), "应用修改");
                WaitDone(approveTask, "批准后的整轮对话", 120);
                Check(File.ReadAllText(file).Contains(UpdatedTitle), "批准的写入没有落盘");
                Check(window.Current == documentBefore, "批准写入替换了 Document 实例（发生了整窗重载）");
                Check(window.Session.Documents.Count == documentsBefore, "批准写入改变了打开的文档数量");
                Check(ReferenceEquals(window.buffers[documentBefore], bufferBefore), "批准写入重建了编辑器缓冲区（应原地替换）");
                Check(bufferBefore.Text.Contains(UpdatedTitle), "打开的编辑器缓冲区没有被原地更新");
                Check(!documentBefore.IsDirty, "原地更新后文档仍被标记为脏");
                Check(window.ActiveEditor.CaretOffset == Math.Min(caretBefore, bufferBefore.TextLength), "原地更新后光标位置跳跃了");
                Check(documentBefore.Path != null && String.Equals(Path.GetFullPath(documentBefore.Path), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase), "原地更新后文档路径变化");
                Pump();
                Thread.Sleep(150);
                Pump();
                CaptureWindow(window, "agent-native-write.png");
                string afterWrite = bufferBefore.Text;
                Check(window.ActiveEditor.Document.UndoStack.CanUndo, "原地更新不可撤销（没有 Undo 步骤）");
                window.ActiveEditor.Document.UndoStack.Undo();
                Pump();
                Check(window.buffers[documentBefore].Text.Contains(OriginalTitle) && !window.buffers[documentBefore].Text.Contains(UpdatedTitle),
                    "撤销没有恢复写入前的缓冲区内容");
                window.ActiveEditor.Document.UndoStack.Redo();
                Pump();
                Check(window.buffers[documentBefore].Text == afterWrite, "重做没有恢复批准写入后的缓冲区内容");
                documentBefore.AcceptChanges();
                window.UpdateStatus();
                Pump();
                string settledText = VisualText(panel);
                Check(settledText.Contains("已完成"), "批准写入后助手的最终回复没有渲染：" + Trim(settledText, 400));
                Check(settledText.Contains("本次改动"), "批准写入后没有出现「本次改动」记录");
                passes.Add("PASS 批准的写入落盘，打开的编辑器缓冲区原地更新（Document 实例不变、光标保持、一次撤销可恢复）");
                passes.Add("PASS 批准后助手回复与「本次改动」记录正常渲染");
                passes.Add("PASS 新任务在同一常驻进程内完成：耗时 " + newTaskMs.ToString("0") + " ms，node PID 保持 " + runtimePid);

                // 5. Scope boundary: a write outside the authorized file must be refused
                //    before any approval card is shown and must never reach disk.
                string outside = Path.GetFullPath(Path.Combine(workspace, "..", "越界-验收.md"));
                if (File.Exists(outside)) File.Delete(outside);
                WaitDone(panel.NewConversationAsync(), "越界测试前新任务", 30);
                PumpUntil(delegate { return !panel.IsBusy; }, "越界测试前新任务未就绪", 30);
                // Hot send: the runtime is already warm, so the prompt must be picked
                // up without a cold start. Failure threshold is generous (1s).
                string statusBefore = panel.StatusText;
                var hot = Stopwatch.StartNew();
                var scopeTask = panel.SubmitAsync("越界测试：把内容写到工作区之外的文件。");
                PumpUntil(delegate { return panel.StatusText != statusBefore || scopeTask.IsCompleted; }, "热发送没有产生任何运行时状态变化", 15);
                double hotSendMs = hot.Elapsed.TotalMilliseconds;
                Check(hotSendMs < 1000, "热发送耗时超过 1s：" + hotSendMs.ToString("0") + " ms（运行时可能被重启）");
                var hotTurn = Stopwatch.StartNew();
                WaitDone(scopeTask, "越界写入请求", 120);
                PumpUntil(delegate { return !panel.IsBusy; }, "越界测试一轮没有结束", 30);
                double hotTurnMs = hotTurn.Elapsed.TotalMilliseconds;
                Check(!File.Exists(outside), "工作区之外的路径被写入了：" + outside);
                Check(!ApprovalCardVisible(panel), "越界写入不应出现审批卡（应在 bridge 直接拒绝）");
                string scopeText = VisualText(panel) + SessionText(panel.CurrentSession);
                Check(scopeText.Contains("只能操作当前文件") || scopeText.Contains("工作区") || scopeText.Contains("error"),
                    "越界写入没有给出明确拒绝信息：" + Trim(scopeText, 400));
                Check(panel.RuntimeProcessId == runtimePid, "越界测试后运行时 PID 变化");
                passes.Add("PASS 工作区外路径写入被拒绝且未落盘（无审批卡）");
                passes.Add("PASS 热发送被常驻运行时立即受理：受理 " + hotSendMs.ToString("0") + " ms，整轮 " + hotTurnMs.ToString("0") + " ms");

                // 6. History: sessions persisted to the temporary history directory,
                //    panel lists them, restoring one resumes the DSH session.
                WaitDone(panel.NewConversationAsync(), "保存历史前新任务", 30);
                PumpUntil(delegate { return !panel.IsBusy; }, "保存历史前新任务未就绪", 30);
                var store = new AgentSessionStore(historyPath);
                var sessions = store.List();
                Check(sessions.Count >= 2, "历史目录没有出现会话记录：" + historyPath);
                var openButton = FindButton(panel, "历史会话");
                Check(openButton != null, "面板缺少「历史会话」按钮");
                Click(openButton, "历史会话");
                PumpUntil(delegate { return HistoryVisible(panel); }, "历史面板没有展开", 15);
                string historyText = VisualText(panel);
                foreach (var info in sessions) Check(historyText.Contains(info.Title ?? ""), "历史面板没有列出会话：" + info.Title);
                Pump();
                Thread.Sleep(150);
                Pump();
                CaptureWindow(window, "agent-native-history.png");
                passes.Add("PASS 历史面板出现已保存的会话（" + sessions.Count + " 条，来自临时历史目录）");

                var target = sessions.FirstOrDefault(info => (info.Title ?? "").Contains("先读取")) ?? sessions[0];
                var openRows = Descendants<Button>(panel).Where(delegate (Button button)
                {
                    var content = button.Content as StackPanel;
                    return content != null && content.Children.Count > 0 && content.Children[0] is TextBlock;
                }).ToList();
                Button targetButton = null;
                foreach (var row in openRows)
                {
                    var content = (StackPanel)row.Content;
                    if (String.Equals(((TextBlock)content.Children[0]).Text, target.Title, StringComparison.Ordinal)) { targetButton = row; break; }
                }
                Check(targetButton != null, "历史面板没有可打开的行：" + target.Title);
                var restore = Stopwatch.StartNew();
                Click(targetButton, "打开历史会话 " + target.Title);
                PumpUntil(delegate { return panel.CurrentSession.Id == target.Id; }, "历史会话没有渲染为当前会话", 30);
                var resume = ReadValue(panel, "resumeTask") as Task;
                if (resume != null) WaitDone(resume, "恢复内置运行时会话", 60);
                double restoreMs = restore.Elapsed.TotalMilliseconds;
                Check((panel.CurrentSession.HarnessSessionId ?? "").Length > 0, "恢复的会话缺少 HarnessSessionId，无法 session/resume");
                WaitDone(panel.SubmitAsync("继续，把刚才的结果说明一下。"), "历史续聊一轮", 120);
                string continued = VisualText(panel);
                Check(continued.Contains("已恢复历史会话"), "历史续聊没有得到模型回复：" + Trim(continued, 400));
                passes.Add("PASS 历史恢复（session/resume）后继续对话成功：耗时 " + restoreMs.ToString("0") + " ms");

                // 7. Narrow windows.
                window.Width = 820;
                window.Height = 740;
                Pump();
                Thread.Sleep(150);
                Pump();
                Check(panel.IsOpen, "820 宽窗口下面板不可见");
                CaptureWindow(window, "agent-native-820.png");
                window.Width = 390;
                Pump();
                Thread.Sleep(150);
                Pump();
                Check(panel.IsOpen, "390 宽窗口下面板不可见");
                Check(panel.ActualWidth > 0, "390 宽窗口下 Agent 面板宽度为 0");
                CaptureWindow(window, "agent-native-390.png");
                window.Width = 1280;
                window.Height = 860;
                Pump();
                passes.Add("PASS 窄窗口 820 / 390 DIP 下原生面板仍可用并完成截图");

                // 8. Shutdown: the persistent runtime and every owned child must be gone.
                int pid = panel.RuntimeProcessId;
                var close = Stopwatch.StartNew();
                foreach (var document in window.Session.Documents) { document.Modified = false; document.MetadataModified = false; }
                WaitDone(panel.ShutdownAsync(), "关闭面板运行时", 30);
                var shutdown = Stopwatch.StartNew();
                window.Close();
                Pump();
                double shutdownMs = shutdown.Elapsed.TotalMilliseconds;
                PumpUntil(delegate { return !ProcessExists(pid); }, "退出后 node 子进程仍在运行（PID " + pid + "）", 15);
                double totalShutdown = close.Elapsed.TotalMilliseconds;
                var owned = OwnedNodePids();
                if (owned != null) Check(owned.Count == 0, "退出后仍有本进程拥有的 node 子进程：" + String.Join(",", owned));
                Check(!HasOwnedWebView2(), "退出前后出现了本进程拥有的 WebView2 子进程");
                Check(!Directory.Exists(applicationData), "验收污染了安装目录 app/agent-data：" + applicationData);
                Check(ArchiveMetadata(existingAppData) == appDataBefore, "验收改动了已有安装存档");
                Check(ArchiveMetadata(existingBuildData) == buildDataBefore, "验收污染了构建目录的既有存档");
                Check(!Directory.Exists(runtimeData), "退出后临时运行目录没有释放");
                passes.Add("PASS 退出后本进程 node 子进程结束（PID " + pid + " 已退出，关闭耗时 " + shutdownMs.ToString("0") + " ms），无 WebView2 进程");

                metrics.Add("首次启动运行时：" + startupMs.ToString("0") + " ms（initialize + 面板就绪，node PID " + runtimePid + "）");
                metrics.Add("冷启动进度状态序列：" + String.Join(" → ", observed.Where(delegate (string value) { return !String.IsNullOrEmpty(value); }).Distinct().Take(6)));
                metrics.Add("新任务（同一进程，创建→可发送）：" + newTaskMs.ToString("0") + " ms" + (panelReportedNewTask > 0 ? "（面板自报 " + panelReportedNewTask.ToString("0") + " ms）" : ""));
                metrics.Add("历史恢复（打开会话→可发送）：" + restoreMs.ToString("0") + " ms");
                metrics.Add("关闭运行时（ShutdownAsync → 进程退出）：" + shutdownMs.ToString("0") + " ms；窗口关闭总耗时 " + totalShutdown.ToString("0") + " ms");
                metrics.Add("热发送（提交→首个运行时状态）：" + hotSendMs.ToString("0") + " ms；整轮 " + hotTurnMs.ToString("0") + " ms（常驻进程，PID " + runtimePid + " 不变）");
                metrics.Add("运行时数据目录：" + (String.IsNullOrEmpty(suppliedData) ? "（组件默认，未污染 app/agent-data）" : runtimeData));
                metrics.Add("验收总耗时：" + total.Elapsed.TotalMilliseconds.ToString("0") + " ms");
                metrics.Add("本机合成模型请求数：" + mock.Requests);

                // 9. Production ordering: the panel is revealed (and the warm runtime
                //    starts) before any file is opened, then a file arrives and the
                //    first send must retarget the SDK session to the editor scope.
                //    This is the late-scope-bind path sdk-core heals; keep it covered.
                MainWindow lateWindow = null;
                AgentPanel latePanel = null;
                try
                {
                    var lateScenario = Stopwatch.StartNew();
                    lateWindow = new MainWindow { Width = 1180, Height = 820, Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
                    latePanel = lateWindow.TestHooks.Agent;
                    latePanel.UseTestEnvironment(Path.Combine(folder, "agent-history-late"), config);
                    lateWindow.Show();
                    Pump();
                    lateWindow.ToggleAgent();
                    Pump();
                    Check(!latePanel.RuntimeStarted, "第二次冷启动前运行时已在运行");
                    var lateStart = Stopwatch.StartNew();
                    bool lateStarted = WaitResult(latePanel.EnsureRuntimeAsync(), "生产顺序：先启动常驻运行时", 60);
                    double lateStartMs = lateStart.Elapsed.TotalMilliseconds;
                    Check(lateStarted && latePanel.RuntimeStarted, "无 scope 时启动运行时失败");
                    int latePid = latePanel.RuntimeProcessId;
                    Check(latePid > 0 && latePid != runtimePid, "第二次启动没有产生新的 node 进程");
                    Await(lateWindow.OpenPathsAsync(new[] { file }, false));
                    Check(lateWindow.Current != null && lateWindow.Current.Path != null, "后绑定场景的 Markdown 没有打开");
                    var lateSend = latePanel.SubmitAsync("后绑定测试：读取当前文件并确认内容。");
                    PumpUntil(delegate { return VisualText(latePanel).Contains("读取文件") || SessionText(latePanel.CurrentSession).Contains("read_file"); },
                        "先启动后绑定 scope 的会话没有出现工具步骤（working-directory/set 自愈失败）", 90);
                    WaitDone(lateSend, "后绑定场景整轮对话", 120);
                    string lateText = VisualText(latePanel) + SessionText(latePanel.CurrentSession);
                    Check(lateText.Contains("后绑定测试完成"), "后绑定场景没有得到助手回复：" + Trim(lateText, 400));
                    Check(latePanel.RuntimeProcessId == latePid, "后绑定场景重启了运行时（PID 变化）");
                    passes.Add("PASS 生产顺序（先启动运行时→后打开文件→发送）：session/working-directory/set 自愈生效，工具步骤正常（冷启动 " + lateStartMs.ToString("0") + " ms）");
                    metrics.Add("生产顺序场景（无文件启动运行时 " + lateStartMs.ToString("0") + " ms → 打开文件 → 首次发送）：工具步骤与助手回复正常，node PID " + latePid);
                    WaitDone(latePanel.ShutdownAsync(), "关闭后绑定场景运行时", 30);
                    var lateShutdown = Stopwatch.StartNew();
                    lateWindow.Close();
                    Pump();
                    PumpUntil(delegate { return !ProcessExists(latePid); }, "后绑定场景退出后 node 子进程仍在运行（PID " + latePid + "）", 15);
                    Check(!HasOwnedWebView2(), "后绑定场景出现了 WebView2 子进程");
                    passes.Add("PASS 后绑定场景退出后 node 子进程结束（PID " + latePid + " 已退出，耗时 " + lateShutdown.Elapsed.TotalMilliseconds.ToString("0") + " ms）");
                    Console.WriteLine("SDK_QA production-order scenario passed in " + lateScenario.Elapsed.TotalMilliseconds.ToString("0") + " ms");
                }
                finally
                {
                    if (lateWindow != null)
                    {
                        try
                        {
                            foreach (var document in lateWindow.Session.Documents) { document.Modified = false; document.MetadataModified = false; }
                            if (latePanel != null && latePanel.IsBusy) { try { latePanel.Stop(); } catch { } }
                            try { lateWindow.Close(); } catch { }
                            Pump();
                        }
                        catch { }
                    }
                }
                completed = true;
            }
            catch (NotImplementedException error)
            {
                WriteDiagnostic(folder, metrics, passes, mock, panel, window, "SDK 控制器尚未实现（等待 core 队友落地）：" + error);
                throw new Exception("SDK 控制器尚未实现（等待 core 队友落地）：" + error.Message, error);
            }
            catch (Exception error)
            {
                WriteDiagnostic(folder, metrics, passes, mock, panel, window, error.ToString());
                throw;
            }
            finally
            {
                try
                {
                    if (window != null)
                    {
                        foreach (var document in window.Session.Documents) { document.Modified = false; document.MetadataModified = false; }
                        try { if (panel != null && panel.IsOpen) WaitDone(panel.ShutdownAsync(), "兜底关闭运行时", 15); } catch (Exception closeError) { Console.WriteLine("SDK_QA shutdown warning: " + closeError.Message); }
                        try { window.Close(); } catch { }
                        Pump();
                    }
                }
                catch { }
                mock.Dispose();
                try
                {
                    if (metrics.Count > 0)
                    {
                        metrics.Add("结果：" + (completed ? "PASS" : "FAIL（见 sdk-qa-diagnostic.txt）"));
                        File.WriteAllLines(Path.Combine(project, "artifacts", "metrics", "sdk-qa-timing.txt"), metrics, new UTF8Encoding(false));
                    }
                    if (passes.Count > 0) File.WriteAllLines(Path.Combine(project, "artifacts", "metrics", "sdk-qa-pass.txt"), passes, new UTF8Encoding(false));
                }
                catch (Exception writeError) { Console.WriteLine("SDK_QA metrics warning: " + writeError.Message); }
                try { application.Shutdown(); } catch { }
                Console.WriteLine("SDK_QA done in " + total.Elapsed.TotalMilliseconds.ToString("0") + " ms");
            }
            foreach (string line in passes) Console.WriteLine(line);
        }

        static string Trim(string text, int length)
        {
            if (text == null) return "";
            text = text.Replace("\r", "").Replace("\n", " | ");
            return text.Length <= length ? text : text.Substring(0, length) + "…";
        }

        static void WriteDiagnostic(string folder, List<string> metrics, List<string> passes, HarnessMockServer mock, AgentPanel panel, MainWindow window, string detail)
        {
            var lines = new List<string>();
            lines.Add("轻页 0.10 原生 SDK 端到端验收：失败诊断");
            lines.Add("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            lines.Add("错误：" + detail);
            lines.Add("");
            lines.Add("已通过项：");
            if (passes.Count == 0) lines.Add("（无）"); else lines.AddRange(passes);
            lines.Add("");
            lines.Add("已记录耗时：");
            if (metrics.Count == 0) lines.Add("（无）"); else lines.AddRange(metrics);
            lines.Add("");
            try
            {
                if (panel != null)
                {
                    lines.Add("面板状态文本：" + (panel.StatusText ?? ""));
                    lines.Add("面板忙碌：" + panel.IsBusy + "；打开：" + panel.IsOpen + "；会话：" + (panel.CurrentSession == null ? "null" : panel.CurrentSession.Id));
                    lines.Add("面板自报耗时：启动=" + ReadDouble(panel, "LastStartupMilliseconds") + " 面板=" + ReadDouble(panel, "LastPanelMilliseconds") + " 新任务=" + ReadDouble(panel, "LastNewTaskMilliseconds"));
                    lines.Add("TimingText：" + (ReadString(panel, "TimingText") ?? ""));
                    lines.Add("会话条目：");
                    lines.Add(SessionText(panel.CurrentSession));
                    lines.Add("面板可视文本：");
                    lines.Add(VisualText(panel));
                }
                if (window != null)
                {
                    lines.Add("打开文档数：" + window.Session.Documents.Count);
                    lines.Add("当前文档：" + (window.Current == null ? "null" : window.Current.Path + " dirty=" + window.Current.IsDirty));
                }
            }
            catch (Exception error) { lines.Add("诊断收集失败：" + error.Message); }
            lines.Add("");
            lines.Add("本进程子进程：");
            var children = OurChildProcesses();
            if (children == null) lines.Add("（无法枚举：PowerShell/CIM 不可用）");
            else if (children.Count == 0) lines.Add("（无）");
            else foreach (var child in children) lines.Add("  " + child.ProcessId + " " + child.Name + " " + child.CommandLine);
            lines.Add("");
            lines.Add("本机合成模型请求数：" + mock.Requests + "；最后一次计划：" + mock.LastPlan);
            lines.Add("最后一次模型请求体：");
            lines.Add(Trim(mock.LastRequest, 20000));
            try { File.WriteAllLines(Path.Combine(project, "artifacts", "metrics", "sdk-qa-diagnostic.txt"), lines, new UTF8Encoding(false)); }
            catch { }
        }
    }
}
