using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Qingye.Core
{
    public sealed class AgentEntry
    {
        public string Kind;
        public string Text;
        public string ToolName;
        public string CallId;
        public int Step;
        public DateTime TimeUtc = DateTime.UtcNow;
        public Dictionary<string, object> Message;
    }
    public sealed class AgentSession
    {
        public int Format = 1;
        public string Id = Guid.NewGuid().ToString("N");
        public string Title = "新对话";
        public string Root;
        public string CurrentFile;
        public string Model;
        public string Draft = "";
        public string State = "idle";
        // DSH SDK identity used by session/resume; empty until the first run.
        public string HarnessSessionId = "";
        public DateTime UpdatedUtc = DateTime.UtcNow;
        public List<AgentEntry> Entries = new List<AgentEntry>();
        public List<string> Attachments = new List<string>();
        public void Record(Dictionary<string, object> message)
        {
            Entries.Add(new AgentEntry
            {
                Kind = "message", Message = message
            });
            UpdatedUtc = DateTime.UtcNow;
        }
        public void Note(string kind, string text, string tool, string callId, int step)
        {
            Entries.Add(new AgentEntry
            {
                Kind = kind, Text = text, ToolName = tool, CallId = callId, Step = step
            });
            UpdatedUtc = DateTime.UtcNow;
        }
        public IList<Dictionary<string, object>> History()
        {
            return Entries.Where(entry => entry.Kind == "message" && entry.Message != null &&
            Convert.ToString(entry.Message["role"]) != "system").Select(entry => entry.Message).ToList();
        }
        // A process can end between an assistant's tool calls and their results.
        public void RecoverInterrupted()
        {
            var pending = new HashSet<string>();
            foreach (var message in History())
            {
                object calls;
                if (message.TryGetValue("tool_calls", out calls))
                foreach (Dictionary<string, object> call in (System.Collections.IEnumerable)calls)
                pending.Add(Convert.ToString(call["id"]));
                if (Convert.ToString(message["role"]) == "tool") pending.Remove(Convert.ToString(message["tool_call_id"]));
            }
            foreach (string id in pending) Record(new Dictionary<string, object>
            {
                {
                    "role", "tool"
                },
                {
                    "tool_call_id", id
                },
                {
                    "content", "{\"status\":\"interrupted\",\"message\":\"上次运行被中断；没有继续执行该工具。\"}"
                }
            });
            if (new[]
            {
                "requesting", "streaming", "tool", "tool-result", "approval", "stopping"
            }.Contains(State))
            {
                State = "stopped";
                Note("run-end", "上次运行被中断，可继续发送指令。", null, null, 0);
            }
        }
    }
    public sealed class AgentSessionInfo
    {
        public string Id;
        public string Title;
        public string Scope;
        public string State;
        public DateTime UpdatedUtc;
    }
    // Data and small list summaries are local to the portable app. No config/key is stored.
    public sealed class AgentSessionStore
    {
        public const int MaximumBytes = 8 * 1024 * 1024;
        readonly string directory;
        readonly string parent;
        readonly Dictionary<string, AgentSession> temporary;
        readonly Dictionary<string, long> temporarySizes = new Dictionary<string, long>();
        long temporaryCharacters;
        public string Notice
        {
            get;
            private set;
        }
        public bool HasMore
        {
            get;
            private set;
        }
        static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = MaximumBytes, RecursionLimit = 64
            };
        }
        public AgentSessionStore(string path, bool memoryOnly = false)
        {
            directory = Path.GetFullPath(path);
            parent = Path.GetDirectoryName(directory);
            if (memoryOnly) temporary = new Dictionary<string, AgentSession>();
        }
        string FilePath(string id, bool metadata)
        {
            Guid value;
            if (!Guid.TryParseExact(id, "N", out value)) throw new AgentException("会话编号无效。");
            string path = Path.Combine(directory, id + (metadata ? ".meta.json" : ".json"));
            if (!FileService.IsInsideRoot(parent, path)) throw new AgentException("历史目录不能使用目录链接。");
            return path;
        }
        public void Save(AgentSession session, string secret = null)
        {
            if (session.Format != 1 || session.Entries.Count > 4096) throw new AgentException("会话已超出保存范围，请新建对话。");
            if (temporary != null)
            {
                if (!temporary.ContainsKey(session.Id) && temporary.Count >= 32) throw new AgentException("本次临时会话已达 32 项，请清空后继续。");
                long characters = (session.Draft ?? "").Length;
                foreach (var entry in session.Entries)
                {
                    characters += (entry.Text ?? "").Length;
                    if (entry.Message != null)
                        foreach (object value in entry.Message.Values) if (value is string) characters += ((string)value).Length;
                }
                if (characters > MaximumBytes / 2) throw new AgentException("临时会话超过 8 MiB，请新建对话。");
                long previousSize;
                temporarySizes.TryGetValue(session.Id, out previousSize);
                long total = temporaryCharacters - previousSize + characters;
                if (total > 16 * 1024 * 1024) throw new AgentException("临时会话总内存达到 32 MiB，请删除较早会话或清空后继续。");
                temporary[session.Id] = CopyTemporary(session);
                temporarySizes[session.Id] = characters;
                temporaryCharacters = total;
                return;
            }
            string path = FilePath(session.Id, false);
            string data = Serializer().Serialize(ModelConfig.ScrubValues(Serializer().DeserializeObject(Serializer().Serialize(session)), secret));
            if (Encoding.UTF8.GetByteCount(data) > MaximumBytes) throw new AgentException("会话超过 8 MiB，请新建对话；现有历史保留。");
            Directory.CreateDirectory(directory);
            AtomicWrite(path, data);
            var info = new AgentSessionInfo
            {
                Id = session.Id, Title = session.Title, Scope = session.Root ?? session.CurrentFile, State = session.State, UpdatedUtc = session.UpdatedUtc
            };
            AtomicWrite(FilePath(session.Id, true), Serializer().Serialize(ModelConfig.ScrubValues(Serializer().DeserializeObject(Serializer().Serialize(info)), secret)));
        }
        static void AtomicWrite(string path, string data)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(output, new UTF8Encoding(false))) writer.Write(data);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        public AgentSession Load(string id)
        {
            if (temporary != null)
            {
                AgentSession saved;
                if (!temporary.TryGetValue(id, out saved)) throw new AgentException("临时会话已清除。");
                return CopyTemporary(saved);
            }
            string path = FilePath(id, false);
            if (new FileInfo(path).Length > MaximumBytes) throw new AgentException("历史会话超过读取上限。");
            var session = Serializer().Deserialize<AgentSession>(File.ReadAllText(path, Encoding.UTF8));
            if (session == null || session.Format != 1 || session.Id != id || session.Entries == null || session.Entries.Count > 4096 || session.Attachments == null || session.Attachments.Count > 8)
            throw new AgentException("历史会话格式无法识别，原文件保留。");
            session.RecoverInterrupted();
            return session;
        }
        public IList<AgentSessionInfo> List(int offset = 0, string query = "")
        {
            Notice = "";
            HasMore = false;
            if (temporary != null)
                return temporary.Values.Where(session => ((session.Title ?? "") + " " + (session.Root ?? session.CurrentFile)).IndexOf(query ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(session => session.UpdatedUtc).Select(session => new AgentSessionInfo {
                        Id = session.Id, Title = session.Title, Scope = session.Root ?? session.CurrentFile, State = session.State, UpdatedUtc = session.UpdatedUtc
                    }).ToList();
            var result = new List<AgentSessionInfo>();
            if (!Directory.Exists(directory)) return result;
            if (!FileService.IsInsideRoot(parent, Path.Combine(directory, "list.json"))) throw new AgentException("历史目录不能使用目录链接。");
            foreach (string path in Directory.EnumerateFiles(directory, "*.meta.json"))
            {
                try
                {
                    if (!FileService.IsInsideRoot(parent, path) || new FileInfo(path).Length > 65536) throw new InvalidDataException();
                    var info = Serializer().Deserialize<AgentSessionInfo>(File.ReadAllText(path, Encoding.UTF8));
                    if (info == null || !File.Exists(FilePath(info.Id, false))) throw new InvalidDataException();
                    result.Add(info);
                }
                catch
                {
                    Notice = "部分历史索引无法读取，原文件已保留。";
                }
            }
            var ordered = result.Where(info => ((info.Title ?? "") + " " + info.Scope).IndexOf(query ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(info => info.UpdatedUtc).ToList();
            offset = Math.Max(0, offset);
            HasMore = ordered.Count > offset + 200;
            return ordered.Skip(offset).Take(200).ToList();
        }
        public void Delete(string id)
        {
            if (temporary != null)
            {
                long size;
                if (temporarySizes.TryGetValue(id, out size)) temporaryCharacters -= size;
                temporarySizes.Remove(id);
                temporary.Remove(id);
                return;
            }
            File.Delete(FilePath(id, false));
            File.Delete(FilePath(id, true));
        }
        public void Clear()
        {
            if (temporary != null) { temporary.Clear(); temporarySizes.Clear(); temporaryCharacters = 0; return; }
            if (!Directory.Exists(directory)) return;
            if (!FileService.IsInsideRoot(parent, Path.Combine(directory, "list.json"))) throw new AgentException("历史目录不能使用目录链接。");
            foreach (string path in Directory.EnumerateFiles(directory, "*.meta.json").ToArray())
            {
                string id = Path.GetFileName(path).Split('.')[0];
                Guid value;
                if (Guid.TryParseExact(id, "N", out value)) Delete(id);
            }
        }

        static AgentSession CopyTemporary(AgentSession source)
        {
            // Message text is immutable. Copy the mutable collections without
            // serializing the complete conversation after every draft change.
            return new AgentSession {
                Format = source.Format, Id = source.Id, Title = source.Title,
                Root = source.Root, CurrentFile = source.CurrentFile, Model = source.Model,
                Draft = source.Draft, State = source.State, HarnessSessionId = source.HarnessSessionId,
                UpdatedUtc = source.UpdatedUtc, Attachments = new List<string>(source.Attachments),
                Entries = source.Entries.Select(entry => new AgentEntry {
                    Kind = entry.Kind, Text = entry.Text, ToolName = entry.ToolName,
                    CallId = entry.CallId, Step = entry.Step, TimeUtc = entry.TimeUtc,
                    Message = entry.Message == null ? null : new Dictionary<string, object>(entry.Message)
                }).ToList()
            };
        }
    }
    public sealed class AgentActivity
    {
        public string SessionId;
        public string State;
        public string Text;
        public string CallId;
        public string Tool;
        public int Step;
        public string Delta;
    }
}
