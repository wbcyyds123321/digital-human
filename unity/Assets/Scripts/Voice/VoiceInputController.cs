using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using Yixuan.AI;

namespace Yixuan.Voice
{
    /// <summary>Microphone input and turn-owned sidecar streaming playback/AEC.</summary>
    [DisallowMultipleComponent]
    public class VoiceInputController : MonoBehaviour
    {
        public string Endpoint = "http://127.0.0.1:8767";
        public bool Enabled = true, VerboseLog = true, VerboseStateLog = true, BargeEnabled = true;
        public ChatSession Session;
        public bool IsTtsActive { get { return IsTurnActive(ActiveTurnId); } }
        public bool IsListening { get; private set; }
        public bool IsMicrophoneReady { get; private set; }
        public string LastAsrText { get; private set; } = "";
        public double LastVadEndUnix { get; private set; } = -1;
        public double LastAudioStartUnix { get; private set; } = -1;
        public float LastVoiceLatencySeconds { get; private set; } = -1;
        public float LastPlaybackStartedAt { get; private set; } = -1;
        public long ActiveTurnId { get; private set; }
        public string ServiceId { get { return _serviceId; } }
        static long _lastTurnId;
        public static long NextTurnId()
        {
            long oldId, next;
            do { oldId = Interlocked.Read(ref _lastTurnId); next = Math.Max(oldId + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
            while (Interlocked.CompareExchange(ref _lastTurnId, next, oldId) != oldId);
            return next;
        }
        sealed class Playback
        {
            public int Token;
            public string Service, Error = "";
            public bool Active = true, Started, Terminal;
            public float StartedAt = -1;
        }
        readonly Dictionary<long, Playback> _turns = new Dictionary<long, Playback>();
        Coroutine _poll;
        UnityWebRequest _pollRequest;
        int _cursor = -1;
        bool _primed, _warnedOffline;
        string _serviceId;
        double _micEnabledAt;
        float _lastBargeTime = -10;
        bool WantsEvents { get { return Enabled || IsTtsActive; } }
        public bool IsTurnActive(long id) { Playback p; return id > 0 && _turns.TryGetValue(id, out p) && p.Active && !p.Terminal; }
        public bool HasTurnStarted(long id) { Playback p; return _turns.TryGetValue(id, out p) && p.Started; }
        public float TurnStartedAt(long id) { Playback p; return _turns.TryGetValue(id, out p) ? p.StartedAt : -1; }
        public string TurnError(long id) { Playback p; return _turns.TryGetValue(id, out p) ? p.Error : "unknown_turn"; }

        [Serializable] class SayReq
        {
            public string text, emotion, demeanor;
            public float intensity;
            public int sentence_id;
            public bool is_last, barge_enabled;
            public long turn_id;
        }
        [Serializable] class TurnReq { public long turn_id; public int last_sentence_id = -1; }
        [Serializable] class Receipt
        {
            public bool ok, accepted, ignored;
            public string reason, error, service_id;
            public long turn_id;
            public int token, playback_protocol;
        }
        [Serializable] class EventsWrapper { public EventItem[] events; public int cursor; public bool mic_ready; public string service_id; }
        [Serializable] class EventItem
        {
            public int seq, token, ms;
            public string type, text, msg, reason, source, utterance_id, service_id;
            public float rms;
            public bool playing, barge, failed;
            public long turn_id;
            public double t, vad_started_at, vad_ended_at;
        }
        public void SetEnabled(bool on)
        {
            if (on && !Enabled) _micEnabledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            Enabled = on;
            if (Session == null) Session = FindObjectOfType<ChatSession>();
            if (on) EnsurePolling();
            else { IsListening = false; IsMicrophoneReady = false; }
            if (VerboseLog) Debug.Log("[VoiceIn] microphone=" + on + "; output events observed independently");
        }
        void OnEnable() { if (Enabled) { _micEnabledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0; EnsurePolling(); } }
        void OnDisable()
        {
            foreach (var pair in _turns) if (!pair.Value.Terminal) CancelOnShutdown(pair.Key);
            var req = _pollRequest; _pollRequest = null;
            if (req != null) req.Abort();
            if (_poll != null) StopCoroutine(_poll);
            _poll = null;
            if (req != null) req.Dispose();
            _primed = false; IsListening = false; IsMicrophoneReady = false;
            foreach (var p in _turns.Values) if (!p.Terminal) End(p, "controller_disabled");
        }
        void EnsurePolling() { if (isActiveAndEnabled && _poll == null) _poll = StartCoroutine(Poll()); }
        IEnumerator Poll()
        {
            yield return null; // StartCoroutine must assign _poll before an early exit.
            try
            {
                while (WantsEvents)
                {
                    bool prime = !_primed;
                    string url = (Endpoint ?? "").TrimEnd('/') + "/events?cursor=" + (prime ? -1 : _cursor) + "&timeout=0.8&mic_enabled=" + (Enabled ? "true" : "false");
                    using (var req = UnityWebRequest.Get(url))
                    {
                        req.timeout = 5; _pollRequest = req;
                        yield return req.SendWebRequest();
                        if (_pollRequest == req) _pollRequest = null;
                        if (req.result != UnityWebRequest.Result.Success)
                        {
                            if (!_warnedOffline) Debug.LogWarning("[VoiceIn] voice_server offline: " + req.error);
                            _warnedOffline = true;
                            yield return new WaitForSecondsRealtime(2);
                            continue;
                        }
                        EventsWrapper w = null;
                        try { w = JsonUtility.FromJson<EventsWrapper>(req.downloadHandler.text); }
                        catch (Exception e) { Debug.LogWarning("[VoiceIn] event parse: " + e.Message); }
                        if (w == null) continue;
                        bool restarted = !string.IsNullOrEmpty(_serviceId) && !string.IsNullOrEmpty(w.service_id) && w.service_id != _serviceId;
                        if (restarted)
                            foreach (var p in _turns.Values)
                                if (!p.Terminal && !string.IsNullOrEmpty(p.Service) && p.Service != w.service_id) End(p, "sidecar_restarted");
                        _serviceId = w.service_id; _warnedOffline = false;
                        bool wasReady = IsMicrophoneReady;
                        IsMicrophoneReady = Enabled && w.mic_ready;
                        if (IsMicrophoneReady && !wasReady && VerboseLog) Debug.Log("[VoiceIn] microphone ready");
                        if (w.events != null) foreach (var e in w.events)
                        {
                            // The sidecar cursor is the NEXT sequence, not the last seen.
                            if (e == null || (!prime && !restarted && e.seq < _cursor)) continue;
                            bool input = e.type == "asr.final" || e.type == "barge_in" || e.type == "vad.start" || e.type == "vad.end";
                            // History is not a new input. A matching output event can precede its HTTP receipt.
                            if (input && (prime || restarted)) continue;
                            HandleEvent(e);
                        }
                        _cursor = w.cursor; _primed = true;
                    }
                    yield return null;
                }
            }
            finally { _pollRequest = null; _poll = null; }
        }
        static void End(Playback p, string error) { p.Terminal = true; p.Active = false; if (!string.IsNullOrEmpty(error)) p.Error = error; }
        bool Owns(EventItem e, out Playback p)
        {
            if (!_turns.TryGetValue(e.turn_id, out p) || e.turn_id != ActiveTurnId || p.Terminal) return false;
            if (!string.IsNullOrEmpty(p.Service) && p.Service != e.service_id) return false;
            if (p.Token != 0 && p.Token != e.token) return false;
            p.Service = e.service_id; p.Token = e.token; return true;
        }
        void HandleEvent(EventItem e)
        {
            if (e == null || string.IsNullOrEmpty(e.type)) return;
            bool input = e.type == "asr.final" || e.type == "barge_in" || e.type == "vad.start" || e.type == "vad.end";
            if (input && !Enabled) return;
            if (input && e.vad_started_at > 0 && e.vad_started_at < _micEnabledAt) return;
            if (e.type == "barge_in" && Session != null && e.vad_started_at > 0 && e.vad_started_at <= Session.ExplicitUiInputFenceUnix) return;
            Playback p;
            switch (e.type)
            {
                case "vad.start": IsListening = true; break;
                case "vad.end": IsListening = false; LastVadEndUnix = e.t; break;
                case "tts.start":
                    Owns(e, out p); // Queue ownership is not evidence of audible PCM.
                    break;
                case "tts.audio_start":
                    if (!Owns(e, out p)) return;
                    p.Started = true; p.StartedAt = Time.realtimeSinceStartup; LastPlaybackStartedAt = p.StartedAt; LastAudioStartUnix = e.t;
                    if (LastVadEndUnix > 0 && e.t > LastVadEndUnix) LastVoiceLatencySeconds = (float)(e.t - LastVadEndUnix);
                    break;
                case "tts.end": case "tts.cancelled":
                    if (Owns(e, out p)) End(p, e.failed ? "tts_stream_failed" : e.type == "tts.cancelled" ? "cancelled" : "");
                    break;
                case "barge_in":
                    if (!Owns(e, out p) || !BargeEnabled || Time.realtimeSinceStartup - _lastBargeTime < 1.5f) return;
                    End(p, "barge_in"); _lastBargeTime = Time.realtimeSinceStartup;
                    if (Session != null) Session.CancelDialogueTurn();
                    break;
                case "asr.final":
                    IsListening = false;
                    string utterance = string.IsNullOrEmpty(e.utterance_id) ? "" : (e.service_id ?? _serviceId ?? "unknown") + ":" + e.utterance_id;
                    if (Session != null && Session.SubmitVoiceEvent(e.text, e.source, utterance, e.vad_started_at, e.vad_ended_at)) LastAsrText = e.text;
                    break;
                case "error":
                    if (e.turn_id > 0 && Owns(e, out p)) End(p, e.text ?? e.msg ?? e.reason ?? "sidecar_error");
                    Debug.LogWarning("[VoiceIn] sidecar: " + (e.text ?? e.msg ?? e.reason)); break;
            }
        }
        Playback Begin(long id)
        {
            Playback p;
            if (_turns.TryGetValue(id, out p)) return p;
            p = new Playback();
            if (id < ActiveTurnId) End(p, "stale_turn");
            else
            {
                Playback old;
                if (_turns.TryGetValue(ActiveTurnId, out old) && !old.Terminal) End(old, "superseded");
                ActiveTurnId = id;
            }
            if (_turns.Count > 128)
            {
                var retired = new List<long>();
                foreach (var kv in _turns) if (kv.Value.Terminal && kv.Key < id - 60000) retired.Add(kv.Key);
                foreach (long key in retired) _turns.Remove(key);
            }
            _turns[id] = p; EnsurePolling(); return p;
        }
        UnityWebRequest Post(string path, object body, int timeout)
        {
            var req = new UnityWebRequest((Endpoint ?? "").TrimEnd('/') + path, "POST");
            req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
            req.downloadHandler = new DownloadHandlerBuffer(); req.SetRequestHeader("Content-Type", "application/json"); req.timeout = timeout;
            return req;
        }
        void CancelOnShutdown(long id)
        {
            // Coroutines cannot run on a disabled/destroying component. Send only
            // this turn's cancellation; a late shutdown request cannot stop a new turn.
            string url = (Endpoint ?? "").TrimEnd('/') + "/cancel";
            byte[] data = System.Text.Encoding.UTF8.GetBytes("{\"turn_id\":" + id + "}");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                    request.Method = "POST"; request.ContentType = "application/json";
                    request.Timeout = 2000; request.ReadWriteTimeout = 2000; request.ContentLength = data.Length;
                    using (var stream = request.GetRequestStream()) stream.Write(data, 0, data.Length);
                    using (request.GetResponse()) { }
                }
                catch (Exception) { /* Best effort during domain/editor shutdown. */ }
            });
        }
        public IEnumerator Say(string text, string emotion, string demeanor, float intensity, Action<bool, string> onDone)
        {
            long id = NextTurnId();
            yield return SayQueue(text, emotion, demeanor, intensity, 1, true, id, onDone);
            yield return SayFinish(id, 1);
        }
        public IEnumerator SayQueue(string text, string emotion, string demeanor, float intensity, int sentenceId, bool isLast, long turnId, Action<bool, string> onDone)
        {
            var p = Begin(turnId);
            if (p.Terminal) { if (onDone != null) onDone(false, p.Error); yield break; }
            float until = Time.realtimeSinceStartup + 6;
            while (!_primed && !p.Terminal && Time.realtimeSinceStartup < until) yield return null;
            if (p.Terminal) { if (onDone != null) onDone(false, p.Error); yield break; }
            if (!_primed) { End(p, "event_connection_unavailable"); if (onDone != null) onDone(false, p.Error); yield break; }
            if (string.IsNullOrEmpty(p.Service)) p.Service = _serviceId;
            var body = new SayReq { text = text ?? "", emotion = emotion ?? "", demeanor = demeanor ?? "", intensity = Mathf.Clamp01(intensity), sentence_id = sentenceId, is_last = isLast, turn_id = turnId, barge_enabled = BargeEnabled && Enabled };
            using (var req = Post("/say_queue", body, 60))
            {
                yield return req.SendWebRequest();
                Receipt receipt = null;
                try { if (req.downloadHandler != null) receipt = JsonUtility.FromJson<Receipt>(req.downloadHandler.text); } catch (Exception) { }
                bool accepted = req.result == UnityWebRequest.Result.Success && receipt != null && receipt.ok && receipt.accepted && !receipt.ignored && receipt.turn_id == turnId;
                bool sameService = receipt != null && (string.IsNullOrEmpty(p.Service) || p.Service == receipt.service_id);
                bool sameToken = receipt != null && (p.Token == 0 || p.Token == receipt.token);
                if (accepted && sameService && sameToken)
                {
                    // HTTP acknowledgment cannot revive a turn already ended by an event.
                    p.Service = receipt.service_id; p.Token = receipt.token;
                    if (onDone != null) onDone(string.IsNullOrEmpty(p.Error), p.Error);
                }
                else
                {
                    string err = receipt != null ? receipt.reason ?? receipt.error : req.responseCode + " " + req.error;
                    if (string.IsNullOrEmpty(err)) err = "playback_receipt_rejected";
                    End(p, err); if (onDone != null) onDone(false, err);
                }
            }
        }
        public IEnumerator SayFinish(long turnId = 0, int lastSentenceId = -1)
        {
            if (turnId == 0) turnId = ActiveTurnId;
            if (!IsTurnActive(turnId)) yield break;
            using (var req = Post("/say_finish", new TurnReq { turn_id = turnId, last_sentence_id = lastSentenceId }, 10))
            {
                yield return req.SendWebRequest();
                Playback p;
                if (!_turns.TryGetValue(turnId, out p) || p.Terminal) yield break;
                Receipt receipt = null;
                try { receipt = JsonUtility.FromJson<Receipt>(req.downloadHandler.text); } catch (Exception) { }
                if (req.result != UnityWebRequest.Result.Success || receipt == null) End(p, "finish_failed:" + req.error);
                else if (!receipt.accepted && receipt.reason != "closed_turn") End(p, "finish_rejected:" + receipt.reason);
            }
        }
        public IEnumerator CancelSay(long turnId = 0)
        {
            if (turnId == 0) turnId = ActiveTurnId;
            if (turnId <= 0) yield break;
            Playback p; if (_turns.TryGetValue(turnId, out p) && !p.Terminal) End(p, "cancelled");
            using (var req = Post("/cancel", new TurnReq { turn_id = turnId }, 5)) yield return req.SendWebRequest();
        }
        public IEnumerator Calibrate()
        {
            using (var req = Post("/calibrate", new TurnReq(), 30))
            {
                yield return req.SendWebRequest();
                Debug.Log("[VoiceIn] calibration: " + (req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text : req.error));
            }
        }
    }
}
