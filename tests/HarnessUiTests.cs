using System;
using System.Linq;
using System.Windows;
using Qingye.UI;

namespace Qingye.Tests
{
    public static partial class UiTests
    {
        static void HarnessUiTests()
        {
            Test("Long Agent conversations bound live controls while retaining the latest message", delegate
            {
                var view = new AgentConversationView();
                for (int index = 0; index < 300; index++) view.AddMessage("user", "bounded message " + index);
                var host = new Window { Content = view, Width = 400, Height = 500, ShowActivated = false, Left = -16000, Top = 0 };
                host.Show();
                Pump();
                try
                {
                    var messages = Walk(view).OfType<System.Windows.Controls.TextBox>().ToArray();
                    Check(messages.Length <= 120, "Live conversation retained unbounded WPF message controls");
                    Check(messages.Any(box => box.Text == "bounded message 299"), "Control trimming removed the latest message");
                }
                finally { host.Close(); Pump(); }
            });
            Test("Agent user messages expose copy and edit-prefill without sending", delegate
            {
                var view = new AgentConversationView();
                view.AddMessage("user", "example prompt");
                var host = new Window { Content = view, ShowActivated = false, Left = -16000, Top = 0 };
                host.Show();
                Pump();
                try
                {
                    var box = Walk(view).OfType<System.Windows.Controls.TextBox>().FirstOrDefault();
                    Check(box != null && box.ContextMenu != null, "Message actions are missing");
                    Check(box.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Any(item => Convert.ToString(item.Header) == "复制整段"), "Message copy action is missing");
                    var edit = box.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(item => Convert.ToString(item.Header) == "编辑后重发");
                    Check(edit != null, "Message edit-prefill action is missing");
                    string prefilled = null;
                    view.EditRequested += text => prefilled = text;
                    edit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                    Check(prefilled == "example prompt", "Edit action did not provide the original prompt");
                }
                finally { host.Close(); Pump(); }
            });
            Test("Agent quotes only a bounded selection from the current authorized source", delegate
            {
                var host = new MainWindow { ShowActivated = false, Left = -16000, Top = 0 };
                string file = System.IO.Path.Combine(root, "agent-selection.txt");
                System.IO.File.WriteAllText(file, "alpha beta gamma");
                host.Show();
                try
                {
                    Await(host.OpenPathsAsync(new[] { file }, false));
                    host.SetViewMode("single");
                    host.ActiveEditor.Select(6, 4);
                    var agent = host.TestHooks.Agent;
                    Check(agent.TryQuoteSelection(), "Valid source selection was rejected");
                    string quoted = agent.ComposePrompt("explain");
                    Check(quoted.Contains("beta") && !quoted.Contains("alpha beta gamma"), "Selection included unrelated source content");
                    agent.NewConversation();
                    Check(!agent.ComposePrompt("next").Contains("beta"), "New task retained a previous quoted selection");
                }
                finally { Await(host.TestHooks.Agent.ShutdownAsync()); host.Close(); Pump(); }
            });
            Test("Agent buffer sync adopts the disk version and undo becomes an unsaved edit", delegate
            {
                var host = new MainWindow { ShowActivated = false, Left = -16000, Top = 0 };
                string file = System.IO.Path.Combine(root, "agent-sync-version.md");
                System.IO.File.WriteAllText(file, "# Before");
                host.Show();
                try
                {
                    Await(host.OpenPathsAsync(new[] { file }, false));
                    System.IO.File.WriteAllText(file, "# After");
                    host.TestHooks.Agent.ApplyEditorWrite(new Qingye.Core.WriteProposal { Path = file, RelativePath = "agent-sync-version.md", OldText = "# Before", NewText = "# After" });
                    Check(host.Current.Version == Qingye.Core.FileService.HashFile(file), "Synced document retained the stale disk version");
                    host.ActiveEditor.Document.UndoStack.Undo();
                    Pump();
                    Check(host.Current.IsDirty && host.ActiveEditor.Text == "# Before", "Undo of an Agent write was falsely marked clean");
                    host.Current.Text = host.ActiveEditor.Text;
                    Qingye.Core.FileService.Save(host.Current, file, false);
                    Check(System.IO.File.ReadAllText(file) == "# Before", "Saving an Agent undo failed its version check");
                }
                finally { foreach (var document in host.Session.Documents) { document.Modified = false; document.MetadataModified = false; } Await(host.TestHooks.Agent.ShutdownAsync()); host.Close(); Pump(); }
            });
            Test("Agent keeps the document visible at 820 DIP and supports explicit focus", delegate
            {
                var host = new MainWindow { Width = 820, Height = 640, ShowActivated = false, Left = -16000, Top = 0 };
                host.Show();
                try
                {
                    host.ToggleAgent();
                    Pump();
                    Check(host.ActiveEditor.IsVisible && host.TestHooks.Agent.IsVisible, "820 DIP hid the document automatically");
                    Check(host.ActiveEditor.ActualWidth >= 240, "Parallel layout left an unusable document width");
                    host.ToggleAgentFocus();
                    Pump();
                    Check(!host.ActiveEditor.IsVisible && host.TestHooks.Agent.IsVisible, "Explicit focus did not hide the document");
                }
                finally { Await(host.TestHooks.Agent.ShutdownAsync()); host.Close(); Pump(); }
            });
            Test("Agent disk diagnostics require an explicit contained directory", delegate
            {
                var host = new MainWindow { ShowActivated = false, Left = -16000, Top = 0 };
                host.Show();
                try
                {
                    string directory = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics-" + Guid.NewGuid().ToString("N"));
                    host.TestHooks.Agent.DiagnosticsDirectory = directory;
                    host.ToggleAgent();
                    Pump();
                    Check(System.IO.File.Exists(System.IO.Path.Combine(directory, "agent-native-timing.txt")), "Explicit diagnostics were not written inside the app directory");
                    string outside = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "outside-diagnostics-" + Guid.NewGuid().ToString("N")));
                    host.TestHooks.Agent.DiagnosticsDirectory = outside;
                    host.TestHooks.Agent.NewConversation();
                    Check(!System.IO.Directory.Exists(outside), "Diagnostics wrote outside the application directory");
                }
                finally { Await(host.TestHooks.Agent.ShutdownAsync()); host.Close(); Pump(); }
            });
            Test("Production Agent keeps new conversations and drafts out of local archives", delegate
            {
                var host = new MainWindow { ShowActivated = false, Left = -16000, Top = 0 };
                host.Show();
                try
                {
                    var agent = host.TestHooks.Agent;
                    string id = agent.CurrentSession.Id;
                    agent.Input.Text = "synthetic unsent temporary draft";
                    System.Threading.Thread.Sleep(1100);
                    Pump();
                    agent.NewConversation();
                    string archived = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent-history", id + ".json");
                    Check(!System.IO.File.Exists(archived), "Production saved a temporary draft as an archive");
                    Check(!Walk(agent).OfType<System.Windows.Controls.TextBlock>().Any(text => text.Text.Contains("对话保存在本机")),
                        "Production still promises archived conversations");
                }
                finally { Await(host.TestHooks.Agent.ShutdownAsync()); host.Close(); Pump(); }
            });
            Test("Native SDK scope follows the active file and revokes old permission", delegate
            {
                var host = new MainWindow { ShowActivated = false, Left = -16000, Top = 0 };
                string folder = System.IO.Path.Combine(root, "sdk-scope-switch");
                System.IO.Directory.CreateDirectory(folder);
                string first = System.IO.Path.Combine(folder, "first.md");
                string second = System.IO.Path.Combine(folder, "second.md");
                System.IO.File.WriteAllText(first, "# First");
                System.IO.File.WriteAllText(second, "# Second");
                host.Show();
                try
                {
                    Await(host.OpenPathsAsync(new[] { first }, false));
                    var agent = host.TestHooks.Agent;
                    agent.ConfigProvider = () => new Qingye.Core.ModelConfig { BaseUrl = "https://example.invalid", ApiKey = "test-only", ModelName = "test" };
                    Await(agent.PrepareControllerAsync());
                    agent.CurrentSession.Record(AgentMessage("user", "old scope context"));
                    agent.CurrentSession.Draft = "old draft";
                    agent.FilePermission = Qingye.Core.HarnessFilePermission.Automatic;
                    Await(host.OpenPathsAsync(new[] { second }, false));
                    Check(agent.CurrentSession.CurrentFile == second, "Agent retained the previous file scope");
                    Check(agent.CurrentSession.Entries.Count == 0 && agent.CurrentSession.Draft == "", "Previous scope context crossed into the new file");
                    Check(agent.FilePermission == Qingye.Core.HarnessFilePermission.Confirm, "File scope switch retained automatic permission");
                }
                finally { Await(host.TestHooks.Agent.ShutdownAsync()); host.Close(); Pump(); }
            });
            Test("Native SDK directory switch resets scope but same-directory file changes keep it", delegate
            {
                var host = new MainWindow { ShowActivated = false, Left = -16000, Top = 0 };
                string firstRoot = System.IO.Path.Combine(root, "sdk-workspace-first");
                string secondRoot = System.IO.Path.Combine(root, "sdk-workspace-second");
                System.IO.Directory.CreateDirectory(firstRoot);
                System.IO.Directory.CreateDirectory(secondRoot);
                string first = System.IO.Path.Combine(firstRoot, "first.md");
                string second = System.IO.Path.Combine(firstRoot, "second.md");
                System.IO.File.WriteAllText(first, "# First");
                System.IO.File.WriteAllText(second, "# Second");
                host.Show();
                try
                {
                    host.ImportWorkspace(firstRoot);
                    Await(host.WorkspaceLoadTask);
                    Await(host.OpenPathsAsync(new[] { first }, true));
                    var agent = host.TestHooks.Agent;
                    agent.ConfigProvider = () => new Qingye.Core.ModelConfig { BaseUrl = "https://example.invalid", ApiKey = "test-only", ModelName = "test" };
                    Await(agent.PrepareControllerAsync());
                    agent.CurrentSession.Record(AgentMessage("user", "same directory context"));
                    agent.FilePermission = Qingye.Core.HarnessFilePermission.Automatic;
                    Await(host.OpenPathsAsync(new[] { second }, true));
                    Check(agent.CurrentSession.Entries.Count == 1 && agent.FilePermission == Qingye.Core.HarnessFilePermission.Automatic,
                        "Switching files within the authorized directory reset its context");
                    host.ImportWorkspace(secondRoot);
                    Await(host.WorkspaceLoadTask);
                    Check(agent.CurrentSession.Root == secondRoot && agent.CurrentSession.Entries.Count == 0, "Agent retained the previous directory scope");
                    Check(agent.FilePermission == Qingye.Core.HarnessFilePermission.Confirm, "Directory scope switch retained automatic permission");
                }
                finally { Await(host.TestHooks.Agent.ShutdownAsync()); host.Close(); Pump(); }
            });
            Test("Production Agent renders the native conversation without WebView2", delegate {
                var host = new MainWindow { ShowActivated = false, Left = -16000, Top = 0 };
                host.Show();
                Pump();
                try
                {
                    var panel = host.TestHooks.Agent;
                    Check(!(panel.Content is HarnessPanel), "Production Agent still hosts the WebView panel");
                    Check(panel.Harness == null, "Production Agent still constructs a HarnessPanel");
                    Check(HarnessPanel.Constructed == 0, "A WebView Agent panel was constructed in the native build");
                    Check(!panel.RuntimeStarted, "Opening the editor starts the SDK runtime without a model or first use");
                    Check(panel.Content is System.Windows.Controls.Grid, "Native conversation root is missing");
                    Check(!System.Diagnostics.Process.GetProcessesByName("msedgewebview2").Any(process => process.ParentId() == System.Diagnostics.Process.GetCurrentProcess().Id), "Native Agent started a browser process");
                    host.Width = 390;
                    Pump();
                    Check(panel.Harness == null && panel.MinWidth == 0, "Native panel imposes a fixed window width");
                }
                finally { host.Close(); Pump(); }
            });
            Test("Native Agent starts the runtime only on reveal and stays idle in tests", delegate {
                var host = new MainWindow { ShowActivated = false, Left = -16000, Top = 0 };
                host.Show();
                Pump();
                try
                {
                    var panel = host.TestHooks.Agent;
                    Check(!panel.RuntimeStarted && !panel.RuntimeStarting, "Runtime started before the panel was revealed");
                    Check(String.IsNullOrEmpty(panel.LastRuntimeError), "Runtime error appeared before a start attempt");
                    host.ToggleAgent();
                    Pump();
                    // AllowAutomaticStart is cleared for the whole UI test run, so a
                    // reveal must not spawn a process while tests only check layout.
                    Check(!panel.RuntimeStarted && !panel.RuntimeStarting, "Test process started the SDK runtime");
                    Check(!panel.IsBusy && panel.StatusText.Contains("待命"), "Idle panel lost its explicit ready state");
                }
                finally { host.Close(); Pump(); }
            });
            Test("Harness navigation admits only its owning loopback origin", delegate {
                var address = new Uri("http://127.0.0.1:43121/?token=test");
                Check(HarnessPanel.IsLocalNavigation(address, "http://127.0.0.1:43121/chat/123"), "Own Harness origin was blocked");
                Check(!HarnessPanel.IsLocalNavigation(address, "http://127.0.0.1:43122/"), "Another local service was trusted");
                Check(!HarnessPanel.IsLocalNavigation(address, "https://example.test/"), "Remote navigation was allowed");
                Check(!HarnessPanel.IsLocalNavigation(address, "file:///C:/secret.txt"), "File navigation was allowed");
                Check(!HarnessPanel.IsAllowedResource(address, "https://remote.test/pixel.png"), "Remote Markdown image escaped the resource fence");
                Check(!HarnessPanel.IsAllowedResource(address, "http://127.0.0.1:43122/pixel.png"), "Other local server escaped the resource fence");
                Check(HarnessPanel.IsAllowedResource(address, "data:image/png;base64,AA=="), "Inline image was blocked");
            });
        }
    }
}
