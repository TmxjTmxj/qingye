using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Qingye.Core
{
    public interface IWriteApprover
    {
        bool Approve(WriteProposal proposal);
    }
    public interface IAsyncWriteApprover : IWriteApprover
    {
        Task<bool> ApproveAsync(WriteProposal proposal, CancellationToken token);
    }
    // Runs the model/tool loop. It can only reach files through AgentTools, which
    // enforces the workspace policy; it has no shell or network tool.
    public sealed class AgentController
    {
        readonly ModelConfig config;
        readonly AgentWorkspace workspace;
        readonly AgentTools tools;
        readonly IAgentTransport transport;
        readonly IWriteApprover approver;
        readonly Action<WriteProposal> onApplied;
        readonly AgentSession session;
        int running;
        public event Action<AgentActivity> Activity;
        public event Action Checkpoint;
        readonly JavaScriptSerializer json = new JavaScriptSerializer
        {
            MaxJsonLength = 8 * 1024 * 1024
        };
        readonly List<Dictionary<string, object>> messages = new List<Dictionary<string, object>>();
        public AgentController(ModelConfig config, AgentWorkspace workspace, AgentTools tools, IAgentTransport transport, IWriteApprover approver, Action<WriteProposal> onApplied = null, AgentSession session = null)
        {
            this.config = config;
            this.workspace = workspace;
            this.tools = tools;
            this.transport = transport;
            this.approver = approver;
            this.onApplied = onApplied;
            this.session = session ?? new AgentSession
            {
                Root = workspace.Root, CurrentFile = workspace.CurrentFile
            };
            this.session.RecoverInterrupted();
            messages.Add(new Dictionary<string, object>
            {
                {
                    "role", "system"
                },
                {
                    "content", SystemPrompt()
                }
            });
            messages.AddRange(this.session.History());
            Trim();
        }
        string SystemPrompt()
        {
            string location = workspace.IsSingleFile
            ? "当前只打开了一个文件：" + workspace.Relative(workspace.CurrentFile) + "。你只能读写这个文件；禁止新建、删除、重命名或访问其它路径。"
            : "当前工作区根目录：" + workspace.Root + "。所有路径都必须是工作区内的相对路径；禁止访问工作区之外。";
            return "你是轻页（Qingye）的本地文件助理，不是通用聊天机器人。\n" +
            location + "\n" +
            "规则：\n" +
            "1. 只能使用提供的工具（list_files、read_file、search_files、write_file）；没有 shell、没有网络、没有删除或重命名工具。\n" +
            "2. 禁止读取或修改密钥、模型配置、.env、.git、证书等受保护文件。\n" +
            "3. 每次 write_file 都会先让用户看到 diff 并确认；在工具返回 applied 之前，不要声称文件已修改。\n" +
            "4. 只改用户要求的内容，保持最小改动；新建文件必须确有需要，能修改现有文件就不要新建。\n" +
            "5. 不确定时先用 list_files/read_file/search_files 了解现状，再提出修改。\n" +
            "6. 用中文回答；先说明你准备做什么，再调用工具。\n" +
            "7. 如果用户的要求超出当前文件/工作区，直接说明限制，不要尝试绕过。";
        }
        void RecordMessage(Dictionary<string, object> message)
        {
            message = ModelConfig.ScrubValues(json.DeserializeObject(json.Serialize(message)), config.ApiKey) as Dictionary<string, object>;
            messages.Add(message);
            session.Record(message);
            if (Checkpoint != null) Checkpoint();
        }
        void Report(string state, int step, string text, AgentToolCall call = null, string delta = null)
        {
            if (delta == null) session.State = state;
            if (Activity != null) Activity(new AgentActivity
            {
                State = state, Step = step, Text = text,
                CallId = call == null ? null : call.Id, Tool = call == null ? null : call.Name, Delta = delta
            });
            if (delta == null && Checkpoint != null) Checkpoint();
        }
        public async Task<string> AskAsync(string userText, CancellationToken token, Action<string> onTool)
        {
            if (String.IsNullOrWhiteSpace(userText)) return "";
            if (Interlocked.Exchange(ref running, 1) != 0) throw new AgentException("当前会话仍在运行。");
            var pending = new List<AgentToolCall>();
            var partial = new StringBuilder();
            int currentStep = 0;
            try
            {
                token.ThrowIfCancellationRequested();
                if (session.Entries.Count == 0) session.Title = userText.Split('\n')[0].Substring(0, Math.Min(48, userText.Split('\n')[0].Length));
                session.Model = config.ModelName;
                session.Note("run-start", "开始任务", null, null, 0);
                RecordMessage(new Dictionary<string, object>
                {
                    {
                        "role", "user"
                    },
                    {
                        "content", ModelConfig.ScrubSecrets(userText, config.ApiKey)
                    }
                });
                string toolsJson = AgentTools.ToolDefinitions();
                int created = 0;
                for (int step = 0; step < Math.Max(1, config.MaxSteps); step++)
                {
                    currentStep = step + 1;
                    token.ThrowIfCancellationRequested();
                    Report("requesting", currentStep, "等待模型响应");
                    partial.Clear();
                    AgentReply reply;
                    var streaming = transport as IStreamingAgentTransport;
                    if (streaming == null) reply = await transport.ChatAsync(messages, toolsJson, token).ConfigureAwait(false);
                    else reply = await streaming.ChatStreamingAsync(messages, toolsJson, token,
                    delta =>
                    {
                        partial.Append(delta);
                        Report("streaming", currentStep, "正在回复", null, delta);
                    }).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (reply == null) throw new AgentException("模型没有返回内容。");
                    if (reply.ToolCalls == null || reply.ToolCalls.Count == 0)
                    {
                        string content = ModelConfig.ScrubSecrets(reply.Content ?? "", config.ApiKey);
                        reply.Content = content;
                        RecordMessage(AssistantMessage(reply));
                        session.Note("run-end", "任务完成", null, null, currentStep);
                        Report("completed", currentStep, "任务完成");
                        Trim();
                        return content;
                    }
                    if (reply.ToolCalls.Count > 32) throw new AgentException("模型一次返回了过多工具调用。");
                    RecordMessage(AssistantMessage(reply));
                    partial.Clear();
                    pending.AddRange(reply.ToolCalls);
                    foreach (var call in reply.ToolCalls)
                    {
                        token.ThrowIfCancellationRequested();
                        string result;
                        try
                        {
                            var args = ParseArguments(call.Arguments);
                            string description = call.Name + " " + Describe(args);
                            session.Note("tool-start", description, call.Name, call.Id, currentStep);
                            Report("tool", currentStep, description, call);
                            if (onTool != null) onTool(description);
                            token.ThrowIfCancellationRequested();
                            var toolResult = tools.Execute(call.Name, args);
                            if (toolResult.Write != null)
                            {
                                if (toolResult.Write.IsNew && created >= 5)
                                result = json.Serialize(new Dictionary<string, object>
                                {
                                    {
                                        "error", "本轮最多新建 5 个文件。"
                                    }
                                });
                                else
                                {
                                    Report("approval", currentStep, "等待确认修改 " + toolResult.Write.RelativePath, call);
                                    var asyncApprover = approver as IAsyncWriteApprover;
                                    bool approved = asyncApprover == null ? approver != null && approver.Approve(toolResult.Write)
                                    : await asyncApprover.ApproveAsync(toolResult.Write, token).ConfigureAwait(false);
                                    token.ThrowIfCancellationRequested();
                                    if (approved)
                                    {
                                        tools.ApplyWrite(toolResult.Write);
                                        if (toolResult.Write.IsNew) created++;
                                        if (onApplied != null) onApplied(toolResult.Write);
                                    }
                                    result = json.Serialize(new Dictionary<string, object>
                                    {
                                        {
                                            "status", approved ? "applied" : "rejected"
                                        },
                                        {
                                            "path", toolResult.Write.RelativePath
                                        }
                                    });
                                }
                            }
                            else result = toolResult.Json;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception error)
                        {
                            result = json.Serialize(new Dictionary<string, object>
                            {
                                {
                                    "error", ModelConfig.Redact(error.Message, config.ApiKey)
                                }
                            });
                        }
                        result = ModelConfig.ScrubSecrets(result, config.ApiKey);
                        RecordMessage(new Dictionary<string, object>
                        {
                            {
                                "role", "tool"
                            },
                            {
                                "tool_call_id", call.Id
                            },
                            {
                                "content", result
                            }
                        });
                        pending.Remove(call);
                        Report("tool-result", currentStep, result, call);
                    }
                    Trim();
                }
                string limit = "已达到本轮最大步骤数。请缩小任务范围，或继续发送下一步指令。";
                RecordMessage(new Dictionary<string, object>
                {
                    {
                        "role", "assistant"
                    },
                    {
                        "content", limit
                    }
                });
                session.Note("run-end", limit, null, null, currentStep);
                Report("limited", currentStep, limit);
                return limit;
            }
            catch (Exception error)
            {
                if (partial.Length > 0) session.Note("attempt", ModelConfig.ScrubSecrets(partial.ToString(), config.ApiKey), null, null, currentStep);
                foreach (var call in pending) RecordMessage(new Dictionary<string, object>
                {
                    {
                        "role", "tool"
                    },
                    {
                        "tool_call_id", call.Id
                    },
                    {
                        "content", "{\"status\":\"interrupted\",\"message\":\"本轮已中断；请确认已有结果后继续。\"}"
                    }
                });
                string state = error is OperationCanceledException ? "stopped" : "failed";
                string text = state == "stopped" ? "已停止" : ModelConfig.Redact(error.Message, config.ApiKey);
                session.Note("run-end", text, null, null, currentStep);
                Report(state, currentStep, text);
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref running, 0);
            }
        }
        Dictionary<string, object> AssistantMessage(AgentReply reply)
        {
            var calls = reply.ToolCalls.Select(call => new Dictionary<string, object>
            {
                {
                    "id", call.Id
                },
                {
                    "type", "function"
                },
                {
                    "function", new Dictionary<string, object>
                    {
                        {
                            "name", call.Name
                        },
                        {
                            "arguments", call.Arguments
                        }
                    }
                }
            }).ToArray();
            var message = new Dictionary<string, object>
            {
                {
                    "role", "assistant"
                },
                {
                    "content", reply.Content ?? ""
                }
            };
            if (calls.Length > 0) message["tool_calls"] = calls;
            if (reply.ReasoningContent != null) message["reasoning_content"] = reply.ReasoningContent;
            return message;
        }
        Dictionary<string, object> ParseArguments(string arguments)
        {
            if (String.IsNullOrWhiteSpace(arguments)) return new Dictionary<string, object>();
            try
            {
                var value = json.DeserializeObject(arguments) as Dictionary<string, object>;
                return value ?? new Dictionary<string, object>();
            }
            catch (Exception error)
            {
                throw new AgentException("工具参数不是有效 JSON：" + error.Message);
            }
        }
        static string Describe(Dictionary<string, object> args)
        {
            object path;
            return args != null && args.TryGetValue("path", out path) && path != null ? Convert.ToString(path) : "";
        }
        void Trim()
        {
            // Remove complete earlier user turns, never half a tool-call batch.
            while (messages.Count > 30)
            {
                int nextUser = messages.FindIndex(2, message => Convert.ToString(message["role"]) == "user");
                if (nextUser < 0) break;
                messages.RemoveRange(1, nextUser - 1);
            }
        }
        public void Reset()
        {
            messages.Clear();
            session.Entries.Clear();
            session.State = "idle";
            messages.Add(new Dictionary<string, object>
            {
                {
                    "role", "system"
                },
                {
                    "content", SystemPrompt()
                }
            });
        }
    }
}
