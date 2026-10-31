using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Qingye.Core
{
    // Native SDK-backed agent controller. It drives one warm DSH runtime over
    // stdio JSON-RPC, maps session events onto the same AgentActivity and
    // AgentSession entries the legacy panel renders, and answers the node
    // plugin's native file tools over the loopback bridge.
    public sealed class SdkAgentController : IDisposable
    {
        static readonly string HomeDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent-data");
        const int MaximumDescriptionCharacters = 160;
        readonly Func<string, string> openText;
        readonly Func<string, bool> openDirty;
        readonly IAsyncWriteApprover approver;
        readonly Action<WriteProposal> applied;
        readonly AgentSession session;
        readonly ModelConfig config;
        readonly string dataHome;
        readonly AgentWorkspace workspace;
        readonly ISdkRuntime runtime;
        readonly bool ownsRuntime;
        readonly HarnessNativeTools nativeTools;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        readonly object gate = new object();
        readonly Dictionary<string, string> callNames = new Dictionary<string, string>();
        readonly SynchronizationContext context;
        TaskCompletionSource<bool> turnCompletion;
        bool activeTurn;
        string activeSessionId;
        bool turnFinished;
        string turnFailure;
        string turnFinalText = "";
        int currentStep;
        int running;
        bool disposed;
        volatile bool fileAccessRevoked;
        readonly CancellationTokenSource fileAccessLifetime = new CancellationTokenSource();
        CancellationTokenSource turnApprovalLifetime;
        string lastRuntimeError;

        public SdkAgentController(ModelConfig config, AgentWorkspace workspace, Func<string, string> openText,
            Func<string, bool> openDirty, IAsyncWriteApprover approver, Action<WriteProposal> applied, AgentSession session)
            : this(config, workspace, openText, openDirty, approver, applied, session, null, false, null)
        {
        }

        // Test seam: an injected runtime replaces the shared node process. The
        // public constructor never starts anything by itself.
        internal SdkAgentController(ModelConfig config, AgentWorkspace workspace, Func<string, string> openText,
            Func<string, bool> openDirty, IAsyncWriteApprover approver, Action<WriteProposal> applied, AgentSession session,
            ISdkRuntime runtimeOverride, bool ownsRuntimeOverride, string homeDirectoryOverride = null)
        {
            this.openText = openText;
            this.openDirty = openDirty;
            this.approver = approver;
            this.applied = applied;
            this.session = session ?? new AgentSession();
            this.config = config ?? new ModelConfig();
            this.workspace = workspace;
            runtime = runtimeOverride ?? SdkRuntime.Shared;
            ownsRuntime = ownsRuntimeOverride;
            context = SynchronizationContext.Current;
            string home = String.IsNullOrEmpty(homeDirectoryOverride) ? HomeDirectory : homeDirectoryOverride;
            dataHome = home;
            nativeTools = new HarnessNativeTools(home, EffectiveWorkspace, openText, openDirty,
                delegate(WriteProposal proposal) { if (this.applied != null) this.applied(proposal); }, false, RuntimeDirectory());
            nativeTools.Permission = HarnessFilePermission.Confirm;
            if (!String.IsNullOrWhiteSpace(config.ModelName)) this.session.Model = config.ModelName;
            runtime.AttachNative(this, HandleNativeAsync);
            runtime.Notification += OnRuntimeNotification;
            runtime.Failed += OnRuntimeFailed;
        }

        // One event stream per controller: state changes, tool steps and run
        // results arrive as AgentActivity, the same shape the legacy native UI
        // already renders.
        public event Action<AgentActivity> Activity;

        // DSH session identity used by session/resume; assigned when a new
        // conversation starts on the persistent runtime.
        public string HarnessSessionId { get { return session.HarnessSessionId; } }
        public bool IsRunning { get { return runtime.IsRunning; } }
        // Node process backing the shared warm runtime; 0 when not started.
        public int ProcessId { get { return runtime.ProcessId; } }
        // Bridge approval wait budget; tests shrink it to exercise timeouts.
        internal int ApprovalTimeoutSeconds = 600;
        // File permission policy forwarded to the native tools.
        public HarnessFilePermission Permission
        {
            get { return nativeTools.Permission; }
            set { nativeTools.Permission = value; }
        }

        public static bool IsSharedRunning { get { return SdkRuntime.IsSharedRunning; } }
        public static int SharedProcessId
        {
            get
            {
                if (!SdkRuntime.IsSharedRunning) return 0;
                return SdkRuntime.Shared.ProcessId;
            }
        }

        // Starts the warm SDK runtime on first use and keeps it alive. Safe to
        // call from the UI thread once the panel becomes visible.
        public async Task EnsureStartedAsync(CancellationToken token)
        {
            if (config == null || !config.IsValid) throw new AgentException("请先配置模型地址、模型名和密钥。");
            token.ThrowIfCancellationRequested();
            string cwd = ScopeDirectory() ?? dataHome;
            await runtime.StartAsync(RuntimeDirectory(), dataHome, config, token).ConfigureAwait(false);
            await runtime.InitializeAsync(config, cwd, token).ConfigureAwait(false);
        }

        // Prewarms the shared runtime without a controller or a file scope.
        public static async Task PrewarmAsync(ModelConfig config, CancellationToken token)
        {
            if (config == null || !config.IsValid) throw new AgentException("请先配置模型地址、模型名和密钥。");
            SdkRuntime shared = SdkRuntime.Shared;
            await shared.StartAsync(HarnessRuntime.Locate(AppDomain.CurrentDomain.BaseDirectory), HomeDirectory, config, token).ConfigureAwait(false);
            await shared.InitializeAsync(config, HomeDirectory, token).ConfigureAwait(false);
        }

        // Ends the owned node process; persisted sessions remain resumable.
        public static Task StopSharedAsync()
        {
            return SdkRuntime.StopSharedAsync();
        }

        // Creates a fresh DSH session in the warm runtime and returns its id.
        public async Task<string> NewSessionAsync(CancellationToken token)
        {
            await EnsureStartedAsync(token).ConfigureAwait(false);
            string id = Guid.NewGuid().ToString("N");
            session.HarnessSessionId = id;
            session.Title = "新对话";
            session.State = "idle";
            session.Draft = "";
            session.Entries.Clear();
            session.UpdatedUtc = DateTime.UtcNow;
            session.Model = config.ModelName;
            runtime.MarkSessionActive(id);
            await SetSessionDirectoryAsync(id, EffectiveWorkspace(), token).ConfigureAwait(false);
            return id;
        }

        // Restores a persisted DSH session (model context) before prompting.
        public async Task ResumeAsync(string harnessSessionId, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(harnessSessionId)) throw new AgentException("会话编号无效。");
            await EnsureStartedAsync(token).ConfigureAwait(false);
            if (!runtime.IsSessionActive(harnessSessionId))
            {
                try
                {
                    await runtime.RequestAsync("session/resume",
                        new Dictionary<string, object> { { "sessionId", harnessSessionId } }, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    throw new AgentException("恢复会话失败：" + ModelConfig.Redact(error.Message, config.ApiKey) + "。请在轻页中打开该会话原来的文件或工作区后重试。");
                }
                session.HarnessSessionId = harnessSessionId;
                runtime.MarkSessionActive(harnessSessionId);
            }
            session.HarnessSessionId = harnessSessionId;
            // The persisted session keeps its recorded cwd; a resumed conversation
            // must still follow the file/workspace the editor currently shows.
            await SetSessionDirectoryAsync(harnessSessionId, EffectiveWorkspace(), token).ConfigureAwait(false);
        }

        // Sends one user prompt and streams its activity until the turn settles.
        public async Task AskAsync(string prompt, CancellationToken token, IList<string> attachments = null)
        {
            if (String.IsNullOrWhiteSpace(prompt)) return;
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new AgentException("当前会话仍在运行。");
            try
            {
                token.ThrowIfCancellationRequested();
                await EnsureStartedAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (disposed || fileAccessRevoked) throw new OperationCanceledException(token);
                string sessionId = await EnsureSessionAsync(token).ConfigureAwait(false);
                AgentWorkspace scope = EffectiveWorkspace();
                if (scope == null || (scope.Root == null && scope.CurrentFile == null)) throw new AgentException("请先打开已保存的文件或导入工作区。");
                session.Model = config.ModelName;
                if (session.Entries.Count == 0) session.Title = FirstLine(prompt, 48);
                session.Note("run-start", "开始任务", null, null, 0);
                lock (gate)
                {
                    turnCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    turnFinished = false;
                    turnFailure = null;
                    turnFinalText = "";
                    activeTurn = true;
                    activeSessionId = sessionId;
                    currentStep = 0;
                    turnApprovalLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, fileAccessLifetime.Token);
                }
                Report("requesting", 0, "等待模型响应");
                // The process-wide initialize cwd may predate the session scope
                // (the panel can start the runtime before a file is opened), so
                // every session must be bound to the editor's current directory
                // before its prompt creates the agent.
                await SetSessionDirectoryAsync(sessionId, scope, token).ConfigureAwait(false);
                var contentBlocks = new object[]
                {
                    new Dictionary<string, object> { { "type", "text" }, { "text", prompt } }
                };
                var parameters = new Dictionary<string, object>
                {
                    { "sessionId", sessionId }, { "contentBlocks", contentBlocks }
                };
                TaskCompletionSource<bool> waiting;
                lock (gate) waiting = turnCompletion;
                try
                {
                    await runtime.RequestAsync("session/prompt", parameters, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    FinishTurn("stopped", "已停止", null);
                    throw;
                }
                catch (Exception error)
                {
                    string text = "请求失败：" + ModelConfig.Redact(error.Message, config.ApiKey);
                    FinishTurn("failed", text, text);
                    throw new AgentException(ModelConfig.Redact(error.Message, config.ApiKey));
                }
                int timeoutMilliseconds = Math.Max(120, config.TimeoutSeconds * Math.Max(1, config.MaxSteps)) * 1000;
                Task delay = Task.Delay(timeoutMilliseconds, token);
                Task settled = await Task.WhenAny(waiting.Task, delay).ConfigureAwait(false);
                if (settled != waiting.Task)
                {
                    if (token.IsCancellationRequested)
                    {
                        FinishTurn("stopped", "已停止", null);
                        throw new OperationCanceledException(token);
                    }
                    FinishTurn("failed", "Agent 响应超时，请检查模型服务后重试。", "Agent 响应超时，请检查模型服务后重试。");
                    throw new AgentException("Agent 响应超时，请检查模型服务后重试。");
                }
                string failure;
                lock (gate) failure = turnFailure;
                if (failure != null) throw new AgentException(failure);
            }
            finally
            {
                lock (gate)
                {
                    activeTurn = false;
                    turnCompletion = null;
                    if (turnApprovalLifetime != null) turnApprovalLifetime.Dispose();
                    turnApprovalLifetime = null;
                }
                Interlocked.Exchange(ref running, 0);
            }
        }

        // Ends the owned runtime process; persisted sessions remain resumable.
        public async Task StopAsync()
        {
            bool active;
            lock (gate) active = activeTurn && !turnFinished;
            if (active) FinishTurn("stopped", "已停止", null);
            await runtime.StopAsync().ConfigureAwait(false);
        }

        public async Task CancelCurrentTurnAsync()
        {
            string id = session.HarnessSessionId;
            lock (gate) { if (turnApprovalLifetime != null) turnApprovalLifetime.Cancel(); }
            if (String.IsNullOrEmpty(id) || !runtime.IsRunning) return;
            Exception cancellationError = null;
            try
            {
                using (var timeout = new CancellationTokenSource(5000))
                    await runtime.RequestAsync("qingye/session/cancel", new Dictionary<string, object> { { "sessionId", id } }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                cancellationError = error;
            }
            if (cancellationError != null)
            {
                await runtime.StopAsync().ConfigureAwait(false);
                Post(() => FinishTurn("stopped", "已停止", null));
                throw new AgentException("任务已停止；运行时取消失败，下次发送将重新启动：" + ModelConfig.Redact(cancellationError.Message, config.ApiKey));
            }
            Post(() => FinishTurn("stopped", "已停止", null));
        }

        internal static async Task DeleteTemporarySessionAsync(string id)
        {
            if (String.IsNullOrEmpty(id) || !SdkRuntime.IsSharedRunning) return;
            var shared = SdkRuntime.Shared;
            await shared.RequestAsync("qingye/session/delete", new Dictionary<string, object> { { "sessionId", id } }, CancellationToken.None).ConfigureAwait(false);
            shared.ForgetSession(id);
        }

        internal static async Task ClearTemporarySessionsAsync()
        {
            if (!SdkRuntime.IsSharedRunning) return;
            var shared = SdkRuntime.Shared;
            await shared.RequestAsync("qingye/sessions/clear", null, CancellationToken.None).ConfigureAwait(false);
            shared.ForgetSessions();
        }

        public void Dispose()
        {
            if (disposed) return;
            RevokeFileAccess();
            disposed = true;
            runtime.Notification -= OnRuntimeNotification;
            runtime.Failed -= OnRuntimeFailed;
            runtime.DetachNative(this);
            if (ownsRuntime) runtime.Dispose();
        }

        // Revocation is synchronous: an old bridge callback cannot race the new
        // editor scope while the SDK finishes cancelling its previous turn.
        public void RevokeFileAccess()
        {
            fileAccessRevoked = true;
            nativeTools.Permission = HarnessFilePermission.ReadOnly;
            fileAccessLifetime.Cancel();
        }

        static string RuntimeDirectory()
        {
            return HarnessRuntime.Locate(AppDomain.CurrentDomain.BaseDirectory);
        }

        AgentWorkspace EffectiveWorkspace()
        {
            if (workspace != null) return workspace;
            if (!String.IsNullOrEmpty(session.Root) || !String.IsNullOrEmpty(session.CurrentFile))
                return new AgentWorkspace(session.Root, session.CurrentFile);
            return null;
        }

        string ScopeDirectory()
        {
            AgentWorkspace scope = EffectiveWorkspace();
            if (scope == null) return null;
            if (!String.IsNullOrEmpty(scope.Root)) return scope.Root;
            if (!String.IsNullOrEmpty(scope.CurrentFile)) return Path.GetDirectoryName(scope.CurrentFile);
            return null;
        }

        async Task<string> EnsureSessionAsync(CancellationToken token)
        {
            string id = session.HarnessSessionId;
            if (String.IsNullOrWhiteSpace(id))
            {
                id = Guid.NewGuid().ToString("N");
                session.HarnessSessionId = id;
                runtime.MarkSessionActive(id);
                return id;
            }
            if (!runtime.IsSessionActive(id)) await ResumeAsync(id, token).ConfigureAwait(false);
            return id;
        }

        // A session created before its scope was known keeps the process
        // initialize cwd; this request rebinds the session (and its persisted
        // header context) to the editor's current file/workspace. Failure is
        // loud: prompting against a stale directory would fail every tool.
        async Task SetSessionDirectoryAsync(string sessionId, AgentWorkspace scope, CancellationToken token)
        {
            if (String.IsNullOrEmpty(sessionId) || scope == null) return;
            string directory = !String.IsNullOrEmpty(scope.Root) ? scope.Root : Path.GetDirectoryName(scope.CurrentFile);
            if (String.IsNullOrEmpty(directory)) return;
            try
            {
                await runtime.RequestAsync("session/working-directory/set",
                    new Dictionary<string, object> { { "sessionId", sessionId }, { "path", directory } }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new AgentException("无法把 Agent 会话切换到当前工作区：" + ModelConfig.Redact(error.Message, config.ApiKey) + "。请打开原文件或原工作区后重试。");
            }
        }

        static string FirstLine(string text, int maximum)
        {
            string line = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')[0].Trim();
            return line.Length > maximum ? line.Substring(0, maximum) : line;
        }

        void Post(Action action)
        {
            if (disposed || fileAccessRevoked) return;
            SynchronizationContext current = context;
            if (current == null) { action(); return; }
            current.Post(delegate
            {
                try { if (!disposed && !fileAccessRevoked) action(); }
                catch { }
            }, null);
        }

        void Report(string state, int step, string text, string tool = null, string callId = null, string delta = null)
        {
            if (disposed || fileAccessRevoked) return;
            session.State = state;
            session.UpdatedUtc = DateTime.UtcNow;
            Action<AgentActivity> handler = Activity;
            if (handler == null) return;
            try
            {
                handler(new AgentActivity
                {
                    State = state, Step = step, Text = text, Tool = tool, CallId = callId, Delta = delta, SessionId = session.HarnessSessionId
                });
            }
            catch { }
        }

        bool IsActiveTurn { get { lock (gate) return activeTurn && !turnFinished; } }

        void FinishTurn(string state, string text, string failure)
        {
            TaskCompletionSource<bool> completion;
            lock (gate)
            {
                if (!activeTurn || turnFinished) return;
                turnFinished = true;
                turnFailure = failure;
                completion = turnCompletion;
            }
            if (OwnsSession(activeSessionId))
            {
                session.Note("run-end", text, null, null, currentStep);
                Report(state, currentStep, text);
            }
            if (completion != null) completion.TrySetResult(true);
        }

        void OnRuntimeFailed(string message)
        {
            string text = ModelConfig.Redact(message, config.ApiKey);
            Post(delegate
            {
                lastRuntimeError = text;
                if (IsActiveTurn) FinishTurn("failed", "运行失败：" + text, "运行失败：" + text);
            });
        }

        void OnRuntimeNotification(string method, Dictionary<string, object> parameters)
        {
            if (disposed) return;
            if (method == "qingye/assistant.delta" || method == "qingye/assistant.start")
            {
                string deltaSession = SdkValues.Text(parameters, "sessionId");
                if (!OwnsSession(deltaSession)) return;
                Post(delegate {
                    if (!IsActiveTurn || !OwnsSession(deltaSession)) return;
                    if (method == "qingye/assistant.start") Report("assistant-start", currentStep, "正在回复");
                    else Report("streaming", currentStep, "正在回复", null, null, SdkValues.Text(parameters, "text"));
                });
                return;
            }
            if (method == "session.status") { HandleStatus(parameters); return; }
            if (method != "session.event") return;
            string sessionId = SdkValues.Text(parameters, "sessionId");
            if (!OwnsSession(sessionId)) return;
            Dictionary<string, object> envelope = SdkValues.Map(Get(parameters, "event"));
            if (envelope == null) return;
            string type = SdkValues.Text(envelope, "type");
            Dictionary<string, object> data = SdkValues.Map(Get(envelope, "data")) ?? new Dictionary<string, object>();
            Post(delegate { if (OwnsSession(sessionId)) ApplyEvent(type, data); });
        }

        void HandleStatus(Dictionary<string, object> parameters)
        {
            string sessionId = SdkValues.Text(parameters, "sessionId");
            if (!OwnsSession(sessionId)) return;
            string status = SdkValues.Text(parameters, "status");
            if (status != "running") return;
            Post(delegate
            {
                if (!IsActiveTurn || !OwnsSession(sessionId)) return;
                Report("requesting", currentStep, "等待模型响应");
            });
        }

        void ApplyEvent(string type, Dictionary<string, object> data)
        {
            switch (type)
            {
                case "turn/start":
                    if (!IsActiveTurn) return;
                    currentStep = 0;
                    Report("requesting", 0, "等待模型响应");
                    break;
                case "step/start":
                    if (!IsActiveTurn) return;
                    currentStep = Math.Max(1, Convert.ToInt32(Get(data, "step")));
                    Report("requesting", currentStep, "等待模型响应");
                    break;
                case "user/message":
                    ApplyUserMessage(data);
                    break;
                case "assistant/message":
                    ApplyAssistantMessage(data);
                    break;
                case "tool/call":
                    ApplyToolCall(data);
                    break;
                case "tool/result":
                    ApplyToolResult(data);
                    break;
                case "turn/end":
                    ApplyTurnEnd(data);
                    break;
                case "session/title":
                    ApplyTitle(data);
                    break;
            }
        }

        void ApplyUserMessage(Dictionary<string, object> data)
        {
            Dictionary<string, object> source = SdkValues.Map(Get(data, "source"));
            string kind = source == null ? "" : SdkValues.Text(source, "kind");
            if (kind != "user") return; // synthetic context injections stay out of the transcript
            string text = SdkValues.ContentText(Get(data, "content"));
            if (String.IsNullOrWhiteSpace(text)) return;
            session.Record(SdkValues.Message("user", text));
        }

        void ApplyAssistantMessage(Dictionary<string, object> data)
        {
            if (!IsActiveTurn) return;
            Dictionary<string, object> message = SdkValues.Map(Get(data, "message"));
            string text = message == null ? "" : SdkValues.ContentText(Get(message, "content"));
            if (String.IsNullOrWhiteSpace(text)) return;
            bool interrupted = Get(data, "interrupted") is bool && (bool)Get(data, "interrupted");
            if (interrupted)
            {
                session.Note("attempt", text, null, null, currentStep);
                return;
            }
            session.Record(SdkValues.Message("assistant", text));
            lock (gate) turnFinalText = text;
            Report("assistant-final", currentStep, text);
        }

        void ApplyToolCall(Dictionary<string, object> data)
        {
            if (!IsActiveTurn) return;
            string callId = SdkValues.Text(data, "callId");
            string name = SdkValues.Text(data, "name");
            string description = name;
            string path = DescribeArguments(SdkValues.Text(data, "arguments"));
            if (path.Length > 0) description = name + " " + path;
            if (description.Length > MaximumDescriptionCharacters) description = description.Substring(0, MaximumDescriptionCharacters) + "…";
            if (!String.IsNullOrEmpty(callId)) callNames[callId] = name;
            session.Note("tool-start", description, name, callId, currentStep);
            Report("tool", currentStep, description, name, callId);
        }

        void ApplyToolResult(Dictionary<string, object> data)
        {
            if (!IsActiveTurn) return;
            Dictionary<string, object> message = SdkValues.Map(Get(data, "message"));
            string callId = message == null ? "" : SdkValues.Text(message, "toolCallId");
            string text = message == null ? "" : SdkValues.ContentText(Get(message, "content"));
            Dictionary<string, object> error = SdkValues.Map(Get(data, "error"));
            if (error != null)
            {
                string reason = SdkValues.Text(error, "reason");
                if (reason.Length > 0) text = reason;
            }
            string toolName;
            if (!String.IsNullOrEmpty(callId)) callNames.TryGetValue(callId, out toolName);
            else toolName = null;
            if (!String.IsNullOrEmpty(callId)) callNames.Remove(callId);
            session.Record(new Dictionary<string, object>
            {
                { "role", "tool" }, { "tool_call_id", callId }, { "content", text ?? "" }
            });
            Report("tool-result", currentStep, text, toolName, callId);
        }

        void ApplyTurnEnd(Dictionary<string, object> data)
        {
            if (!IsActiveTurn) return;
            Dictionary<string, object> reason = SdkValues.Map(Get(data, "reason"));
            string kind = reason == null ? "" : SdkValues.Text(reason, "kind");
            string state;
            string text;
            switch (kind)
            {
                case "completed":
                    state = "completed"; text = "任务完成";
                    break;
                case "aborted":
                    state = "stopped"; text = "已停止";
                    break;
                case "blocked":
                    state = "failed"; text = "任务被阻止：请确认文件权限或工作区范围。";
                    break;
                case "error":
                    state = "failed";
                    Dictionary<string, object> failure = SdkValues.Map(Get(reason, "error"));
                    string detail = failure == null ? "" : ModelConfig.Redact(SdkValues.Text(failure, "message"), config.ApiKey);
                    text = detail.Length > 0 ? "运行失败：" + detail : "运行失败。";
                    break;
                case "max-tokens":
                    state = "limited"; text = "达到本次输出上限，可继续发送指令。";
                    break;
                case "interrupted":
                    state = "stopped"; text = "上次运行被中断，可继续发送指令。";
                    break;
                default:
                    state = "completed"; text = "任务完成";
                    break;
            }
            FinishTurn(state, text, state == "failed" ? text : null);
        }

        void ApplyTitle(Dictionary<string, object> data)
        {
            string title = SdkValues.Text(data, "title");
            if (title.Length > 0) session.Title = title;
        }

        static object Get(Dictionary<string, object> map, string name)
        {
            object value;
            return map != null && map.TryGetValue(name, out value) ? value : null;
        }

        string DescribeArguments(string arguments)
        {
            if (String.IsNullOrWhiteSpace(arguments)) return "";
            try
            {
                Dictionary<string, object> args = json.Deserialize<Dictionary<string, object>>(arguments);
                string value = SdkValues.Text(args, "path");
                if (value.Length > 0) return value;
                value = SdkValues.Text(args, "query");
                if (value.Length > 0) return value;
            }
            catch { }
            return "";
        }

        // ---- native tool bridge -------------------------------------------------

        Task<object> HandleNativeAsync(Dictionary<string, object> request, CancellationToken token)
        {
            string operation = SdkValues.Text(request, "operation");
            string sessionId = SdkValues.Text(request, "sessionId");
            RequireOwnSession(sessionId);
            string name = SdkValues.Text(request, "name");
            Dictionary<string, object> args = SdkValues.Map(Get(request, "args"));
            if (operation == "approval") return Task.FromResult<object>(AnswerApproval(request, sessionId, name));
            if (operation == "context")
            {
                AgentWorkspace scope = EffectiveWorkspace();
                return Task.FromResult<object>(new Dictionary<string, object>
                {
                    { "root", scope == null ? null : scope.Root },
                    { "file", scope == null ? null : scope.CurrentFile }
                });
            }
            if (operation != "tool" && operation != "pin" && operation != "apply" && operation != "discard")
                throw new AgentException("未知原生操作。");
            RequireOwnSession(sessionId);
            string proposalId = SdkValues.Text(request, "proposal");
            if (proposalId.Length == 0) proposalId = SdkValues.Text(request, "proposalId");
            if (operation == "pin") return Task.FromResult<object>(PinNativeScope(sessionId, name, args));
            object value = nativeTools.Execute(operation, sessionId, name, args, proposalId.Length == 0 ? null : proposalId);
            return Task.FromResult(value);
        }

        // Native-tools pins on `agent/created`; the SDK can create that agent
        // while `session/working-directory/set` is still applying, so the pin
        // may carry the process initialize cwd. Only that known stale value is
        // rebound to the editor scope; a genuinely foreign directory is still
        // rejected so a mismatched session never silently changes files.
        object PinNativeScope(string sessionId, string name, Dictionary<string, object> args)
        {
            try
            {
                return nativeTools.Execute("pin", sessionId, name, args, null);
            }
            catch (AgentException)
            {
                string requested = args == null ? "" : SdkValues.Text(args, "cwd");
                string processCwd = runtime.InitializedCwd;
                bool knownStale = requested.Length == 0 || (processCwd.Length > 0 && String.Equals(requested, processCwd, StringComparison.OrdinalIgnoreCase));
                if (!knownStale) throw;
                string directory = ScopeDirectory();
                if (String.IsNullOrEmpty(directory)) throw;
                var corrected = args == null ? new Dictionary<string, object>() : new Dictionary<string, object>(args);
                corrected["cwd"] = directory;
                return nativeTools.Execute("pin", sessionId, name, corrected, null);
            }
        }

        void RequireOwnSession(string sessionId)
        {
            if (disposed || fileAccessRevoked) throw new AgentException("旧文件范围已撤销，请在当前范围重新开始。");
            string current = session.HarnessSessionId;
            if (String.IsNullOrEmpty(sessionId) || String.IsNullOrEmpty(current)) throw new AgentException("原生工具尚未绑定当前会话。");
            if (!String.Equals(sessionId, current, StringComparison.Ordinal)) throw new AgentException("原生工具会话与当前对话不一致。");
        }

        bool OwnsSession(string sessionId)
        {
            return !disposed && !fileAccessRevoked && !String.IsNullOrEmpty(sessionId) &&
                !String.IsNullOrEmpty(session.HarnessSessionId) && sessionId == session.HarnessSessionId;
        }

        // The node plugin asks the bridge before writing. The pending proposal
        // created by the preceding tool call carries the exact diff; a missing
        // or foreign proposal is an error, never an implicit approval.
        object AnswerApproval(Dictionary<string, object> request, string sessionId, string toolName)
        {
            RequireOwnSession(sessionId);
            string proposalId = SdkValues.Text(request, "proposalId");
            if (proposalId.Length == 0) proposalId = SdkValues.Text(request, "proposal");
            WriteProposal proposal;
            bool found = proposalId.Length > 0
                ? nativeTools.TryGetProposal(sessionId, proposalId, out proposal)
                : nativeTools.TryFindLatestProposal(sessionId, toolName, out proposal);
            if (!found) throw new AgentException("修改提案已失效或不属于当前会话。");
            string requestedPath = SdkValues.Text(request, "path");
            if (requestedPath.Length > 0 && !String.Equals(requestedPath, proposal.RelativePath, StringComparison.OrdinalIgnoreCase))
                throw new AgentException("修改提案路径不一致，已拒绝写入。");
            Report("approval", currentStep, "等待确认修改 " + proposal.RelativePath, toolName, null);
            bool approved;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, ApprovalTimeoutSeconds))))
            {
                try
                {
                    CancellationToken turnToken;
                    lock (gate) turnToken = turnApprovalLifetime == null ? fileAccessLifetime.Token : turnApprovalLifetime.Token;
                    using (var approvalLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, turnToken))
                        approved = approver != null && approver.ApproveAsync(proposal, approvalLifetime.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return new Dictionary<string, object>
                    {
                        { "allowed", false }, { "message", "等待确认超时；文件未修改。" }
                    };
                }
                catch (Exception error)
                {
                    return new Dictionary<string, object>
                    {
                        { "allowed", false }, { "message", "确认修改失败：" + ModelConfig.Redact(error.Message, config.ApiKey) }
                    };
                }
            }
            if (!approved) return new Dictionary<string, object> { { "allowed", false } };
            return new Dictionary<string, object> { { "allowed", true } };
        }
    }
}
