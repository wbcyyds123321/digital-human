using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Yixuan.AI
{
    /// <summary>
    /// DeepSeek（OpenAI 兼容）LLM 客户端。
    ///
    /// 【为什么用"协程 + TaskCompletionSource"】
    /// UnityWebRequest 必须在主线程发；而 ChatSession 那边用 async/await 写最顺手。
    /// 所以把"协程驱动网络"包成一个 Task：
    ///     ChatAsync 里 StartCoroutine 发起请求；
    ///     协程里每帧 yield return null 等 op.isDone；
    ///     结束时 tcs.SetResult(结果) → await 方拿到结果。
    /// ★ 绝不能用 while 空转等网络 —— 会把 Unity 编辑器卡死（PROJECT_NOTES 的教训）。
    ///
    /// 【失败处理】网络错误 / 超时 / HTTP 非 2xx / 回复为空 → 重试 1 次 → 仍失败返回 null，
    /// 同时写 LastError，绝不抛异常（见 ILLMClient.cs 的约定）。
    /// 日志只打印长度/耗时/错误原因，绝不打印 API Key。
    /// </summary>
    public partial class DeepSeekClient : ILLMClient
    {
        /// <summary>B outbound-call ledger hook (observation only; never alters requests).</summary>
        public static Action<string> RequestObserver;
        internal static void NotifyOutbound(string detail){var h=RequestObserver;if(h!=null)try{h(detail);}catch{}}

        // ---------- 请求 / 响应 JSON 结构（JsonUtility 用；字段名对应线上协议）----------

        [System.Serializable]
        class RequestBody
        {
            public string model;
            public ChatMessage[] messages;   // 直接复用 ILLMClient.cs 的 ChatMessage（role / content）
            public float temperature;
            public int max_tokens;
            public bool stream;
            public ThinkingOption thinking;   // {"thinking":{"type":"disabled"}} 关闭思考模式
            public string reasoning_effort;   // low / high（thinking=on 时生效）
        }

        [System.Serializable]
        class ThinkingOption
        {
            public string type;               // enabled / disabled
        }

        [System.Serializable]
        class ResponseFormat
        {
            public string type;
        }

        // 下面三个类是 JsonUtility 反序列化的目标：字段由运行时反射赋值，
        // 编译器看不到赋值点会报 CS0649（不是 bug），这里统一关掉。
#pragma warning disable 0649
        [System.Serializable]
        class ResponseBody
        {
            public Choice[] choices;
            public UsageInfo usage;
        }

        [System.Serializable]
        class Choice
        {
            public ResponseMessage message;
            public string finish_reason;          // stop / length / content_filter …
        }

        [System.Serializable]
        class ResponseMessage
        {
            public string content;
            public string reasoning_content;       // 推理类模型（V4 flash 可能带）的思考内容
        }

        [System.Serializable]
        class UsageInfo
        {
            public int prompt_tokens;
            public int completion_tokens;
            public int total_tokens;
            public int prompt_cache_hit_tokens;
            public int prompt_cache_miss_tokens;
        }
#pragma warning restore 0649

        // ---------- 常量 ----------

        const int MaxAttempts = 3;          // 首次 + 重试 2 次（空回复概率低时足够兜住）
        // max_tokens 现在随 MaxReplyChars 动态计算（见 BuildRequestBody）
        const float RetryDelaySec = 0.6f;   // 重试前稍等，避开瞬时抖动
        const float TimeoutPaddingSec = 2f; // 协程内兜底超时的余量（比 UnityWebRequest.timeout 多留一点）

        static bool _warnedLegacyModel;     // 旧模型名 → 新模型名的兼容映射只警告一次

        readonly AIConfig _config;
        readonly MonoBehaviour _host;

        /// <summary>是否有请求正在进行（防重入）。接口是只读属性，所以 setter 私有。</summary>
        public bool IsBusy { get; private set; }

        /// <summary>最近一次失败原因；成功时为空串</summary>
        public string LastError { get; private set; }

        // ---- usage / 前缀缓存统计（DeepSeek 返回 prompt_cache_hit/miss_tokens 时才有值）----
        public int LastPromptTokens { get; private set; } = -1;
        public int LastCompletionTokens { get; private set; } = -1;
        public int LastPromptCacheHitTokens { get; private set; } = -1;
        public int LastPromptCacheMissTokens { get; private set; } = -1;

        void ResetUsageStats()
        {
            LastPromptTokens = LastCompletionTokens = -1;
            LastPromptCacheHitTokens = LastPromptCacheMissTokens = -1;
        }

        void CaptureUsage(string responseBody)
        {
            if (string.IsNullOrEmpty(responseBody)) return;
            try
            {
                UsageInfo usage = JsonUtility.FromJson<ResponseBody>(responseBody)?.usage;
                if (usage == null) return;
                LastPromptTokens = usage.prompt_tokens;
                LastCompletionTokens = usage.completion_tokens;
                LastPromptCacheHitTokens = usage.prompt_cache_hit_tokens;
                LastPromptCacheMissTokens = usage.prompt_cache_miss_tokens;
            }
            catch (Exception) { }
        }

        string UsageSuffix()
        {
            if (LastPromptTokens < 0) return "";
            string text = "，prompt=" + LastPromptTokens + "，completion=" + LastCompletionTokens;
            if (LastPromptCacheHitTokens >= 0 || LastPromptCacheMissTokens >= 0)
                text += "，cacheHit=" + LastPromptCacheHitTokens + "，cacheMiss=" + LastPromptCacheMissTokens;
            return text;
        }

        /// <param name="config">AI 配置（Endpoint / Model / Temperature / TimeoutSec / ApiKey）</param>
        /// <param name="coroutineHost">用来跑协程的 MonoBehaviour（一般是 ChatSession，必须处于激活状态）</param>
        public DeepSeekClient(AIConfig config, MonoBehaviour coroutineHost)
        {
            _config = config;
            _host = coroutineHost;
            LastError = "";
        }

        /// <summary>
        /// 发一轮对话，返回 LLM 原始文本（可能是包着废话的 JSON）；失败/取消/忙 返回 null，不抛异常。
        /// </summary>
        public Task<string> ChatAsync(List<ChatMessage> messages, CancellationToken ct)
        {
            // ---- 前置检查：任一项不满足都直接返回 null（不抛异常）----
            if (IsBusy)
            {
                LastError = "上一次请求还在进行中";
                Debug.LogWarning("[DeepSeek] ✗ " + LastError);
                return Task.FromResult<string>(null);
            }
            if (_config == null) return Fail("AIConfig 为空");
            if (_host == null) return Fail("coroutineHost 为空（构造 DeepSeekClient 时应传一个 MonoBehaviour）");
            if (messages == null || messages.Count == 0) return Fail("消息列表为空");
            if (string.IsNullOrEmpty(_config.Endpoint)) return Fail("Endpoint 未配置");
            if (!_config.HasApiKey) return Fail("未配置 API Key（环境变量 DEEPSEEK_API_KEY 或 apikey.txt）");
            if (ct.IsCancellationRequested)
            {
                LastError = "请求已取消";
                return Task.FromResult<string>(null);
            }

            LastError = "";
            ResetUsageStats();
            IsBusy = true;

            var tcs = new TaskCompletionSource<string>();
            try
            {
                _host.StartCoroutine(CoChat(messages, ct, tcs));
            }
            catch (Exception e)
            {
                // 例如宿主 GameObject 未激活时 StartCoroutine 会抛异常
                IsBusy = false;
                LastError = "启动协程失败：" + e.Message;
                Debug.LogWarning("[DeepSeek] ✗ " + LastError);
                return Task.FromResult<string>(null);
            }
            return tcs.Task;
        }

        /// <summary>统一的失败出口：写 LastError + 一条警告日志，返回"已完成且结果为 null"的 Task</summary>
        Task<string> Fail(string reason)
        {
            LastError = reason;
            Debug.LogWarning("[DeepSeek] ✗ " + reason);
            return Task.FromResult<string>(null);
        }

        /// <summary>
        /// 协程主体：发请求 → 等完成（每帧检查取消/兜底超时）→ 解析 content → 失败重试。
        /// 全程只 yield，不空转；真正的 Unity 对象操作都在主线程。
        /// </summary>
        IEnumerator CoChat(List<ChatMessage> messages, CancellationToken ct, TaskCompletionSource<string> tcs)
        {
            string body = null;
            string bodyError = null;
            try { body = BuildRequestBody(messages); }
            catch (Exception e) { bodyError = "构造请求体失败：" + e.Message; }

            string result = null;
            string lastError = bodyError;
            int attempts = 0;
            float t0 = Time.realtimeSinceStartup;

            if (body != null)
            {
                for (int attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    if (ct.IsCancellationRequested) { lastError = "请求已取消"; break; }

                    if (attempt > 0)
                    {
                        // 重试前等一会儿（协程等待，不阻塞主线程）
                        yield return new WaitForSeconds(RetryDelaySec);
                        if (ct.IsCancellationRequested) { lastError = "请求已取消"; break; }
                    }

                    attempts = attempt + 1;
                    NotifyOutbound("request|kind=llm|model=" + (_config != null ? _config.Model : "") + "|attempt=" + attempts);

                    UnityWebRequest req = null;
                    string attemptError = null;
                    try
                    {
                        req = BuildRequest(body);
                    }
                    catch (Exception e)
                    {
                        attemptError = "创建请求失败：" + e.Message;
                    }

                    if (req != null)
                    {
                        UnityWebRequestAsyncOperation op = null;
                        bool startFailed = false;
                        try { op = req.SendWebRequest(); }
                        catch (Exception e) { attemptError = "发送请求失败：" + e.Message; startFailed = true; }

                        if (!startFailed && op != null)
                        {
                            // 兜底超时：UnityWebRequest.timeout 之外再守一道，避免极端情况卡死状态机
                            float timeoutSec = _config.TimeoutSec > 0 ? _config.TimeoutSec : 20f;
                            float deadline = Time.realtimeSinceStartup + timeoutSec + TimeoutPaddingSec;

                            // 每帧检查一次：既不空转，也能及时响应取消
                            while (!op.isDone)
                            {
                                if (ct.IsCancellationRequested)
                                {
                                    req.Abort();
                                    attemptError = "请求已取消";
                                    break;
                                }
                                if (Time.realtimeSinceStartup > deadline)
                                {
                                    req.Abort();
                                    attemptError = "请求超时（>" + timeoutSec.ToString("F0") + "s）";
                                    break;
                                }
                                yield return null;
                            }

                            if (op.isDone)
                            {
                                attemptError = null;
                                if (req.result == UnityWebRequest.Result.Success)
                                {
                                    string parseError = null;
                                    string content = ExtractContent(
                                        req.downloadHandler != null ? req.downloadHandler.text : null,
                                        out parseError);
                                    if (!string.IsNullOrEmpty(content) && req.downloadHandler != null)
                                        CaptureUsage(req.downloadHandler.text);

                                    if (string.IsNullOrEmpty(content))
                                        attemptError = string.IsNullOrEmpty(parseError) ? "回复内容为空" : parseError;
                                    else
                                        result = content;
                                }
                                else
                                {
                                    attemptError = DescribeFailure(req);
                                }
                            }
                        }

                        req.Dispose();   // 释放上传/下载 handler，避免长时间对话累积内存
                    }

                    NotifyOutbound("result|kind=llm|model=" + (_config != null ? _config.Model : "") + "|attempt=" + attempts +
                        "|error=" + (attemptError ?? ""));
                    if (result != null) break;

                    lastError = attemptError;
                    if (ct.IsCancellationRequested) break;
                    // 这里不打印日志：等最终成功/最终失败各打一条，避免一次失败出现两条日志
                }
            }

            IsBusy = false;

            if (result != null)
            {
                LastError = "";
                float dt = Time.realtimeSinceStartup - t0;
                Debug.Log("[DeepSeek] ✓ 回复 " + result.Length + " 字，耗时 " + dt.ToString("F2") + "s" +
                          (attempts > 1 ? "（第 " + attempts + " 次尝试成功）" : "") +
                          UsageSuffix());
                tcs.TrySetResult(result);
            }
            else
            {
                LastError = string.IsNullOrEmpty(lastError) ? "请求失败" : lastError;
                Debug.LogWarning("[DeepSeek] ✗ 请求失败（尝试 " + Mathf.Max(1, attempts) + " 次）：" + LastError);
                // 失败也 SetResult(null)：调用方 await 后拿到 null 去回退 RuleBrain，不抛异常
                tcs.TrySetResult(null);
            }
        }

        // ---------- 请求构造 ----------

        /// <summary>拼 OpenAI 兼容请求体：model / messages / temperature / response_format / max_tokens / stream</summary>
        string BuildRequestBody(List<ChatMessage> messages, bool stream = false)
        {
            var body = new RequestBody();
            string model = _config.Model;
            // 2026-10 官方 GET /models 只剩 deepseek-flash / deepseek-v4-pro；
            // 旧的 deepseek-chat / deepseek-reasoner 已下架（多轮后会返回空内容）。
            // 这里做一次兼容映射，防止旧配置资产静默失效；警告只打一次。
            if (string.IsNullOrEmpty(model) || model == "deepseek-chat" || model == "deepseek-reasoner")
            {
                if (!string.IsNullOrEmpty(model) && !_warnedLegacyModel)
                {
                    _warnedLegacyModel = true;
                    Debug.LogWarning("[DeepSeek] 模型「" + model + "」已从官方模型列表下架，自动改用 deepseek-flash（建议在 AIConfig 里直接改掉）");
                }
                model = "deepseek-flash";
            }
            body.model = model;

            body.messages = new ChatMessage[messages.Count];
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                body.messages[i] = (m != null)
                    ? new ChatMessage(m.role, m.content)
                    : new ChatMessage("user", "");
            }

            body.temperature = _config.Temperature;
            // ★ 不再使用 response_format=json_object（2026-10-18）：
            //   官方 JSON Output 文档明确写着「API 有概率返回空 content，正在优化，建议改 prompt 缓解」；
            //   实测同一条请求重试 2 次都返回纯空格（确定性失败），且多轮后概率显著上升，
            //   表现就是「聊两句就断」。改为靠 system prompt 约束输出 JSON，
            //   再由 ResponseParser 从容错解析（它本来就能从代码块/前后缀里截 JSON）。
            int maxTokens = 400;
            if (_config != null)
                maxTokens = Mathf.Clamp(300 + Mathf.Max(20, _config.MaxReplyChars) * 3, 400, 4000);
            body.max_tokens = maxTokens;
            body.stream = stream;
            // ★ 关闭思考模式（2026-10-18）：
            //   deepseek-flash 默认开启 thinking（effort=high），但我们只需要一段几十字的 JSON。
            //   开着会：① 更慢 ② 撞上官方已知 bug（deepseek-ai/DeepSeek-V3#1673：
            //   答案被放进 reasoning_content，content 只剩空白）→ 多轮对话从第 3 轮起"答非所问"。
            body.thinking = new ThinkingOption();
            bool thinkingOn = _config != null && _config.ThinkingEnabled;
            body.thinking.type = thinkingOn ? "enabled" : "disabled";
            // 注意：JsonUtility 会把 null string 序列化成 ""，而 DeepSeek 的
            // reasoning_effort 只接受 none/minimal/low/medium/high/...；空串会 422。
            // 关闭思考时显式发 none，既合法又是最快档。
            if (thinkingOn)
                body.reasoning_effort = (_config != null && _config.ThinkingEffort == "high") ? "high" : "low";
            else
                body.reasoning_effort = "none";

            return JsonUtility.ToJson(body);
        }

        /// <summary>构造 UnityWebRequest：POST JSON + Authorization 头 + 超时</summary>
        UnityWebRequest BuildRequest(string json)
        {
            var req = new UnityWebRequest(_config.Endpoint, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.disposeUploadHandlerOnDispose = true;
            req.disposeDownloadHandlerOnDispose = true;

            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Accept", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + _config.ApiKey);

            if (_config.TimeoutSec > 0) req.timeout = _config.TimeoutSec;
            return req;
        }

        // ---------- 响应解析 / 错误描述 ----------

        /// <summary>从 OpenAI 兼容响应里取 choices[0].message.content；失败时写 error 并返回 null</summary>
        static string ExtractContent(string body, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(body))
            {
                error = "响应体为空";
                return null;
            }

            ResponseBody resp = null;
            try { resp = JsonUtility.FromJson<ResponseBody>(body); }
            catch (Exception e)
            {
                error = "响应 JSON 解析失败：" + e.Message;
                return null;
            }

            if (resp == null || resp.choices == null || resp.choices.Length == 0)
            {
                error = "响应里没有 choices（可能不是正常的对话响应）";
                return null;
            }

            Choice choice = resp.choices[0];
            ResponseMessage msg = (choice != null) ? choice.message : null;
            string content = (msg != null) ? msg.content : null;
            // ★ 修复（2026-10-18 用户实测）：旧模型别名在多轮对话后会返回**纯空格**内容
            //   （实测 21 / 17 个空格）。原来只判 IsNullOrEmpty，空格被当成合法回复送进解析器，
            //   于是从第 3 轮起每轮都「解析失败，回退 RuleBrain」。这里把"只有空白"当失败处理，
            //   交给重试 / RuleBrain 兜底，而不是让一个空格串污染状态机。
            if (string.IsNullOrWhiteSpace(content))
            {
                // 诊断/兜底（2026-10-18）：deepseek-flash 思考模式默认开启，官方已知 bug
                // （deepseek-ai/DeepSeek-V3#1673）会把答案放进 reasoning_content、content 只剩空白。
                // 正常路径已在请求里 thinking=disabled；万一仍出现，就从 reasoning 里抽最后一个带 reply 的 JSON。
                string reasoning = msg != null ? msg.reasoning_content : null;
                string finish = choice != null ? choice.finish_reason : null;

                string salvaged = ExtractJsonWithReply(reasoning);
                if (!string.IsNullOrEmpty(salvaged))
                {
                    Debug.LogWarning("[DeepSeek] content 为空但 reasoning 里有答案，已从 reasoning 兜底提取（finish=" + finish + "）");
                    return salvaged;
                }

                error = "choices[0].message.content 为空或只有空白（" +
                        (string.IsNullOrEmpty(content) ? "null" : content.Length + " 个空白字符") +
                        "，finish=" + (string.IsNullOrEmpty(finish) ? "?" : finish) +
                        "，reasoning=" + (string.IsNullOrEmpty(reasoning) ? "0" : reasoning.Length.ToString()) + "字" +
                        "）响应=" + Shorten(body, 260);
                return null;
            }
            return content.Trim();
        }

        /// <summary>
        /// 从 reasoning_content 里抽最后一个「配对完整且含 reply 字段」的 JSON 对象（思考模式 bug 兜底用）。
        /// 从后往前找 —— 思维链里最后出现的 JSON 才是最终答案。
        /// </summary>
        static string ExtractJsonWithReply(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string best = null;
            for (int start = 0; start < text.Length; start++)
            {
                if (text[start] != '{') continue;

                int depth = 0;
                bool inStr = false, esc = false, closed = false;
                int end = -1;
                for (int i = start; i < text.Length; i++)
                {
                    char ch = text[i];
                    if (inStr)
                    {
                        if (esc) { esc = false; continue; }
                        if (ch == '\\') { esc = true; continue; }
                        if (ch == '"') inStr = false;
                        continue;
                    }
                    if (ch == '"') { inStr = true; continue; }
                    if (ch == '{') depth++;
                    else if (ch == '}')
                    {
                        depth--;
                        if (depth == 0) { end = i; closed = true; break; }
                    }
                }
                if (!closed || end < 0) continue;

                string cand = text.Substring(start, end - start + 1);
                if (cand.IndexOf("\"reply\"", System.StringComparison.Ordinal) < 0) continue;
                try
                {
                    var plan = JsonUtility.FromJson<BehaviorPlan>(cand);
                    if (plan != null && !string.IsNullOrEmpty(plan.reply)) best = cand;
                }
                catch (Exception) { /* 不是合法行为 JSON，跳过 */ }
            }
            return best;
        }

        /// <summary>把失败请求描述成一句可读日志（HTTP 码 / 结果 / 错误 / 响应片段）</summary>
        static string DescribeFailure(UnityWebRequest req)
        {
            if (req == null) return "请求对象为空";

            string body = "";
            try
            {
                if (req.downloadHandler != null && !string.IsNullOrEmpty(req.downloadHandler.text))
                    body = "，响应=" + Shorten(req.downloadHandler.text, 200);
            }
            catch (Exception)
            {
                // 读不到响应体就算了，不影响错误描述
            }

            string err = string.IsNullOrEmpty(req.error) ? "" : "，error=" + req.error;
            return "HTTP " + req.responseCode + " / " + req.result + err + body;
        }

        static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
