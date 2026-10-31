using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Qingye.Core
{
    // Transport surface the SDK controller needs: a warm node runtime speaking
    // newline-delimited JSON-RPC over stdio, plus a loopback TCP bridge for the
    // native file tools. Tests substitute an in-memory implementation.
    internal interface ISdkRuntime : IDisposable
    {
        bool IsRunning { get; }
        int ProcessId { get; }
        string LastDiagnostic { get; }
        bool IsSessionActive(string sessionId);
        void MarkSessionActive(string sessionId);
        bool IsBridgeConnected { get; }
        bool IsInitialized { get; }
        string InitializedCwd { get; }
        event Action<string> Failed;
        event Action<string, Dictionary<string, object>> Notification;
        Task StartAsync(string runtimeDirectory, string home, ModelConfig model, CancellationToken token);
        Task InitializeAsync(ModelConfig model, string cwd, CancellationToken token);
        Task<Dictionary<string, object>> RequestAsync(string method, Dictionary<string, object> parameters, CancellationToken token);
        Task StopAsync();
        void AttachNative(object owner, Func<Dictionary<string, object>, CancellationToken, Task<object>> handler);
        void DetachNative(object owner);
    }

    internal static class SdkValues
    {
        internal static string Text(Dictionary<string, object> map, string name)
        {
            object value;
            return map != null && map.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : "";
        }

        internal static Dictionary<string, object> Map(object value)
        {
            return value as Dictionary<string, object>;
        }

        // The SDK sends content as blocks; the editor renders text blocks only.
        // JavaScriptSerializer represents JSON arrays as non-generic sequences,
        // so iterate them through IEnumerable rather than assuming object[].
        internal static string ContentText(object content)
        {
            if (content == null) return "";
            string direct = content as string;
            if (direct != null) return direct;
            Dictionary<string, object> single = Map(content);
            if (single != null) return Text(single, "text");
            System.Collections.IEnumerable blocks = content as System.Collections.IEnumerable;
            if (blocks == null) return "";
            var builder = new StringBuilder();
            foreach (object block in blocks)
            {
                Dictionary<string, object> map = Map(block);
                if (map == null) continue;
                if (Text(map, "type") != "text") continue;
                builder.Append(Text(map, "text"));
            }
            return builder.ToString();
        }

        internal static Dictionary<string, object> Message(string role, string content)
        {
            return new Dictionary<string, object> { { "role", role }, { "content", content ?? "" } };
        }
    }

    // One JSON-RPC 2.0 peer over a text stream pair. Frames are one JSON object
    // per line; responses correlate by numeric id, notifications dispatch by
    // method, and server-to-client requests get an asynchronous answer.
    internal sealed class SdkJsonRpcChannel : IDisposable
    {
        const int MaximumFrameCharacters = 8 * 1024 * 1024;
        const int DefaultTimeoutMilliseconds = 60000;
        readonly TextReader input;
        readonly TextWriter output;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = MaximumFrameCharacters };
        readonly object gate = new object();
        readonly object writeGate = new object();
        readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
        readonly Func<string, string> redact;
        Thread pump;
        int nextId;
        bool closed;

        sealed class Pending
        {
            internal TaskCompletionSource<Dictionary<string, object>> Completion;
            internal Timer Timer;
            internal CancellationTokenRegistration Registration;

            internal void Release()
            {
                if (Timer != null) { try { Timer.Dispose(); } catch { } Timer = null; }
                try { Registration.Dispose(); } catch { }
            }
        }

        internal SdkJsonRpcChannel(TextReader input, TextWriter output, Func<string, string> redact)
        {
            if (input == null) throw new ArgumentNullException("input");
            if (output == null) throw new ArgumentNullException("output");
            this.input = input;
            this.output = output;
            this.redact = redact ?? delegate(string value) { return value; };
        }

        // Raised for every server notification; method plus its params object.
        internal event Action<string, Dictionary<string, object>> Notification;
        // Raised for protocol-level diagnostics (stderr-like, already redacted).
        internal event Action<string> Diagnostic;
        // Server-to-client requests; a null handler answers -32601.
        internal Func<string, Dictionary<string, object>, CancellationToken, Task<object>> ServerRequestHandler;

        internal bool IsClosed
        {
            get { lock (gate) return closed; }
        }

        internal void Start()
        {
            if (pump != null) return;
            pump = new Thread(Pump) { IsBackground = true, Name = "SdkJsonRpc" };
            pump.Start();
        }

        void Pump()
        {
            try
            {
                while (true)
                {
                    string line = input.ReadLine();
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    if (line.Length > MaximumFrameCharacters)
                    {
                        RaiseDiagnostic("忽略超大的 JSON-RPC 帧。");
                        continue;
                    }
                    Process(line);
                }
            }
            catch (Exception error)
            {
                if (!IsClosed) RaiseDiagnostic("读取 Agent 运行时协议流出错：" + Redact(error.Message));
            }
            Close(new AgentException("Agent 运行时已断开连接。"));
        }

        void Process(string line)
        {
            Dictionary<string, object> message;
            try { message = json.Deserialize<Dictionary<string, object>>(line); }
            catch { RaiseDiagnostic("忽略无法解析的 JSON-RPC 帧。"); return; }
            if (message == null) return;
            bool hasMethod = message.ContainsKey("method");
            bool hasId = message.ContainsKey("id");
            if (hasMethod && hasId) { HandleServerRequest(message); return; }
            if (hasMethod) { HandleNotification(message); return; }
            if (hasId) Resolve(message);
        }

        void HandleNotification(Dictionary<string, object> message)
        {
            string method = SdkValues.Text(message, "method");
            if (method.Length == 0) return;
            Dictionary<string, object> parameters = SdkValues.Map(Get(message, "params")) ?? new Dictionary<string, object>();
            Action<string, Dictionary<string, object>> handler = Notification;
            if (handler == null) return;
            try { handler(method, parameters); }
            catch (Exception error) { RaiseDiagnostic("处理运行事件失败：" + Redact(error.Message)); }
        }

        void HandleServerRequest(Dictionary<string, object> message)
        {
            object id = Get(message, "id");
            string method = SdkValues.Text(message, "method");
            Dictionary<string, object> parameters = SdkValues.Map(Get(message, "params")) ?? new Dictionary<string, object>();
            Task.Run(async delegate
            {
                Dictionary<string, object> frame = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id } };
                try
                {
                    Func<string, Dictionary<string, object>, CancellationToken, Task<object>> handler = ServerRequestHandler;
                    if (handler == null) throw new AgentException("不支持的服务端请求：" + method);
                    object result = await handler(method, parameters, CancellationToken.None).ConfigureAwait(false);
                    frame["result"] = result ?? new Dictionary<string, object>();
                }
                catch (Exception error)
                {
                    frame["error"] = new Dictionary<string, object> { { "code", -32603 }, { "message", Redact(error.Message) } };
                }
                try { WriteFrame(frame); }
                catch (AgentException) { }
            });
        }

        static object Get(Dictionary<string, object> message, string name)
        {
            object value;
            return message.TryGetValue(name, out value) ? value : null;
        }

        void Resolve(Dictionary<string, object> message)
        {
            int id;
            try { id = Convert.ToInt32(Get(message, "id")); }
            catch { return; }
            Dictionary<string, object> error = SdkValues.Map(Get(message, "error"));
            if (error != null)
            {
                string text = SdkValues.Text(error, "message");
                if (text.Length == 0) text = "Agent 运行时返回错误。";
                Settle(id, null, new AgentException(Redact(text)));
                return;
            }
            Dictionary<string, object> result = SdkValues.Map(Get(message, "result"));
            Settle(id, result ?? new Dictionary<string, object>(), null);
        }

        void Settle(int id, Dictionary<string, object> result, Exception error)
        {
            Pending entry;
            lock (gate)
            {
                if (!pending.TryGetValue(id, out entry)) return;
                pending.Remove(id);
            }
            entry.Release();
            if (error != null) entry.Completion.TrySetException(error);
            else entry.Completion.TrySetResult(result ?? new Dictionary<string, object>());
        }

        internal Task<Dictionary<string, object>> RequestAsync(string method, Dictionary<string, object> parameters, CancellationToken token, int timeoutMilliseconds)
        {
            if (token.IsCancellationRequested) return Task.FromCanceled<Dictionary<string, object>>(token);
            if (timeoutMilliseconds <= 0) timeoutMilliseconds = DefaultTimeoutMilliseconds;
            var entry = new Pending();
            entry.Completion = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id;
            lock (gate)
            {
                if (closed) return Task.FromException<Dictionary<string, object>>(new AgentException("Agent 运行时已断开连接。"));
                id = ++nextId;
                pending.Add(id, entry);
            }
            // Timer and cancellation are advisory: Settle is idempotent.
            entry.Timer = new Timer(delegate { Settle(id, null, new AgentException("运行时请求超时：" + method)); }, null, timeoutMilliseconds, Timeout.Infinite);
            if (token.CanBeCanceled) entry.Registration = token.Register(delegate { Settle(id, null, new OperationCanceledException(token)); });
            var frame = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "method", method } };
            if (parameters != null) frame["params"] = parameters;
            try { WriteFrame(frame); }
            catch (Exception error)
            {
                Settle(id, null, error);
            }
            return entry.Completion.Task;
        }

        internal void WriteFrame(Dictionary<string, object> frame)
        {
            string line = json.Serialize(frame);
            lock (writeGate)
            {
                if (IsClosed) throw new AgentException("Agent 运行时已断开连接。");
                try
                {
                    output.Write(line);
                    output.Write("\n");
                    output.Flush();
                }
                catch (Exception error)
                {
                    throw new AgentException("无法写入 Agent 运行时：" + Redact(error.Message));
                }
            }
        }

        void RaiseDiagnostic(string text)
        {
            Action<string> handler = Diagnostic;
            if (handler == null) return;
            try { handler(Redact(text)); }
            catch { }
        }

        string Redact(string text)
        {
            try { return redact(text); }
            catch { return text ?? ""; }
        }

        internal void Close(Exception error)
        {
            List<Pending> failed;
            lock (gate)
            {
                if (closed) return;
                closed = true;
                failed = new List<Pending>(pending.Values);
                pending.Clear();
            }
            foreach (Pending entry in failed)
            {
                entry.Release();
                entry.Completion.TrySetException(error);
            }
        }

        public void Dispose()
        {
            Close(new AgentException("Agent 运行时已停止。"));
        }
    }

    // C# is the server for the loopback bridge. The node launcher connects once
    // with a one-shot token and then exchanges one JSON request per line:
    // {id, operation, sessionId, name, args, proposal, path, diff, proposalId}
    // answered by {id, value} or {id, error}.
    internal sealed class SdkNativeBridge : IDisposable
    {
        const int MaximumFrameCharacters = 8 * 1024 * 1024;
        const int HandshakeTimeoutMilliseconds = 20000;
        readonly Func<Dictionary<string, object>, CancellationToken, Task<object>> handler;
        readonly Func<string, string> redact;
        readonly string token;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = MaximumFrameCharacters };
        readonly object gate = new object();
        readonly object writeGate = new object();
        TcpListener listener;
        Thread accept;
        TcpClient client;
        volatile bool stopping;

        internal SdkNativeBridge(Func<Dictionary<string, object>, CancellationToken, Task<object>> handler, Func<string, string> redact, string token)
        {
            this.handler = handler;
            this.redact = redact ?? delegate(string value) { return value; };
            this.token = token;
        }

        internal int Port { get; private set; }

        internal bool IsConnected
        {
            get
            {
                TcpClient value = client;
                if (value == null) return false;
                try { return value.Connected; }
                catch { return false; }
            }
        }

        internal void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(8);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            accept = new Thread(AcceptLoop) { IsBackground = true, Name = "SdkNativeBridge" };
            accept.Start();
        }

        void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient incoming;
                try { incoming = listener.AcceptTcpClient(); }
                catch { break; }
                if (stopping) { CloseQuietly(incoming); break; }
                try { Serve(incoming); }
                catch (Exception error) { RaiseDiagnostic("原生工具桥连接结束：" + Redact(error.Message)); }
                finally
                {
                    lock (gate) { if (Object.ReferenceEquals(client, incoming)) client = null; }
                    CloseQuietly(incoming);
                }
            }
        }

        void Serve(TcpClient connection)
        {
            connection.NoDelay = true;
            connection.ReceiveTimeout = HandshakeTimeoutMilliseconds;
            NetworkStream stream = connection.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false), false, 8192, true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, true);
            writer.AutoFlush = true;
            string hello = reader.ReadLine();
            if (!TokenMatches(hello)) return;
            connection.ReceiveTimeout = 0;
            lock (gate) client = connection;
            while (!stopping)
            {
                string line;
                try { line = reader.ReadLine(); }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
                if (line == null) break;
                if (line.Length == 0) continue;
                if (line.Length > MaximumFrameCharacters) continue;
                Answer(writer, line);
            }
        }

        void Answer(StreamWriter writer, string line)
        {
            Dictionary<string, object> request;
            try { request = json.Deserialize<Dictionary<string, object>>(line); }
            catch { return; }
            if (request == null) return;
            object id;
            request.TryGetValue("id", out id);
            var response = new Dictionary<string, object> { { "id", id } };
            try
            {
                Func<Dictionary<string, object>, CancellationToken, Task<object>> callback = handler;
                if (callback == null) throw new AgentException("原生工具未连接。");
                object value = callback(request, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
                response["value"] = value;
            }
            catch (Exception error)
            {
                response["error"] = Redact(error.Message);
            }
            string payload = json.Serialize(response);
            lock (writeGate)
            {
                try { writer.Write(payload); writer.Write("\n"); writer.Flush(); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        bool TokenMatches(string line)
        {
            if (String.IsNullOrEmpty(token) || line == null || line.Length > 4096) return false;
            try
            {
                Dictionary<string, object> hello = json.Deserialize<Dictionary<string, object>>(line);
                return hello != null && String.Equals(SdkValues.Text(hello, "token"), token, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        void RaiseDiagnostic(string text)
        {
            // Bridge diagnostics ride on stderr-like state; nothing to raise here.
        }

        string Redact(string text)
        {
            try { return redact(text); }
            catch { return text ?? ""; }
        }

        static void CloseQuietly(TcpClient value)
        {
            if (value == null) return;
            try { value.Close(); }
            catch { }
        }

        public void Dispose()
        {
            Stop();
        }

        internal void Stop()
        {
            stopping = true;
            if (listener != null) { try { listener.Stop(); } catch { } }
            lock (gate)
            {
                CloseQuietly(client);
                client = null;
            }
            Thread thread = accept;
            if (thread != null && thread != Thread.CurrentThread) { try { thread.Join(1000); } catch { } }
            accept = null;
        }
    }

    // The single warm SDK runtime process owned by this editor. It speaks
    // JSON-RPC on stdio and hosts the loopback bridge for native tools. The
    // controller starts it on demand; it stays warm across sessions.
    internal sealed class SdkRuntime : ISdkRuntime
    {
        static readonly object sharedGate = new object();
        static SdkRuntime shared;
        readonly string executableOverride;
        readonly string argumentsOverride;
        readonly string workingDirectoryOverride;
        readonly int stopWaitMilliseconds;
        readonly object gate = new object();
        readonly SemaphoreSlim startGate = new SemaphoreSlim(1, 1);
        readonly SemaphoreSlim initializeGate = new SemaphoreSlim(1, 1);
        readonly HashSet<string> activeSessions = new HashSet<string>();
        Process process;
        ProcessLifetime lifetime;
        SdkJsonRpcChannel channel;
        SdkNativeBridge bridge;
        string key = "";
        string modelSignature;
        string home;
        bool stopping;
        bool initialized;
        string initializedCwd = "";
        object nativeOwner;
        Func<Dictionary<string, object>, CancellationToken, Task<object>> nativeHandler;

        internal SdkRuntime()
        {
        }

        // Test seam: run an arbitrary peer process instead of node + launcher.
        internal SdkRuntime(string executable, string arguments, string workingDirectory)
            : this(executable, arguments, workingDirectory, 6500)
        {
        }

        internal SdkRuntime(string executable, string arguments, string workingDirectory, int stopWaitMilliseconds)
        {
            executableOverride = executable;
            argumentsOverride = arguments;
            workingDirectoryOverride = workingDirectory;
            this.stopWaitMilliseconds = stopWaitMilliseconds > 0 ? stopWaitMilliseconds : 6500;
        }

        internal static SdkRuntime Shared
        {
            get
            {
                lock (sharedGate)
                {
                    if (shared == null) shared = new SdkRuntime();
                    return shared;
                }
            }
        }

        internal static bool IsSharedRunning
        {
            get
            {
                SdkRuntime value;
                lock (sharedGate) value = shared;
                return value != null && value.IsRunning;
            }
        }

        public event Action<string> Failed;
        public event Action<string, Dictionary<string, object>> Notification;

        public bool IsRunning
        {
            get
            {
                Process child;
                lock (gate) child = process;
                if (child == null) return false;
                try { return !child.HasExited; }
                catch { return false; }
            }
        }

        public int ProcessId
        {
            get
            {
                Process child;
                lock (gate) child = process;
                if (child == null) return 0;
                try { return child.HasExited ? 0 : child.Id; }
                catch { return 0; }
            }
        }

        public string LastDiagnostic { get; private set; }

        public bool IsBridgeConnected { get { SdkNativeBridge value; lock (gate) value = bridge; return value != null && value.IsConnected; } }

        public bool IsInitialized
        {
            get
            {
                Process child;
                lock (gate) { child = process; return initialized && child != null; }
            }
        }

        public string InitializedCwd
        {
            get { lock (gate) return initializedCwd; }
        }

        // The process-wide handshake is sent exactly once per started process;
        // later controllers reuse the warm runtime and only open sessions.
        public async Task InitializeAsync(ModelConfig model, string cwd, CancellationToken token)
        {
            if (model == null || !model.IsValid) throw new AgentException("请先配置模型地址、模型名和密钥。");
            await initializeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (IsInitialized) return;
                string directory = Path.GetFullPath(String.IsNullOrWhiteSpace(cwd) ? home : cwd);
                var parameters = new Dictionary<string, object>
                {
                    { "provider", "qingye-model" },
                    { "model", model.ModelName },
                    { "cwd", directory },
                    { "approvalRequests", false },
                    { "capabilities", new Dictionary<string, object> { { "userQuestions", false } } }
                };
                await RequestAsync("initialize", parameters, token).ConfigureAwait(false);
                lock (gate) { initialized = true; initializedCwd = directory; }
            }
            finally
            {
                initializeGate.Release();
            }
        }

        public bool IsSessionActive(string sessionId)
        {
            if (String.IsNullOrEmpty(sessionId)) return false;
            lock (gate) return activeSessions.Contains(sessionId);
        }

        public void MarkSessionActive(string sessionId)
        {
            if (String.IsNullOrEmpty(sessionId)) return;
            lock (gate) activeSessions.Add(sessionId);
        }

        internal void ForgetSession(string sessionId)
        {
            lock (gate) activeSessions.Remove(sessionId);
        }

        internal void ForgetSessions()
        {
            lock (gate) activeSessions.Clear();
        }

        public void AttachNative(object owner, Func<Dictionary<string, object>, CancellationToken, Task<object>> handler)
        {
            lock (gate) { nativeOwner = owner; nativeHandler = handler; }
        }

        public void DetachNative(object owner)
        {
            lock (gate)
            {
                if (!Object.ReferenceEquals(nativeOwner, owner)) return;
                nativeOwner = null;
                nativeHandler = null;
            }
        }

        internal static Task StopSharedAsync()
        {
            SdkRuntime value;
            lock (sharedGate) value = shared;
            if (value == null) return Task.FromResult<object>(null);
            return value.StopAsync();
        }

        // Ends and forgets the shared instance so later tests start clean.
        internal static Task ResetSharedForTestsAsync()
        {
            SdkRuntime value;
            lock (sharedGate) { value = shared; shared = null; }
            if (value == null) return Task.FromResult<object>(null);
            return value.StopAsync().ContinueWith(delegate
            {
                value.Dispose();
                return (object)null;
            });
        }

        public async Task StartAsync(string runtimeDirectory, string home, ModelConfig model, CancellationToken token)
        {
            if (model == null || !model.IsValid) throw new AgentException("请先配置模型地址、模型名和密钥。");
            token.ThrowIfCancellationRequested();
            string signature = Signature(model);
            await startGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (IsRunning && String.Equals(modelSignature, signature, StringComparison.Ordinal)) return;
                await StopCoreAsync().ConfigureAwait(false);
                await StartCoreAsync(runtimeDirectory, home, model, signature, token).ConfigureAwait(false);
            }
            finally
            {
                startGate.Release();
            }
        }

        static string Signature(ModelConfig model)
        {
            return model.BaseUrl + "\n" + model.ModelName + "\n" + model.ApiKey;
        }

        static string ModelBaseUrl(ModelConfig model)
        {
            string endpoint = model.Endpoint;
            const string suffix = "/chat/completions";
            if (endpoint.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return endpoint.Substring(0, endpoint.Length - suffix.Length);
            return endpoint;
        }

        Task StartCoreAsync(string runtimeDirectory, string home, ModelConfig model, string signature, CancellationToken token)
        {
            string executable = executableOverride;
            string arguments = argumentsOverride;
            string workingDirectory = workingDirectoryOverride;
            if (String.IsNullOrEmpty(executable))
            {
                executable = Path.Combine(runtimeDirectory, "node.exe");
                arguments = "\"" + Path.Combine(runtimeDirectory, "sdk-launcher.mjs") + "\"";
                workingDirectory = runtimeDirectory;
            }
            if (String.IsNullOrEmpty(executable) || !File.Exists(executable))
                throw new IOException("内置 Harness 运行时不完整。请使用包含 Harness 目录的完整便携包。");
            string launcher = Path.Combine(runtimeDirectory, "sdk-launcher.mjs");
            if (String.IsNullOrEmpty(argumentsOverride) && !File.Exists(launcher))
                throw new IOException("内置 Harness 运行时不完整。请使用包含 Harness 目录的完整便携包。");
            string homePath = Path.GetFullPath(home);
            Directory.CreateDirectory(homePath);
            string temporary = Path.Combine(homePath, "temp");
            Directory.CreateDirectory(temporary);
            string bridgeToken = Guid.NewGuid().ToString("N");
            string newKey = model.ApiKey;
            var info = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            info.EnvironmentVariables["TEMP"] = temporary;
            info.EnvironmentVariables["TMP"] = temporary;
            info.EnvironmentVariables["TMPDIR"] = temporary;
            info.EnvironmentVariables.Remove("NODE_OPTIONS");
            info.EnvironmentVariables.Remove("NODE_PATH");
            info.EnvironmentVariables["QINGYE_MODEL_BASE_URL"] = ModelBaseUrl(model);
            info.EnvironmentVariables["QINGYE_MODEL_NAME"] = model.ModelName;
            info.EnvironmentVariables["QINGYE_MODEL_KEY"] = newKey;
            info.EnvironmentVariables["QINGYE_APP_HOME"] = homePath;
            info.EnvironmentVariables["QINGYE_SESSION_STORAGE"] = "memory";
            info.EnvironmentVariables["DSH_HOME"] = homePath;
            info.EnvironmentVariables["DSH_TELEMETRY_DISABLED"] = "1";
            info.EnvironmentVariables["DSH_PERMISSION_MODE"] = "workspace-write";
            var nativeBridge = new SdkNativeBridge(HandleNativeAsync, delegate(string text) { return ModelConfig.Redact(text, newKey); }, bridgeToken);
            nativeBridge.Start();
            info.EnvironmentVariables["QINGYE_BRIDGE_PORT"] = nativeBridge.Port.ToString();
            info.EnvironmentVariables["QINGYE_BRIDGE_TOKEN"] = bridgeToken;
            var child = new Process { StartInfo = info, EnableRaisingEvents = true };
            SdkJsonRpcChannel frameChannel = null;
            child.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
            {
                if (!String.IsNullOrWhiteSpace(args.Data)) LastDiagnostic = ModelConfig.Redact(args.Data, newKey);
            };
            child.Exited += delegate
            {
                string message = LastDiagnostic ?? "Agent 运行时已停止。";
                bool notify;
                ProcessLifetime retired = null;
                lock (gate)
                {
                    notify = ReferenceEquals(process, child) && !stopping;
                    if (ReferenceEquals(process, child)) retired = lifetime;
                }
                SdkJsonRpcChannel frames = frameChannel;
                if (frames != null) frames.Close(new AgentException(message));
                if (retired != null) { try { retired.Dispose(); } catch { } }
                if (notify)
                {
                    Action<string> handler = Failed;
                    if (handler != null)
                    {
                        try { handler(message); }
                        catch { }
                    }
                }
            };
            lock (gate)
            {
                process = child;
                bridge = nativeBridge;
                key = newKey;
                modelSignature = signature;
                this.home = homePath;
                activeSessions.Clear();
                initialized = false;
                initializedCwd = String.Empty;
                LastDiagnostic = null;
            }
            try
            {
                child.Start();
                lifetime = new ProcessLifetime(child);
                child.BeginErrorReadLine();
                frameChannel = new SdkJsonRpcChannel(child.StandardOutput,
                    new StreamWriter(child.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true },
                    delegate(string text) { return ModelConfig.Redact(text, newKey); });
                frameChannel.Notification += delegate(string method, Dictionary<string, object> parameters)
                {
                    Action<string, Dictionary<string, object>> handler = Notification;
                    if (handler == null) return;
                    try { handler(method, parameters); }
                    catch { }
                };
                frameChannel.Diagnostic += delegate(string text) { LastDiagnostic = text; };
                // Compatibility only: the pinned runtime never sends
                // approval/request because initialize sets approvalRequests:false.
                frameChannel.ServerRequestHandler = delegate(string method, Dictionary<string, object> parameters, CancellationToken ignored)
                {
                    return Task.FromResult<object>(new Dictionary<string, object> { { "outcome", "cancelled" } });
                };
                lock (gate) channel = frameChannel;
                frameChannel.Start();
            }
            catch (Exception error)
            {
                lock (gate)
                {
                    if (ReferenceEquals(process, child)) { process = null; channel = null; bridge = null; modelSignature = null; }
                }
                nativeBridge.Stop();
                if (frameChannel != null) frameChannel.Close(new AgentException("启动 Agent 运行时失败。"));
                try { if (!child.HasExited) child.Kill(); } catch { }
                try { child.Dispose(); } catch { }
                throw new AgentException("启动 Agent 运行时失败：" + ModelConfig.Redact(error.Message, newKey));
            }
            return Task.FromResult<object>(null);
        }

        Task<object> HandleNativeAsync(Dictionary<string, object> request, CancellationToken token)
        {
            Func<Dictionary<string, object>, CancellationToken, Task<object>> callback;
            lock (gate) callback = nativeHandler;
            if (callback == null) throw new AgentException("原生文件工具未连接。");
            return callback(request, token);
        }

        public Task<Dictionary<string, object>> RequestAsync(string method, Dictionary<string, object> parameters, CancellationToken token)
        {
            SdkJsonRpcChannel current;
            lock (gate) current = channel;
            if (current == null || !IsRunning) return Task.FromException<Dictionary<string, object>>(new AgentException("Agent 运行时尚未启动。"));
            return current.RequestAsync(method, parameters, token, TimeoutFor(method));
        }

        static int TimeoutFor(string method)
        {
            switch (method)
            {
                case "initialize": return 90000;
                case "session/prompt": return 60000;
                case "session/resume": return 60000;
                case "session/working-directory/set": return 30000;
                case "shutdown": return 5000;
                case "qingye/session/cancel":
                case "qingye/session/delete":
                case "qingye/sessions/clear": return 5000;
                default: return 60000;
            }
        }

        public async Task StopAsync()
        {
            await startGate.WaitAsync().ConfigureAwait(false);
            try { await StopCoreAsync().ConfigureAwait(false); }
            finally { startGate.Release(); }
        }

        async Task StopCoreAsync()
        {
            Process child;
            ProcessLifetime retired;
            SdkJsonRpcChannel frames;
            SdkNativeBridge bridgeToStop;
            lock (gate)
            {
                if (process == null && bridge == null && channel == null) return;
                stopping = true;
                child = process;
                retired = lifetime;
                frames = channel;
                bridgeToStop = bridge;
                process = null;
                lifetime = null;
                channel = null;
                bridge = null;
                modelSignature = null;
                activeSessions.Clear();
                initialized = false;
                initializedCwd = String.Empty;
            }
            try
            {
                if (frames != null && child != null && SafeRunning(child))
                {
                    try
                    {
                        using (var timeout = new CancellationTokenSource(2000))
                        {
                            await frames.RequestAsync("shutdown", null, timeout.Token, 2000).ConfigureAwait(false);
                        }
                    }
                    catch (Exception) { }
                }
                if (child != null)
                {
                    bool exited = !SafeRunning(child) || await Task.Run(delegate { return child.WaitForExit(stopWaitMilliseconds); }).ConfigureAwait(false);
                    if (!exited && SafeRunning(child))
                    {
                        try { child.Kill(); } catch { }
                        try { child.WaitForExit(2000); } catch { }
                    }
                }
            }
            finally
            {
                if (frames != null) frames.Close(new AgentException("Agent 运行时已停止。"));
                if (bridgeToStop != null) bridgeToStop.Stop();
                if (retired != null) { try { retired.Dispose(); } catch { } }
                if (child != null) { try { child.Dispose(); } catch { } }
                lock (gate) { stopping = false; }
            }
        }

        static bool SafeRunning(Process child)
        {
            try { return child != null && !child.HasExited; }
            catch { return false; }
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); }
            catch { }
        }
    }
}
