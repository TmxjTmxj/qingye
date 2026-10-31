using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Qingye.Core;
using Qingye.UI;

namespace Qingye.Tests
{
    public static partial class ExperienceHarness
    {
        static void HarnessMemoryQa()
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            string folder = Path.Combine(project, "build", "agent-memory-qa-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "阅读计划.md");
            string second = Path.Combine(folder, "另一份.md");
            File.WriteAllText(file, OriginalTitle + "\n\n" + OriginalBody, new UTF8Encoding(false));
            File.WriteAllText(second, "# 另一份文件\n", new UTF8Encoding(false));
            string home = Path.Combine(folder, "runtime", Guid.NewGuid().ToString("N"));
            string history = Path.Combine(folder, "agent-history");
            var mock = new HarnessMockServer();
            var window = new MainWindow { Width = 1280, Height = 820, Left = -16000, Top = 0, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual };
            var panel = window.TestHooks.Agent;
            panel.UseMemoryTestEnvironment(history, home, () => new ModelConfig {
                BaseUrl = mock.BaseUrl, ApiKey = "qa-local-only", ModelName = "本地验收模型"
            });
            var measurements = new List<string>();
            int pid = 0;
            try
            {
                window.Show();
                Pump();
                Check(!panel.RuntimeStarted, "原生阅读提前启动了 Node");
                Await(window.OpenPathsAsync(new[] { file }, false));
                window.ToggleAgent();
                var watch = Stopwatch.StartNew();
                Check(WaitResult(panel.EnsureRuntimeAsync(), "启动临时 SDK", 30), "临时 SDK 启动失败");
                measurements.Add("sdk_cold_ready_ms: " + watch.Elapsed.TotalMilliseconds.ToString("F1"));
                pid = panel.RuntimeProcessId;
                Check(pid > 0, "SDK PID 缺失");
                using (var process = Process.GetProcessById(pid)) measurements.Add("sdk_idle_working_set_bytes: " + process.WorkingSet64);
                CheckNoWebView2InPanel(window);

                var stopped = panel.SubmitAsync("先读取阅读计划.md，再把标题改为本周阅读计划。");
                PumpUntil(() => ApprovalCardVisible(panel), "临时 SDK 审批", 60);
                watch.Restart();
                panel.Stop();
                WaitDone(stopped, "停止审批中的当前轮", 20);
                measurements.Add("stop_pending_approval_ms: " + watch.Elapsed.TotalMilliseconds.ToString("F1"));
                Check(File.ReadAllText(file).Contains(OriginalTitle), "停止审批仍写入文件");
                Check(panel.RuntimeProcessId == pid && panel.RuntimeStarted, "停止任务重启了 SDK");
                Console.WriteLine("PASS Stop cancels pending approval and retains the SDK PID");

                watch.Restart();
                var writing = panel.SubmitAsync("直接修改：把标题改为本周阅读计划。");
                PumpUntil(() => ApprovalCardVisible(panel) || writing.IsCompleted, "热发送后的审批", 60);
                Check(ApprovalCardVisible(panel), "热发送提前结束：" + panel.StatusText + " / " + Trim(SessionText(panel.CurrentSession), 1000));
                measurements.Add("warm_prompt_to_approval_ms: " + watch.Elapsed.TotalMilliseconds.ToString("F1"));
                Click(FindApprovalButton(panel, "应用修改"), "应用修改");
                WaitDone(writing, "临时 SDK 批准写入", 60);
                Check(File.ReadAllText(file).Contains(UpdatedTitle), "批准写入没有落盘");
                PumpUntil(() => Descendants<FlowDocumentScrollViewer>(window).Any(reader =>
                    new TextRange(reader.Document.ContentStart, reader.Document.ContentEnd).Text.Contains("本周阅读计划")), "阅读内容同步", 10);
                CaptureWindow(window, "agent-optimized-desktop.png");
                Check(!Directory.Exists(history), "生产临时会话生成了原生历史目录");
                Check(!Directory.Exists(Path.Combine(home, "qingye-scopes")), "生产临时会话记录了文件范围");
                Check(Directory.GetFiles(home, "*.jsonl", SearchOption.AllDirectories).Length == 0, "生产临时会话生成了 JSONL");
                Console.WriteLine("PASS Native and SDK conversation state stays off disk");

                string erased = panel.CurrentSession.HarnessSessionId;
                panel.DeleteConversation(panel.CurrentSession.Id, false);
                WaitDone(panel.SessionMaintenance, "删除 SDK 内存会话", 15);
                bool refused = false;
                try { WaitDone(SdkRuntime.Shared.RequestAsync("session/resume", new Dictionary<string, object> { { "sessionId", erased } }, CancellationToken.None), "检查已删除会话", 10); }
                catch (AgentException) { refused = true; }
                Check(refused && panel.CurrentSession.Entries.Count == 0, "删除后旧 SDK 上下文仍可恢复");
                Check(panel.RuntimeProcessId == pid, "清除会话重启了 SDK");
                Console.WriteLine("PASS Delete removes the SDK context without restarting the process");

                watch.Restart();
                WaitDone(panel.NewConversationAsync(), "新任务本地重置", 5);
                measurements.Add("new_task_ms: " + watch.Elapsed.TotalMilliseconds.ToString("F1"));
                Check(panel.RuntimeProcessId == pid, "新任务重启了 SDK");
                var switching = panel.SubmitAsync("直接修改：把标题改为本周阅读计划。");
                PumpUntil(() => ApprovalCardVisible(panel), "切换范围前审批", 60);
                panel.FilePermission = HarnessFilePermission.Automatic;
                Await(window.OpenPathsAsync(new[] { second }, false));
                WaitDone(switching, "结束旧范围任务", 20);
                WaitDone(panel.SessionMaintenance, "清空旧范围上下文", 15);
                Check(panel.CurrentSession.CurrentFile == second && panel.CurrentSession.Entries.Count == 0, "切换文件仍保留旧会话");
                Check(panel.FilePermission == HarnessFilePermission.Confirm, "切换文件仍保留自动权限");
                Check(File.ReadAllText(file).Contains(UpdatedTitle), "切换文件应用了旧审批");
                Console.WriteLine("PASS Switching files during approval revokes the old scope and context");

                Await(window.OpenPathsAsync(new[] { file }, false));
                var clearing = panel.SubmitAsync("直接修改：把标题改为本周阅读计划。");
                PumpUntil(() => ApprovalCardVisible(panel), "清空前审批", 60);
                panel.ClearHistory(false);
                WaitDone(clearing, "审批中清空会话", 15);
                WaitDone(panel.SessionMaintenance, "清除审批中的 SDK 上下文", 15);
                Check(panel.CurrentSession.Entries.Count == 0 && panel.RuntimeProcessId == pid, "清空活动会话没有保留干净的常驻运行时");
                Console.WriteLine("PASS Clear cancels active approval and removes its context without a restart");
                Await(window.OpenPathsAsync(new[] { second }, false));
                WaitDone(panel.SessionMaintenance, "清空后切换文件", 15);

                window.Width = 820;
                Pump();
                Check(window.TestHooks.Preview.IsVisible && panel.IsVisible, "820 DIP 未保持文档并排");
                CaptureWindow(window, "agent-optimized-820.png");
                window.Width = 390;
                Pump();
                Check(panel.ActualWidth > 200, "窄窗中 Agent 不可用");
                CaptureWindow(window, "agent-optimized-390.png");
                Console.WriteLine("PASS 820 DIP keeps the document visible and 390 DIP remains usable");

                panel.ClearHistory(false);
                WaitDone(panel.SessionMaintenance, "清空全部临时会话", 15);
                WaitDone(panel.ShutdownAsync(), "退出临时 SDK", 20);
                Check(!Directory.Exists(home), "退出没有释放运行目录");
                Check(!ProcessExists(pid), "退出没有结束拥有的 Node 进程");
                Check(!HasOwnedWebView2(), "Agent 启动了 WebView2");
                Console.WriteLine("PASS Exit releases the runtime directory and owned process");
                Directory.CreateDirectory(Path.Combine(project, "artifacts", "metrics"));
                File.WriteAllLines(Path.Combine(project, "artifacts", "metrics", "optimization-native-measured.txt"), measurements, new UTF8Encoding(false));
                foreach (string measurement in measurements) Console.WriteLine(measurement);
            }
            catch (Exception error)
            {
                WriteDiagnostic(folder, measurements, new List<string>(), mock, panel, window, error.ToString());
                CaptureWindow(window, "agent-optimized-failure.png");
                throw;
            }
            finally
            {
                foreach (var document in window.Session.Documents) { document.Modified = false; document.MetadataModified = false; }
                try { WaitDone(panel.ShutdownAsync(), "兜底关闭临时 SDK", 20); } catch { }
                window.Close();
                Pump();
                mock.Dispose();
                application.Shutdown();
            }
        }
    }
}
