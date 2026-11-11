using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Yixuan.AI;

namespace Yixuan.Stage5
{
    [Serializable]
    public class Stage5VisionRawReply
    {
        public string status;
        public float[] point_uv;
        public float[] bbox_uv;
        public string description;
        public string error;
    }

    public sealed class Stage5VisionResult
    {
        public bool Ok;
        public string Error = "";
        public string Raw = "";
        public Stage5VisionRawReply Reply;
        public string Model = "";
        public int PromptTokens = -1;
        public int CompletionTokens = -1;
        public float Seconds;
        public string ImageSha256 = "";
        public int HttpStatus;
    }

    /// <summary>
    /// Dedicated DeepSeek text+image branch for Stage 5. It uses the same
    /// AIConfig endpoint/model/key as normal chat, but does not alter the
    /// text-only ChatMessage/ChatAsync path or streaming chat.
    /// Coordinates are top-left UV as requested from the model; the caller
    /// converts to Unity viewport bottom-left before ray math.
    /// </summary>
    public static class Stage5VisionClient
    {
        public const int MaxAttempts = 2;
        const float RequestPaddingSeconds = 3f;

        public static IEnumerator Locate(MonoBehaviour host, AIConfig config, string instruction,
            byte[] jpegBytes, int width, int height, string imageSha256,
            Func<bool> cancelled, Action<Stage5VisionResult> done)
        {
            var result = new Stage5VisionResult { ImageSha256 = imageSha256 ?? "" };
            done = done ?? (_ => { });
            if (config == null) { result.Error = "AIConfig 为空"; done(result); yield break; }
            if (!config.HasApiKey) { result.Error = "DeepSeek API Key 未配置"; done(result); yield break; }
            if (jpegBytes == null || jpegBytes.Length == 0) { result.Error = "图片为空"; done(result); yield break; }

            string model = string.IsNullOrEmpty(config.Model) ? "deepseek-flash" : config.Model;
            result.Model = model;
            string body = BuildBody(model, instruction, jpegBytes, width, height, config.Temperature);
            int maxAttempts = MaxAttempts;
            string lastError = "";
            float timeout = Mathf.Max(15f, config.TimeoutSec + RequestPaddingSeconds);
            float started = Time.realtimeSinceStartup;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (cancelled != null && cancelled()) { result.Error = "cancelled"; done(result); yield break; }
                if (attempt > 0) yield return new WaitForSeconds(0.5f);
                if (cancelled != null && cancelled()) { result.Error = "cancelled"; done(result); yield break; }

                UnityWebRequest request = null;
                try
                {
                    request = new UnityWebRequest(config.Endpoint, "POST");
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.disposeUploadHandlerOnDispose = true;
                    request.disposeDownloadHandlerOnDispose = true;
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.SetRequestHeader("Accept", "application/json");
                    request.SetRequestHeader("Authorization", "Bearer " + config.ApiKey);
                    if (config.TimeoutSec > 0) request.timeout = config.TimeoutSec;
                }
                catch (Exception e)
                {
                    lastError = "创建请求失败：" + e.Message;
                    if (request != null) request.Dispose();
                    continue;
                }

                var op = request.SendWebRequest();
                float deadline = Time.realtimeSinceStartup + timeout;
                while (op != null && !op.isDone)
                {
                    if (cancelled != null && cancelled())
                    {
                        request.Abort();
                        request.Dispose();
                        result.Error = "cancelled";
                        done(result);
                        yield break;
                    }
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        request.Abort();
                        lastError = "请求超时";
                        break;
                    }
                    yield return null;
                }

                if (op == null || !op.isDone)
                {
                    request.Dispose();
                    continue;
                }

                result.HttpStatus = (int)request.responseCode;
                string responseText = request.downloadHandler != null ? request.downloadHandler.text : null;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    lastError = "HTTP " + result.HttpStatus + " " + Truncate(responseText, 300);
                    request.Dispose();
                    continue;
                }

                result.Raw = responseText ?? "";
                string parseError;
                Stage5VisionResult parsed = ParseResponse(result, responseText, out parseError);
                result.PromptTokens = parsed.PromptTokens;
                result.CompletionTokens = parsed.CompletionTokens;
                result.Reply = parsed.Reply;
                if (parsed.Reply == null || string.IsNullOrEmpty(parsed.Reply.status))
                {
                    lastError = string.IsNullOrEmpty(parseError) ? "模型返回中没有有效定位 JSON" : parseError;
                    request.Dispose();
                    continue;
                }

                result.Ok = true;
                result.Error = "";
                result.Seconds = Time.realtimeSinceStartup - started;
                request.Dispose();
                LogResult(result);
                done(result);
                yield break;
            }

            result.Ok = false;
            result.Error = string.IsNullOrEmpty(lastError) ? "图片定位请求失败" : lastError;
            result.Seconds = Time.realtimeSinceStartup - started;
            LogResult(result);
            done(result);
        }

        static Stage5VisionResult ParseResponse(Stage5VisionResult target, string responseText, out string error)
        {
            error = null;
            var shell = new Stage5VisionResult();
            try
            {
                var resp = JsonUtility.FromJson<Stage5ChatResponse>(responseText);
                if (resp != null && resp.usage != null)
                {
                    shell.PromptTokens = resp.usage.prompt_tokens;
                    shell.CompletionTokens = resp.usage.completion_tokens;
                }
                if (resp == null || resp.choices == null || resp.choices.Length == 0 ||
                    resp.choices[0] == null || resp.choices[0].message == null)
                {
                    error = "响应没有 choices[0].message";
                    return shell;
                }
                string content = resp.choices[0].message.content;
                if (string.IsNullOrWhiteSpace(content)) content = resp.choices[0].message.reasoning_content;
                if (string.IsNullOrWhiteSpace(content)) { error = "模型 content 为空"; return shell; }
                string json = ExtractJsonObject(content);
                if (string.IsNullOrEmpty(json)) { error = "模型回复中没有 JSON 对象"; return shell; }
                shell.Reply = JsonUtility.FromJson<Stage5VisionRawReply>(json);
                if (shell.Reply == null) error = "定位 JSON 解析失败";
            }
            catch (Exception e) { error = "响应解析异常：" + e.Message; }
            return shell;
        }

        static void LogResult(Stage5VisionResult r)
        {
            if (r.Ok)
                Debug.Log("[Stage5Vision] ✓ model=" + r.Model + " status=" + (r.Reply != null ? r.Reply.status : "?") +
                          " " + (r.Seconds * 1000f).ToString("F0") + "ms http=" + r.HttpStatus +
                          " prompt=" + r.PromptTokens + " completion=" + r.CompletionTokens +
                          " image=" + ShortHash(r.ImageSha256));
            else
                Debug.LogWarning("[Stage5Vision] ✗ model=" + r.Model + " " + (r.Seconds * 1000f).ToString("F0") +
                                 "ms http=" + r.HttpStatus + " error=" + r.Error + " image=" + ShortHash(r.ImageSha256));
        }

        static string BuildBody(string model, string instruction, byte[] jpeg, int width, int height, float temperature)
        {
            string system =
                "你是角色第一人称视觉定位器。只根据用户指令和这一张真实游戏截图，判断目标长椅在画面中的位置。" +
                "严格只返回一个 JSON 对象，不要 markdown、不要解释。格式：" +
                "{\"status\":\"located|not_found|ambiguous\",\"point_uv\":[x,y],\"bbox_uv\":[x1,y1,x2,y2],\"description\":\"...\"}。" +
                "坐标以图片左上角(0,0)、右下角(1,1)为准；point_uv 必须落在长椅可见实心表面（尽量避开板条缝隙），不能只给 bbox 中心。" +
                "看不到、遮挡或无法区分时填 not_found/ambiguous，不要编造实体 ID、世界坐标或交互能力。";
            string user = string.IsNullOrWhiteSpace(instruction) ? "找到画面中的长椅。" : instruction;
            var sb = new StringBuilder(1024 + jpeg.Length * 2);
            sb.Append("{\"model\":\"").Append(Escape(model)).Append("\",")
              .Append("\"messages\":[")
              .Append("{\"role\":\"system\",\"content\":\"").Append(Escape(system)).Append("\"},")
              .Append("{\"role\":\"user\",\"content\":[")
              .Append("{\"type\":\"text\",\"text\":\"").Append(Escape("用户指令：" + user + "。图片尺寸：" + width + "x" + height + "。请给出长椅的可见表面点。")).Append("\"},")
              .Append("{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/jpeg;base64,")
              .Append(Convert.ToBase64String(jpeg)).Append("\"}}]}]")
              .Append(",\"temperature\":").Append(temperature.ToString("F2"))
              .Append(",\"max_tokens\":500,\"stream\":false")
              .Append(",\"thinking\":{\"type\":\"disabled\"},\"reasoning_effort\":\"none\"}");
            return sb.ToString();
        }

        static string ExtractJsonObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int first = text.IndexOf('{');
            int last = text.LastIndexOf('}');
            if (first < 0 || last <= first) return null;
            return text.Substring(first, last - first + 1);
        }

        static string Escape(string s)
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

        static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        static string ShortHash(string hash)
        {
            return string.IsNullOrEmpty(hash) ? "-" : hash.Substring(0, Mathf.Min(12, hash.Length));
        }

#pragma warning disable 0649
        [Serializable] class Stage5ChatResponse
        {
            public Stage5Choice[] choices;
            public Stage5Usage usage;
        }
        [Serializable] class Stage5Choice
        {
            public Stage5Message message;
        }
        [Serializable] class Stage5Message
        {
            public string content;
            public string reasoning_content;
        }
        [Serializable] class Stage5Usage
        {
            public int prompt_tokens;
            public int completion_tokens;
            public int total_tokens;
        }
#pragma warning restore 0649
    }
}
