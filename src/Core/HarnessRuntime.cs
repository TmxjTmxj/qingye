using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Qingye.Core
{
    // One private Harness process per editor window; control stays on anonymous pipes.
    public sealed class HarnessRuntime
    {
        readonly object inputLock = new object();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        readonly Dictionary<string, CancellationTokenSource> requests = new Dictionary<string, CancellationTokenSource>();
        Process process;
        ProcessLifetime lifetime;
        TaskCompletionSource<Uri> ready;
        string key;
        bool stopping;
        public string LastDiagnostic { get; private set; }
        public Func<Dictionary<string,object>,CancellationToken,Task<object>> NativeRequest;
        public event Action<string> Failed;
        public bool IsRunning { get { try { return process != null && !process.HasExited; } catch { return false; } } }

        public static string Locate(string baseDirectory)
        {
            string bundled = Path.Combine(baseDirectory, "Harness");
            if (File.Exists(Path.Combine(bundled, "launcher.mjs"))) return bundled;
            string project = Path.GetFullPath(Path.Combine(baseDirectory, "..", ".."));
            return Path.Combine(project, "build", "harness-runtime");
        }

        public async Task<Uri> StartAsync(string runtimeDirectory, string home, ModelConfig model)
        {
            if (IsRunning) return await ready.Task;
            string executable = Path.Combine(runtimeDirectory, "node.exe");
            string launcher = Path.Combine(runtimeDirectory, "launcher.mjs");
            if (!File.Exists(executable) || !File.Exists(launcher)) throw new IOException("内置 Harness 运行时不完整。请使用包含 Harness 目录的完整便携包。");
            if (!model.IsValid) throw new AgentException("请先配置模型地址、模型名和密钥。");
            stopping = false;
            key = model.ApiKey;
            ready = new TaskCompletionSource<Uri>();
            var info = new ProcessStartInfo(executable, "\"" + launcher + "\"") {
                WorkingDirectory = runtimeDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            string temporary = Path.Combine(home, "temp");
            Directory.CreateDirectory(temporary);
            info.EnvironmentVariables["TEMP"] = temporary;
            info.EnvironmentVariables["TMP"] = temporary;
            info.EnvironmentVariables["TMPDIR"] = temporary;
            // Set before Node starts so inherited debugger/preload settings cannot execute.
            info.EnvironmentVariables.Remove("NODE_OPTIONS");
            info.EnvironmentVariables.Remove("NODE_PATH");
            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var child = process;
            var childReady = ready;
            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) {
                if (!String.IsNullOrWhiteSpace(args.Data)) LastDiagnostic = ModelConfig.Redact(args.Data, key);
            };
            process.Exited += delegate {
                string message = LastDiagnostic ?? "Harness 进程已停止，可点击重启。";
                childReady.TrySetException(new AgentException(message));
                if (Object.ReferenceEquals(process, child) && !stopping && Failed != null) Failed(message);
            };
            process.Start();
            lifetime = new ProcessLifetime(process);
            process.StandardInput.AutoFlush = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            string endpoint = model.Endpoint;
            string baseUrl = endpoint.Substring(0, endpoint.Length - "/chat/completions".Length);
            Send(new Dictionary<string,object> {
                { "kind", "initialize" }, { "home", Path.GetFullPath(home) },
                { "model", new Dictionary<string,string> { { "name", model.ModelName }, { "baseURL", baseUrl }, { "key", key } } }
            });
            if (await Task.WhenAny(ready.Task, Task.Delay(45000)) != ready.Task) {
                await StopAsync();
                throw new AgentException("Harness 启动超时。" + (LastDiagnostic ?? "请点击重试。"));
            }
            return await ready.Task;
        }

        void OnOutput(object sender, DataReceivedEventArgs args)
        {
            if (!Object.ReferenceEquals(sender, process)) return;
            string line = args.Data;
            if (line == null || !line.StartsWith("QINGYE ", StringComparison.Ordinal) || line.Length > 4 * 1024 * 1024) return;
            try {
                var message = json.Deserialize<Dictionary<string,object>>(line.Substring(7));
                string kind = Text(message, "kind");
                if (kind == "ready") {
                    Uri address;
                    if (Uri.TryCreate(Text(message, "url"), UriKind.Absolute, out address) && address.Scheme == "http" && address.Host == "127.0.0.1" && address.Port > 0) ready.TrySetResult(address);
                } else if (kind == "native") {
                    HandleNativeAsync(message);
                } else if (kind == "cancel") {
                    lock (requests) {
                        CancellationTokenSource cancellation;
                        if (requests.TryGetValue(Text(message, "id"), out cancellation)) cancellation.Cancel();
                    }
                } else if (kind == "error") {
                    LastDiagnostic = ModelConfig.Redact(Text(message, "message"), key);
                    ready.TrySetException(new AgentException(LastDiagnostic));
                } else if (kind == "diagnostic") {
                    LastDiagnostic = ModelConfig.Redact(Text(message, "message"), key);
                }
            } catch (Exception error) {
                LastDiagnostic = ModelConfig.Redact(error.Message, key);
            }
        }

        async void HandleNativeAsync(Dictionary<string,object> message)
        {
            string id = Text(message, "id");
            var cancellation = new CancellationTokenSource();
            lock (requests) requests[id] = cancellation;
            var response = new Dictionary<string,object> { { "kind", "result" }, { "id", id } };
            try {
                if (NativeRequest == null) throw new AgentException("原生文件工具未连接。");
                object value = await NativeRequest(message, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                response["value"] = ModelConfig.ScrubValues(value, key);
            } catch (Exception error) {
                response["error"] = ModelConfig.Redact(error.Message, key);
            } finally {
                lock (requests) requests.Remove(id);
                cancellation.Dispose();
            }
            try { if (!stopping) Send(response); } catch (IOException) { }
        }

        public static string Text(Dictionary<string,object> message, string name)
        {
            object value;
            return message.TryGetValue(name, out value) ? Convert.ToString(value) : "";
        }

        public void UpdateContext(string root, string file)
        {
            if (!IsRunning || stopping) return;
            try { Send(new Dictionary<string,object> {
                { "kind", "context-changed" }, { "context", new Dictionary<string,string> { { "root", root }, { "file", file } } }
            }); } catch (IOException) { }
        }

        void Send(Dictionary<string,object> message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Serialize(message) + "\n");
            lock (inputLock) {
                process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                process.StandardInput.BaseStream.Flush();
            }
        }

        public async Task StopAsync()
        {
            stopping = true;
            lock (requests) foreach (var cancellation in requests.Values) cancellation.Cancel();
            var child = process;
            var childLifetime = lifetime;
            if (child == null) return;
            try {
                if (!child.HasExited) {
                    try { Send(new Dictionary<string,object> { { "kind", "shutdown" } }); } catch (IOException) { }
                    bool exited = await Task.Run(() => child.WaitForExit(6500));
                    if (!exited) child.Kill();
                }
            } finally {
                if (childLifetime != null) childLifetime.Dispose();
                child.Dispose();
                if (Object.ReferenceEquals(process, child)) { process = null; lifetime = null; }
            }
        }
    }
}
