using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Yixuan.AI
{
    /// <summary>
    /// DeepSeek SSE 流式客户端（partial：与 DeepSeekClient 共享 AIConfig / 模型映射 / 契约约束）。
    ///
    /// 为什么要用 HttpClient 而不是 UnityWebRequest：
    ///   UnityWebRequest 的流式回调（DownloadHandlerScript）时序/线程容易踩坑；
    ///   HttpClient + ResponseHeadersRead 在后台线程逐行读 SSE，最简单可靠；
    ///   回调只把 delta 放进 ConcurrentQueue，由 ChatSession 主线程协程消费。
    ///
    /// 只发送：model / messages / temperature / max_tokens / stream=true / thinking=disabled。
    /// </summary>
    public partial class DeepSeekClient
    {
        /// <summary>一次流式请求的句柄（主线程轮询 Deltas / Done / Error）。</summary>
        public class StreamHandle
        {
            public readonly ConcurrentQueue<string> Deltas = new ConcurrentQueue<string>();
            public volatile bool Done;
            public volatile bool Faulted;
            public volatile string Error = "";
            public Task Task;
            internal CancellationTokenSource Cts;
            public int Chunks;
            public volatile int PromptTokens = -1;
            public volatile int CompletionTokens = -1;
            public volatile int CacheHitTokens = -1;
            public volatile int CacheMissTokens = -1;

            public void Cancel()
            {
                try { if (Cts != null) Cts.Cancel(); } catch (Exception) { }
            }
        }

        /// <summary>启动一次流式请求（立即返回；真正的数据在后台线程读）。</summary>
        public StreamHandle StartStream(System.Collections.Generic.List<ChatMessage> messages)
        {
            StreamHandle h = new StreamHandle();
            if (_config == null || messages == null || messages.Count == 0)
            {
                h.Done = true; h.Faulted = true; h.Error = "参数为空";
                return h;
            }
            if (string.IsNullOrEmpty(_config.Endpoint))
            {
                h.Done = true; h.Faulted = true; h.Error = "Endpoint 未配置";
                return h;
            }
            if (!_config.HasApiKey)
            {
                h.Done = true; h.Faulted = true; h.Error = "未配置 API Key";
                return h;
            }

            h.Cts = new CancellationTokenSource(TimeSpan.FromSeconds(Mathf.Max(10, _config.TimeoutSec) + 10));
            h.Task = Task.Run(delegate { StreamWorker(messages, h); });
            return h;
        }

        async void StreamWorker(System.Collections.Generic.List<ChatMessage> messages, StreamHandle h)
        {
            try
            {
                string body;
                try { body = BuildRequestBody(messages, true); }
                catch (Exception e) { h.Error = "构造请求体失败：" + e.Message; h.Faulted = true; return; }

                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(Mathf.Max(15, _config.TimeoutSec) + 10);

                    using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, _config.Endpoint))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
                        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                        using (HttpResponseMessage resp = await client.SendAsync(
                                   req, HttpCompletionOption.ResponseHeadersRead, h.Cts.Token).ConfigureAwait(false))
                        {
                            if (!resp.IsSuccessStatusCode)
                            {
                                string errBody = "";
                                try { errBody = (await resp.Content.ReadAsStringAsync().ConfigureAwait(false)); } catch (Exception) { }
                                h.Error = "HTTP " + (int)resp.StatusCode + " " + (errBody.Length > 180 ? errBody.Substring(0, 180) : errBody);
                                h.Faulted = true;
                                return;
                            }

                            using (Stream stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                            {
                                while (true)
                                {
                                    if (h.Cts.IsCancellationRequested) { h.Error = "已取消"; h.Faulted = true; return; }

                                    string line = await reader.ReadLineAsync().ConfigureAwait(false);
                                    if (line == null) break;
                                    if (line.Length == 0) continue;
                                    if (!line.StartsWith("data:")) continue;

                                    string payload = line.Substring(5).Trim();
                                    if (payload == "[DONE]") break;
                                    // 部分供应商/模型会在最后一个块带 usage（含前缀缓存命中数）
                                    if (payload.IndexOf("\"usage\"", StringComparison.Ordinal) >= 0)
                                        CaptureStreamUsage(payload, h);
                                    string delta = ExtractJsonStringField(payload, "content");
                                    if (!string.IsNullOrEmpty(delta))
                                    {
                                        h.Deltas.Enqueue(delta);
                                        h.Chunks++;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                h.Faulted = true;
                h.Error = "已取消/超时";
            }
            catch (Exception e)
            {
                h.Faulted = true;
                h.Error = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                h.Done = true;
                try { h.Cts.Dispose(); } catch (Exception) { }
                string usage = h.PromptTokens < 0
                    ? ""
                    : "，prompt=" + h.PromptTokens + "，completion=" + h.CompletionTokens +
                      (h.CacheHitTokens >= 0 || h.CacheMissTokens >= 0
                          ? "，cacheHit=" + h.CacheHitTokens + "，cacheMiss=" + h.CacheMissTokens
                          : "");
                if (h.Faulted) Debug.LogWarning("[DeepSeek] ✗ 流式失败：" + h.Error);
                else Debug.Log("[DeepSeek] ✓ 流结束（" + h.Chunks + " 个增量块" + usage + "）");
            }
        }

        static void CaptureStreamUsage(string payload, StreamHandle handle)
        {
            int prompt = ExtractJsonIntField(payload, "prompt_tokens");
            int completion = ExtractJsonIntField(payload, "completion_tokens");
            int hit = ExtractJsonIntField(payload, "prompt_cache_hit_tokens");
            int miss = ExtractJsonIntField(payload, "prompt_cache_miss_tokens");
            if (prompt >= 0) handle.PromptTokens = prompt;
            if (completion >= 0) handle.CompletionTokens = completion;
            if (hit >= 0) handle.CacheHitTokens = hit;
            if (miss >= 0) handle.CacheMissTokens = miss;
        }

        static int ExtractJsonIntField(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return -1;
            string needle = "\"" + field + "\"";
            int index = json.IndexOf(needle, StringComparison.Ordinal);
            if (index < 0) return -1;
            int colon = json.IndexOf(':', index + needle.Length);
            if (colon < 0) return -1;
            int start = colon + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            if (end <= start) return -1;
            int value;
            return int.TryParse(json.Substring(start, end - start), out value) ? value : -1;
        }

        /// <summary>从 SSE 的 JSON 块里提取 "content" 字符串（纯 C#，后台线程安全）。</summary>
        static string ExtractJsonStringField(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string needle = "\"" + field + "\"";
            int idx = json.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + needle.Length);
            if (colon < 0) return null;
            int q = colon + 1;
            while (q < json.Length && char.IsWhiteSpace(json[q])) q++;
            if (q >= json.Length || json[q] != '"') return null;

            StringBuilder sb = new StringBuilder();
            int i = q + 1;
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i + 1 >= json.Length) return sb.ToString();
                    char e = json[i + 1];
                    if (e == 'u')
                    {
                        if (i + 5 >= json.Length) return sb.ToString();
                        int code;
                        if (int.TryParse(json.Substring(i + 2, 4),
                                         System.Globalization.NumberStyles.HexNumber,
                                         System.Globalization.CultureInfo.InvariantCulture, out code))
                            sb.Append((char)code);
                        i += 6; continue;
                    }
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(e); break;
                    }
                    i += 2; continue;
                }
                sb.Append(c); i++;
            }
            return sb.ToString();
        }
    }
}
