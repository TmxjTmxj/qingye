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
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using Qingye.Core;

namespace Qingye.UI
{
    // The right-hand agent panel. Production renders the native 0.7 conversation
    // UI and drives the persistent DSH SDK runtime through SdkAgentController;
    // the retired WebView Harness panel is never constructed here.
    internal sealed partial class AgentPanel : UserControl
    {
        // Automatic start on first reveal; tests disable it so no process or
        // network activity happens while UI structure is asserted.
        internal static bool AllowAutomaticStart = true;
        // Startup prewarm is opt-in (memory cost); the app no longer warms up at
        // launch, only on first panel reveal.
        internal static bool AllowPrewarm;
        const string StartingText = "正在启动内置运行时…";

        readonly MainWindow owner;
        object legacyContent;
        bool legacyTesting;
        readonly AgentConversationView conversation = new AgentConversationView();
        readonly AgentHistoryView history = new AgentHistoryView();
        readonly TextBox input = new TextBox();
        readonly Button send = Theme.Button(null, "发送", delegate
        {
        });
        readonly Button stop = Theme.Button(null, "停止", delegate
        {
        });
        readonly Button retry = Theme.Button(null, "重试", delegate
        {
        });
        readonly TextBlock status = new TextBlock
        {
            Text = "待命", FontSize = 12
        };
        readonly TextBlock model = new TextBlock
        {
            FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis
        };
        readonly TextBlock elapsed = new TextBlock
        {
            FontSize = 11, Margin = new Thickness(10, 0, 0, 0)
        };
        // File access permission for the native tools; defaults to per-write
        // confirmation and resets when the app restarts.
        readonly ComboBox permission = new ComboBox { Width = 132, FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
        readonly TextBlock context = new TextBlock
        {
            FontSize = 12, Margin = new Thickness(14, 0, 14, 10), TextTrimming = TextTrimming.CharacterEllipsis
        };
        readonly TextBlock title = new TextBlock
        {
            Text = "Agent", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
        readonly ProgressBar progress = new ProgressBar
        {
            Height = 2, IsIndeterminate = true, Visibility = Visibility.Collapsed
        };
        readonly WrapPanel attachmentsBar = new WrapPanel
        {
            Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6)
        };
        readonly Expander changesBox = new Expander
        {
            Header = "本次改动", Visibility = Visibility.Collapsed, IsExpanded = false, Margin = new Thickness(0, 0, 0, 6)
        };
        readonly StackPanel changesList = new StackPanel();
        List<string> attachments
        {
            get
            {
                return session.Attachments;
            }
        }
        bool HasScope
        {
            get
            {
                return session.Root != null || session.CurrentFile != null;
            }
        }
        readonly AgentChangeSet changes = new AgentChangeSet();
        readonly DispatcherTimer busyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        readonly DispatcherTimer draftTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        readonly StringBuilder pendingText = new StringBuilder();
        readonly object pendingLock = new object();
        readonly Button fresh;
        readonly Button gear;
        AgentSessionStore store;
        AgentSession session;
        CancellationTokenSource cancellation;
        Task activeTask = Task.FromResult(0);
        DateTime busySince;
        bool busy, loading, shuttingDown, stopRequested;
        string lastPrompt, phase = "待命";
        string historyQuery = "";
        readonly List<AgentSessionInfo> historyItems = new List<AgentSessionInfo>();
        int step;
        internal Func<ModelConfig> ConfigProvider;
        internal Func<ModelConfig, IAgentTransport> TransportFactory;
        // E2E controls: reveal-time auto start can be disabled while the harness
        // drives EnsureRuntimeAsync explicitly.
        internal bool AutoStartOnReveal = true;
        internal string HistoryDirectory;
        internal string AppDataDirectory;
        Task newSessionTask = Task.FromResult(0);
        Task resumeTask = Task.FromResult(0);

        // Persistent SDK runtime state. One controller owns the warm runtime; it
        // is recreated only when the scope or the model configuration changes.
        readonly object runtimeLock = new object();
        SdkAgentController sdk;
        CancellationTokenSource runtimeToken;
        Task<bool> runtimeStartTask;
        bool runtimeReady, runtimeStarting;
        string controllerScope = "", controllerConfig = "", resumedHarnessId, lastRuntimeError;
        double lastPanelMilliseconds = -1, lastStartupMilliseconds = -1, lastNewTaskMilliseconds = -1;
        readonly List<string> timing = new List<string>();
        internal string DiagnosticsDirectory = null;

        internal AgentSession CurrentSession
        {
            get
            {
                return session;
            }
        }
        internal bool IsBusy
        {
            get
            {
                return busy;
            }
        }
        internal bool IsOpen
        {
            get
            {
                return Visibility == Visibility.Visible;
            }
        }
        internal string ContextText
        {
            get
            {
                return context.Text;
            }
        }
        internal string StatusText
        {
            get
            {
                return status.Text;
            }
        }
        // Compatibility handle for the retired embedded page. Production never
        // creates it, so it stays null; the old QA harness only compiles against it.
        internal HarnessPanel Harness
        {
            get
            {
                return null;
            }
        }
        // File permission selected in the native panel; surfaced for tests.
        internal HarnessFilePermission FilePermission
        {
            get
            {
                return (HarnessFilePermission)permission.SelectedIndex;
            }
            set
            {
                permission.SelectedIndex = (int)value;
            }
        }
        internal SdkAgentController Controller
        {
            get
            {
                return sdk;
            }
        }
        internal bool RuntimeStarted
        {
            get
            {
                return runtimeReady;
            }
        }
        internal bool RuntimeReady
        {
            get
            {
                return runtimeReady;
            }
        }
        internal bool RuntimeStarting
        {
            get
            {
                return runtimeStarting;
            }
        }
        // 0 until the shared SDK runtime process is running.
        internal int RuntimeProcessId
        {
            get
            {
                var controller = sdk;
                if (controller != null)
                {
                    try { return controller.ProcessId; }
                    catch (Exception) { return 0; }
                }
                try { return SdkAgentController.SharedProcessId; }
                catch (Exception) { return 0; }
            }
        }
        internal string LastRuntimeError
        {
            get
            {
                return lastRuntimeError;
            }
        }
        internal double LastPanelMilliseconds
        {
            get
            {
                return lastPanelMilliseconds;
            }
        }
        internal double LastStartupMilliseconds
        {
            get
            {
                return lastStartupMilliseconds;
            }
        }
        internal double LastNewTaskMilliseconds
        {
            get
            {
                return lastNewTaskMilliseconds;
            }
        }
        internal string TimingText
        {
            get
            {
                return String.Join("\n", timing.ToArray());
            }
        }
        internal AgentPanel(MainWindow owner)
        {
            this.owner = owner;
            store = new AgentSessionStore(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent-history"), true);
            session = new AgentSession();
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            SetResourceReference(BackgroundProperty, "SurfaceBrush");
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });
            var header = new Grid
            {
                Margin = new Thickness(14, 8, 8, 8)
            };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            header.Children.Add(title);
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            Grid.SetColumn(actions, 1);
            header.Children.Add(actions);
            fresh = SmallButton("plus", "新任务 (Ctrl+Alt+N)", NewConversation);
            gear = SmallButton("sliders-horizontal", "模型配置 (Ctrl+Alt+M)", OpenSettings);
            var historyButton = SmallButton("files", "历史会话 (Ctrl+Alt+H)", ToggleHistory);
            var historyMenu = new ContextMenu();
            var cleanupLegacy = new MenuItem { Header = "清理旧版存档…" };
            cleanupLegacy.Click += delegate { ClearLegacyArchives(); };
            historyMenu.Items.Add(cleanupLegacy);
            historyButton.ContextMenu = historyMenu;
            actions.Children.Add(historyButton);
            actions.Children.Add(fresh);
            actions.Children.Add(SmallButton("panel-right", "切换并排 / 专注 (Ctrl+Alt+F)", delegate
            {
                owner.ToggleAgentFocus();
            }));
            actions.Children.Add(gear);
            actions.Children.Add(SmallButton("x", "收起 Agent (Ctrl+Alt+A)", delegate
            {
                owner.ToggleAgent();
            }));
            root.Children.Add(header);
            context.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetRow(context, 1);
            root.Children.Add(context);
            Grid.SetRow(history, 2);
            root.Children.Add(history);
            Grid.SetRow(conversation, 3);
            root.Children.Add(conversation);
            history.OpenRequested += OpenConversation;
            history.DeleteRequested += id => DeleteConversation(id, true);
            history.ClearRequested += delegate
            {
                ClearHistory(true);
            };
            history.MoreRequested += delegate
            {
                RefreshHistory(true);
            };
            history.SearchRequested += query =>
            {
                historyQuery = query;
                RefreshHistory();
            };
            var composer = new Border
            {
                Padding = new Thickness(12), BorderThickness = new Thickness(0, 1, 0, 0)
            };
            composer.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            var composerStack = new StackPanel();
            permission.ItemsSource = new[] { "只读", "每次确认", "自动编辑工作区" };
            permission.SelectedIndex = 1;
            permission.ToolTip = "权限由原生沙盒执行；自动编辑只作用于当前导入的工作区，单文件始终逐次确认。重启软件后恢复每次确认。";
            permission.SelectionChanged += delegate
            {
                var controller = sdk;
                if (controller != null) controller.Permission = (HarnessFilePermission)permission.SelectedIndex;
            };
            var stateRow = new DockPanel
            {
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(permission, Dock.Right);
            System.Windows.Automation.AutomationProperties.SetName(permission, "文件权限");
            stateRow.Children.Add(permission);
            DockPanel.SetDock(elapsed, Dock.Right);
            stateRow.Children.Add(elapsed);
            status.TextTrimming = TextTrimming.CharacterEllipsis;
            stateRow.Children.Add(status);
            composerStack.Children.Add(stateRow);
            composerStack.Children.Add(progress);
            composerStack.Children.Add(attachmentsBar);
            changesBox.Content = new ScrollViewer
            {
                Content = changesList, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            changesBox.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            composerStack.Children.Add(changesBox);
            input.AcceptsReturn = true;
            input.TextWrapping = TextWrapping.Wrap;
            input.MinHeight = 64;
            input.MaxHeight = 140;
            input.Padding = new Thickness(10, 8, 10, 8);
            input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            input.ToolTip = "Enter 发送，Shift+Enter 换行（Ctrl+Alt+S 停止）";
            input.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
            input.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            input.SetResourceReference(TextBox.CaretBrushProperty, "TextBrush");
            input.PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                // Tunneling keeps Enter for "send" even though the TextBox would
                // otherwise consume it to insert a newline.
                if (!IsSendShortcut(args.Key, Keyboard.Modifiers)) return;
                Observe(StartSend());
                args.Handled = true;
            };
            input.TextChanged += delegate
            {
                if (!loading && session != null)
                {
                    session.Draft = input.Text;
                    draftTimer.Stop();
                    draftTimer.Start();
                }
            };
            composerStack.Children.Add(input);
            var bottom = new Grid
            {
                Margin = new Thickness(0, 8, 0, 0)
            };
            bottom.ColumnDefinitions.Add(new ColumnDefinition());
            bottom.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            model.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            model.VerticalAlignment = VerticalAlignment.Center;
            bottom.Children.Add(model);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            Grid.SetColumn(buttons, 1);
            var attachmentButton = SmallButton("paperclip", "附加上下文文件", AttachFiles);
            attachmentButton.ContextMenu = BuildContextMenu();
            buttons.Children.Add(attachmentButton);
            buttons.Children.Add(SmallButton("chevron-down", "回到最新消息", conversation.JumpToLatest));
            retry.FontSize = 12;
            retry.Padding = new Thickness(8, 4, 8, 4);
            retry.Visibility = Visibility.Collapsed;
            retry.Click += delegate
            {
                if (!busy && lastPrompt != null)
                {
                    input.Text = "上次任务未完成，请先核对已有结果再继续：\n" + lastPrompt;
                    Observe(StartSend());
                }
            };
            buttons.Children.Add(retry);
            stop.Visibility = Visibility.Collapsed;
            stop.ToolTip = "停止 (Ctrl+Alt+S)";
            System.Windows.Automation.AutomationProperties.SetName(stop, "停止");
            stop.Click += delegate
            {
                Stop();
            };
            buttons.Children.Add(stop);
            send.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
            send.SetResourceReference(Control.ForegroundProperty, "SurfaceBrush");
            send.Click += delegate
            {
                Observe(StartSend());
            };
            buttons.Children.Add(send);
            bottom.Children.Add(buttons);
            composerStack.Children.Add(bottom);
            composer.Child = composerStack;
            Grid.SetRow(composer, 4);
            root.Children.Add(composer);
            Content = root;
            // Escape is panel-scoped: close the history drawer first, otherwise
            // hand focus back to the document instead of closing the panel.
            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key != Key.Escape) return;
                HandleEscape();
                args.Handled = true;
            };
            busyTimer.Tick += delegate
            {
                FlushText();
                if (busy)
                {
                    status.Text = phase + (step > 0 ? " · 第 " + step + " 步" : "");
                    elapsed.Text = Math.Max(0, (int)(DateTime.UtcNow - busySince).TotalSeconds) + " 秒";
                }
            };
            draftTimer.Tick += delegate
            {
                draftTimer.Stop();
                SaveSession();
            };
            RenderSession();
            RefreshModelState();
            UpdateContext();
            legacyContent = Content;
            conversation.EditRequested += text => { if (!busy) { input.Text = text; FocusInput(); } };
        }
        static Button SmallButton(string icon, string tooltip, Action action)
        {
            var button = Theme.Button(icon, "", action);
            button.ToolTip = tooltip;
            System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
            return button;
        }
        static string Describe(Exception error)
        {
            var root = error == null ? null : error.GetBaseException();
            string message = root == null ? "" : root.Message;
            return String.IsNullOrWhiteSpace(message) ? (root == null ? "未知错误" : root.GetType().Name) : ModelConfig.Redact(message, null);
        }
        // Enter sends; Shift+Enter keeps a newline. Ctrl+Enter stays supported
        // for muscle memory from the previous shortcut.
        internal static bool IsSendShortcut(Key key, ModifierKeys modifiers)
        {
            if (key != Key.Enter) return false;
            if ((modifiers & ModifierKeys.Shift) != 0 && (modifiers & ModifierKeys.Control) == 0) return false;
            return (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) == 0;
        }
        internal bool HistoryVisible
        {
            get
            {
                return history.Visibility == Visibility.Visible;
            }
        }
        internal TextBox Input
        {
            get
            {
                return input;
            }
        }
        internal void HandleEscape()
        {
            if (history.Visibility == Visibility.Visible)
            {
                history.Visibility = Visibility.Collapsed;
                FocusInput();
                return;
            }
            owner.FocusDocument();
        }
        internal void OpenModelSettings()
        {
            OpenSettings();
        }
        void RecordTiming(string label, double milliseconds)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + label + "\t" + milliseconds.ToString("F1") + " ms";
            timing.Add(line);
            if (timing.Count > 64) timing.RemoveAt(0);
            try
            {
                string directory = DiagnosticsDirectory;
                if (String.IsNullOrEmpty(directory)) return;
                if (!FileService.IsInsideRoot(AppDomain.CurrentDomain.BaseDirectory, Path.Combine(directory, "agent-native-timing.txt")))
                    throw new IOException("诊断目录必须位于程序目录内。");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "agent-native-timing.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                if (File.Exists(path)) File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                else File.WriteAllText(path, line + Environment.NewLine, new UTF8Encoding(true));
            }
            catch (Exception)
            {
                // Timing is diagnostic only and must never affect the editor.
            }
        }
        void AddTool(string text)
        {
            conversation.AddNotice(text);
        }
        internal Task<bool> RequestApprovalAsync(WriteProposal proposal, CancellationToken token)
        {
            var completion = new TaskCompletionSource<bool>();
            Dispatcher.Invoke(new Action(delegate
            {
                if (token.IsCancellationRequested)
                {
                    completion.TrySetCanceled();
                    return;
                }
                var card = new StackPanel
                {
                    Margin = new Thickness(0, 14, 0, 14)
                };
                card.Children.Add(AgentConversationView.Label("等待确认 · " + proposal.RelativePath, 14));
                card.Children.Add(AgentConversationView.Label("确认后写入磁盘；停止任务会取消尚未确认的修改。", 12));
                var diff = new RichTextBox
                {
                    Document = DiffDialog.BuildDocument(proposal), IsReadOnly = true, Height = 210, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(1), Padding = new Thickness(8)
                };
                diff.SetResourceReference(Control.BorderBrushProperty, "Rule");
                card.Children.Add(diff);
                var actions = new WrapPanel
                {
                    Margin = new Thickness(0, 8, 0, 0)
                };
                actions.Children.Add(Theme.Button(null, "拒绝", delegate
                {
                    completion.TrySetResult(false);
                }));
                actions.Children.Add(Theme.Button(null, "应用修改", delegate
                {
                    completion.TrySetResult(true);
                }));
                card.Children.Add(actions);
                conversation.AddApproval(card);
                var registration = token.Register(delegate
                {
                    completion.TrySetCanceled();
                });
                completion.Task.ContinueWith(delegate
                {
                    registration.Dispose();
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        actions.IsEnabled = false;
                        // After approving or rejecting, the composer is the next
                        // place the user types.
                        FocusInput();
                    }));
                });
            }));
            return completion.Task;
        }
        internal void SetDockLabel(bool focus)
        {
            title.Text = focus ? "Agent · 专注" : "Agent";
        }
        // Native controls reference theme brushes, so a theme change repaints
        // without touching the running conversation or the runtime process.
        internal void RefreshAppearance()
        {
        }
        internal void FocusInput()
        {
            input.Focus();
        }
        internal void SetVisible(bool visible)
        {
            bool hadFocus = IsKeyboardFocusWithin;
            Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
            {
                // Hiding the panel must not leave keyboard focus on a hidden
                // control; the document takes it back.
                if (hadFocus) owner.FocusDocument();
                return;
            }
            RefreshModelState();
            UpdateContext();
            if (legacyTesting || shuttingDown) return;
            var watch = Stopwatch.StartNew();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                lastPanelMilliseconds = watch.Elapsed.TotalMilliseconds;
                RecordTiming("打开面板首帧", lastPanelMilliseconds);
            }));
            // Without any file or workspace the runtime would initialize with an
            // app-data cwd; start it on the first send instead (scope binds then).
            bool hasAvailableScope = session.Root != null || session.CurrentFile != null ||
                owner.WorkspaceRoot != null || owner.Current != null && owner.Current.Path != null;
            if (AllowAutomaticStart && AutoStartOnReveal && hasAvailableScope) Observe(EnsureRuntimeAsync(Configuration(), true));
        }
        ModelConfig Configuration()
        {
            return ConfigProvider == null ? ModelConfig.Load(ModelConfig.Locate(AppDomain.CurrentDomain.BaseDirectory)) : ConfigProvider();
        }
        internal void RefreshModelState()
        {
            var config = Configuration();
            model.Text = config.IsValid ? config.ModelName : "设置模型以开始";
            model.ToolTip = model.Text;
        }
        // Bind the editor's current file/workspace the first time the runtime is
        // needed, exactly like the first send does. Without this the SDK would
        // initialize with the app data directory as cwd and the node native-tool
        // bridge would later disagree with the editor's pinned workspace.
        void BindInitialScope()
        {
            SynchronizeEditorScope();
            if (session.Root != null || session.CurrentFile != null) return;
            session.Root = owner.WorkspaceRoot;
            session.CurrentFile = owner.Current == null ? null : owner.Current.Path;
        }
        internal void UpdateContext()
        {
            SynchronizeEditorScope();
            string root = HasScope ? session.Root : owner.WorkspaceRoot;
            string file = HasScope ? session.CurrentFile : owner.Current == null ? null : owner.Current.Path;
            context.Text = root != null ? "工作区：" + Path.GetFileName(root) : file != null ? "单文件模式：" + Path.GetFileName(file) : "打开文件或导入工作区后开始";
            context.ToolTip = root ?? file;
        }
        void OpenSettings()
        {
            ModelSettingsDialog.Show(Window.GetWindow(this), ModelConfig.Locate(AppDomain.CurrentDomain.BaseDirectory), RefreshModelState);
        }
        string CurrentScopeSignature()
        {
            return ScopeSignature(session.Root, session.CurrentFile);
        }
        static string ConfigSignature(ModelConfig config)
        {
            return (config == null ? "" : config.BaseUrl) + "|" + (config == null ? "" : config.ModelName) + "|" + (config == null ? "" : config.ApiKey);
        }
        CancellationToken RuntimeCancellation
        {
            get
            {
                if (runtimeToken == null) runtimeToken = new CancellationTokenSource();
                return runtimeToken.Token;
            }
        }
        internal Task<bool> EnsureRuntimeAsync()
        {
            return EnsureRuntimeAsync(Configuration(), true);
        }
        internal Task<bool> EnsureRuntimeAsync(ModelConfig config, bool interactive)
        {
            if (runtimeReady) return Task.FromResult(true);
            if (config == null || !config.IsValid) return Task.FromResult(false);
            lock (runtimeLock)
            {
                if (runtimeStartTask == null) runtimeStartTask = StartRuntimeCoreAsync(config, interactive);
                return runtimeStartTask;
            }
        }
        async Task<bool> StartRuntimeCoreAsync(ModelConfig config, bool interactive)
        {
            runtimeStarting = true;
            var watch = Stopwatch.StartNew();
            bool ok = false;
            if (interactive)
            {
                phase = StartingText;
                status.Text = phase;
            }
            try
            {
                await EnsureControllerAsync(config).ConfigureAwait(true);
                if (sdk == null) throw new AgentException("模型配置不可用。");
                await sdk.EnsureStartedAsync(RuntimeCancellation).ConfigureAwait(true);
                runtimeReady = true;
                ok = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                lastRuntimeError = Describe(error);
                if (interactive)
                {
                    conversation.AddNotice("内置运行时未能启动：" + lastRuntimeError + "\n文件阅读与编辑仍可使用；稍后可再次发送任务重试。");
                    phase = "运行失败";
                    status.Text = "运行时未连接";
                }
            }
            finally
            {
                runtimeStarting = false;
                lastStartupMilliseconds = watch.Elapsed.TotalMilliseconds;
                RecordTiming(ok ? "启动内置运行时" : "启动内置运行时失败", lastStartupMilliseconds);
                if (ok && status.Text == StartingText) status.Text = phase = "待命";
                lock (runtimeLock)
                {
                    runtimeStartTask = null;
                }
            }
            return ok;
        }
        async Task EnsureControllerAsync(ModelConfig config)
        {
            BindInitialScope();
            var workspace = new AgentWorkspace(session.Root, session.CurrentFile);
            string scope = ScopeSignature(workspace.Root, workspace.CurrentFile);
            string signature = ConfigSignature(config);
            if (sdk != null && scope == controllerScope && signature == controllerConfig) return;
            if (sdk != null)
            {
                var previous = sdk;
                sdk = null;
                try
                {
                    previous.Dispose();
                }
                catch (Exception)
                {
                    // A retired controller may already own a stopped process.
                }
                // A brand-new conversation has no persisted SDK context; a
                // history conversation keeps its id so resume can be attempted.
                if (session.Entries.Count == 0) session.HarnessSessionId = "";
                resumedHarnessId = null;
            }
            string ownerSession = session.Id;
            var controller = new SdkAgentController(config, workspace, OpenText, OpenDirty, new UiApprover(this),
                proposal => ApplyOwnedEditorWrite(proposal, ownerSession), session,
                null, false, RuntimeDataDirectory);
            controller.Permission = (HarnessFilePermission)permission.SelectedIndex;
            controller.Activity += OnActivity;
            sdk = controller;
            controllerScope = scope;
            controllerConfig = signature;
            // The runtime process is shared, so an already warm runtime just
            // needs to be attached to the replacement controller.
            if (runtimeReady) await controller.EnsureStartedAsync(RuntimeCancellation).ConfigureAwait(true);
        }
        internal Task PrepareControllerAsync()
        {
            // Test seam: create the controller (and its bound workspace) without
            // starting the node process, so tests can assert scope pre-binding.
            return EnsureControllerAsync(Configuration());
        }
        internal Task PrewarmAsync()
        {
            // Startup prewarm is disabled by default (idle memory). When enabled,
            // a throwaway controller wakes the process-level shared runtime and is
            // disposed immediately; the real controller is created on first send.
            if (!AllowPrewarm || legacyTesting || shuttingDown || busy || runtimeReady) return Task.FromResult(0);
            var config = Configuration();
            if (!config.IsValid) return Task.FromResult(0);
            return PrewarmCoreAsync(config);
        }
        async Task PrewarmCoreAsync(ModelConfig config)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await SdkAgentController.PrewarmAsync(config, RuntimeCancellation).ConfigureAwait(true);
                runtimeReady = true;
                lastStartupMilliseconds = watch.Elapsed.TotalMilliseconds;
                RecordTiming("预热运行时", lastStartupMilliseconds);
            }
            catch (Exception)
            {
                // Prewarm failures stay silent; the next real send retries.
                runtimeReady = false;
            }
        }
        internal void ToggleHistory()
        {
            if (busy)
            {
                conversation.AddNotice("停止当前任务后可切换历史会话。");
                return;
            }
            RefreshHistory();
            history.Visibility = history.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (history.Visibility == Visibility.Visible) history.FocusSearch();
            else FocusInput();
        }
        void RefreshHistory(bool more = false)
        {
            try
            {
                if (!more) historyItems.Clear();
                historyItems.AddRange(store.List(historyItems.Count, historyQuery));
                history.SetSessions(historyItems, store.HasMore);
                if (!String.IsNullOrEmpty(store.Notice)) conversation.AddNotice(store.Notice);
            }
            catch (Exception error)
            {
                conversation.AddNotice("历史无法读取：" + error.Message);
            }
        }
        void Adopt(AgentSession value)
        {
            if (!legacyTesting) RetireSdkController();
            session = value ?? new AgentSession();
            lock (pendingLock) pendingText.Clear();
        }
        internal void NewConversation()
        {
            if (busy)
            {
                conversation.AddNotice("当前任务进行中，先停止或等待完成后再新建任务。");
                return;
            }
            if (!SaveSession()) return;
            newSessionTask = Task.FromResult(0);
            Adopt(new AgentSession());
            attachments.Clear();
            ClearQuotedSelection();
            RefreshAttachments();
            changes.Clear();
            changesList.Children.Clear();
            changesBox.Visibility = Visibility.Collapsed;
            lastPrompt = null;
            elapsed.Text = "";
            retry.Visibility = Visibility.Collapsed;
            history.Visibility = Visibility.Collapsed;
            resumedHarnessId = null;
            RenderSession();
            UpdateContext();
            FocusInput();
            if (legacyTesting || shuttingDown) return;
            var watch = Stopwatch.StartNew();
            lastNewTaskMilliseconds = watch.Elapsed.TotalMilliseconds;
            RecordTiming("新任务界面", lastNewTaskMilliseconds);
            // Create its SDK session lazily on first send, so an empty new task
            // costs no IPC or unused agent allocation.
        }
        void StartNewRuntimeSession(Stopwatch watch)
        {
            if (sdk == null || !runtimeReady) return;
            try
            {
                var task = sdk.NewSessionAsync(RuntimeCancellation);
                newSessionTask = task.ContinueWith(delegate(Task<string> result)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        lastNewTaskMilliseconds = watch.Elapsed.TotalMilliseconds;
                        if (result.Status == TaskStatus.RanToCompletion)
                        {
                            session.HarnessSessionId = result.Result ?? "";
                            resumedHarnessId = session.HarnessSessionId;
                            RecordTiming("新任务", lastNewTaskMilliseconds);
                            SaveSession();
                        }
                        else
                        {
                            RecordTiming("新任务失败", lastNewTaskMilliseconds);
                            conversation.AddNotice("新会话创建失败：" + (result.IsFaulted ? Describe(result.Exception.GetBaseException()) : "已取消") + "；首次发送时会重试。");
                        }
                    }));
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
            catch (Exception error)
            {
                conversation.AddNotice("新会话创建失败：" + Describe(error) + "；首次发送时会重试。");
            }
        }
        internal Task NewConversationAsync()
        {
            NewConversation();
            return newSessionTask;
        }
        internal void OpenConversation(string id)
        {
            if (busy)
            {
                conversation.AddNotice("当前任务进行中，先停止或等待完成后再打开历史会话。");
                return;
            }
            if (!SaveSession()) return;
            resumeTask = Task.FromResult(0);
            try
            {
                Adopt(store.Load(id));
                history.Visibility = Visibility.Collapsed;
                ResetExtras(false);
                RenderSession();
                UpdateContext();
                PrepareHistoryResume();
                FocusInput();
            }
            catch (Exception error)
            {
                conversation.AddNotice("无法打开历史：" + error.Message);
            }
        }
        void PrepareHistoryResume()
        {
            resumedHarnessId = null;
            if (legacyTesting) return;
            string id = session.HarnessSessionId;
            if (String.IsNullOrEmpty(id))
            {
                if (session.Entries.Count > 0) conversation.AddNotice("这份历史没有内置运行时会话，将作为新上下文继续（不会恢复旧模型上下文）。");
                return;
            }
            if (!runtimeReady || sdk == null || CurrentScopeSignature() != controllerScope)
            {
                conversation.AddNotice("历史会话已载入；首次发送前会恢复内置运行时会话。");
                return;
            }
            resumeTask = ResumeNowAsync(id);
        }
        internal Task OpenConversationAsync(string id)
        {
            OpenConversation(id);
            return resumeTask;
        }
        async Task ResumeNowAsync(string id)
        {
            try
            {
                phase = "正在恢复会话";
                status.Text = phase;
                await sdk.ResumeAsync(id, RuntimeCancellation).ConfigureAwait(true);
                resumedHarnessId = id;
                conversation.AddNotice("已恢复历史会话的内置上下文，可以继续发送。");
                SaveSession();
            }
            catch (Exception error)
            {
                if (session.HarnessSessionId == id) session.HarnessSessionId = "";
                conversation.AddNotice("无法恢复历史会话上下文：" + Describe(error) + "；将作为新上下文继续。");
                SaveSession();
            }
            finally
            {
                if (status.Text == "正在恢复会话") status.Text = StateLabel(session.State);
            }
        }
        internal void DeleteConversation(string id, bool confirm)
        {
            if (busy) return;
            if (confirm && MessageBox.Show(Window.GetWindow(this), "删除这份本地历史？文件修改不会撤销。", "删除会话", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            try
            {
                string harnessId = store.Load(id).HarnessSessionId;
                if (!legacyTesting)
                {
                    RetireSdkController();
                    QueueSessionMaintenance(() => SdkAgentController.DeleteTemporarySessionAsync(harnessId));
                }
                store.Delete(id);
                if (session.Id == id)
                {
                    Adopt(new AgentSession());
                    ResetExtras();
                    RenderSession();
                    UpdateContext();
                }
                RefreshHistory();
            }
            catch (Exception error)
            {
                conversation.AddNotice("历史删除失败：" + error.Message);
            }
        }
        internal void ClearHistory(bool confirm)
        {
            if (confirm && MessageBox.Show(Window.GetWindow(this), "清空全部本地对话历史？文件修改不会撤销。", "清空历史", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            if (busy)
            {
                // Clearing wins over a running turn or a pending approval: cancel
                // first, wait for the turn to settle, then drop every session.
                var turn = activeTask;
                if (cancellation != null) cancellation.Cancel();
                if (sdk != null) Observe(ClearAfterTurnAsync(turn, sdk));
                else Observe(ClearAfterTurnAsync(turn, null));
                return;
            }
            ClearHistoryNow();
        }
        async Task ClearAfterTurnAsync(Task turn, SdkAgentController active)
        {
            try
            {
                if (active != null)
                {
                    try { await active.CancelCurrentTurnAsync().ConfigureAwait(true); }
                    catch (Exception) { /* the turn still settles below */ }
                }
                if (turn != null) await turn.ConfigureAwait(true);
            }
            catch (Exception)
            {
                // A failed turn must not keep the old context alive.
            }
            ClearHistoryNow();
        }
        void ClearHistoryNow()
        {
            try
            {
                if (!legacyTesting)
                {
                    RetireSdkController();
                    QueueSessionMaintenance(SdkAgentController.ClearTemporarySessionsAsync);
                }
                store.Clear();
                Adopt(new AgentSession());
                ResetExtras();
                RenderSession();
                UpdateContext();
                RefreshHistory();
            }
            catch (Exception error)
            {
                conversation.AddNotice("历史清空失败：" + error.Message);
            }
        }
        bool SaveSession()
        {
            if (session == null || session.Entries.Count == 0 && String.IsNullOrWhiteSpace(session.Draft) && attachments.Count == 0) return true;
            try
            {
                store.Save(session, Configuration().ApiKey);
                return true;
            }
            catch (Exception error)
            {
                conversation.AddNotice("历史未保存，当前对话已保留：" + error.Message);
                return false;
            }
        }
        void ResetExtras(bool clearAttachments = true)
        {
            if (clearAttachments) attachments.Clear();
            ClearQuotedSelection();
            RefreshAttachments();
            changes.Clear();
            changesList.Children.Clear();
            changesBox.Visibility = Visibility.Collapsed;
        }
        void RenderSession()
        {
            loading = true;
            input.Text = session.Draft ?? "";
            loading = false;
            conversation.Render(session);
            phase = StateLabel(session.State);
            status.Text = phase;
            lastPrompt = session.History().Where(message => Convert.ToString(message["role"]) == "user").Select(message => Convert.ToString(message["content"])).LastOrDefault();
            retry.Visibility = lastPrompt != null && new[]
            {
                "failed", "stopped", "limited"
            }.Contains(session.State) ? Visibility.Visible : Visibility.Collapsed;
        }
        static string StateLabel(string state)
        {
            switch (state)
            {
                case "requesting": return "等待模型";
                case "streaming": return "正在回复";
                case "assistant-start": return "正在回复";
                case "tool": return "正在执行工具";
                case "approval": return "等待确认修改";
                case "completed": return "任务完成";
                case "failed": return "运行失败";
                case "stopped": return "已停止";
                case "limited": return "达到步骤上限";
                default: return "待命";
            }
        }
        void Observe(Task task)
        {
            if (task == null) return;
            task.ContinueWith(result => Dispatcher.BeginInvoke(new Action(() => conversation.AddNotice("操作失败：" + result.Exception.GetBaseException().Message))), TaskContinuationOptions.OnlyOnFaulted);
        }
        Task StartSend()
        {
            if (busy) return activeTask;
            activeTask = SendAsync();
            return activeTask;
        }
        internal Task SubmitAsync(string text)
        {
            if (busy) return activeTask;
            input.Text = text;
            return StartSend();
        }
        internal void UseTestServices(string historyPath, Func<ModelConfig> config, Func<ModelConfig, IAgentTransport> factory)
        {
            legacyTesting = true;
            history.SetTemporary(false);
            Content = legacyContent;
            HistoryDirectory = historyPath;
            store = new AgentSessionStore(historyPath);
            ConfigProvider = config;
            TransportFactory = factory;
            session = new AgentSession();
            RenderSession();
            RefreshModelState();
        }
        // E2E entry point: point history at a temporary directory, inject a local
        // mock model and let the harness drive runtime startup explicitly.
        internal void UseTestEnvironment(string historyDirectory, Func<ModelConfig> config)
        {
            UseTestEnvironment(historyDirectory, null, config);
        }
        internal void UseTestEnvironment(string historyDirectory, string appDataDirectory, Func<ModelConfig> config)
        {            legacyTesting = false;
            history.SetTemporary(false);
            Content = legacyContent;
            HistoryDirectory = historyDirectory;
            AppDataDirectory = appDataDirectory ?? Path.Combine(Path.GetDirectoryName(historyDirectory), "runtime", Guid.NewGuid().ToString("N"));
            store = new AgentSessionStore(historyDirectory);
            ConfigProvider = config;
            AutoStartOnReveal = false;
            Adopt(new AgentSession());
            RenderSession();
            RefreshModelState();
        }
        internal Task<bool> ApproveAsync(WriteProposal proposal)
        {
            return RequestApprovalAsync(proposal, CancellationToken.None);
        }
        void FlushText()
        {
            string delta;
            lock (pendingLock)
            {
                delta = pendingText.ToString();
                pendingText.Clear();
            }
            if (delta.Length > 0)
            {
                phase = "正在回复";
                conversation.AppendDelta(delta);
            }
        }
        void OnActivity(AgentActivity activity)
        {
            if (activity == null) return;
            if (!legacyTesting && activity.SessionId != session.HarnessSessionId) return;
            if (activity.Delta != null)
            {
                lock (pendingLock) pendingText.Append(activity.Delta);
                return;
            }
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.Invoke(new Action(delegate
            {
                if (!legacyTesting && activity.SessionId != session.HarnessSessionId) return;
                FlushText();
                step = activity.Step;
                if (activity.State == "title")
                {
                    if (!String.IsNullOrWhiteSpace(activity.Text)) session.Title = activity.Text;
                    return;
                }
                if (activity.State == "tool-result") conversation.ToolFinished(activity.CallId, activity.Text);
                else if (activity.State == "assistant-final") conversation.CompleteAssistant(activity.Text);
                else
                {
                    phase = StateLabel(activity.State);
                    if (activity.State == "requesting" || activity.State == "assistant-start") conversation.BeginStep();
                    if (activity.State == "tool") conversation.ToolStarted(activity.CallId, activity.Text);
                }
                status.Text = phase + (step > 0 ? " · 第 " + step + " 步" : "");
            }));
        }
        void BeginBusy(string text)
        {
            cancellation = new CancellationTokenSource();
            busy = true;
            busySince = DateTime.UtcNow;
            busyTimer.Start();
            fresh.IsEnabled = gear.IsEnabled = send.IsEnabled = false;
            stop.Visibility = progress.Visibility = Visibility.Visible;
            retry.Visibility = Visibility.Collapsed;
            lastPrompt = text;
            input.Text = "";
            session.Draft = "";
            ClearQuotedSelection();
            RefreshAttachments();
            conversation.AddMessage("user", text);
        }
        void EndBusy()
        {
            busy = false;
            busyTimer.Stop();
            FlushText();
            fresh.IsEnabled = gear.IsEnabled = send.IsEnabled = true;
            stop.Visibility = progress.Visibility = Visibility.Collapsed;
            elapsed.Text = Math.Max(0, (int)(DateTime.UtcNow - busySince).TotalSeconds) + " 秒";
            if (cancellation != null)
            {
                cancellation.Dispose();
                cancellation = null;
            }
        }
        async Task SendAsync()
        {
            string text = input.Text.Trim();
            if (text.Length == 0 || shuttingDown) return;
            var config = Configuration();
            if (!config.IsValid)
            {
                OpenSettings();
                return;
            }
            if (!HasScope)
            {
                session.Root = owner.WorkspaceRoot;
                session.CurrentFile = owner.Current == null ? null : owner.Current.Path;
            }
            if (session.Root == null && session.CurrentFile == null)
            {
                conversation.AddNotice("请先打开已保存的文件或导入工作区。");
                return;
            }
            if (session.Root != null && !Directory.Exists(session.Root) || session.Root == null && !File.Exists(session.CurrentFile))
            {
                conversation.AddNotice("会话的文件范围已不存在，请新建对话选择当前范围。");
                return;
            }
            if (session.Title == "新对话" || String.IsNullOrWhiteSpace(session.Title))
            {
                string first = text.Split('\n')[0];
                session.Title = first.Length > 48 ? first.Substring(0, 48) : first;
            }
            if (legacyTesting)
            {
                await SendLegacyAsync(text, config).ConfigureAwait(true);
                return;
            }
            await SendRuntimeAsync(text, config).ConfigureAwait(true);
        }
        async Task SendLegacyAsync(string text, ModelConfig config)
        {
            string prompt = ComposePrompt(text);
            var workspace = new AgentWorkspace(session.Root, session.CurrentFile);
            IAgentTransport transport = TransportFactory == null ? new AgentClient(config) : TransportFactory(config);
            var controller = new AgentController(config, workspace, new AgentTools(workspace, OpenText, OpenDirty), transport, new UiApprover(this), ApplyEditorWrite, session);
            controller.Activity += OnActivity;
            controller.Checkpoint += delegate
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.Invoke(new Action(delegate
                {
                    SaveSession();
                }));
            };
            BeginBusy(text);
            try
            {
                await controller.AskAsync(prompt, cancellation.Token, null).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                conversation.AddNotice("请求失败：" + ModelConfig.Redact(error.Message, config.ApiKey));
            }
            finally
            {
                EndBusy();
                var disposable = transport as IDisposable;
                if (disposable != null) disposable.Dispose();
                RenderSession();
                SaveSession();
                UpdateContext();
            }
        }
        async Task SendRuntimeAsync(string text, ModelConfig config)
        {
            string prompt = ComposePrompt(text);
            BeginBusy(text);
            stopRequested = false;
            var token = cancellation.Token;
            try
            {
                // The runtime process is shared and persistent; a controller for
                // the current scope/config can be recreated without a cold start.
                await sessionMaintenance.ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                await EnsureControllerAsync(config).ConfigureAwait(true);
                bool started = await EnsureRuntimeAsync(config, true).ConfigureAwait(true);
                if (!started || sdk == null) throw new AgentException(String.IsNullOrEmpty(lastRuntimeError) ? "内置运行时不可用。" : lastRuntimeError);
                if (String.IsNullOrEmpty(session.HarnessSessionId))
                {
                    // A fresh conversation: the SDK creates its own session id.
                    string id = await sdk.NewSessionAsync(token).ConfigureAwait(true);
                    session.HarnessSessionId = id ?? "";
                    resumedHarnessId = session.HarnessSessionId;
                }
                else if (resumedHarnessId != session.HarnessSessionId)
                {
                    // History was opened on a cold runtime: restore the persisted
                    // model context, and fall back to a new context on failure
                    // instead of pretending the old turns are remembered.
                    phase = "正在恢复会话";
                    status.Text = phase;
                    bool restored = false;
                    try
                    {
                        await sdk.ResumeAsync(session.HarnessSessionId, token).ConfigureAwait(true);
                        restored = true;
                        resumedHarnessId = session.HarnessSessionId;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        conversation.AddNotice("无法恢复历史会话上下文：" + Describe(error) + "；将作为新上下文继续。");
                    }
                    if (restored)
                    {
                        conversation.AddNotice("已恢复历史会话的内置上下文。");
                    }
                    else
                    {
                        session.HarnessSessionId = "";
                        resumedHarnessId = null;
                        string id = await sdk.NewSessionAsync(token).ConfigureAwait(true);
                        session.HarnessSessionId = id ?? "";
                        resumedHarnessId = session.HarnessSessionId;
                    }
                }
                await sdk.AskAsync(prompt, token, attachments.Count == 0 ? null : new List<string>(attachments)).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                if (!stopRequested) conversation.AddNotice("本次任务已取消。");
            }
            catch (Exception error)
            {
                lastRuntimeError = Describe(error);
                conversation.AddNotice("请求失败：" + lastRuntimeError);
                phase = "运行失败";
                session.State = "failed";
            }
            finally
            {
                EndBusy();
                // SDK events already updated the visible rows. Re-parsing the
                // whole conversation after every turn made long chats costly.
                phase = stopRequested ? "已停止" : StateLabel(session.State);
                status.Text = phase;
                retry.Visibility = new[] { "failed", "stopped", "limited" }.Contains(session.State) ? Visibility.Visible : Visibility.Collapsed;
                SaveSession();
                UpdateContext();
            }
        }
        internal void Stop()
        {
            stopRequested = true;
            phase = "正在停止";
            status.Text = phase;
            if (legacyTesting || sdk == null)
            {
                if (cancellation != null) cancellation.Cancel();
                return;
            }
            Observe(CancelNativeTurnAsync(sdk, cancellation));
        }
        async Task CancelNativeTurnAsync(SdkAgentController controller, CancellationTokenSource turn)
        {
            try { await controller.CancelCurrentTurnAsync().ConfigureAwait(true); }
            catch (Exception error)
            {
                lastRuntimeError = Describe(error);
                runtimeReady = controller.IsRunning;
                conversation.AddNotice(lastRuntimeError);
            }
            finally
            {
                try { if (turn != null) turn.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
        internal async Task<bool> ShutdownAsync()
        {
            shuttingDown = true;
            draftTimer.Stop();
            // Cancel the running turn without triggering a second StopAsync; the
            // shared runtime is stopped exactly once below.
            stopRequested = true;
            if (cancellation != null) cancellation.Cancel();
            await activeTask;
            busyTimer.Stop();
            if (!legacyTesting)
            {
                var controller = sdk;
                sdk = null;
                runtimeReady = false;
                runtimeStarting = false;
                if (controller != null)
                {
                    try
                    {
                        await controller.StopAsync();
                    }
                    catch (Exception error)
                    {
                        if (!(error is NotImplementedException)) lastRuntimeError = Describe(error);
                    }
                    try
                    {
                        controller.Dispose();
                    }
                    catch (Exception)
                    {
                        // Disposal is best effort during application shutdown.
                    }
                }
                else
                {
                    // A prewarmed shared runtime may exist without a controller.
                    try { await SdkAgentController.StopSharedAsync(); }
                    catch (Exception) { }
                }
                if (runtimeToken != null)
                {
                    runtimeToken.Cancel();
                    runtimeToken.Dispose();
                    runtimeToken = null;
                }
            }
            await sessionMaintenance.ConfigureAwait(true);
            await ReleaseRuntimeDirectoryAsync().ConfigureAwait(true);
            bool saved = SaveSession();
            if (!saved) shuttingDown = false;
            return saved;
        }
        void AttachFiles()
        {
            var files = Dialogs.PickFiles(Window.GetWindow(this), true);
            if (files == null || files.Length == 0) return;
            foreach (string path in files) TryAttachFile(path);
        }
        internal bool TryAttachFile(string path)
        {
            var workspace = HasScope ? new AgentWorkspace(session.Root, session.CurrentFile)
            : new AgentWorkspace(owner.WorkspaceRoot, owner.Current == null ? null : owner.Current.Path);
            try
            {
                string full = workspace.Resolve(path, false);
                if (Directory.Exists(full)) return false;
                if (attachments.Count >= 8)
                {
                    AddTool("最多附加 8 个文件，请先移除不需要的附件。");
                    return false;
                }
                if (!HasScope)
                {
                    session.Root = workspace.Root;
                    session.CurrentFile = workspace.CurrentFile;
                }
                string relative = workspace.Relative(full);
                if (!attachments.Contains(relative)) attachments.Add(relative);
                session.UpdatedUtc = DateTime.UtcNow;
                if (session.Entries.Count == 0) session.Title = "文件上下文 · " + Path.GetFileName(full);
                RefreshAttachments();
                SaveSession();
                UpdateContext();
                return true;
            }
            catch (Exception error)
            {
                AddTool("无法附加：" + error.Message);
                return false;
            }
        }
        void RefreshAttachments()
        {
            attachmentsBar.Children.Clear();
            foreach (string relative in attachments)
            {
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2, 3, 2), Margin = new Thickness(0, 0, 4, 4)
                };
                chip.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal
                };
                var label = new TextBlock
                {
                    Text = relative, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = relative
                };
                row.Children.Add(label);
                string target = relative;
                var remove = Theme.Button(null, "×", delegate
                {
                    attachments.Remove(target);
                    RefreshAttachments();
                });
                remove.Width = 18;
                remove.Height = 18;
                remove.MinHeight = 0;
                remove.Padding = new Thickness(0);
                remove.FontSize = 11;
                remove.Margin = new Thickness(4, 0, 0, 0);
                remove.ToolTip = "移除上下文";
                row.Children.Add(remove);
                chip.Child = row;
                attachmentsBar.Children.Add(chip);
            }
            AddSelectionChip();
            attachmentsBar.Visibility = attachmentsBar.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        void AddChange(WriteProposal proposal)
        {
            if (!revertingChange) changes.Record(proposal);
            RefreshChanges();
        }
        // Entry point used by SdkAgentController and the legacy loop when the
        // approved write has reached disk: refresh the buffer in place.
        internal void ApplyEditorWrite(WriteProposal proposal)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(new Action(delegate
                {
                    ApplyEditorWrite(proposal);
                }));
                return;
            }
            if (proposal == null) return;
            AddChange(proposal);
            string message = DocumentSync.Apply(owner, proposal);
            if (!String.IsNullOrEmpty(message)) conversation.AddNotice(message);
        }
        string OpenText(string path)
        {
            if (!Dispatcher.CheckAccess()) return (string)Dispatcher.Invoke(new Func<string>(() => OpenText(path)));
            var doc = FindOpen(path);
            if (doc == null || doc.Kind != DocumentKind.Text) return null;
            TextDocument buffer;
            return owner.buffers.TryGetValue(doc, out buffer) ? buffer.Text : doc.Text;
        }
        bool OpenDirty(string path)
        {
            if (!Dispatcher.CheckAccess()) return (bool)Dispatcher.Invoke(new Func<bool>(() => OpenDirty(path)));
            var doc = FindOpen(path);
            return doc != null && (doc.IsDirty || owner.richEdit.IsEditing(doc));
        }
        Document FindOpen(string path)
        {
            if (String.IsNullOrEmpty(path)) return null;
            return owner.Session.Documents.FirstOrDefault(doc => doc.Path != null && String.Equals(Path.GetFullPath(doc.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        }
        sealed class UiApprover : IAsyncWriteApprover
        {
            readonly AgentPanel panel;
            internal UiApprover(AgentPanel panel)
            {
                this.panel = panel;
            }
            public bool Approve(WriteProposal proposal)
            {
                return false;
            }
            public Task<bool> ApproveAsync(WriteProposal proposal, CancellationToken token)
            {
                return panel.RequestApprovalAsync(proposal, token);
            }
        }
    }
}
