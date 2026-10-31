using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Qingye.Core
{
    public sealed class AgentToolCall
    {
        public string Id;
        public string Name;
        public string Arguments;
    }

    public sealed class AgentReply
    {
        public string Content;
        public string ReasoningContent;
        public IList<AgentToolCall> ToolCalls = new List<AgentToolCall>();
    }

    public interface IAgentTransport
    {
        Task<AgentReply> ChatAsync(IList<Dictionary<string, object>> messages, string toolsJson, CancellationToken token);
    }
    public interface IStreamingAgentTransport : IAgentTransport
    {
        Task<AgentReply> ChatStreamingAsync(IList<Dictionary<string, object>> messages, string toolsJson, CancellationToken token, Action<string> onDelta);
    }

    // OpenAI-compatible chat completions over HTTPS. No local harness and no port.
    public sealed partial class AgentClient : IStreamingAgentTransport, IDisposable
    {
        readonly ModelConfig config;
        readonly HttpClient http;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };

        public AgentClient(ModelConfig config) : this(config, null) { }

        internal AgentClient(ModelConfig config, HttpMessageHandler handler)
        {
            this.config = config;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            http = handler == null ? new HttpClient() : new HttpClient(handler, false);
            http.Timeout = TimeSpan.FromSeconds(Math.Max(10, config.TimeoutSeconds));
        }

        public async Task<AgentReply> ChatAsync(IList<Dictionary<string, object>> messages, string toolsJson, CancellationToken token)
        {
            if (!config.IsValid) throw new AgentException("模型配置不完整，请检查 模型.txt。");
            var payload = new Dictionary<string, object> {
                { "model", config.ModelName },
                { "messages", messages },
                { "temperature", 0.2 },
                { "stream", false }
            };
            if (!String.IsNullOrEmpty(toolsJson))
            {
                payload["tools"] = json.DeserializeObject(toolsJson);
                payload["tool_choice"] = "auto";
            }
            string body = json.Serialize(payload);
            using (var request = new HttpRequestMessage(HttpMethod.Post, config.Endpoint))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + config.ApiKey);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                HttpResponseMessage response;
                try { response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { throw new AgentException("请求模型失败：" + ModelConfig.Redact(Describe(error), config.ApiKey)); }
                string text;
                try { text = await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
                catch (Exception error) { throw new AgentException("读取模型响应失败：" + ModelConfig.Redact(error.Message, config.ApiKey)); }
                if (!response.IsSuccessStatusCode)
                    throw new AgentException("模型返回 HTTP " + (int)response.StatusCode + "：" + ModelConfig.Redact(text, config.ApiKey));
                return Parse(text);
            }
        }

        public async Task<string> TestAsync(CancellationToken token)
        {
            var messages = new List<Dictionary<string, object>> {
                new Dictionary<string, object> { { "role", "user" }, { "content", "只回复 OK" } }
            };
            var reply = await ChatAsync(messages, null, token).ConfigureAwait(false);
            return reply.Content ?? "";
        }

        AgentReply Parse(string text)
        {
            var root = json.DeserializeObject(text) as Dictionary<string, object>;
            var choices = root != null && root.ContainsKey("choices") ? root["choices"] as object[] : null;
            var choice = choices != null && choices.Length > 0 ? choices[0] as Dictionary<string, object> : null;
            var message = choice != null && choice.ContainsKey("message") ? choice["message"] as Dictionary<string, object> : null;
            if (message == null) throw new AgentException("模型响应格式无法识别。");
            var reply = new AgentReply { Content = message.ContainsKey("content") ? Convert.ToString(message["content"]) : "", ReasoningContent = message.ContainsKey("reasoning_content") ? Convert.ToString(message["reasoning_content"]) : null };
            var calls = message.ContainsKey("tool_calls") ? message["tool_calls"] as object[] : null;
            if (calls != null)
            {
                foreach (var item in calls)
                {
                    var call = item as Dictionary<string, object>;
                    if (call == null) continue;
                    var function = call.ContainsKey("function") ? call["function"] as Dictionary<string, object> : null;
                    if (function == null) continue;
                    reply.ToolCalls.Add(new AgentToolCall {
                        Id = call.ContainsKey("id") ? Convert.ToString(call["id"]) : Guid.NewGuid().ToString("N"),
                        Name = function.ContainsKey("name") ? Convert.ToString(function["name"]) : "",
                        Arguments = function.ContainsKey("arguments") ? (function["arguments"] is string ? (string)function["arguments"] : json.Serialize(function["arguments"])) : "{}"
                    });
                }
            }
            return reply;
        }

        static string Describe(Exception error)
        {
            if (error == null) return "";
            return error.InnerException == null ? error.Message : error.Message + " / " + error.InnerException.Message;
        }

        public void Dispose() { http.Dispose(); }
    }
}
