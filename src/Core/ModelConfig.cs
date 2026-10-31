using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Qingye.Core
{
    // Reads and writes the portable model configuration next to the application.
    // The key is never logged, serialized into preferences, or written to diagnostics.
    public sealed class ModelConfig
    {
        public string BaseUrl = "";
        public string ApiKey = "";
        public string ModelName = "";
        public string ChatPath = "/v1/chat/completions";
        public int TimeoutSeconds = 90;
        public int MaxSteps = 8;

        public bool IsValid
        {
            get
            {
                Uri uri;
                if (String.IsNullOrWhiteSpace(BaseUrl) || String.IsNullOrWhiteSpace(ApiKey) || String.IsNullOrWhiteSpace(ModelName)) return false;
                if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out uri)) return false;
                if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) return false;
                return true;
            }
        }

        public string Endpoint
        {
            get
            {
                string value = (BaseUrl ?? "").Trim().TrimEnd('/');
                if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return value;
                if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return value + "/chat/completions";
                string path = String.IsNullOrWhiteSpace(ChatPath) ? "/v1/chat/completions" : ChatPath;
                if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;
                return value + path;
            }
        }

        public static string Locate(string baseDirectory)
        {
            string direct = Path.Combine(baseDirectory, "模型.txt");
            if (File.Exists(direct)) return direct;
            try
            {
                string parent = Path.GetDirectoryName(baseDirectory.TrimEnd(Path.DirectorySeparatorChar));
                string fallback = String.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "模型.txt");
                if (fallback != null && File.Exists(fallback)) return fallback;
            }
            catch { }
            return direct;
        }

        public static ModelConfig Load(string path)
        {
            try { return File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8)) : new ModelConfig(); }
            catch { return new ModelConfig(); }
        }

        public static ModelConfig Parse(string text)
        {
            var config = new ModelConfig();
            foreach (string raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal)) continue;
                line = line.Replace('：', ':').Replace('＝', '=');
                int split = line.IndexOf(':');
                int equals = line.IndexOf('=');
                if (split < 0 || equals >= 0 && equals < split) split = equals;
                if (split <= 0) continue;
                string key = line.Substring(0, split).Trim().ToLowerInvariant();
                string value = line.Substring(split + 1).Trim().Trim('"', '\'');
                if (value.Length == 0) continue;
                if (key == "api地址" || key == "api" || key == "base_url" || key == "baseurl" || key == "endpoint") config.BaseUrl = value;
                else if (key == "key" || key == "api_key" || key == "apikey" || key == "密钥") config.ApiKey = value;
                else if (key == "模型" || key == "model") config.ModelName = value;
                else if (key == "path" || key == "chat_path") config.ChatPath = value;
            }
            config.BaseUrl = config.BaseUrl.Trim().TrimEnd('/');
            return config;
        }

        public string Serialize()
        {
            return "api地址：" + BaseUrl + "\nkey：" + ApiKey + "\n模型：" + ModelName + "\n";
        }

        public void Save(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory)) throw new DirectoryNotFoundException("配置目录不存在。");
            string temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, Serialize(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static string Redact(string text, string key)
        {
            string value = ScrubSecrets(text, key);
            return value.Length > 1200 ? value.Substring(0, 1200) + "…" : value;
        }

        public static string ScrubSecrets(string text, string key)
        {
            string value = text ?? "";
            if (!String.IsNullOrEmpty(key)) value = value.Replace(key, "sk-***");
            value = Regex.Replace(value, "sk-[A-Za-z0-9_\\-]{8,}", "sk-***");
            return value;
        }

        public static object ScrubValues(object value, string key)
        {
            var text = value as string;
            if (text != null) return ScrubSecrets(text, key);
            var map = value as System.Collections.Generic.Dictionary<string, object>;
            if (map != null)
            {
                var result = new System.Collections.Generic.Dictionary<string, object>();
                foreach (var pair in map) result[pair.Key] = ScrubValues(pair.Value, key);
                return result;
            }
            var array = value as object[];
            if (array != null)
            {
                var result = new object[array.Length];
                for (int index = 0; index < array.Length; index++) result[index] = ScrubValues(array[index], key);
                return result;
            }
            return value;
        }
    }
}
