using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Yixuan.Vision
{
    /// <summary>
    /// 千问视觉请求客户端：把 EyeCamera / 电脑摄像头的 JPEG 或视频帧序列发到配置的 Qwen 模型，
    /// 拿回中文场景描述。key 只从本地文件读取，不写进场景/日志。
    ///
    /// ★ 为什么不用 UnityWebRequest：
    ///   实测每次新建 HTTPS 连接（TLS 握手）会让同样的请求延迟从 ~0.9s 抖到 2~3s；
    ///   改用**单例 HttpClient**（HTTP/1.1 keep-alive 连接复用）后，稳定在 ~0.9s。
    /// </summary>
    public partial class QwenVisionClient : MonoBehaviour
    {
        // 2026-11-02：qwen3.7-flash / qwen3-vl-plus 的免费额度耗尽（403 AllocationQuota.FreeTierOnly），
        // 换用同一网关下有额度、且同样支持图片输入的 qwen3.5-omni-flash（实测 0.4~0.6s，中文描述准确）。
        // 另外保留一条回退链：主模型额度/限流/不支持图片时自动换下一个，不必等到 5 分钟退避。
        public const string DefaultModel = "qwen3.5-omni-flash";
        [Tooltip("主视觉模型；旧场景序列化的旧模型名会在 Awake 被统一替换成本值")]
        public string Model = DefaultModel;
        [Tooltip("主模型不可用（额度耗尽/限流/不支持图片）时按顺序回退的模型（同一 endpoint）")]
        public string[] FallbackModels = { "qwen-vl-ocr", "deepseek-v4.1-flash-1017", "qwen3.8-omni-flash" };
        public string Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";
        [Tooltip("后台观察的最大输出 token（48 太短，描述容易只剩一句；64 更完整）")]
        public int MaxTokens = 64;
        [Tooltip("实时问答的最大输出 token（回答要短，但留出“看不清”之类完整句子的空间）")]
        public int AskMaxTokens = 128;
        public float TimeoutSec = 60f;

        string _apiKey;
        CancellationTokenSource _ambientCts;
        CancellationTokenSource _askCts;   // 实时问答的在途请求（取消/超时用）

        // 单例 HttpClient：连接复用，避免每次请求都做 DNS/TCP/TLS 握手
        static HttpClient _http;
        static HttpClient Http
        {
            get
            {
                if (_http == null)
                {
                    // 注意：不要设置 MaxConnectionsPerServer —— Unity(Mono) 的 HttpClientHandler
                    // 对该属性直接抛 NotImplementedException（实测）。
                    _http = new HttpClient();
                    _http.Timeout = TimeSpan.FromSeconds(90);
                }
                return _http;
            }
        }
        public bool HasKey { get { return !string.IsNullOrEmpty(_apiKey); } }
        public string LastError { get; private set; }
        long _blockedUntilTicks;string _serviceStatus;
        public bool ServiceBlocked {get {return DateTime.UtcNow.Ticks<System.Threading.Interlocked.Read(ref _blockedUntilTicks);}}
        public string ServiceStatus {get {return ServiceBlocked?_serviceStatus:"视觉服务待命";}}
        public void ResetServiceBackoff(){System.Threading.Interlocked.Exchange(ref _blockedUntilTicks,0);_serviceStatus=null;LastError=null;}
        void BlockService(string raw,int seconds){_serviceStatus=raw;System.Threading.Interlocked.Exchange(ref _blockedUntilTicks,DateTime.UtcNow.AddSeconds(seconds).Ticks);}
        public int LastPromptTokens { get; private set; }
        public int LastCompletionTokens { get; private set; }

        void Awake()
        {
            // 场景序列化会保留旧模型名；统一用当前的主模型，并补全回退链。
            Model = DefaultModel;
            if (FallbackModels == null || FallbackModels.Length == 0)
                FallbackModels = new[] { "qwen-vl-ocr", "deepseek-v4.1-flash-1017", "qwen3.8-omni-flash" };
            LoadKey();
            Debug.Log("[Vision] 视觉模型=" + Model + "；回退=" + string.Join(",", FallbackModels) + "；HasKey=" + HasKey);
        }

        public bool LoadKey()
        {
            _apiKey = null;
            var candidates = new List<string>();

#if UNITY_EDITOR
            // 编辑器测试：之前用户已经放好的本地 key
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "_vision_poc", "qwen_apikey.txt"));
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "_vision_poc", "qwen_apikey.txt"));
#endif
            // 打包后：放到 persistentDataPath，或手动填到 Inspector
            candidates.Add(Path.Combine(Application.persistentDataPath, "qwen_apikey.txt"));

            foreach (var p in candidates)
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    string s = File.ReadAllText(p).Trim().TrimStart('\uFEFF');
                    if (!string.IsNullOrEmpty(s) && s.StartsWith("sk-", StringComparison.Ordinal))
                    {
                        _apiKey = s;
                        break;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Vision] 读取 key 失败（" + p + "）：" + e.Message);
                }
            }

            if (string.IsNullOrEmpty(_apiKey))
                Debug.LogWarning("[Vision] 未找到 Qwen API key（" + string.Join(" | ", candidates) + "）");
            else
                Debug.Log("[Vision] Qwen key 已加载（长度 " + _apiKey.Length + "，不打印内容）");
            return HasKey;
        }

        public void SetApiKey(string key)
        {
            _apiKey = string.IsNullOrEmpty(key) ? null : key.Trim();
        }

        bool _warmupStarted;
        /// <summary>预热：进入 Play 后先发一个极小的请求，把 DNS/TCP/TLS 和鉴权握手提前做掉，
        /// 避免用户第一次问问题时吃 3~5s 的冷启动。</summary>
        public void Warmup()
        {
            if (_warmupStarted) return;
            _warmupStarted = true;
            StartCoroutine(WarmupRoutine());
        }

        IEnumerator WarmupRoutine()
        {
            yield return new WaitForSecondsRealtime(0.5f);
            var observer = GetComponent<VisionObserver>();
            if (observer != null && !observer.AnySourceEnabled)
            {
                _warmupStarted = false;
                yield break;
            }
            if (!HasKey) yield break;
            string json = "{\"model\":\"" + JsonEscape(Model) + "\",\"messages\":[{\"role\":\"user\",\"content\":\"回复OK\"}],"
                        + "\"max_tokens\":2,\"enable_thinking\":false}";
            CancellationTokenSource cts = new CancellationTokenSource();
            Task<string> task = null;
            try { task = PostJsonAsync(json, cts.Token); }
            catch (Exception e)
            {
                Debug.Log("[Vision] Qwen 预热启动失败（不影响主流程）：" + e.Message);
                cts.Dispose();
                yield break;
            }
            float t0 = Time.realtimeSinceStartup;
            while (!task.IsCompleted && Time.realtimeSinceStartup - t0 < 20f)
                yield return null;
            if (task.IsCompleted && !task.IsFaulted)
                Debug.Log("[Vision] Qwen 预热完成（" + (Time.realtimeSinceStartup - t0).ToString("F2") + "s，连接已复用）");
            else
                Debug.Log("[Vision] Qwen 预热超时/失败（不影响主流程）");
            cts.Dispose();
        }

        /// <summary>中止正在进行的实时问答 HTTP 请求（取消/超时用；不影响后台观察请求）。</summary>
        public void AbortAskRequest()
        {
            var cts = _askCts;
            _askCts = null;
            if (cts == null) return;
            try { cts.Cancel(); } catch (Exception) { }
        }

        public void AbortAmbientRequest()
        {
            var cancellation = _ambientCts;
            _ambientCts = null;
            if (cancellation != null)
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>发送最多两张图片（游戏视角 + 电脑摄像头），回调中文描述。</summary>
        public IEnumerator Describe(byte[] gameJpg, byte[] webcamJpg, Action<string> onDone, Action<string> onError = null)
        {
            yield return Send(gameJpg, webcamJpg,
                BuildPrompt(gameJpg != null, webcamJpg != null), MaxTokens, false, onDone, onError);
        }

        /// <summary>
        /// 问题驱动的实时问答：把用户的**原问题**作为 prompt 发过去，只要一句针对性答案。
        /// 与 Describe 的区别：Describe 是泛泛描述场景，答不了"我手里拿的是什么"这类具体问题。
        /// </summary>
        public IEnumerator Ask(string question, byte[] gameJpg, byte[] webcamJpg,
                               Action<string> onDone, Action<string> onError = null)
        {
            yield return Send(gameJpg, webcamJpg,
                BuildAskPrompt(question, gameJpg != null, webcamJpg != null), AskMaxTokens, true, onDone, onError);
        }

        /// <summary>Structured task queries keep the complete question and caller's JSON contract.</summary>
        public IEnumerator AskStructured(string prompt, byte[] gameJpg, Action<string> onDone, Action<string> onError = null)
        {
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 6000)
            {
                if (onError != null) onError("structured_vision_prompt_size_limit");
                yield break;
            }
            yield return Send(gameJpg, null, prompt, AskMaxTokens, true, onDone, onError);
        }

        IEnumerator Send(byte[] gameJpg, byte[] webcamJpg, string prompt, int maxTokens,
                         bool isAsk, Action<string> onDone, Action<string> onError)
        {
            if ((gameJpg == null || gameJpg.Length == 0) && (webcamJpg == null || webcamJpg.Length == 0))
            {
                if (onError != null) onError("没有可发送的图片");
                yield break;
            }
            yield return SendJson(BuildJson(gameJpg, webcamJpg, prompt, maxTokens), isAsk, onDone, onError);
        }

        IEnumerator SendJson(string json, bool isAsk, Action<string> onDone, Action<string> onError)
        {
            if(ServiceBlocked){LastError=ServiceStatus;if(onError!=null)onError(LastError);yield break;}
            if (!HasKey && !LoadKey())
            {
                LastError = "未找到 Qwen API key";
                if (onError != null) onError(LastError);
                yield break;
            }
            var cancellation = new CancellationTokenSource();
            if (isAsk)
            {
                AbortAskRequest();
                _askCts = cancellation;
            }
            else
            {
                AbortAmbientRequest();
                _ambientCts = cancellation;
            }
            Task<string> task = null;
            try
            {
                task = PostJsonAsync(json, cancellation.Token);
                float started = Time.realtimeSinceStartup;
                while (!task.IsCompleted && Time.realtimeSinceStartup - started < Mathf.Max(5f, TimeoutSec))
                    yield return null;
                string error = null;
                if (!task.IsCompleted) error = "视觉请求超时";
                else if (task.IsCanceled) error = "视觉请求已取消";
                else if (task.IsFaulted) error = task.Exception.GetBaseException().Message;
                if (error != null)
                {
                    LastError = error;
                    if (onError != null) onError(error);
                    yield break;
                }
                string content = ParseContent(task.Result);
                if (string.IsNullOrEmpty(content))
                {
                    LastError = "模型返回空文本";
                    if (onError != null) onError(LastError);
                    yield break;
                }
                LastError = null;
                if (onDone != null) onDone(content);
            }
            finally
            {
                if (task != null && !task.IsCompleted) cancellation.Cancel();
                if (ReferenceEquals(_askCts, cancellation)) _askCts = null;
                if (ReferenceEquals(_ambientCts, cancellation)) _ambientCts = null;
                cancellation.Dispose();
            }
        }

        /// <summary>实际发 HTTP 的 async 方法：复用单例 HttpClient 的连接（keep-alive）。</summary>
        sealed class VisionServiceException : Exception
        {
            public VisionServiceException(string message) : base(message) { }
        }

        async Task<string> PostJsonAsync(string json, CancellationToken ct)
        {
            if (ServiceBlocked) throw new VisionServiceException(ServiceStatus);
            var chain = ModelChain();
            string lastError = null;
            for (int mi = 0; mi < chain.Count; mi++)
            {
                string model = chain[mi];
                string body = SwapModel(json, chain[0], model);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    try
                    {
                        using (var req = new HttpRequestMessage(HttpMethod.Post, Endpoint))
                        {
                            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                            using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                                                       .ConfigureAwait(false))
                            {
                                string raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (resp.IsSuccessStatusCode) return raw;
                                int code = (int)resp.StatusCode;
                                bool quota = ContainsQuota(raw);
                                bool retryable = code == 429 || code >= 500;
                                // 额度耗尽 / 模型不可用 / 不支持图片：如果还有回退模型就直接换，不整段退避。
                                if ((quota || code == 404 || code == 403 || code == 400) && mi + 1 < chain.Count)
                                {
                                    lastError = "model " + model + " HTTP " + code + " " + Truncate(raw, 120);
                                    Debug.LogWarning("[Vision] 模型 " + model + " 不可用（HTTP " + code + "），回退到 " +
                                                     chain[mi + 1] + "：" + Truncate(raw, 140));
                                    break; // 跳到下一个模型
                                }
                                if (retryable && attempt < 2)
                                {
                                    Debug.LogWarning("[Vision] 模型 " + model + " HTTP " + code + "，第 " + (attempt + 1) + " 次重试");
                                    await Task.Delay(1200 * (attempt + 1)).ConfigureAwait(false);
                                    continue;
                                }
                                if (quota || code == 401 || code == 403)
                                {
                                    BlockService("视觉服务的免费额度已耗尽/拒绝访问（已尝试 " + chain.Count +
                                                 " 个模型，主模型=" + chain[0] + "），暂时无法识别画面；已暂停重复请求五分钟。", 300);
                                    throw new VisionServiceException(ServiceStatus);
                                }
                                if (code == 429)
                                {
                                    BlockService("视觉服务当前限流（HTTP 429），已暂停重试 30 秒；稍后把“游戏内视角”关→开即可恢复。", 30);
                                    throw new VisionServiceException(ServiceStatus);
                                }
                                throw new VisionServiceException("HTTP " + code + " " + Truncate(raw, 200));
                            }
                        }
                    }
                    catch (VisionServiceException) { throw; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        lastError = e.Message;
                        if (attempt < 2) { await Task.Delay(800 * (attempt + 1)).ConfigureAwait(false); continue; }
                        break;
                    }
                }
            }
            throw new VisionServiceException(lastError ?? "视觉服务不可用");
        }

        List<string> ModelChain()
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(Model)) list.Add(Model);
            if (FallbackModels != null)
            {
                for (int i = 0; i < FallbackModels.Length; i++)
                {
                    string m = FallbackModels[i];
                    if (!string.IsNullOrEmpty(m) && !list.Contains(m)) list.Add(m);
                }
            }
            if (list.Count == 0) list.Add(DefaultModel);
            return list;
        }

        static bool ContainsQuota(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            return raw.IndexOf("FreeTierOnly", StringComparison.OrdinalIgnoreCase) >= 0
                || raw.IndexOf("Free quota exhausted", StringComparison.OrdinalIgnoreCase) >= 0
                || raw.IndexOf("insufficient_quota", StringComparison.OrdinalIgnoreCase) >= 0
                || raw.IndexOf("余额不足", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string SwapModel(string json, string from, string to)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to) return json;
            return json.Replace("\"model\":\"" + JsonEscape(from) + "\"", "\"model\":\"" + JsonEscape(to) + "\"");
        }

        string BuildPrompt(bool hasGame, bool hasCam)
        {
            // 注意：别让模型输出"（共38字）"这类元信息 —— 实测会漏进时间线，污染上下文。
            if (hasGame && hasCam)
                return "两张图：1 游戏画面，2 摄像头用户。各一句描述（每句20字内），只写看到的场景/人物/动作；不要解释、不要写字数。";
            if (hasGame)
                return "一句话描述游戏画面（场景、物体，20字内），只写看到的内容；不要解释、不要写字数。";
            return "一句话描述摄像头里的用户（外貌、动作，20字内），只写看到的内容；不要解释、不要写字数。";
        }

        /// <summary>
        /// 实时问答 prompt：只回答这一个问题，禁止反问、禁止闲聊。
        /// 注意：不能把"看不清就说看不清"写得太强硬 —— 实测用户举着物品靠近镜头时，
        /// 模型明明能看出"白色小风扇/红白瓶子"，却因为这句保守指令直接回"看不清"。
        /// 现在改成"优先描述最大/最近的物体；能看出类别/颜色/包装就说；完全看不到才说不清"。
        /// </summary>
        string BuildAskPrompt(string question, bool hasGame, bool hasCam)
        {
            string q = Truncate(question, 80);
            string images;
            if (hasGame && hasCam) images = "图1=游戏画面，图2=摄像头里的用户";
            else if (hasGame) images = "图1=游戏画面";
            else images = "图1=摄像头里的用户";

            return "只回答下面这个问题，不要反问、不要闲聊、不要解释。"
                 + "优先看画面中最大、离镜头最近的物体或人（比如用户手里举着的东西）。"
                 + "用一句中文回答（20字内）：能看出物品的外形/颜色/包装，就说出它是什么（不确定可以说“看起来像…”）；"
                 + "能看出人的动作/表情就直接说；完全看不到时才说“看不清”，不要编造看不到的细节。"
                 + images + "。问题：" + q;
        }

        string BuildJson(byte[] gameJpg, byte[] webcamJpg, string prompt, int maxTokens)
        {
            var sb = new StringBuilder(4096);
            sb.Append("{\"model\":\"").Append(JsonEscape(Model)).Append("\",");
            sb.Append("\"messages\":[{\"role\":\"user\",\"content\":[");
            bool first = true;
            if (gameJpg != null && gameJpg.Length > 0)
            {
                AppendImage(sb, gameJpg, ref first);
            }
            if (webcamJpg != null && webcamJpg.Length > 0)
            {
                AppendImage(sb, webcamJpg, ref first);
            }
            if (!first) sb.Append(',');
            sb.Append("{\"type\":\"text\",\"text\":\"").Append(JsonEscape(prompt)).Append("\"}");
            sb.Append("]}],\"max_tokens\":").Append(maxTokens).Append(",\"enable_thinking\":false}");
            return sb.ToString();
        }

        static void AppendImage(StringBuilder sb, byte[] jpg, ref bool first)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/jpeg;base64,");
            sb.Append(Convert.ToBase64String(jpg));
            sb.Append("\"}}");
        }

        string ParseContent(string raw)
        {
            try
            {
                var resp = JsonUtility.FromJson<QwenResponse>(raw);
                if (resp != null && resp.choices != null && resp.choices.Length > 0 &&
                    resp.choices[0] != null && resp.choices[0].message != null)
                {
                    if (resp.usage != null)
                    {
                        LastPromptTokens = resp.usage.prompt_tokens;
                        LastCompletionTokens = resp.usage.completion_tokens;
                    }
                    return resp.choices[0].message.content;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Vision] 解析 JSON 失败：" + e.Message);
            }
            return null;
        }

        static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        [Serializable]
        class QwenResponse
        {
            public QwenChoice[] choices;
            public QwenUsage usage;
        }

        [Serializable]
        class QwenChoice
        {
            public QwenMessage message;
        }

        [Serializable]
        class QwenMessage
        {
            public string content;
        }

        [Serializable]
        class QwenUsage
        {
            public int prompt_tokens;
            public int completion_tokens;
            public int total_tokens;
        }
    }
}
