using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed class HarnessPanel : UserControl
    {
        readonly MainWindow owner;
        readonly Grid page = new Grid();
        readonly TextBlock state = new TextBlock { Text = "待命", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        readonly TextBlock scope = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock invitation = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, FontSize = 15 };
        readonly Button focus;
        readonly ComboBox permission = new ComboBox { Width = 170, FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
        readonly HarnessRuntime runtime = new HarnessRuntime();
        readonly SemaphoreSlim lifecycle = new SemaphoreSlim(1, 1);
        readonly string home = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "harness-data");
        HarnessNativeTools tools;
        WebView2 view;
        Uri origin;
        Task startup;
        bool disposed;
        string lastNativeContext;
        string runDirectory;
        readonly System.Windows.Threading.DispatcherTimer contextTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        internal static bool AllowAutomaticStart = true;
        // The WebView page is retired from the production path; the counter lets
        // tests prove the native panel never constructs one.
        internal static int Constructed;
        internal Func<ModelConfig> ConfigurationProvider { get; set; }
        internal string DataDirectory { get; set; }
        internal bool HasStarted { get { return startup != null; } }
        internal WebView2 Browser { get { return view; } }
        internal Uri Address { get { return origin; } }
        internal string Status { get { return state.Text; } }
        internal string LastDiagnostic { get { return runtime.LastDiagnostic; } }
        internal bool Running { get { return runtime.IsRunning; } }
        internal string RunDirectory { get { return runDirectory; } }
        internal int BlockedResources { get; private set; }
        internal HarnessFilePermission FilePermission
        {
            get { return (HarnessFilePermission)permission.SelectedIndex; }
            set { permission.SelectedIndex = (int)value; }
        }

        internal HarnessPanel(MainWindow owner)
        {
            Constructed++;
            this.owner = owner;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            SetResourceReference(BackgroundProperty, "SurfaceBrush");
            var root = new DockPanel();
            Content = root;
            var header = new Grid { Margin = new Thickness(10, 6, 6, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock { Text = "轻页 Agent", FontWeight = FontWeights.SemiBold, FontSize = 13 });
            state.FontSize = 11;
            state.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            labels.Children.Add(state);
            header.Children.Add(labels);
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            focus = Theme.Button(null, "专注", owner.ToggleAgentFocus);
            actions.Children.Add(focus);
            var settings = Theme.Button("sliders-horizontal", "", delegate {
                ModelSettingsDialog.Show(owner, ModelConfig.Locate(AppDomain.CurrentDomain.BaseDirectory), delegate { Observe(RestartAsync()); });
            });
            settings.ToolTip = "模型配置";
            actions.Children.Add(settings);
            var restart = Theme.Button(null, "新任务", delegate { Observe(RestartAsync()); });
            restart.ToolTip = "开始新的临时对话；右键可删除旧版已保存历史。已写入文件保留";
            restart.ContextMenu = new ContextMenu();
            restart.ContextMenu.Items.Add(Theme.Menu("开始新任务", delegate { Observe(RestartAsync()); }));
            restart.ContextMenu.Items.Add(Theme.Menu("删除旧版已保存历史…", delegate {
                if (MessageBox.Show(owner, "删除旧版 Agent 保存的全部对话、草稿和附件引用？此操作不可撤销，不影响文档文件。", "删除旧版历史", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                try {
                    new AgentSessionStore(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent-history")).Clear();
                    owner.SetNotice("旧版已保存对话已删除。");
                } catch (Exception error) { owner.SetNotice("旧版历史未能全部删除：" + error.Message); }
            }));
            actions.Children.Add(restart);
            actions.Children.Add(Theme.Button("x", "", owner.ToggleAgent));
            Grid.SetColumn(actions, 1);
            header.Children.Add(actions);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            scope.Margin = new Thickness(14, 0, 14, 8);
            scope.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            DockPanel.SetDock(scope, Dock.Top);
            root.Children.Add(scope);
            var permissions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 10, 8) };
            permissions.Children.Add(new TextBlock { Text = "文件权限", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            permission.ItemsSource = new[] { "只读", "每次确认", "自动编辑工作区" };
            permission.SelectedIndex = 1;
            permission.ToolTip = "权限由原生沙盒执行；自动编辑只作用于当前导入的工作区，单文件始终逐次确认。重启软件后恢复每次确认。";
            permission.SelectionChanged += delegate { if (tools != null) tools.Permission = (HarnessFilePermission)permission.SelectedIndex; };
            permissions.Children.Add(permission);
            permissions.Children.Add(new TextBlock { Text = "仅本次 · 不留档", FontSize = 11, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(permissions, Dock.Top);
            root.Children.Add(permissions);
            var line = Theme.Line();
            DockPanel.SetDock(line, Dock.Top);
            root.Children.Add(line);
            root.Children.Add(page);
            page.Children.Add(invitation);
            invitation.Text = "轻页 Agent\n\n在右侧直接描述任务；读取当前文件或工作区需要你在卡片里确认。\n\n打开文件或导入工作区，配置模型后即可开始。";
            runtime.Failed += message => Dispatcher.BeginInvoke(new Action(delegate { if (!disposed) ShowFailure(message); }));
            runtime.NativeRequest = NativeRequestAsync;
            contextTimer.Tick += delegate { contextTimer.Stop(); Observe(RestartAsync()); };
            UpdateContext();
        }

        internal void UpdateContext()
        {
            string file = owner.Current == null ? null : owner.Current.Path;
            scope.Text = owner.WorkspaceRoot != null ? "工作区 · " + Path.GetFileName(owner.WorkspaceRoot) : file != null ? "当前文件 · " + Path.GetFileName(file) : "打开文件或导入工作区后开始";
            scope.ToolTip = (owner.WorkspaceRoot ?? file ?? "") + "\n对话仅本次有效。切换文件范围会清空对话并恢复每次确认。";
            string signature = owner.WorkspaceRoot ?? file ?? "";
            if (signature != lastNativeContext) {
                bool changed = lastNativeContext != null;
                lastNativeContext = signature;
                if (changed) permission.SelectedIndex = 1;
                if (changed && runtime.IsRunning && !disposed) {
                    if (tools != null) tools.Permission = HarnessFilePermission.ReadOnly;
                    if (view != null) view.Visibility = Visibility.Collapsed;
                    invitation.Text = "文件范围已变化，正在清空上个范围的临时对话…";
                    invitation.Visibility = Visibility.Visible;
                    contextTimer.Stop();
                    contextTimer.Start();
                }
            }
        }

        internal void SetDockLabel(bool focused)
        {
            var text = ((StackPanel)focus.Content).Children.OfType<TextBlock>().First();
            text.Text = focused ? "并排" : "专注";
        }

        internal void Reveal()
        {
            UpdateContext();
            if (AllowAutomaticStart && startup == null && !runtime.IsRunning && !disposed) {
                startup = StartAsync();
                Observe(startup);
            }
        }

        void Observe(Task task)
        {
            task.ContinueWith(result => Dispatcher.BeginInvoke(new Action(delegate {
                if (!disposed) ShowFailure(result.Exception.GetBaseException().Message);
            })), TaskContinuationOptions.OnlyOnFaulted);
        }

        void ShowFailure(string message)
        {
            state.Text = "未连接";
            invitation.Text = message + "\n\n检查模型配置或点击重启。文件阅读编辑仍可使用。";
            invitation.Visibility = Visibility.Visible;
            if (view != null) view.Visibility = Visibility.Collapsed;
        }

        internal async Task StartAsync()
        {
            await lifecycle.WaitAsync();
            try { if (!disposed) await StartCoreAsync(); }
            finally { lifecycle.Release(); }
        }

        async Task StartCoreAsync()
        {
            state.Text = "正在启动内置运行时…";
            var model = ConfigurationProvider == null ? ModelConfig.Load(ModelConfig.Locate(AppDomain.CurrentDomain.BaseDirectory)) : ConfigurationProvider();
            string data = Path.Combine(DataDirectory ?? home, Guid.NewGuid().ToString("N"));
            HarnessBrowserCleanup.ValidateRunDirectory(DataDirectory ?? home, data);
            runDirectory = data;
            string runtimeDirectory = HarnessRuntime.Locate(AppDomain.CurrentDomain.BaseDirectory);
            tools = new HarnessNativeTools(data, () => new AgentWorkspace(owner.WorkspaceRoot, owner.Current == null ? null : owner.Current.Path, runtimeDirectory, DataDirectory ?? home), OpenText, OpenDirty, ReloadOpenFile, false);
            tools.Permission = (HarnessFilePermission)permission.SelectedIndex;
            origin = await runtime.StartAsync(runtimeDirectory, data, model);
            if (disposed) return;
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(data, "browser"),
                new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-sync --disable-features=msEdgeSidebarV2"));
            if (disposed) return;
            view = new WebView2 { CreationProperties = new CoreWebView2CreationProperties { IsInPrivateModeEnabled = true } };
            view.DefaultBackgroundColor = AgentSkin.Background(owner);
            page.Children.Insert(0, view);
            await view.EnsureCoreWebView2Async(environment);
            if (disposed) return;
            var settings = view.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = true;
            settings.IsStatusBarEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsWebMessageEnabled = false;
            await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(AgentSkin.DocumentScript(owner));
            if (disposed) return;
            view.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            view.CoreWebView2.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs args) {
                if (!IsAllowedResource(origin, args.Request.Uri)) {
                    BlockedResources++;
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "Content-Length: 0");
                }
            };
            view.CoreWebView2.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs args) { args.Cancel = !IsLocalNavigation(origin, args.Uri); };
            view.CoreWebView2.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs args) { args.Handled = true; };
            view.CoreWebView2.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs args) { args.State = CoreWebView2PermissionState.Deny; };
            view.CoreWebView2.DownloadStarting += delegate(object sender, CoreWebView2DownloadStartingEventArgs args) { args.Cancel = true; };
            view.CoreWebView2.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs args) {
                if (disposed) return;
                if (!args.IsSuccess) { ShowFailure("Harness 页面未能载入：" + args.WebErrorStatus); return; }
                invitation.Visibility = Visibility.Collapsed;
                state.Text = "已连接";
                ApplySkin();
            };
            view.CoreWebView2.Navigate(origin.AbsoluteUri);
        }

        // Re-applies the native palette without restarting the runtime, so a
        // theme change keeps the conversation and its running task.
        internal void ApplySkin()
        {
            if (view == null) return;
            view.DefaultBackgroundColor = AgentSkin.Background(owner);
            var browser = view.CoreWebView2;
            if (browser == null) return;
            ApplySkinAsync(browser);
        }

        async void ApplySkinAsync(CoreWebView2 browser)
        {
            try { await browser.ExecuteScriptAsync(AgentSkin.ApplyScript(owner)); }
            catch (Exception) { /* The page may be navigating; the next completion re-applies. */ }
        }

        internal void RefreshAppearance()
        {
            ApplySkin();
        }

        internal static bool IsLocalNavigation(Uri address, string target)
        {
            Uri uri;
            return address != null && Uri.TryCreate(target, UriKind.Absolute, out uri) && uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port == address.Port;
        }

        internal static bool IsAllowedResource(Uri address, string target)
        {
            Uri uri;
            return IsLocalNavigation(address, target) || Uri.TryCreate(target, UriKind.Absolute, out uri) && (uri.Scheme == "data" || uri.Scheme == "blob");
        }

        async Task<object> NativeRequestAsync(Dictionary<string,object> message, CancellationToken token)
        {
            return await Dispatcher.InvokeAsync(new Func<object>(delegate {
                token.ThrowIfCancellationRequested();
                if (disposed) throw new OperationCanceledException();
                if (HarnessRuntime.Text(message, "operation") == "context") return new Dictionary<string,object> {
                    { "root", owner.WorkspaceRoot }, { "file", owner.Current == null ? null : owner.Current.Path }
                };
                object arguments;
                message.TryGetValue("args", out arguments);
                return tools.Execute(HarnessRuntime.Text(message, "operation"), HarnessRuntime.Text(message, "sessionId"),
                    HarnessRuntime.Text(message, "name"), arguments as Dictionary<string,object>, HarnessRuntime.Text(message, "proposal"));
            }));
        }

        Document FindOpen(string path)
        {
            return owner.Session.Documents.FirstOrDefault(document => document.Path != null && String.Equals(Path.GetFullPath(document.Path), path, StringComparison.OrdinalIgnoreCase));
        }
        string OpenText(string path)
        {
            var document = FindOpen(path);
            if (document == null || document.Kind != DocumentKind.Text) return null;
            ICSharpCode.AvalonEdit.Document.TextDocument buffer;
            return owner.buffers.TryGetValue(document, out buffer) ? buffer.Text : document.Text;
        }
        bool OpenDirty(string path)
        {
            var document = FindOpen(path);
            return document != null && (document.IsDirty || owner.richEdit.IsEditing(document));
        }
        void ReloadOpenFile(WriteProposal proposal)
        {
            var document = FindOpen(proposal.Path);
            if (document == null) { owner.SetNotice("Harness 已更新 " + proposal.RelativePath); return; }
            if (OpenDirty(proposal.Path)) { owner.SetNotice("Harness 已写入磁盘；最新编辑缓冲区保留，请核对后保存。"); return; }
            var fresh = FileService.Read(proposal.Path);
            owner.Session.Documents[owner.Session.Documents.IndexOf(document)] = fresh;
            owner.buffers.Remove(document);
            owner.copiedVersions.Remove(document);
            owner.viewModes.Remove(document);
            owner.DisplayedDirty.Remove(document);
            if (owner.primary.BoundDocument == document) owner.primary.Bind(fresh);
            if (owner.secondary.BoundDocument == document) owner.secondary.Bind(fresh);
            owner.RefreshTabs();
            owner.UpdateStatus();
            if (owner.showPreview) owner.Observe(owner.UpdatePreviewAsync());
        }

        internal async Task RestartAsync()
        {
            await lifecycle.WaitAsync();
            try {
                if (disposed) return;
                await runtime.StopAsync();
                await HarnessBrowserCleanup.CloseAsync(view, page, DataDirectory ?? home, runDirectory);
                view = null;
                runDirectory = null;
                origin = null;
                invitation.Visibility = Visibility.Visible;
                await StartCoreAsync();
            } finally { lifecycle.Release(); }
        }

        internal async Task<bool> ShutdownAsync()
        {
            disposed = true;
            contextTimer.Stop();
            await lifecycle.WaitAsync();
            try {
                await runtime.StopAsync();
                await HarnessBrowserCleanup.CloseAsync(view, page, DataDirectory ?? home, runDirectory);
                view = null;
                runDirectory = null;
                return true;
            } finally { lifecycle.Release(); }
        }
    }
}
