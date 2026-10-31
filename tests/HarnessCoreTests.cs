using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Qingye.Core;

namespace Qingye.Tests
{
    public static partial class CoreTests
    {
        static void HarnessCoreTests()
        {
            string folder = Path.Combine(root, "harness-native");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "note.md");
            File.WriteAllText(file, "original");
            string other = Path.Combine(folder, "other.md");
            File.WriteAllText(other, "outside single file");
            string data = Path.Combine(folder, "harness-data");
            var scope = new AgentWorkspace(null, file);
            bool dirty = false;
            var bridge = new HarnessNativeTools(data, () => scope, path => path == file ? "latest buffer" : null, path => dirty, null);
            Test("Harness native tools persist each session scope and read live buffers", delegate {
                bridge.Execute("pin", "session-one", null, null, null);
                scope = new AgentWorkspace(folder, file);
                string result = (string)bridge.Execute("tool", "session-one", "read_file", new Dictionary<string,object>{{"path", "note.md"}}, null);
                Check(result.Contains("latest buffer"), "Harness read stale disk text");
                var restarted = new HarnessNativeTools(data, () => scope, null, null, null);
                bool blocked = false;
                try { restarted.Execute("tool", "session-one", "read_file", new Dictionary<string,object>{{"path", "other.md"}}, null); } catch (AgentException) { blocked = true; }
                Check(blocked, "Restart expanded the saved single-file scope");
            });
            Test("Harness writes are proposals bound to a session and recheck dirty buffers", delegate {
                var proposal = (Dictionary<string,object>)bridge.Execute("tool", "session-one", "write_file", new Dictionary<string,object>{{"path", "note.md"},{"content", "changed"}}, null);
                Check(File.ReadAllText(file) == "original", "Proposal wrote without approval");
                string id = (string)proposal["id"];
                bool blocked = false;
                try { bridge.Execute("apply", "other-session", null, null, id); } catch (AgentException) { blocked = true; }
                Check(blocked, "A different session applied the proposal");
                dirty = true;
                blocked = false;
                try { bridge.Execute("apply", "session-one", null, null, id); } catch (AgentException) { blocked = true; }
                Check(blocked && File.ReadAllText(file) == "original", "Dirty buffer was overwritten");
                dirty = false;
                bridge.Execute("discard", "session-one", null, null, id);
            });
            Test("Harness private runtime data and proposal IDs are protected", delegate {
                Check(new AgentWorkspace(folder, file).IsProtected(Path.Combine(data, "sessions", "x.jsonl")), "Harness history is readable by model tools");
                Check(AgentWorkspace.ShouldSkipDirectory("harness-data"), "Search enters Harness private data");
                bool blocked = false;
                try { bridge.Execute("apply", "session-one", null, null, "invented"); } catch (AgentException) { blocked = true; }
                Check(blocked, "Unknown proposal was accepted");
            });
            Test("Harness rejects a stale UI workspace instead of rebinding relative paths", delegate {
                var contextBridge = new HarnessNativeTools(Path.Combine(folder, "contexts"), () => new AgentWorkspace(folder, file), null, null, null);
                bool blocked = false;
                try { contextBridge.Execute("pin", "wrong-context", null, new Dictionary<string,object>{{"cwd", root}}, null); } catch (AgentException) { blocked = true; }
                Check(blocked, "Harness cwd and native workspace silently diverged");
            });
            Test("Production Harness scope binding remains memory-only", delegate {
                string transient = Path.Combine(folder, "volatile-scopes");
                var volatileBridge = new HarnessNativeTools(transient, () => new AgentWorkspace(folder, file), null, null, null, false);
                volatileBridge.Execute("pin", "volatile-session", null, new Dictionary<string,object>{{"cwd", folder}}, null);
                volatileBridge.Execute("tool", "volatile-session", "read_file", new Dictionary<string,object>{{"path", "note.md"}}, null);
                Check(!Directory.Exists(transient), "Volatile native scope wrote a scope archive");
            });
            Test("Harness cannot rewrite its active runtime even from a parent workspace", delegate {
                string runtime = Path.Combine(folder, "active-runtime");
                Directory.CreateDirectory(runtime);
                string launcher = Path.Combine(runtime, "launcher.mjs");
                File.WriteAllText(launcher, "trusted code");
                var protectedBridge = new HarnessNativeTools(Path.Combine(folder, "runtime-protection"), () => new AgentWorkspace(folder, file, runtime), null, null, null, false);
                protectedBridge.Permission = HarnessFilePermission.Automatic;
                bool blocked = false;
                try { protectedBridge.Execute("tool", "runtime-scope", "write_file", new Dictionary<string,object>{{"path", "active-runtime/launcher.mjs"},{"content", "injected code"}}, null); } catch (AgentException) { blocked = true; }
                Check(blocked && File.ReadAllText(launcher) == "trusted code", "Agent rewrote the host code that runs on restart");
            });
            Test("Harness workspace permissions support readonly, confirmed and automatic edits", delegate {
                var permissionBridge = new HarnessNativeTools(Path.Combine(folder, "permissions"), () => new AgentWorkspace(folder, file), null, null, null);
                permissionBridge.Permission = HarnessFilePermission.ReadOnly;
                bool blocked = false;
                try { permissionBridge.Execute("tool", "workspace-session", "write_file", new Dictionary<string,object>{{"path", "new.md"},{"content", "new"}}, null); } catch (AgentException) { blocked = true; }
                Check(blocked && !File.Exists(Path.Combine(folder, "new.md")), "Readonly permission allowed a write");
                permissionBridge.Permission = HarnessFilePermission.Confirm;
                var proposal = (Dictionary<string,object>)permissionBridge.Execute("tool", "workspace-session", "write_file", new Dictionary<string,object>{{"path", "new.md"},{"content", "new"}}, null);
                Check((bool)proposal["requiresApproval"], "Confirm mode skipped approval");
                permissionBridge.Permission = HarnessFilePermission.ReadOnly;
                blocked = false;
                try { permissionBridge.Execute("apply", "workspace-session", null, null, (string)proposal["id"]); } catch (AgentException) { blocked = true; }
                Check(blocked, "Revoked write permission left a pending grant usable");
                permissionBridge.Permission = HarnessFilePermission.Automatic;
                proposal = (Dictionary<string,object>)permissionBridge.Execute("tool", "workspace-session", "write_file", new Dictionary<string,object>{{"path", "new.md"},{"content", "new"}}, null);
                Check(!(bool)proposal["requiresApproval"], "Automatic workspace edits still require an approval dialog");
                permissionBridge.Execute("apply", "workspace-session", null, null, (string)proposal["id"]);
                Check(File.ReadAllText(Path.Combine(folder, "new.md")) == "new", "Workspace file was not created");
                proposal = (Dictionary<string,object>)permissionBridge.Execute("tool", "workspace-session", "write_file", new Dictionary<string,object>{{"path", "second.md"},{"content", "second"}}, null);
                permissionBridge.Permission = HarnessFilePermission.Confirm;
                blocked = false;
                try { permissionBridge.Execute("apply", "workspace-session", null, null, (string)proposal["id"]); } catch (AgentException) { blocked = true; }
                Check(blocked && !File.Exists(Path.Combine(folder, "second.md")), "Automatic grant survived downgrade to confirmation");
                blocked = false;
                try { permissionBridge.Execute("tool", "workspace-session", "read_file", new Dictionary<string,object>{{"path", "../outside.txt"}}, null); } catch (AgentException) { blocked = true; }
                Check(blocked, "Automatic mode widened the workspace root");
            });
            Test("Harness edit_file proposes a unique replacement and applies only after approval", delegate {
                string editRoot = Path.Combine(folder, "edit-workspace");
                Directory.CreateDirectory(editRoot);
                string doc = Path.Combine(editRoot, "doc.md");
                File.WriteAllText(doc, "alpha\nbeta\ngamma\n", new UTF8Encoding(false));
                var editScope = new AgentWorkspace(editRoot, doc);
                var editBridge = new HarnessNativeTools(Path.Combine(folder, "edit-data"), () => editScope, null, null, null);
                editBridge.Execute("pin", "edit-session", null, new Dictionary<string,object>{{"cwd", editRoot}}, null);
                var proposal = (Dictionary<string,object>)editBridge.Execute("tool", "edit-session", "edit_file",
                    new Dictionary<string,object>{{"path", "doc.md"},{"old_text", "beta"},{"new_text", "BETA"}}, null);
                Check((bool)proposal["requiresApproval"], "Confirm mode edit_file skipped approval");
                Check(File.ReadAllText(doc).Contains("beta"), "edit_file wrote before approval");
                string diff = (string)proposal["diff"];
                Check(diff.Contains("-beta") && diff.Contains("+BETA"), "Approval diff is not a +/- line diff");
                editBridge.Execute("apply", "edit-session", null, null, (string)proposal["id"]);
                Check(File.ReadAllText(doc).Replace("\r\n", "\n") == "alpha\nBETA\ngamma\n", "Unique edit was not applied");
            });
            Test("Harness edit_file rejects missing and ambiguous matches and replaces all on request", delegate {
                string matchRoot = Path.Combine(folder, "edit-matches");
                Directory.CreateDirectory(matchRoot);
                string doc = Path.Combine(matchRoot, "dup.md");
                File.WriteAllText(doc, "dup\ndup\nkeep\n", new UTF8Encoding(false));
                var editScope = new AgentWorkspace(matchRoot, doc);
                var editBridge = new HarnessNativeTools(Path.Combine(folder, "edit-matches-data"), () => editScope, null, null, null);
                editBridge.Execute("pin", "match-session", null, new Dictionary<string,object>{{"cwd", matchRoot}}, null);
                bool blocked = false;
                try { editBridge.Execute("tool", "match-session", "edit_file", new Dictionary<string,object>{{"path", "dup.md"},{"old_text", "absent"},{"new_text", "x"}}, null); }
                catch (AgentException error) { blocked = true; Check(error.Message.Contains("未找到"), "Missing-match error is not actionable"); }
                Check(blocked, "edit_file accepted a missing old_text");
                blocked = false;
                try { editBridge.Execute("tool", "match-session", "edit_file", new Dictionary<string,object>{{"path", "dup.md"},{"old_text", "dup"},{"new_text", "x"}}, null); }
                catch (AgentException error) { blocked = true; Check(error.Message.Contains("2") && error.Message.Contains("replace_all"), "Ambiguous-match error lacks the count or hint"); }
                Check(blocked, "edit_file applied an ambiguous old_text");
                var all = (Dictionary<string,object>)editBridge.Execute("tool", "match-session", "edit_file",
                    new Dictionary<string,object>{{"path", "dup.md"},{"old_text", "dup"},{"new_text", "ok"},{"replace_all", true}}, null);
                editBridge.Execute("apply", "match-session", null, null, (string)all["id"]);
                Check(File.ReadAllText(doc).Replace("\r\n", "\n") == "ok\nok\nkeep\n", "replace_all did not rewrite every match");
                string windows = Path.Combine(matchRoot, "windows.md");
                File.WriteAllText(windows, "第一行\r\n第二行\r\n", new UTF8Encoding(false));
                var windowsEdit = (Dictionary<string,object>)editBridge.Execute("tool", "match-session", "edit_file",
                    new Dictionary<string,object>{{"path", "windows.md"},{"old_text", "第一行\n第二行"},{"new_text", "新的第一行\n新的第二行"}}, null);
                editBridge.Execute("apply", "match-session", null, null, (string)windowsEdit["id"]);
                Check(File.ReadAllText(windows) == "新的第一行\r\n新的第二行\r\n", "CRLF edit did not keep the file newline style");
            });
            Test("Harness edit_file enforces readonly, unsaved buffers and stale disk state", delegate {
                string guardRoot = Path.Combine(folder, "edit-guards");
                Directory.CreateDirectory(guardRoot);
                string doc = Path.Combine(guardRoot, "guarded.md");
                File.WriteAllText(doc, "one\ntwo\n", new UTF8Encoding(false));
                var editScope = new AgentWorkspace(guardRoot, doc);
                bool editDirty = false;
                var editBridge = new HarnessNativeTools(Path.Combine(folder, "edit-guards-data"), () => editScope, null, path => editDirty, null);
                editBridge.Execute("pin", "guard-session", null, new Dictionary<string,object>{{"cwd", guardRoot}}, null);
                editBridge.Permission = HarnessFilePermission.ReadOnly;
                bool blocked = false;
                try { editBridge.Execute("tool", "guard-session", "edit_file", new Dictionary<string,object>{{"path", "guarded.md"},{"old_text", "one"},{"new_text", "ONE"}}, null); }
                catch (AgentException error) { blocked = true; Check(error.Message.Contains("只读"), "Read-only edit error is unclear"); }
                Check(blocked, "Read-only permission allowed edit_file");
                editBridge.Permission = HarnessFilePermission.Confirm;
                editDirty = true;
                blocked = false;
                try { editBridge.Execute("tool", "guard-session", "edit_file", new Dictionary<string,object>{{"path", "guarded.md"},{"old_text", "one"},{"new_text", "ONE"}}, null); }
                catch (AgentException error) { blocked = true; Check(error.Message.Contains("未保存"), "Dirty-buffer error is unclear"); }
                Check(blocked, "edit_file accepted an unsaved buffer");
                editDirty = false;
                var proposal = (Dictionary<string,object>)editBridge.Execute("tool", "guard-session", "edit_file",
                    new Dictionary<string,object>{{"path", "guarded.md"},{"old_text", "one"},{"new_text", "ONE"}}, null);
                File.WriteAllText(doc, "one\ntwo\nexternal\n", new UTF8Encoding(false));
                blocked = false;
                try { editBridge.Execute("apply", "guard-session", null, null, (string)proposal["id"]); }
                catch (AgentException error) { blocked = true; Check(error.Message.Contains("变化"), "Stale-edit error is unclear"); }
                Check(blocked && File.ReadAllText(doc).Contains("external"), "A confirmed edit overwrote an externally changed file");
            });
            Test("Harness edit_file reads live buffers and keeps single-file edits confirmed", delegate {
                string liveRoot = Path.Combine(folder, "edit-live");
                Directory.CreateDirectory(liveRoot);
                string doc = Path.Combine(liveRoot, "live.md");
                File.WriteAllText(doc, "disk text\n", new UTF8Encoding(false));
                var editScope = new AgentWorkspace(liveRoot, doc);
                var editBridge = new HarnessNativeTools(Path.Combine(folder, "edit-live-data"), () => editScope, path => path == doc ? "buffer text\n" : null, null, null);
                editBridge.Execute("pin", "live-session", null, new Dictionary<string,object>{{"cwd", liveRoot}}, null);
                editBridge.Permission = HarnessFilePermission.Automatic;
                var buffered = (Dictionary<string,object>)editBridge.Execute("tool", "live-session", "edit_file",
                    new Dictionary<string,object>{{"path", "live.md"},{"old_text", "buffer"},{"new_text", "BUFFER"}}, null);
                Check(!(bool)buffered["requiresApproval"], "Automatic workspace edit still asked for approval");
                Check(((string)buffered["diff"]).Contains("BUFFER"), "edit_file ignored the live editor buffer");
                editBridge.Execute("discard", "live-session", null, null, (string)buffered["id"]);
                var singleBridge = new HarnessNativeTools(Path.Combine(folder, "edit-single-data"), () => new AgentWorkspace(null, doc), null, null, null);
                singleBridge.Execute("pin", "single-session", null, null, null);
                singleBridge.Permission = HarnessFilePermission.Automatic;
                var single = (Dictionary<string,object>)singleBridge.Execute("tool", "single-session", "edit_file",
                    new Dictionary<string,object>{{"path", "live.md"},{"old_text", "disk"},{"new_text", "DISK"}}, null);
                Check((bool)single["requiresApproval"], "Single-file edit skipped confirmation even in automatic mode");
                singleBridge.Execute("discard", "single-session", null, null, (string)single["id"]);
                bool blocked = false;
                try { singleBridge.Execute("tool", "single-session", "edit_file", new Dictionary<string,object>{{"path", "other.md"},{"old_text", "other"},{"new_text", "OTHER"}}, null); }
                catch (AgentException) { blocked = true; }
                Check(blocked, "Single-file edit reached another file");
            });
            Test("Harness create_file only creates new files in a workspace", delegate {
                string createRoot = Path.Combine(folder, "create-workspace");
                Directory.CreateDirectory(createRoot);
                string anchor = Path.Combine(createRoot, "anchor.md");
                File.WriteAllText(anchor, "anchor\n", new UTF8Encoding(false));
                var createScope = new AgentWorkspace(createRoot, anchor);
                var createBridge = new HarnessNativeTools(Path.Combine(folder, "create-data"), () => createScope, null, null, null);
                createBridge.Execute("pin", "create-session", null, new Dictionary<string,object>{{"cwd", createRoot}}, null);
                var created = (Dictionary<string,object>)createBridge.Execute("tool", "create-session", "create_file",
                    new Dictionary<string,object>{{"path", "new.md"},{"content", "新文件内容\n"}}, null);
                Check(!File.Exists(Path.Combine(createRoot, "new.md")), "create_file wrote before approval");
                createBridge.Execute("apply", "create-session", null, null, (string)created["id"]);
                Check(File.ReadAllText(Path.Combine(createRoot, "new.md")) == "新文件内容\n", "create_file content was not written");
                bool blocked = false;
                try { createBridge.Execute("tool", "create-session", "create_file", new Dictionary<string,object>{{"path", "new.md"},{"content", "again"}}, null); }
                catch (AgentException error) { blocked = true; Check(error.Message.Contains("已存在"), "create_file overwrite error is unclear"); }
                Check(blocked, "create_file overwrote an existing file");
                var singleBridge = new HarnessNativeTools(Path.Combine(folder, "create-single-data"), () => new AgentWorkspace(null, anchor), null, null, null);
                singleBridge.Execute("pin", "create-single", null, null, null);
                blocked = false;
                try { singleBridge.Execute("tool", "create-single", "create_file", new Dictionary<string,object>{{"path", "second.md"},{"content", "x"}}, null); }
                catch (AgentException) { blocked = true; }
                Check(blocked, "create_file is available in single-file mode");
            });
            Test("Harness list depth and search regex/context stay inside the budgets", delegate {
                string treeRoot = Path.Combine(folder, "walk-depth");
                Directory.CreateDirectory(Path.Combine(treeRoot, "one", "two"));
                File.WriteAllText(Path.Combine(treeRoot, "root.txt"), "root\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(treeRoot, "one", "mid.txt"), "head\norder-123\ntail\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(treeRoot, "one", "two", "deep.txt"), "deep\n", new UTF8Encoding(false));
                var walkTools = new AgentTools(new AgentWorkspace(treeRoot, Path.Combine(treeRoot, "root.txt")), null, null);
                string one = walkTools.Execute("list_files", new Dictionary<string,object>{{"depth", 1}}).Json;
                Check(one.Contains("one/") && !one.Contains("mid.txt"), "depth 1 descended into subdirectories");
                string two = walkTools.Execute("list_files", new Dictionary<string,object>{{"depth", 2}}).Json;
                Check(two.Contains("mid.txt") && !two.Contains("deep.txt"), "depth 2 missed a child or descended too far");
                string three = walkTools.Execute("list_files", new Dictionary<string,object>{{"depth", 3}}).Json;
                Check(three.Contains("deep.txt"), "depth 3 missed a nested file");
                bool blocked = false;
                try { walkTools.Execute("list_files", new Dictionary<string,object>{{"depth", 9}}); }
                catch (AgentException error) { blocked = true; Check(error.Message.Contains("1 到 6"), "depth range error is unclear"); }
                Check(blocked, "list_files accepted depth 9");
                string regex = walkTools.Execute("search_files", new Dictionary<string,object>{{"query", "order-\\d+"},{"regex", true}}).Json;
                Check(regex.Contains("order-123"), "Regex search did not match");
                string context = walkTools.Execute("search_files", new Dictionary<string,object>{{"query", "order-123"},{"context", 1}}).Json;
                Check(context.Contains("head") && context.Contains("tail"), "Search context lines were not returned");
                File.WriteAllBytes(Path.Combine(treeRoot, "binary.bin"), new byte[]{0,1,2,3,0,5});
                string binarySafe = walkTools.Execute("search_files", new Dictionary<string,object>{{"query", "order-123"}}).Json;
                Check(binarySafe.Contains("mid.txt"), "A binary file aborted a text search");
                blocked = false;
                try { walkTools.Execute("search_files", new Dictionary<string,object>{{"query", "("},{"regex", true}}); }
                catch (AgentException error) { blocked = true; Check(error.Message.Contains("正则"), "Invalid-regex error is unclear"); }
                Check(blocked, "search_files accepted an invalid regex");
                string schema = AgentTools.ToolDefinitions();
                Check(schema.Contains("edit_file") && schema.Contains("create_file") && schema.Contains("replace_all") && schema.Contains("depth"), "Tool schema omitted the new tools or options");
            });
            Test("Harness approval diff stays a bounded line diff for large files", delegate {
                string diffRoot = Path.Combine(folder, "diff-budget");
                Directory.CreateDirectory(diffRoot);
                string doc = Path.Combine(diffRoot, "large.md");
                var lines = new List<string>();
                for (int index = 1; index <= 2000; index++) lines.Add("line " + index);
                File.WriteAllText(doc, String.Join("\n", lines) + "\n", new UTF8Encoding(false));
                var diffScope = new AgentWorkspace(diffRoot, doc);
                var diffBridge = new HarnessNativeTools(Path.Combine(folder, "diff-data"), () => diffScope, null, null, null);
                diffBridge.Execute("pin", "diff-session", null, new Dictionary<string,object>{{"cwd", diffRoot}}, null);
                var proposal = (Dictionary<string,object>)diffBridge.Execute("tool", "diff-session", "edit_file",
                    new Dictionary<string,object>{{"path", "large.md"},{"old_text", "line 1000"},{"new_text", "LINE 1000"}}, null);
                string diff = (string)proposal["diff"];
                Check(diff.Contains("-line 1000") && diff.Contains("+LINE 1000"), "Large-file diff lost the changed lines");
                Check(!diff.Contains("line 500") && !diff.Contains("line 1500"), "Large-file diff printed the whole file");
                Check(diff.Length <= 9000, "Large-file diff exceeded its budget");
                diffBridge.Execute("apply", "diff-session", null, null, (string)proposal["id"]);
                Check(File.ReadAllText(doc).Contains("LINE 1000"), "Large-file edit was not applied");
            });
            SdkCoreTests(folder);
        }

        static readonly JavaScriptSerializer SdkJson = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };

        static Dictionary<string, object> Json(string line)
        {
            return SdkJson.Deserialize<Dictionary<string, object>>(line);
        }

        static ModelConfig SdkTestConfig()
        {
            return new ModelConfig { BaseUrl = "http://127.0.0.1:9/v1", ApiKey = "sk-test-key-1234", ModelName = "probe" };
        }

        static object[] Blocks(string text)
        {
            return new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", text } } };
        }

        static Dictionary<string, object> SdkMessage(string role, object[] content)
        {
            return new Dictionary<string, object> { { "role", role }, { "content", content } };
        }

        static void SdkCoreTests(string folder)
        {
            Test("Temporary history has a shared memory budget across conversations", delegate
            {
                var store = new AgentSessionStore(Path.Combine(folder, "bounded-memory-history"), true);
                string draft = new string('x', 2 * 1024 * 1024);
                bool blocked = false;
                for (int index = 0; index < 9; index++)
                {
                    try { store.Save(new AgentSession { Draft = draft }); }
                    catch (AgentException) { blocked = true; break; }
                }
                Check(blocked, "Many individually bounded sessions exceeded the shared 32 MiB history budget");
                store.Clear();
                store.Save(new AgentSession { Draft = draft });
                Check(store.List().Count == 1, "Clearing memory did not release the shared budget");
            });
            Test("Revoked turn cancellation cannot add state to a replacement session", delegate
            {
                var fake = new FakeSdkRuntime();
                var session = new AgentSession();
                using (var cancellation = new CancellationTokenSource())
                using (var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(folder, null),
                    null, null, null, null, session, fake, true, Path.Combine(folder, "sdk-revoked-turn")))
                {
                    Task asking = controller.AskAsync("old turn", cancellation.Token);
                    controller.RevokeFileAccess();
                    session.HarnessSessionId = "replacement";
                    session.Entries.Clear();
                    session.State = "idle";
                    cancellation.Cancel();
                    try { asking.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                    Check(session.Entries.Count == 0 && session.State == "idle", "Old turn settlement changed the replacement session");
                }
            });
            Test("Agent change set combines a file and refuses conflicting undo", delegate
            {
                string editRoot = Path.Combine(folder, "change-set");
                Directory.CreateDirectory(editRoot);
                string file = Path.Combine(editRoot, "note.txt");
                var changes = new AgentChangeSet();
                File.WriteAllText(file, "second");
                changes.Record(new WriteProposal { Path = file, RelativePath = "note.txt", OldText = "first", NewText = "second" });
                File.WriteAllText(file, "third");
                changes.Record(new WriteProposal { Path = file, RelativePath = "note.txt", OldText = "second", NewText = "third" });
                Check(changes.Entries.Count == 1 && changes.Entries[0].OldText == "first" && changes.Entries[0].NewText == "third", "Repeated file edits were not combined");
                File.WriteAllText(file, "external change");
                bool blocked = false;
                try { changes.Revert(file, new AgentWorkspace(editRoot, file), null, null); }
                catch (AgentException) { blocked = true; }
                Check(blocked && File.ReadAllText(file) == "external change" && changes.Entries.Count == 1, "Undo overwrote an external edit or lost the checkpoint");
                File.WriteAllText(file, "third");
                changes.Record(new WriteProposal { Path = file, RelativePath = "note.txt", OldText = "second", NewText = "third" });
                changes.Revert(file, new AgentWorkspace(editRoot, file), null, null);
                Check(File.ReadAllText(file) == "second" && changes.Entries.Count == 0, "Safe undo did not restore its retained checkpoint");
            });
            Test("Legacy archive cleanup removes both stores and preserves documents and live temporary data", delegate
            {
                string install = Path.Combine(folder, "legacy-cleanup");
                Directory.CreateDirectory(Path.Combine(install, "agent-history"));
                Directory.CreateDirectory(Path.Combine(install, "agent-data", "sessions"));
                Directory.CreateDirectory(Path.Combine(install, "harness-data"));
                File.WriteAllText(Path.Combine(install, "agent-history", "old.json"), "synthetic UI archive");
                File.WriteAllText(Path.Combine(install, "agent-data", "sessions", "old.jsonl"), "synthetic SDK archive");
                File.WriteAllText(Path.Combine(install, "note.md"), "document");
                File.WriteAllText(Path.Combine(install, "harness-data", "live.txt"), "active temporary runtime");
                LegacyAgentArchives.Clear(install);
                Check(!Directory.Exists(Path.Combine(install, "agent-data")) && !Directory.Exists(Path.Combine(install, "agent-history")), "Cleanup left an archive store");
                Check(File.ReadAllText(Path.Combine(install, "note.md")) == "document" && Directory.Exists(Path.Combine(install, "harness-data")), "Cleanup touched documents or the active runtime");
            });
            Test("Queued SDK events cannot populate a replacement conversation", delegate
            {
                var queue = new QueuedSdkContext();
                SynchronizationContext previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(queue);
                try
                {
                    var fake = new FakeSdkRuntime();
                    var session = new AgentSession { HarnessSessionId = "old-session" };
                    using (var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(folder, null),
                        null, null, null, null, session, fake, true, Path.Combine(folder, "sdk-queued-data")))
                    {
                        fake.EmitEvent("old-session", "user/message", new Dictionary<string, object> {
                            { "source", new Dictionary<string, object> { { "kind", "user" } } }, { "content", Blocks("old private text") }
                        });
                        session.HarnessSessionId = "new-session";
                        queue.Drain();
                        Check(session.Entries.Count == 0, "Queued old event entered a replacement session");
                        session.HarnessSessionId = "";
                        fake.EmitEvent("old-session", "session/title", new Dictionary<string, object> { { "title", "old private title" } });
                        queue.Drain();
                        Check(session.Title != "old private title", "Unbound conversation accepted an old session title");
                    }
                }
                finally { SynchronizationContext.SetSynchronizationContext(previous); }
            });
            Test("Native write rechecks revoked policy at its final publication", delegate
            {
                string editRoot = Path.Combine(folder, "final-write-guard");
                Directory.CreateDirectory(editRoot);
                string file = Path.Combine(editRoot, "note.txt");
                File.WriteAllText(file, "original");
                bool revoke = false;
                HarnessNativeTools bridge = null;
                bridge = new HarnessNativeTools(Path.Combine(folder, "final-write-data"), () => new AgentWorkspace(editRoot, file),
                    null, path => { if (revoke) bridge.Permission = HarnessFilePermission.ReadOnly; return false; }, null, false);
                bridge.Execute("pin", "final-write", null, null, null);
                var proposal = (Dictionary<string, object>)bridge.Execute("tool", "final-write", "write_file",
                    new Dictionary<string, object> { { "path", file }, { "content", "must not publish" } }, null);
                revoke = true;
                bool blocked = false;
                try { bridge.Execute("apply", "final-write", null, null, Convert.ToString(proposal["id"])); }
                catch (AgentException) { blocked = true; }
                Check(blocked && File.ReadAllText(file) == "original", "Revoked write still replaced the file");
            });
            Test("Single-file scope refuses directory-junction aliases", delegate
            {
                string real = Path.Combine(folder, "junction-real");
                string alias = Path.Combine(folder, "junction-alias");
                Directory.CreateDirectory(real);
                File.WriteAllText(Path.Combine(real, "note.txt"), "protected");
                string command = "New-Item -ItemType Junction -Path '" + alias.Replace("'", "''") + "' -Target '" + real.Replace("'", "''") + "' | Out-Null";
                using (var process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"" + command + "\"") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                }))
                {
                    process.WaitForExit();
                    Check(process.ExitCode == 0 && Directory.Exists(alias), "Junction fixture could not be created");
                }
                try
                {
                    var scope = new AgentWorkspace(null, Path.Combine(alias, "note.txt"), real);
                    foreach (string path in new[] { ".", Path.Combine(alias, "note.txt") })
                    {
                        bool blocked = false;
                        try { scope.Resolve(path, false); } catch (AgentException) { blocked = true; }
                        Check(blocked, "Single-file alias bypassed reparse protection");
                    }
                }
                finally { if (Directory.Exists(alias)) Directory.Delete(alias); }
            });
            Test("An unbound SDK controller rejects callbacks from a retired session", delegate
            {
                var fake = new FakeSdkRuntime();
                using (var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(folder, null),
                    null, null, null, null, new AgentSession(), fake, true, Path.Combine(folder, "sdk-unbound-data")))
                {
                    foreach (string operation in new[] { "context", "pin", "tool" })
                    {
                        bool blocked = false;
                        try
                        {
                            fake.NativeHandler(new Dictionary<string, object> {
                                { "operation", operation }, { "sessionId", "retired-scope-session" }, { "name", "list_files" },
                                { "args", new Dictionary<string, object> { { "cwd", folder } } }
                            }, CancellationToken.None).GetAwaiter().GetResult();
                        }
                        catch (AgentException) { blocked = true; }
                        Check(blocked, "Unbound controller adopted a retired " + operation + " callback");
                    }
                }
            });
            Test("SDK forwards live public text before the completed assistant message", delegate
            {
                var fake = new FakeSdkRuntime();
                var session = new AgentSession();
                var deltas = new StringBuilder();
                bool finalized = false;
                using (var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(folder, null),
                    null, null, null, null, session, fake, true, Path.Combine(folder, "sdk-delta-data")))
                {
                    controller.Activity += activity => {
                        if (activity.Delta != null) deltas.Append(activity.Delta);
                        if (activity.State == "assistant-final") finalized = true;
                    };
                    fake.Prompt = parameters => {
                        string id = SdkValues.Text(parameters, "sessionId");
                        fake.Emit("qingye/assistant.start", new Dictionary<string, object> { { "sessionId", id } });
                        fake.Emit("qingye/assistant.delta", new Dictionary<string, object> { { "sessionId", id }, { "text", "公开回复" } });
                        Check(deltas.ToString() == "公开回复" && !finalized, "Live text was withheld until settlement");
                        fake.EmitEvent(id, "assistant/message", new Dictionary<string, object> { { "message", SdkMessage("assistant", Blocks("公开回复")) } });
                        fake.EmitEvent(id, "turn/end", new Dictionary<string, object> { { "reason", new Dictionary<string, object> { { "kind", "completed" } } } });
                    };
                    controller.AskAsync("reply", CancellationToken.None).GetAwaiter().GetResult();
                    Check(finalized && session.History().Count == 1, "Completed text was duplicated or not finalized");
                }
            });
            Test("SDK cancels the current turn while retaining its warm runtime", delegate
            {
                var fake = new FakeSdkRuntime();
                var session = new AgentSession();
                using (var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(folder, null),
                    null, null, null, null, session, fake, true, Path.Combine(folder, "sdk-cancel-data")))
                {
                    Task asking = controller.AskAsync("wait", CancellationToken.None);
                    controller.CancelCurrentTurnAsync().GetAwaiter().GetResult();
                    Check(asking.Wait(2000), "Cancelled turn did not settle");
                    Check(fake.Running && fake.FirstIndex("qingye/session/cancel") >= 0, "Stop terminated the warm runtime");
                    Check(session.State == "stopped", "Cancelled turn was not marked stopped");
                }
            });
            Test("Memory session history never creates archives and owns detached entries", delegate
            {
                string memoryPath = Path.Combine(folder, "never-create-memory-history");
                var store = new AgentSessionStore(memoryPath, true);
                var session = new AgentSession { Title = "temporary", Draft = "unsent draft" };
                session.Record(new Dictionary<string, object> { { "role", "user" }, { "content", "first" } });
                store.Save(session);
                session.Entries.Clear();
                Check(store.Load(session.Id).Entries.Count == 1, "Memory history shares its mutable list with the live session");
                Check(store.List().Count == 1 && !Directory.Exists(memoryPath), "Temporary history created files");
                store.Delete(session.Id);
                Check(store.List().Count == 0, "Deleted temporary conversation stayed in memory");
                store.Save(session);
                store.Clear();
                Check(store.List().Count == 0 && !Directory.Exists(memoryPath), "Clear recreated an archive directory");
            });
            Test("SDK editor scope protects its executing runtime and data", delegate
            {
                string runtimeDirectory = HarnessRuntime.Locate(AppDomain.CurrentDomain.BaseDirectory);
                string scopeRoot = Path.GetDirectoryName(runtimeDirectory);
                string dataHome = Path.Combine(folder, "sdk-protected-data");
                Directory.CreateDirectory(dataHome);
                string privateFile = Path.Combine(dataHome, "probe.txt");
                File.WriteAllText(privateFile, "synthetic private state");
                var session = new AgentSession { HarnessSessionId = "protection-probe" };
                var fake = new FakeSdkRuntime();
                using (var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(scopeRoot, null),
                    null, null, null, null, session, fake, true, dataHome))
                {
                    fake.NativeHandler(new Dictionary<string, object> {
                        { "operation", "pin" }, { "sessionId", session.HarnessSessionId },
                        { "args", new Dictionary<string, object> { { "cwd", scopeRoot } } }
                    }, CancellationToken.None).GetAwaiter().GetResult();
                    foreach (string target in new[] { Path.Combine(runtimeDirectory, "native-tools.mjs"), privateFile })
                    {
                        foreach (string tool in new[] { "read_file", "write_file" })
                        {
                            bool blocked = false;
                            try
                            {
                                fake.NativeHandler(new Dictionary<string, object> {
                                    { "operation", "tool" }, { "sessionId", session.HarnessSessionId }, { "name", tool },
                                    { "args", new Dictionary<string, object> { { "path", target }, { "content", "never applied" } } }
                                }, CancellationToken.None).GetAwaiter().GetResult();
                            }
                            catch (AgentException) { blocked = true; }
                            Check(blocked, tool + " allowed the editor runtime/data: " + target);
                        }
                    }
                }
            });
            Test("SDK controller starts inside its supplied data home", delegate
            {
                string dataHome = Path.Combine(folder, "sdk-injected-home");
                var fake = new FakeSdkRuntime();
                using (var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(folder, null),
                    null, null, null, null, new AgentSession(), fake, true, dataHome))
                {
                    controller.EnsureStartedAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Check(fake.StartedHome == dataHome, "Runtime ignored its isolated data directory");
                }
            });
            Test("SDK JSON-RPC channel correlates frames, dispatches notifications and answers server requests", delegate
            {
                var reader = new QueueTextReader();
                var writer = new QueueTextWriter();
                var channel = new SdkJsonRpcChannel(reader, writer,
                    delegate(string text) { return ModelConfig.Redact(text, "sk-hidden-key-123456"); });
                var notifications = new List<string>();
                channel.Notification += delegate(string method, Dictionary<string, object> parameters)
                {
                    lock (notifications) notifications.Add(method + ":" + SdkValues.Text(parameters, "sessionId"));
                };
                channel.ServerRequestHandler = delegate(string method, Dictionary<string, object> parameters, CancellationToken token)
                {
                    return Task.FromResult<object>(new Dictionary<string, object> { { "outcome", "allowed-once" } });
                };
                channel.Start();
                try
                {
                    Task<Dictionary<string, object>> pending = channel.RequestAsync("initialize",
                        new Dictionary<string, object> { { "model", "probe" } }, CancellationToken.None, 5000);
                    string line = writer.ReadLine(5000);
                    Check(line != null, "JSON-RPC request was not written");
                    Dictionary<string, object> frame = Json(line);
                    Check(Convert.ToString(frame["jsonrpc"]) == "2.0" && Convert.ToString(frame["method"]) == "initialize", "JSON-RPC request frame is malformed");
                    Check(SdkValues.Map(frame["params"]) != null, "JSON-RPC request lost its params");
                    int id = Convert.ToInt32(frame["id"]);
                    reader.Push("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"serverInfo\":{\"name\":\"deepseek-harness-sdk-runtime\"}}}");
                    Dictionary<string, object> result = pending.GetAwaiter().GetResult();
                    Dictionary<string, object> info = SdkValues.Map(result["serverInfo"]);
                    Check(info != null && SdkValues.Text(info, "name") == "deepseek-harness-sdk-runtime", "JSON-RPC response was not correlated");
                    reader.Push("{\"jsonrpc\":\"2.0\",\"method\":\"session.event\",\"params\":{\"sessionId\":\"s-1\",\"event\":{\"type\":\"turn/start\"}}}");
                    Check(SpinWait.SpinUntil(delegate { lock (notifications) return notifications.Contains("session.event:s-1"); }, 3000), "Notification was not dispatched");
                    reader.Push("{\"jsonrpc\":\"2.0\",\"id\":\"approval-9\",\"method\":\"approval/request\",\"params\":{\"sessionId\":\"s-1\"}}");
                    string answerLine = writer.ReadLine(5000);
                    Check(answerLine != null, "Server request was not answered");
                    Dictionary<string, object> answer = Json(answerLine);
                    Check(Convert.ToString(answer["id"]) == "approval-9", "Server request answer lost its id");
                    Dictionary<string, object> answerResult = SdkValues.Map(answer["result"]);
                    Check(answerResult != null && SdkValues.Text(answerResult, "outcome") == "allowed-once", "Server request answer lost its outcome");
                    Task<Dictionary<string, object>> failing = channel.RequestAsync("session/wait", null, CancellationToken.None, 5000);
                    Dictionary<string, object> failingFrame = Json(writer.ReadLine(5000));
                    reader.Push("{\"jsonrpc\":\"2.0\",\"id\":" + Convert.ToInt32(failingFrame["id"]) + ",\"error\":{\"code\":-32603,\"message\":\"bad key sk-hidden-key-123456\"}}");
                    bool rejected = false;
                    try { failing.GetAwaiter().GetResult(); }
                    catch (AgentException error) { rejected = !error.Message.Contains("sk-hidden-key-123456") && error.Message.Contains("sk-***"); }
                    Check(rejected, "JSON-RPC error frame lost its message or leaked the key");
                    bool timedOut = false;
                    try { channel.RequestAsync("session/wait", null, CancellationToken.None, 120).GetAwaiter().GetResult(); }
                    catch (AgentException error) { timedOut = error.Message.Contains("超时"); }
                    Check(timedOut, "JSON-RPC request timeout was not reported");
                }
                finally { channel.Dispose(); reader.Complete(); writer.Dispose(); }
            });

            Test("SDK controller maps session events to native entries and activity", delegate
            {
                string mapRoot = Path.Combine(folder, "sdk-map");
                Directory.CreateDirectory(mapRoot);
                string doc = Path.Combine(mapRoot, "note.md");
                File.WriteAllText(doc, "original", new UTF8Encoding(false));
                var session = new AgentSession();
                var fake = new FakeSdkRuntime();
                var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(mapRoot, doc), null, null,
                    null, null, session, fake, true, Path.Combine(folder, "sdk-map-data"));
                var states = new List<string>();
                controller.Activity += delegate(AgentActivity activity) { lock (states) states.Add(activity.State); };
                try
                {
                    fake.Prompt = delegate(Dictionary<string, object> parameters)
                    {
                        string runningId = SdkValues.Text(parameters, "sessionId");
                        fake.Emit("session.status", new Dictionary<string, object> { { "sessionId", runningId }, { "status", "running" } });
                        fake.EmitEvent(runningId, "turn/start", new Dictionary<string, object> { { "turn", 1 } });
                        fake.EmitEvent(runningId, "step/start", new Dictionary<string, object> { { "turn", 1 }, { "step", 1 } });
                        fake.EmitEvent(runningId, "user/message", new Dictionary<string, object>
                        {
                            { "source", new Dictionary<string, object> { { "kind", "user" } } },
                            { "content", Blocks("请改文件") }
                        });
                        fake.EmitEvent(runningId, "assistant/message", new Dictionary<string, object>
                        {
                            { "turn", 1 }, { "step", 1 }, { "message", SdkMessage("assistant", Blocks("先看看文件")) }
                        });
                        fake.EmitEvent(runningId, "tool/call", new Dictionary<string, object>
                        {
                            { "turn", 1 }, { "step", 1 }, { "callId", "c1" }, { "name", "read_file" }, { "arguments", "{\"path\":\"note.md\"}" }
                        });
                        fake.EmitEvent(runningId, "tool/result", new Dictionary<string, object>
                        {
                            { "turn", 1 }, { "step", 1 },
                            { "message", new Dictionary<string, object> { { "role", "tool" }, { "toolCallId", "c1" }, { "content", Blocks("original") } } }
                        });
                        fake.EmitEvent(runningId, "assistant/message", new Dictionary<string, object>
                        {
                            { "turn", 1 }, { "step", 1 }, { "message", SdkMessage("assistant", Blocks("已读取 note.md")) }
                        });
                        fake.EmitEvent(runningId, "session/title", new Dictionary<string, object> { { "title", "测试标题" } });
                        fake.EmitEvent(runningId, "turn/end", new Dictionary<string, object>
                        {
                            { "turn", 1 }, { "reason", new Dictionary<string, object> { { "kind", "completed" } } }
                        });
                        fake.Emit("session.status", new Dictionary<string, object> { { "sessionId", runningId }, { "status", "idle" } });
                    };
                    controller.AskAsync("请改文件", CancellationToken.None).GetAwaiter().GetResult();
                    Check(session.State == "completed", "turn/end did not settle the session state");
                    Check(session.Title == "测试标题", "session/title was not applied");
                    Check(session.Entries.Exists(delegate(AgentEntry entry)
                    {
                        return entry.Kind == "message" && Convert.ToString(entry.Message["role"]) == "user" && Convert.ToString(entry.Message["content"]) == "请改文件";
                    }), "user message was not recorded");
                    Check(session.Entries.Exists(delegate(AgentEntry entry)
                    {
                        return entry.Kind == "message" && Convert.ToString(entry.Message["role"]) == "assistant" && Convert.ToString(entry.Message["content"]) == "已读取 note.md";
                    }), "assistant message was not recorded");
                    Check(session.Entries.Exists(delegate(AgentEntry entry)
                    {
                        return entry.Kind == "tool-start" && entry.CallId == "c1" && entry.ToolName == "read_file" && entry.Text.Contains("note.md");
                    }), "tool call was not mapped");
                    Check(session.Entries.Exists(delegate(AgentEntry entry)
                    {
                        return entry.Kind == "message" && Convert.ToString(entry.Message["role"]) == "tool" && Convert.ToString(entry.Message["tool_call_id"]) == "c1";
                    }), "tool result was not recorded");
                    Check(session.Entries.Exists(delegate(AgentEntry entry) { return entry.Kind == "run-start"; }), "run start entry is missing");
                    Check(session.Entries.Exists(delegate(AgentEntry entry) { return entry.Kind == "run-end"; }), "run end entry is missing");
                    Check(session.HarnessSessionId.Length == 32, "a new session id was not generated");
                    Check(fake.Calls.Contains("initialize") && fake.Calls.Contains("session/prompt"), "controller skipped initialize or prompt");
                    Dictionary<string, object> init = fake.ParameterOf("initialize");
                    Dictionary<string, object> capabilities = SdkValues.Map(init["capabilities"]);
                    Check(SdkValues.Text(init, "provider") == "qingye-model" && SdkValues.Text(init, "model") == "probe", "initialize provider/model are wrong");
                    Check(!Convert.ToBoolean(init["approvalRequests"]), "initialize enabled JSON-RPC approval");
                    Check(capabilities != null && !Convert.ToBoolean(capabilities["userQuestions"]), "initialize enabled user questions");
                    Check(String.Equals(Convert.ToString(init["cwd"]), mapRoot, StringComparison.OrdinalIgnoreCase), "initialize cwd does not follow the workspace");
                    Check(fake.Count("session/working-directory/set") == 1, "session working directory was not set");
                    Check(fake.FirstIndex("session/working-directory/set") < fake.FirstIndex("session/prompt"), "prompt ran before the session working directory was set");
                    Check(String.Equals(Convert.ToString(fake.ParameterOf("session/working-directory/set")["path"]), mapRoot, StringComparison.OrdinalIgnoreCase), "session working directory is not the workspace root");
                    Check(controller.ProcessId == 4321, "ProcessId was not forwarded from the runtime");
                    lock (states) Check(states.Contains("requesting") && states.Contains("tool") && states.Contains("tool-result") && states.Contains("completed"), "activity states are incomplete");
                }
                finally { controller.Dispose(); }
            });

            Test("SDK controller resumes a persisted session once and keeps Ask idempotent", delegate
            {
                string resumeRoot = Path.Combine(folder, "sdk-resume");
                Directory.CreateDirectory(resumeRoot);
                string doc = Path.Combine(resumeRoot, "note.md");
                File.WriteAllText(doc, "original", new UTF8Encoding(false));
                string home = Path.Combine(folder, "sdk-resume-data");
                var session = new AgentSession { HarnessSessionId = "persisted-1" };
                var fake = new FakeSdkRuntime { Running = true, Initialized = true };
                var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(resumeRoot, doc), null, null,
                    null, null, session, fake, true, home);
                try
                {
                    fake.Prompt = delegate(Dictionary<string, object> parameters)
                    {
                        fake.EmitEvent(SdkValues.Text(parameters, "sessionId"), "turn/end", new Dictionary<string, object>
                        {
                            { "turn", 1 }, { "reason", new Dictionary<string, object> { { "kind", "completed" } } }
                        });
                    };
                    controller.AskAsync("继续", CancellationToken.None).GetAwaiter().GetResult();
                    Check(fake.Count("session/resume") == 1, "persisted session was not resumed before the first prompt");
                    Check(fake.Count("initialize") == 0, "a warm runtime was initialized again");
                    Check(String.Equals(Convert.ToString(fake.ParameterOf("session/working-directory/set")["path"]), resumeRoot, StringComparison.OrdinalIgnoreCase), "resumed session did not follow the editor workspace");
                    controller.AskAsync("再继续", CancellationToken.None).GetAwaiter().GetResult();
                    Check(fake.Count("session/resume") == 1, "the second turn resumed an already-active session");
                    string created = controller.NewSessionAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Check(created != "persisted-1" && controller.HarnessSessionId == created && session.Entries.Count == 0, "NewSessionAsync did not reset the conversation");
                    controller.ResumeAsync("persisted-1", CancellationToken.None).GetAwaiter().GetResult();
                    Check(fake.Count("session/resume") == 1, "resuming an already-active session sent another request");

                    var secondFake = new FakeSdkRuntime { Running = true, Initialized = true };
                    var secondSession = new AgentSession { HarnessSessionId = "resume-me" };
                    var second = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(resumeRoot, doc), null, null,
                        null, null, secondSession, secondFake, true, home);
                    try
                    {
                        second.ResumeAsync("resume-me", CancellationToken.None).GetAwaiter().GetResult();
                        Check(secondFake.Count("session/resume") == 1, "explicit ResumeAsync did not reach the runtime");
                        secondFake.Prompt = delegate(Dictionary<string, object> parameters)
                        {
                            secondFake.EmitEvent(SdkValues.Text(parameters, "sessionId"), "turn/end", new Dictionary<string, object>
                            {
                                { "turn", 1 }, { "reason", new Dictionary<string, object> { { "kind", "completed" } } }
                            });
                        };
                        second.AskAsync("继续", CancellationToken.None).GetAwaiter().GetResult();
                        Check(secondFake.Count("session/resume") == 1, "AskAsync resumed after an explicit ResumeAsync");
                        secondFake.ResumeFailure = new AgentException("session missing");
                        var thirdSession = new AgentSession { HarnessSessionId = "broken-1" };
                        var third = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(resumeRoot, doc), null, null,
                            null, null, thirdSession, secondFake, true, home);
                        bool friendly = false;
                        try { third.ResumeAsync("broken-1", CancellationToken.None).GetAwaiter().GetResult(); }
                        catch (AgentException error)
                        {
                            friendly = error.Message.Contains("恢复会话失败") && error.Message.Contains("原来的文件或工作区");
                        }
                        Check(friendly, "resume failure is not actionable");
                        third.Dispose();
                    }
                    finally { second.Dispose(); }
                }
                finally { controller.Dispose(); }
            });

            Test("SDK controller rebinds the session directory before prompt when the runtime was prewarmed", delegate
            {
                string scopeRoot = Path.Combine(folder, "sdk-rebind");
                Directory.CreateDirectory(scopeRoot);
                string doc = Path.Combine(scopeRoot, "note.md");
                File.WriteAllText(doc, "original", new UTF8Encoding(false));
                string staleHome = Path.Combine(folder, "sdk-rebind-home");
                Directory.CreateDirectory(staleHome);
                var session = new AgentSession { Root = scopeRoot, CurrentFile = doc };
                var fake = new FakeSdkRuntime { Running = true, Initialized = true, InitializedCwdValue = staleHome, Pid = 4321 };
                var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(scopeRoot, doc), null, null,
                    null, null, session, fake, true, Path.Combine(folder, "sdk-rebind-data"));
                string pinError = null;
                object pinValue = null;
                object readValue = null;
                try
                {
                    fake.Prompt = delegate(Dictionary<string, object> parameters)
                    {
                        string runningId = SdkValues.Text(parameters, "sessionId");
                        // The SDK creates the agent while session/working-directory/set
                        // is still applying, so its agent/created pin carries the old
                        // process initialize cwd.
                        var pin = new Dictionary<string, object>
                        {
                            { "id", "1" }, { "operation", "pin" }, { "sessionId", runningId },
                            { "args", new Dictionary<string, object> { { "cwd", staleHome } } }
                        };
                        try { pinValue = fake.NativeHandler(pin, CancellationToken.None).GetAwaiter().GetResult(); }
                        catch (Exception error) { pinError = error.Message; }
                        var read = new Dictionary<string, object>
                        {
                            { "id", "2" }, { "operation", "tool" }, { "sessionId", runningId }, { "name", "read_file" },
                            { "args", new Dictionary<string, object> { { "path", "note.md" } } }
                        };
                        readValue = fake.NativeHandler(read, CancellationToken.None).GetAwaiter().GetResult();
                        fake.EmitEvent(runningId, "turn/end", new Dictionary<string, object>
                        {
                            { "turn", 1 }, { "reason", new Dictionary<string, object> { { "kind", "completed" } } }
                        });
                    };
                    controller.AskAsync("读取", CancellationToken.None).GetAwaiter().GetResult();
                    Check(pinError == null && pinValue != null, "stale agent pin failed the turn: " + pinError);
                    Check(readValue != null && Convert.ToString(readValue).Contains("original"), "native scope did not follow the editor workspace");
                    Check(fake.Count("session/working-directory/set") == 1, "session directory was not set");
                    Check(fake.FirstIndex("session/working-directory/set") < fake.FirstIndex("session/prompt"), "prompt ran before the session directory was set");
                    Check(String.Equals(Convert.ToString(fake.ParameterOf("session/working-directory/set")["path"]), scopeRoot, StringComparison.OrdinalIgnoreCase), "session directory is not the workspace root");
                }
                finally { controller.Dispose(); }
            });

            Test("SDK controller refuses to prompt when the session directory cannot be set", delegate
            {
                string scopeRoot = Path.Combine(folder, "sdk-set-failure");
                Directory.CreateDirectory(scopeRoot);
                string doc = Path.Combine(scopeRoot, "note.md");
                File.WriteAllText(doc, "original", new UTF8Encoding(false));
                var session = new AgentSession { Root = scopeRoot, CurrentFile = doc };
                var fake = new FakeSdkRuntime { Running = true, Initialized = true };
                fake.SetDirectoryFailure = new AgentException("working directory service unavailable");
                var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(scopeRoot, doc), null, null,
                    null, null, session, fake, true, Path.Combine(folder, "sdk-set-failure-data"));
                bool prompted = false;
                fake.Prompt = delegate { prompted = true; };
                try
                {
                    bool failed = false;
                    try { controller.AskAsync("你好", CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (AgentException error)
                    {
                        failed = error.Message.Contains("无法把 Agent 会话切换到当前工作区") && error.Message.Contains("原文件或原工作区");
                    }
                    Check(failed, "directory set failure did not surface a Chinese error");
                    Check(!prompted && fake.Count("session/prompt") == 0, "prompt was sent after a failed directory set");
                    bool newSessionFailed = false;
                    try { controller.NewSessionAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (AgentException error) { newSessionFailed = error.Message.Contains("无法把 Agent 会话切换到当前工作区"); }
                    Check(newSessionFailed, "NewSessionAsync accepted a failed directory set");
                    bool resumeFailed = false;
                    try { controller.ResumeAsync("persisted-x", CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (AgentException error) { resumeFailed = error.Message.Contains("无法把 Agent 会话切换到当前工作区"); }
                    Check(resumeFailed, "ResumeAsync accepted a failed directory set");
                }
                finally { controller.Dispose(); }
            });

            Test("SDK bridge approval allows a confirmed write and applies it once", delegate
            {
                string editRoot = Path.Combine(folder, "sdk-bridge-allow");
                Directory.CreateDirectory(editRoot);
                string doc = Path.Combine(editRoot, "note.md");
                File.WriteAllText(doc, "alpha\nbeta\ngamma\n", new UTF8Encoding(false));
                var session = new AgentSession { HarnessSessionId = "bridge-session" };
                var fake = new FakeSdkRuntime();
                var approver = new RecordingApprover { Allow = true };
                bool applied = false;
                var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(editRoot, doc), null, null,
                    approver, delegate(WriteProposal proposal) { applied = true; }, session, fake, true, Path.Combine(folder, "sdk-bridge-allow-data"));
                var bridge = new SdkNativeBridge(fake.NativeHandler, delegate(string text) { return text; }, "bridge-token");
                bridge.Start();
                try
                {
                    using (BridgeClient client = BridgeClient.Connect(bridge.Port, "bridge-token"))
                    {
                        Dictionary<string, object> pin = client.Send(new Dictionary<string, object>
                        {
                            { "id", 1 }, { "operation", "pin" }, { "sessionId", "bridge-session" },
                            { "args", new Dictionary<string, object> { { "cwd", editRoot } } }
                        });
                        Check(pin.ContainsKey("value"), "bridge pin failed: " + BridgeError(pin));
                        Dictionary<string, object> tool = client.Send(new Dictionary<string, object>
                        {
                            { "id", 2 }, { "operation", "tool" }, { "sessionId", "bridge-session" }, { "name", "edit_file" },
                            { "args", new Dictionary<string, object> { { "path", "note.md" }, { "old_text", "beta" }, { "new_text", "BETA" } } }
                        });
                        Dictionary<string, object> proposal = SdkValues.Map(tool["value"]);
                        Check(proposal != null && Convert.ToBoolean(proposal["requiresApproval"]), "write tool did not ask for approval: " + BridgeError(tool));
                        string proposalId = SdkValues.Text(proposal, "id");
                        Check(File.ReadAllText(doc).Contains("beta"), "tool wrote before approval");
                        Dictionary<string, object> approval = client.Send(new Dictionary<string, object>
                        {
                            { "id", 3 }, { "operation", "approval" }, { "sessionId", "bridge-session" }, { "name", "edit_file" },
                            { "path", "note.md" }, { "diff", Convert.ToString(proposal["diff"]) }, { "proposalId", proposalId }
                        });
                        Dictionary<string, object> decision = SdkValues.Map(approval["value"]);
                        Check(decision != null && Convert.ToBoolean(decision["allowed"]), "bridge approval was not allowed: " + BridgeError(approval));
                        Check(approver.Calls == 1 && approver.Proposal != null && approver.Proposal.RelativePath == "note.md", "approver did not receive the pending proposal");
                        Check(File.ReadAllText(doc).Contains("beta"), "approval wrote the file before apply");
                        Dictionary<string, object> apply = client.Send(new Dictionary<string, object>
                        {
                            { "id", 4 }, { "operation", "apply" }, { "sessionId", "bridge-session" }, { "name", "edit_file" },
                            { "proposal", proposalId }, { "args", new Dictionary<string, object> { { "path", "note.md" } } }
                        });
                        Check(apply.ContainsKey("value") && Convert.ToString(apply["value"]).Contains("note.md"), "bridge apply failed: " + BridgeError(apply));
                        Check(File.ReadAllText(doc).Replace("\r\n", "\n") == "alpha\nBETA\ngamma\n", "bridge apply did not write the approved edit");
                        Check(applied, "applied callback was not called");
                    }
                }
                finally { bridge.Stop(); controller.Dispose(); }
            });

            Test("SDK bridge approval rejects without writing and validates the pending proposal", delegate
            {
                string editRoot = Path.Combine(folder, "sdk-bridge-reject");
                Directory.CreateDirectory(editRoot);
                string doc = Path.Combine(editRoot, "note.md");
                File.WriteAllText(doc, "original", new UTF8Encoding(false));
                var session = new AgentSession { HarnessSessionId = "reject-session" };
                var fake = new FakeSdkRuntime();
                var approver = new RecordingApprover { Allow = false };
                bool applied = false;
                var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(editRoot, doc), null, null,
                    approver, delegate(WriteProposal proposal) { applied = true; }, session, fake, true, Path.Combine(folder, "sdk-bridge-reject-data"));
                var bridge = new SdkNativeBridge(fake.NativeHandler, delegate(string text) { return text; }, "reject-token");
                bridge.Start();
                try
                {
                    using (BridgeClient client = BridgeClient.Connect(bridge.Port, "reject-token"))
                    {
                        client.Send(new Dictionary<string, object>
                        {
                            { "id", 1 }, { "operation", "pin" }, { "sessionId", "reject-session" },
                            { "args", new Dictionary<string, object> { { "cwd", editRoot } } }
                        });
                        Dictionary<string, object> tool = client.Send(new Dictionary<string, object>
                        {
                            { "id", 2 }, { "operation", "tool" }, { "sessionId", "reject-session" }, { "name", "write_file" },
                            { "args", new Dictionary<string, object> { { "path", "note.md" }, { "content", "changed" } } }
                        });
                        Dictionary<string, object> proposal = SdkValues.Map(tool["value"]);
                        string proposalId = SdkValues.Text(proposal, "id");
                        Dictionary<string, object> approval = client.Send(new Dictionary<string, object>
                        {
                            { "id", 3 }, { "operation", "approval" }, { "sessionId", "reject-session" }, { "name", "write_file" },
                            { "path", "note.md" }, { "proposalId", proposalId }
                        });
                        Dictionary<string, object> decision = SdkValues.Map(approval["value"]);
                        Check(decision != null && !Convert.ToBoolean(decision["allowed"]), "rejected approval was not rejected: " + BridgeError(approval));
                        Check(File.ReadAllText(doc) == "original" && !applied, "rejected approval wrote the file");
                        Dictionary<string, object> unknown = client.Send(new Dictionary<string, object>
                        {
                            { "id", 4 }, { "operation", "approval" }, { "sessionId", "reject-session" }, { "name", "write_file" },
                            { "proposalId", "invented" }
                        });
                        Check(unknown.ContainsKey("error") && Convert.ToString(unknown["error"]).Contains("提案"), "unknown proposal was not rejected");
                        Dictionary<string, object> foreign = client.Send(new Dictionary<string, object>
                        {
                            { "id", 5 }, { "operation", "approval" }, { "sessionId", "other-session" }, { "name", "write_file" },
                            { "proposalId", proposalId }
                        });
                        Check(foreign.ContainsKey("error") && Convert.ToString(foreign["error"]).Contains("会话"), "foreign session approval was not rejected");
                        Dictionary<string, object> discard = client.Send(new Dictionary<string, object>
                        {
                            { "id", 6 }, { "operation", "discard" }, { "sessionId", "reject-session" }, { "name", "write_file" },
                            { "proposal", proposalId }, { "args", new Dictionary<string, object> { { "path", "note.md" } } }
                        });
                        Check(discard.ContainsKey("value"), "discard failed: " + BridgeError(discard));
                        Check(File.ReadAllText(doc) == "original" && !applied, "discard changed the file");
                    }
                }
                finally { bridge.Stop(); controller.Dispose(); }
            });

            Test("SDK bridge approval reports timeout without writing", delegate
            {
                string editRoot = Path.Combine(folder, "sdk-bridge-timeout");
                Directory.CreateDirectory(editRoot);
                string doc = Path.Combine(editRoot, "note.md");
                File.WriteAllText(doc, "original", new UTF8Encoding(false));
                var session = new AgentSession { HarnessSessionId = "timeout-session" };
                var fake = new FakeSdkRuntime();
                var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(editRoot, doc), null, null,
                    new SlowApprover(), null, session, fake, true, Path.Combine(folder, "sdk-bridge-timeout-data"));
                controller.ApprovalTimeoutSeconds = 1;
                var bridge = new SdkNativeBridge(fake.NativeHandler, delegate(string text) { return text; }, "timeout-token");
                bridge.Start();
                try
                {
                    using (BridgeClient client = BridgeClient.Connect(bridge.Port, "timeout-token"))
                    {
                        client.Send(new Dictionary<string, object>
                        {
                            { "id", 1 }, { "operation", "pin" }, { "sessionId", "timeout-session" },
                            { "args", new Dictionary<string, object> { { "cwd", editRoot } } }
                        });
                        Dictionary<string, object> tool = client.Send(new Dictionary<string, object>
                        {
                            { "id", 2 }, { "operation", "tool" }, { "sessionId", "timeout-session" }, { "name", "write_file" },
                            { "args", new Dictionary<string, object> { { "path", "note.md" }, { "content", "changed" } } }
                        });
                        string proposalId = SdkValues.Text(SdkValues.Map(tool["value"]), "id");
                        Dictionary<string, object> approval = client.Send(new Dictionary<string, object>
                        {
                            { "id", 3 }, { "operation", "approval" }, { "sessionId", "timeout-session" }, { "name", "write_file" },
                            { "proposalId", proposalId }
                        });
                        Dictionary<string, object> decision = SdkValues.Map(approval["value"]);
                        Check(decision != null && !Convert.ToBoolean(decision["allowed"]), "timed out approval was allowed");
                        Check(SdkValues.Text(decision, "message").Contains("超时"), "timed out approval has no Chinese message");
                        Check(File.ReadAllText(doc) == "original", "timed out approval wrote the file");
                    }
                }
                finally { bridge.Stop(); controller.Dispose(); }
            });

            Test("SDK bridge rejects a bad token before serving native operations", delegate
            {
                string editRoot = Path.Combine(folder, "sdk-bridge-auth");
                Directory.CreateDirectory(editRoot);
                string doc = Path.Combine(editRoot, "note.md");
                File.WriteAllText(doc, "original", new UTF8Encoding(false));
                var session = new AgentSession { HarnessSessionId = "auth-session" };
                var fake = new FakeSdkRuntime();
                var controller = new SdkAgentController(SdkTestConfig(), new AgentWorkspace(editRoot, doc), null, null,
                    null, null, session, fake, true, Path.Combine(folder, "sdk-bridge-auth-data"));
                var bridge = new SdkNativeBridge(fake.NativeHandler, delegate(string text) { return text; }, "right-token");
                bridge.Start();
                try
                {
                    using (var connection = new TcpClient())
                    {
                        connection.Connect(IPAddress.Loopback, bridge.Port);
                        connection.ReceiveTimeout = 1500;
                        var writer = new StreamWriter(connection.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                        var reader = new StreamReader(connection.GetStream(), new UTF8Encoding(false));
                        writer.WriteLine("{\"token\":\"wrong-token\"}");
                        writer.WriteLine("{\"id\":1,\"operation\":\"context\",\"sessionId\":\"auth-session\"}");
                        bool closed = false;
                        try { closed = reader.ReadLine() == null; }
                        catch (IOException) { closed = true; }
                        Check(closed, "bridge served a bad token");
                    }
                    using (BridgeClient client = BridgeClient.Connect(bridge.Port, "right-token"))
                    {
                        Dictionary<string, object> context = client.Send(new Dictionary<string, object>
                        {
                            { "id", 7 }, { "operation", "context" }, { "sessionId", "auth-session" }
                        });
                        Dictionary<string, object> value = SdkValues.Map(context["value"]);
                        Check(value != null && String.Equals(Convert.ToString(value["root"]), editRoot, StringComparison.OrdinalIgnoreCase), "bridge context was wrong: " + BridgeError(context));
                        Dictionary<string, object> stalePin = client.Send(new Dictionary<string, object>
                        {
                            { "id", 8 }, { "operation", "pin" }, { "sessionId", "auth-session" },
                            { "args", new Dictionary<string, object> { { "cwd", folder } } }
                        });
                        Check(stalePin.ContainsKey("error") && Convert.ToString(stalePin["error"]).Contains("工作区"), "a stale bridge cwd was accepted");
                        Dictionary<string, object> goodPin = client.Send(new Dictionary<string, object>
                        {
                            { "id", 9 }, { "operation", "pin" }, { "sessionId", "auth-session" },
                            { "args", new Dictionary<string, object> { { "cwd", editRoot } } }
                        });
                        Check(goodPin.ContainsKey("value"), "bridge pin failed after a rejected scope: " + BridgeError(goodPin));
                    }
                }
                finally { bridge.Stop(); controller.Dispose(); }
            });

            Test("SDK runtime stops its child process and reports an unexpected exit", delegate
            {
                string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                Check(File.Exists(cmd), "cmd.exe is missing");
                string home = Path.Combine(folder, "sdk-runtime-home");
                var runtime = new SdkRuntime(cmd, "/d /c \"more\"", folder, 700);
                try
                {
                    runtime.StartAsync(folder, home, SdkTestConfig(), CancellationToken.None).GetAwaiter().GetResult();
                    Check(runtime.IsRunning, "SDK runtime did not start");
                    int pid = runtime.ProcessId;
                    Check(pid != 0, "SDK runtime has no process id");
                    runtime.StopAsync().GetAwaiter().GetResult();
                    Check(!runtime.IsRunning, "SDK runtime is still running after stop");
                    Check(runtime.ProcessId == 0, "SDK runtime kept its process id after stop");
                    bool exited = false;
                    try { using (Process child = Process.GetProcessById(pid)) { exited = child.HasExited; } }
                    catch (ArgumentException) { exited = true; }
                    Check(exited, "SDK runtime child survived stop");
                }
                finally { runtime.Dispose(); }

                var unexpected = new SdkRuntime(cmd, "/d /c \"more\"", folder, 700);
                string failure = null;
                unexpected.Failed += delegate(string message) { failure = message; };
                try
                {
                    unexpected.StartAsync(folder, home, SdkTestConfig(), CancellationToken.None).GetAwaiter().GetResult();
                    int pid = unexpected.ProcessId;
                    Check(pid != 0, "second SDK runtime has no process id");
                    using (Process child = Process.GetProcessById(pid)) child.Kill();
                    Check(SpinWait.SpinUntil(delegate { return failure != null; }, 5000), "unexpected runtime exit was not reported");
                    Check(!unexpected.IsRunning, "crashed runtime still reports running");
                }
                finally { unexpected.Dispose(); }
            });
        }

        sealed class QueueTextReader : TextReader
        {
            readonly BlockingCollection<string> lines = new BlockingCollection<string>();

            internal void Push(string line)
            {
                try { if (!lines.IsAddingCompleted) lines.Add(line); }
                catch (InvalidOperationException) { }
            }

            internal void Complete()
            {
                try { lines.CompleteAdding(); }
                catch (ObjectDisposedException) { }
            }

            public override string ReadLine()
            {
                try { return lines.Take(); }
                catch (InvalidOperationException) { return null; }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) Complete();
                base.Dispose(disposing);
            }
        }

        sealed class QueueTextWriter : TextWriter
        {
            readonly BlockingCollection<string> lines = new BlockingCollection<string>();
            readonly StringBuilder buffer = new StringBuilder();

            public override Encoding Encoding { get { return new UTF8Encoding(false); } }

            internal string ReadLine(int timeout)
            {
                string line;
                return lines.TryTake(out line, timeout) ? line : null;
            }

            public override void Write(char value)
            {
                lock (buffer)
                {
                    buffer.Append(value);
                    Drain();
                }
            }

            public override void Write(string value)
            {
                lock (buffer)
                {
                    if (value != null) buffer.Append(value);
                    Drain();
                }
            }

            void Drain()
            {
                int index;
                while ((index = IndexOf(buffer, '\n')) >= 0)
                {
                    string line = buffer.ToString(0, index);
                    buffer.Remove(0, index + 1);
                    try { lines.Add(line); }
                    catch (InvalidOperationException) { }
                }
            }

            static int IndexOf(StringBuilder value, char target)
            {
                for (int index = 0; index < value.Length; index++) if (value[index] == target) return index;
                return -1;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { try { lines.CompleteAdding(); } catch (ObjectDisposedException) { } }
                base.Dispose(disposing);
            }
        }

        sealed class QueuedSdkContext : SynchronizationContext
        {
            readonly Queue<Action> queued = new Queue<Action>();
            public override void Post(SendOrPostCallback callback, object state) { queued.Enqueue(() => callback(state)); }
            internal void Drain() { while (queued.Count > 0) queued.Dequeue()(); }
        }

        sealed class FakeSdkRuntime : ISdkRuntime
        {
            readonly object gate = new object();
            readonly HashSet<string> sessions = new HashSet<string>();
            internal bool Running;
            internal int Pid;
            internal bool Initialized;
            internal string InitializedCwdValue = "";
            internal string StartedHome;
            internal bool BridgeConnected = true;
            internal string Diagnostic = "";
            internal readonly List<string> Calls = new List<string>();
            internal readonly List<Dictionary<string, object>> Parameters = new List<Dictionary<string, object>>();
            internal Action<Dictionary<string, object>> Prompt;
            internal Exception ResumeFailure;
            internal Exception SetDirectoryFailure;
            internal Func<Dictionary<string, object>, CancellationToken, Task<object>> NativeHandler;
            object nativeOwner;

            public bool IsRunning { get { return Running; } }
            public int ProcessId { get { return Pid; } }
            public string LastDiagnostic { get { return Diagnostic; } }
            public bool IsBridgeConnected { get { return BridgeConnected; } }
            public bool IsInitialized { get { return Initialized; } }
            public string InitializedCwd { get { return InitializedCwdValue; } }
            public event Action<string> Failed { add { } remove { } }
            public event Action<string, Dictionary<string, object>> Notification;

            internal void Emit(string method, Dictionary<string, object> parameters)
            {
                Action<string, Dictionary<string, object>> handler = Notification;
                if (handler != null) handler(method, parameters);
            }

            internal void EmitEvent(string sessionId, string type, Dictionary<string, object> data)
            {
                Emit("session.event", new Dictionary<string, object>
                {
                    { "sessionId", sessionId },
                    { "event", new Dictionary<string, object> { { "type", type }, { "data", data } } }
                });
            }

            internal int Count(string method)
            {
                lock (gate)
                {
                    int count = 0;
                    foreach (string call in Calls) if (call == method) count++;
                    return count;
                }
            }

            internal Dictionary<string, object> ParameterOf(string method)
            {
                lock (gate)
                {
                    for (int index = Calls.Count - 1; index >= 0; index--) if (Calls[index] == method) return Parameters[index];
                }
                return null;
            }

            internal int FirstIndex(string method)
            {
                lock (gate)
                {
                    for (int index = 0; index < Calls.Count; index++) if (Calls[index] == method) return index;
                }
                return -1;
            }

            void Record(string method, Dictionary<string, object> parameters)
            {
                lock (gate)
                {
                    Calls.Add(method);
                    Parameters.Add(parameters);
                }
            }

            public Task StartAsync(string runtimeDirectory, string home, ModelConfig model, CancellationToken token)
            {
                StartedHome = home;
                Running = true;
                Pid = 4321;
                return Task.FromResult<object>(null);
            }

            public Task InitializeAsync(ModelConfig model, string cwd, CancellationToken token)
            {
                if (Initialized) return Task.FromResult<object>(null);
                Initialized = true;
                InitializedCwdValue = cwd;
                Record("initialize", new Dictionary<string, object>
                {
                    { "provider", "qingye-model" }, { "model", model.ModelName }, { "cwd", cwd },
                    { "approvalRequests", false },
                    { "capabilities", new Dictionary<string, object> { { "userQuestions", false } } }
                });
                return Task.FromResult<object>(null);
            }

            public Task<Dictionary<string, object>> RequestAsync(string method, Dictionary<string, object> parameters, CancellationToken token)
            {
                Record(method, parameters);
                if (method == "qingye/session/cancel")
                    EmitEvent(SdkValues.Text(parameters, "sessionId"), "turn/end", new Dictionary<string, object> {
                        { "reason", new Dictionary<string, object> { { "kind", "aborted" } } }
                    });
                if (method == "session/resume")
                {
                    if (ResumeFailure != null) return Task.FromException<Dictionary<string, object>>(ResumeFailure);
                    MarkSessionActive(SdkValues.Text(parameters, "sessionId"));
                    return Task.FromResult(new Dictionary<string, object>());
                }
                if (method == "session/prompt")
                {
                    MarkSessionActive(SdkValues.Text(parameters, "sessionId"));
                    Action<Dictionary<string, object>> responder = Prompt;
                    if (responder != null) responder(parameters);
                    return Task.FromResult(new Dictionary<string, object> { { "messageId", "m-1" } });
                }
                if (method == "session/working-directory/set")
                {
                    if (SetDirectoryFailure != null) return Task.FromException<Dictionary<string, object>>(SetDirectoryFailure);
                    return Task.FromResult(new Dictionary<string, object> { { "cwd", SdkValues.Text(parameters, "path") } });
                }
                return Task.FromResult(new Dictionary<string, object>());
            }

            public Task StopAsync()
            {
                Running = false;
                Pid = 0;
                lock (gate) sessions.Clear();
                return Task.FromResult<object>(null);
            }

            public bool IsSessionActive(string sessionId)
            {
                lock (gate) return sessions.Contains(sessionId);
            }

            public void MarkSessionActive(string sessionId)
            {
                if (String.IsNullOrEmpty(sessionId)) return;
                lock (gate) sessions.Add(sessionId);
            }

            public void AttachNative(object owner, Func<Dictionary<string, object>, CancellationToken, Task<object>> handler)
            {
                nativeOwner = owner;
                NativeHandler = handler;
            }

            public void DetachNative(object owner)
            {
                if (!Object.ReferenceEquals(nativeOwner, owner)) return;
                nativeOwner = null;
                NativeHandler = null;
            }

            public void Dispose()
            {
                Running = false;
                Pid = 0;
            }
        }

        sealed class RecordingApprover : IAsyncWriteApprover
        {
            internal bool Allow;
            internal int Calls;
            internal WriteProposal Proposal;

            public bool Approve(WriteProposal proposal) { return Allow; }

            public Task<bool> ApproveAsync(WriteProposal proposal, CancellationToken token)
            {
                Calls++;
                Proposal = proposal;
                return Task.FromResult(Allow);
            }
        }

        sealed class SlowApprover : IAsyncWriteApprover
        {
            public bool Approve(WriteProposal proposal) { return false; }

            public async Task<bool> ApproveAsync(WriteProposal proposal, CancellationToken token)
            {
                await Task.Delay(10000, token).ConfigureAwait(false);
                return true;
            }
        }

        sealed class BridgeClient : IDisposable
        {
            readonly TcpClient connection;
            readonly StreamReader reader;
            readonly StreamWriter writer;

            BridgeClient(TcpClient connection)
            {
                this.connection = connection;
                reader = new StreamReader(connection.GetStream(), new UTF8Encoding(false));
                writer = new StreamWriter(connection.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            }

            internal static BridgeClient Connect(int port, string token)
            {
                var connection = new TcpClient();
                connection.Connect(IPAddress.Loopback, port);
                connection.ReceiveTimeout = 15000;
                var client = new BridgeClient(connection);
                client.writer.WriteLine("{\"token\":\"" + token + "\"}");
                return client;
            }

            internal Dictionary<string, object> Send(Dictionary<string, object> request)
            {
                writer.WriteLine(SdkJson.Serialize(request));
                string line = reader.ReadLine();
                Check(line != null, "bridge closed before answering");
                return Json(line);
            }

            public void Dispose()
            {
                try { writer.Dispose(); }
                catch (IOException) { }
                try { connection.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }

        static string BridgeError(Dictionary<string, object> response)
        {
            object error;
            return response != null && response.TryGetValue("error", out error) ? Convert.ToString(error) : "";
        }
    }
}
