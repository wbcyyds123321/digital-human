using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using Yixuan.AI;

namespace Yixuan.Voice
{
    /// <summary>
    /// P5 全双工语音输入客户端：
    ///   - 长轮询 voice_server /events（100ms 级延迟）
    ///   - 收到 asr.final → ChatSession.SubmitVoice(text)
    ///   - 收到 barge_in → ChatSession.CancelCurrentTurn()
    ///   - ChatSession 说话时调 Say() → voice_server /say（sidecar 播放 + AEC 参考）
    ///
    /// ★ 播放搬到 sidecar 后，Unity 不再持有音频；字幕/表情仍由 Unity 驱动。
    /// ★ 轮询失败（sidecar 未启动）只警告一次并每 2s 重试，不影响文字聊天。
    /// </summary>
    [DisallowMultipleComponent]
    public class VoiceInputController : MonoBehaviour
    {
        [Header("voice_server（Tools/Voice/start_voice.bat）")]
        public string Endpoint = "http://127.0.0.1:8767";
        public bool Enabled = true;
        public bool VerboseLog = true;

        [Header("状态（只读）")]
        public bool VerboseStateLog = true;
        public bool IsTtsActive { get; private set; }
        public bool IsListening { get; private set; }
        public bool IsMicrophoneReady { get; private set; }
        public string LastAsrText { get; private set; } = "";

        // ---- 端到端延迟打点（同一台机器，用 sidecar 事件里的 unix 时间算，不受 Unity 轮询延迟影响）----
        public double LastVadEndUnix { get; private set; } = -1;
        public double LastAudioStartUnix { get; private set; } = -1;
        /// <summary>最近一次"说话结束 → 真正开始出声"的秒数；-1 表示还没测到。</summary>
        public float LastVoiceLatencySeconds { get; private set; } = -1f;
        /// <summary>Unity 侧收到 tts.start 的本地时间，用于和 turnStart 对齐。</summary>
        public float LastPlaybackStartedAt { get; private set; } = -1f;

        public ChatSession Session;

        Coroutine _poll;
        UnityWebRequest _pollRequest;
        bool _resumePolling;
        int _cursor = -1;
        bool _warnedOffline;
        bool _connected;
        float _lastBargeTime = -10f;

        [Header("行为")]
        public bool BargeEnabled = true;

        [Serializable] class SayReq
        {
            public string text;
            public string emotion;
            public string demeanor;
            public float intensity;
            public int sentence_id;
            public bool is_last;
            public int turn_id;
            public bool barge_enabled;
        }

        [Serializable] class EventsWrapper
        {
            public EventItem[] events;
            public int cursor;
            public bool mic_ready;
        }

        [Serializable] class EventItem
        {
            public int seq;
            public string type;
            public string text;
            public string reason;
            public float rms;
            public bool playing;
            public bool barge;
            public int token;
            public double t;        // sidecar 侧事件时间（unix 秒）
            public int ms;
        }

        public void SetEnabled(bool on)        {
            Enabled = on;
            if (on)
            {
                if (Session == null) Session = FindObjectOfType<ChatSession>();
                if (_poll == null) { IsMicrophoneReady=false; _connected=false; _poll = StartCoroutine(PrimeThenPoll()); }
            }
            else
            {
                _resumePolling=false;
                StopPolling();
                IsMicrophoneReady=false; IsListening=false;
            }
            if (VerboseLog) Debug.Log("[VoiceIn] " + (on ? "启用（轮询 " + Endpoint + "）" : "禁用"));
        }

        void StopPolling()
        {
            var req = _pollRequest; _pollRequest = null;
            if (req != null) req.Abort();
            if (_poll != null) { StopCoroutine(_poll); _poll = null; }
            if (req != null) req.Dispose();
            IsMicrophoneReady=false; IsListening=false;
        }

        void OnDisable() { _resumePolling=Enabled&&_poll!=null; StopPolling(); }
        void OnEnable() { if (_resumePolling&&Enabled) { _resumePolling=false; SetEnabled(true); } }

        IEnumerator SendPollRequest(UnityWebRequest req)
        {
            _pollRequest=req;
            try
            {
                var operation=req.SendWebRequest();
                // No native coroutine continuation survives a stopped polling loop.
                while (Enabled&&!operation.isDone) yield return null;
            }
            finally { if (_pollRequest==req) _pollRequest=null; }
        }

        /// <summary>首次进入时先把 cursor 跳到"现在"，丢弃 sidecar 里历史事件，避免重放旧 ASR。</summary>
        IEnumerator PrimeThenPoll()
        {
            string url = (Endpoint ?? "").TrimEnd('/') + "/events?cursor=-1&timeout=0.1";
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 3;
                yield return SendPollRequest(req);
                if (!Enabled) yield break;
                if (req.result == UnityWebRequest.Result.Success && req.downloadHandler != null)
                {
                    try
                    {
                        EventsWrapper w = JsonUtility.FromJson<EventsWrapper>(req.downloadHandler.text);
                        if (w != null) _cursor = w.cursor;
                    }
                    catch (Exception) { }
                }
            }
            yield return PollLoop();
        }

        // ================= 轮询事件 =================
        IEnumerator PollLoop()
        {
            while (Enabled)
            {
                string url = (Endpoint ?? "").TrimEnd('/') + "/events?cursor=" + _cursor + "&timeout=0.8";
                using (UnityWebRequest req = UnityWebRequest.Get(url))
                {
                    req.timeout = 5;
                    yield return SendPollRequest(req);
                if (!Enabled) yield break;

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        if (!_warnedOffline)
                        {
                            _warnedOffline = true;
                            _connected = false;
                            Debug.LogWarning("[VoiceIn] voice_server 不可用（" + req.error +
                                             "）——文字聊天照常；启动 Tools/Voice/start_voice.bat 后自动恢复");
                            if (Session != null && Session.UI != null) Session.UI.SetStatus("在线（语音未连接）");
                        }
                        yield return new WaitForSecondsRealtime(2f);
                        continue;
                    }

                    _warnedOffline = false;
                    if (!_connected)
                    {
                        _connected = true;
                        Debug.Log("[VoiceIn] ✓ 已连接 voice_server（等待麦克风就绪提示后再开口）");
                        if (Session != null && Session.UI != null) Session.UI.SetStatus("在线 · 收音准备中…");
                    }
                    string json = req.downloadHandler != null ? req.downloadHandler.text : null;
                    if (string.IsNullOrEmpty(json)) { yield return null; continue; }

                    EventsWrapper wrapper = null;
                    try { wrapper = JsonUtility.FromJson<EventsWrapper>(json); }
                    catch (Exception e) { Debug.LogWarning("[VoiceIn] 事件解析失败: " + e.Message); }

                    if (wrapper != null)
                    {
                        bool wasReady=IsMicrophoneReady; IsMicrophoneReady=wrapper.mic_ready;
                        if(IsMicrophoneReady&&!wasReady) { Debug.Log("[VoiceIn] 🎤 麦克风已就绪，可以直接说话（含开头音频预缓冲）"); if(Session!=null&&Session.UI!=null) Session.UI.SetStatus("在线 · 麦克风就绪"); }
                        if (wrapper.events != null)
                        {
                            for (int i = 0; i < wrapper.events.Length; i++)
                                HandleEvent(wrapper.events[i]);
                        }
                        _cursor = wrapper.cursor;
                    }
                }
                yield return null;
            }
        }

        void HandleEvent(EventItem e)
        {
            if (e == null || string.IsNullOrEmpty(e.type)) return;
            switch (e.type)
            {
                case "vad.start":
                    IsListening = true;
                    if (VerboseLog) Debug.Log("[VoiceIn] 🎤 听到你说话（rms=" + e.rms.ToString("F3") + "）");
                    if (Session != null && Session.UI != null) Session.UI.SetStatus("听你说…");
                    break;

                case "vad.end":
                    IsListening = false;
                    if (e.t > 0) LastVadEndUnix = e.t;
                    break;

                case "tts.start":
                    IsTtsActive = true;
                    LastPlaybackStartedAt = Time.realtimeSinceStartup;
                    if (e.t > 0) LastAudioStartUnix = e.t;
                    if (LastVadEndUnix > 0 && LastAudioStartUnix > LastVadEndUnix)
                    {
                        LastVoiceLatencySeconds = (float)(LastAudioStartUnix - LastVadEndUnix);
                        Debug.Log("[Latency] 语音端到端（说话结束 → 开始出声）≈ " +
                                  LastVoiceLatencySeconds.ToString("F2") + "s");
                    }
                    if (VerboseLog) Debug.Log("[VoiceIn] 🔊 开始播放");
                    break;

                case "tts.end":
                case "tts.cancelled":
                    IsTtsActive = false;
                    if (VerboseLog) Debug.Log("[VoiceIn] ⏹ 播放结束（" + e.type + "）");
                    break;

                case "barge_in":
                    IsTtsActive = false;
                    if (Time.realtimeSinceStartup - _lastBargeTime < 1.5f)
                    {
                        if (VerboseLog) Debug.Log("[VoiceIn] 忽略重复 barge_in");
                        break;
                    }
                    _lastBargeTime = Time.realtimeSinceStartup;
                    if (VerboseLog) Debug.Log("[VoiceIn] ✋ 用户打断！停止当前回答");
                    if (Session != null) Session.CancelDialogueTurn();
                    break;

                case "asr.final":
                    LastAsrText = e.text;
                    IsListening = false;
                    if (VerboseLog) Debug.Log("[VoiceIn] 📝 ASR: " + e.text);
                    if (Session != null) Session.SubmitVoice(e.text);
                    break;

                case "error":
                    Debug.LogWarning("[VoiceIn] sidecar 错误: " + e.text);
                    break;
            }
        }

        // ================= 调 sidecar 播放 =================
        /// <summary>让 sidecar 合成并播放一句；返回 success + 错误信息。</summary>
        public IEnumerator Say(string text, string emotion, string demeanor, float intensity,
                               Action<bool, string> onDone)
        {
            string url = (Endpoint ?? "").TrimEnd('/') + "/say";
            SayReq body = new SayReq
            {
                text = text ?? "",
                emotion = emotion ?? "",
                demeanor = demeanor ?? "",
                intensity = Mathf.Clamp01(intensity),
                barge_enabled = BargeEnabled,
            };
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(body));

            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 60;   // 合成 + 开始播放
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    string msg = req.responseCode + " " + (req.error ?? "unknown");
                    if (onDone != null) onDone(false, msg);
                    yield break;
                }
                // 乐观标记：tts.start 事件可能比 HTTP 响应晚到
                IsTtsActive = true;
                if (VerboseLog) Debug.Log("[VoiceIn] /say ok: " + text);
                if (onDone != null) onDone(true, req.downloadHandler != null ? req.downloadHandler.text : "");
            }
        }

        /// <summary>P6 流式：把一句追加到 sidecar 播放队列（不打断当前句）。</summary>
        public IEnumerator SayQueue(string text, string emotion, string demeanor, float intensity,
                                    int sentenceId, bool isLast, int turnId, Action<bool, string> onDone)
        {
            string url = (Endpoint ?? "").TrimEnd('/') + "/say_queue";
            SayReq body = new SayReq
            {
                text = text ?? "",
                emotion = emotion ?? "",
                demeanor = demeanor ?? "",
                intensity = Mathf.Clamp01(intensity),
                sentence_id = sentenceId,
                is_last = isLast,
                turn_id = turnId,
                barge_enabled = BargeEnabled,
            };
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(body));
            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 60;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    string msg = req.responseCode + " " + (req.error ?? "unknown");
                    if (VerboseLog) Debug.LogWarning("[VoiceIn] /say_queue 失败：" + msg);
                    if (onDone != null) onDone(false, msg);
                    yield break;
                }
                IsTtsActive = true;
                if (onDone != null) onDone(true, "");
            }
        }

        /// <summary>P6 流式：标记本轮句子都入队了（队列播完发 tts.end）。</summary>
        public IEnumerator SayFinish()
        {
            string url = (Endpoint ?? "").TrimEnd('/') + "/say_finish";
            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 10;
                yield return req.SendWebRequest();
            }
        }

        public IEnumerator CancelSay()        {
            string url = (Endpoint ?? "").TrimEnd('/') + "/cancel";
            // 注意：不要用 UnityWebRequest.Post(string,string)（Unity 2022.3 已标记过时）
            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 5;
                yield return req.SendWebRequest();
            }
        }

        public IEnumerator Calibrate()
        {
            string url = (Endpoint ?? "").TrimEnd('/') + "/calibrate";
            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 30;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                    Debug.Log("[VoiceIn] 校准: " + (req.downloadHandler != null ? req.downloadHandler.text : ""));
                else
                    Debug.LogWarning("[VoiceIn] 校准失败: " + req.error);
            }
        }
    }
}
