using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Qingye.Core
{
    public sealed partial class AgentClient
    {
        public async Task<AgentReply> ChatStreamingAsync(IList<Dictionary<string, object>> messages, string toolsJson, CancellationToken token, Action<string> onDelta)
        {
            if (!config.IsValid) throw new AgentException("模型配置不完整，请检查 模型.txt。");
            var payload = new Dictionary<string, object>
            {
                {
                    "model", config.ModelName
                },
                {
                    "messages", messages
                },
                {
                    "temperature", 0.2
                },
                {
                    "stream", true
                }
            };
            if (!String.IsNullOrEmpty(toolsJson))
            {
                payload["tools"] = json.DeserializeObject(toolsJson);
                payload["tool_choice"] = "auto";
            }
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var request = new HttpRequestMessage(HttpMethod.Post, config.Endpoint))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, config.TimeoutSeconds)));
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + config.ApiKey);
                request.Content = new StringContent(json.Serialize(payload), Encoding.UTF8, "application/json");
                try
                {
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (timeout.Token.Register(stream.Dispose))
                    using (var reader = new StreamReader(new LimitedResponseStream(stream), Encoding.UTF8))
                    {
                        if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new AgentException("模型响应超过 8 MiB。");
                        string media = response.Content.Headers.ContentType == null ? "" : response.Content.Headers.ContentType.MediaType;
                        if (!response.IsSuccessStatusCode || media != "text/event-stream")
                        {
                            string text = await ReadBounded(reader).ConfigureAwait(false);
                            if (!response.IsSuccessStatusCode) throw new AgentException("模型返回 HTTP " + (int)response.StatusCode + "：" + ModelConfig.Redact(text, config.ApiKey));
                            var reply = Parse(text);
                            if (onDelta != null && !String.IsNullOrEmpty(reply.Content)) onDelta(reply.Content);
                            return reply;
                        }
                        return await ReadEvents(reader, onDelta).ConfigureAwait(false);
                    }
                }
                catch (Exception error)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    if (timeout.IsCancellationRequested) throw new AgentException("模型请求超时，可重试本轮。");
                    if (error is AgentException) throw;
                    throw new AgentException("读取模型响应失败：" + ModelConfig.Redact(error.Message, config.ApiKey));
                }
            }
        }
        static async Task<string> ReadBounded(StreamReader reader)
        {
            var result = new StringBuilder();
            char[] buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                if (result.Length + count > 8 * 1024 * 1024) throw new AgentException("模型响应超过读取上限。");
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }
        async Task<AgentReply> ReadEvents(StreamReader reader, Action<string> onDelta)
        {
            var reply = new AgentReply();
            var calls = new SortedDictionary<int, StreamCall>();
            var reasoning = new StringBuilder();
            var text = new StringBuilder();
            var line = new StringBuilder();
            var frame = new StringBuilder();
            char[] buffer = new char[4096];
            int received = 0, count;
            bool settled = false, done = false;
            while (!done && (count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                received += count;
                if (received > 8 * 1024 * 1024) throw new AgentException("模型响应超过读取上限。");
                for (int index = 0; index < count && !done; index++)
                {
                    char character = buffer[index];
                    if (character != '\n')
                    {
                        line.Append(character);
                        continue;
                    }
                    string value = line.ToString().TrimEnd('\r');
                    line.Clear();
                    if (value.Length == 0 && frame.Length > 0)
                    {
                        done = ApplyFrame(frame.ToString(), reply, calls, text, reasoning, onDelta, ref settled);
                        frame.Clear();
                    }
                    else if (value.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (frame.Length > 0) frame.Append('\n');
                        frame.Append(value.Substring(5).TrimStart(' '));
                    }
                }
            }
            if (line.Length > 0 && line.ToString().StartsWith("data:", StringComparison.Ordinal)) frame.Append(line.ToString().Substring(5).Trim());
            if (!done && frame.Length > 0) done = ApplyFrame(frame.ToString(), reply, calls, text, reasoning, onDelta, ref settled);
            if (!done && !settled) throw new AgentException("连接在回复完成前中断；可重试本轮。");
            reply.Content = text.ToString();
            reply.ToolCalls = calls.Values.Select(call => new AgentToolCall
            {
                Id = call.Id.ToString(), Name = call.Name.ToString(), Arguments = call.Arguments.ToString()
            }).ToList();
            if (reply.ReasoningContent != null) reply.ReasoningContent = reasoning.ToString();
            return reply;
        }
        bool ApplyFrame(string frame, AgentReply reply, SortedDictionary<int, StreamCall> calls, StringBuilder text, StringBuilder reasoning, Action<string> onDelta, ref bool settled)
        {
            if (frame.Trim() == "[DONE]") return true;
            var root = json.DeserializeObject(frame) as Dictionary<string, object>;
            if (Field(root, "error") != null) throw new AgentException("模型流返回错误：" + ModelConfig.Redact(json.Serialize(root["error"]), config.ApiKey));
            var choices = Field(root, "choices") as object[];
            if (choices == null || choices.Length == 0) return false;
            var choice = choices[0] as Dictionary<string, object>;
            if (Field(choice, "finish_reason") != null) settled = true;
            var delta = Field(choice, "delta") as Dictionary<string, object>;
            if (Field(delta, "reasoning_content") != null)
            {
                reasoning.Append(Convert.ToString(Field(delta, "reasoning_content")));
                reply.ReasoningContent = "";
            }
            string content = Convert.ToString(Field(delta, "content"));
            if (content.Length > 0)
            {
                text.Append(content);
                if (onDelta != null) onDelta(content);
            }
            var fragments = Field(delta, "tool_calls") as object[];
            if (fragments == null) return false;
            foreach (var item in fragments)
            {
                var fragment = item as Dictionary<string, object>;
                int index = Convert.ToInt32(Field(fragment, "index") ?? 0);
                if (index < 0 || index >= 32) throw new AgentException("模型返回过多工具调用。");
                StreamCall call;
                if (!calls.TryGetValue(index, out call))
                {
                    call = new StreamCall();
                    calls.Add(index, call);
                }
                call.Id.Append(Convert.ToString(Field(fragment, "id")));
                var function = Field(fragment, "function") as Dictionary<string, object>;
                call.Name.Append(Convert.ToString(Field(function, "name")));
                call.Arguments.Append(Convert.ToString(Field(function, "arguments")));
                if (call.Arguments.Length > AgentTools.MaxWriteCharacters + 65536) throw new AgentException("工具参数超过大小限制。");
            }
            return false;
        }
        static object Field(Dictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) ? result : null;
        }
        sealed class StreamCall
        {
            internal readonly StringBuilder Id = new StringBuilder();
            internal readonly StringBuilder Name = new StringBuilder();
            internal readonly StringBuilder Arguments = new StringBuilder();
        }
        sealed class LimitedResponseStream : Stream
        {
            readonly Stream input;
            long received;
            internal LimitedResponseStream(Stream input)
            {
                this.input = input;
            }
            int Count(int count)
            {
                received += count;
                if (received > 8L * 1024 * 1024) throw new AgentException("模型响应超过 8 MiB。");
                return count;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                return Count(input.Read(buffer, offset, count));
            }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                return Count(await input.ReadAsync(buffer, offset, count, token).ConfigureAwait(false));
            }
            public override bool CanRead
            {
                get
                {
                    return true;
                }
            }
            public override bool CanSeek
            {
                get
                {
                    return false;
                }
            }
            public override bool CanWrite
            {
                get
                {
                    return false;
                }
            }
            public override long Length
            {
                get
                {
                    throw new NotSupportedException();
                }
            }
            public override long Position
            {
                get
                {
                    throw new NotSupportedException();
                }
                set
                {
                    throw new NotSupportedException();
                }
            }
            public override void Flush()
            {
            }
            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }
            public override void SetLength(long length)
            {
                throw new NotSupportedException();
            }
            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
