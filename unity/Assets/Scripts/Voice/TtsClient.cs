using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Yixuan.Voice
{
    /// <summary>
    /// 本地 TTS sidecar（Tools/Voice/tts_server.py）的 Unity 客户端。
    ///
    /// 流程：POST http://127.0.0.1:8766/tts  {text, voice_id, engine, emotion, demeanor, intensity}
    ///      → 返回 audio/mpeg（MiniMax 克隆音色） → DownloadHandlerAudioClip 解码成 AudioClip。
    ///
    /// ★ 必须由协程驱动（UnityWebRequest 只能在主线程）；失败返回 null + 原因，绝不抛异常。
    /// ★ 情绪映射在 sidecar 里做（15 表情/10 神态 → 7 类语音情绪），Unity 只传原始字段。
    /// </summary>
    [DisallowMultipleComponent]
    public class TtsClient : MonoBehaviour
    {
        [Header("sidecar")]
        public string Endpoint = "http://127.0.0.1:8766";
        public string VoiceId = "zhuangfy001";
        public string Engine = "minimax";
        [Tooltip("TTS 请求超时（秒）")]
        public int TimeoutSec = 20;
        public bool Enabled = true;

        [Serializable]
        class RequestBody
        {
            public string text;
            public string voice_id;
            public string engine;
            public string emotion;
            public string demeanor;
            public float intensity;
        }

        /// <summary>
        /// 合成一句台词。成功回调 onOk(clip)；失败回调 onFail(原因)。
        /// 返回的 AudioClip 由调用方负责 Destroy。
        /// </summary>
        public IEnumerator Synthesize(string text, string emotion, string demeanor, float intensity,
                                      Action<AudioClip> onOk, Action<string> onFail)
        {
            if (!Enabled)
            {
                if (onFail != null) onFail("TTS 已禁用");
                yield break;
            }
            if (string.IsNullOrEmpty(text))
            {
                if (onFail != null) onFail("文本为空");
                yield break;
            }

            string url = (Endpoint ?? "").TrimEnd('/') + "/tts";
            RequestBody body = new RequestBody
            {
                text = text,
                voice_id = string.IsNullOrEmpty(VoiceId) ? "zhuangfy001" : VoiceId,
                engine = string.IsNullOrEmpty(Engine) ? "minimax" : Engine,
                emotion = emotion ?? "",
                demeanor = demeanor ?? "",
                intensity = Mathf.Clamp01(intensity),
            };
            byte[] payload = Encoding.UTF8.GetBytes(JsonUtility.ToJson(body));

            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerAudioClip(url, AudioType.MPEG);
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = Mathf.Max(3, TimeoutSec);

                float t0 = Time.realtimeSinceStartup;
                yield return req.SendWebRequest();
                float cost = Time.realtimeSinceStartup - t0;

                if (req.result != UnityWebRequest.Result.Success)
                {
                    string msg = req.responseCode + " " + (req.error ?? "unknown");
                    Debug.LogWarning("[TTS] ✗ 请求失败（" + cost.ToString("F2") + "s）：" + msg +
                                     " —— 确认 Tools/Voice/start_tts.bat 已启动");
                    if (onFail != null) onFail(msg);
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null || clip.length <= 0.01f)
                {
                    string msg = "音频解码失败（clip 为空）";
                    Debug.LogWarning("[TTS] ✗ " + msg);
                    if (onFail != null) onFail(msg);
                    yield break;
                }

                string durHeader = req.GetResponseHeader("X-Duration-Ms");
                Debug.Log("[TTS] ✓ " + text.Length + " 字 → " + clip.length.ToString("F2") + "s" +
                          "（" + clip.frequency + "Hz, 请求 " + cost.ToString("F2") + "s" +
                          (string.IsNullOrEmpty(durHeader) ? "" : ", 头时长 " + durHeader + "ms") + "）");
                if (onOk != null) onOk(clip);
            }
        }

        /// <summary>检查 sidecar 是否在线（给 UI/调试用）。</summary>
        public IEnumerator Health(Action<bool, string> onDone)
        {
            string url = (Endpoint ?? "").TrimEnd('/') + "/health";
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 3;
                yield return req.SendWebRequest();
                bool ok = req.result == UnityWebRequest.Result.Success;
                string body = ok ? req.downloadHandler.text : (req.error ?? "unknown");
                if (onDone != null) onDone(ok, body);
            }
        }
    }
}
