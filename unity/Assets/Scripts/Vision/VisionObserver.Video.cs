using System;
using System.Collections;
using UnityEngine;

namespace Yixuan.Vision
{
    public partial class VisionObserver
    {
        [Header("连续视频与事件记忆（只在本轮运行内存中保留）")]
        public bool EnableTemporalVideo = true;
        [Range(0.25f, 2f)] public float VideoSampleInterval = 0.5f;
        public float VideoBufferSeconds = 30f;
        public int VideoMaxFrames = 128;
        public int VideoMaxMegabytes = 16;
        public float VideoAmbientSeconds = 6f;
        public float VideoAmbientInterval = 4f;
        public float VideoQuestionSeconds = 20f;

        readonly VisualFrameBuffer _videoFrames = new VisualFrameBuffer();
        readonly VisualMemoryStore _visualMemory = new VisualMemoryStore();
        readonly bool[] _sourceAvailable = new bool[3];
        readonly bool[] _sourceStateKnown = new bool[3];
        float _nextVideoSample;
        float _lastWebcamUpdate = -100f;
        bool _allowDarkVideoCapture;
        int _nextVideoSource = 2;
        float _nextVideoRequest;
        float _lastCurrentAnswerCapturedAt = -1f;
        int _lastCurrentAnswerMask;
        public bool CurrentAnswerStillValid
        {
            get { return _lastCurrentAnswerCapturedAt >= 0f && !HasNewerUnavailableObservation(_lastCurrentAnswerMask, _lastCurrentAnswerCapturedAt); }
        }

        bool HasNewerUnavailableObservation(int sourceMask, float capturedAt)
        {
            foreach (var observation in _timeline)
                if ((observation.SourceMask & sourceMask) != 0 && observation.LastSeenAt > capturedAt && IsOcclusionText(observation.Text)) return true;
            return false;
        }
        readonly float[] _lastAmbientClipEnd = new float[3];
        public int BufferedVideoFrames { get { return _videoFrames.Count; } }
        public long BufferedVideoBytes { get { return _videoFrames.Bytes; } }
        public int VisualEventCount { get { return _visualMemory.EventCount; } }
        public int VisualKeyframeCount { get { return _visualMemory.KeyframeCount; } }
        public int VisualFactCount { get { return _visualMemory.FactCount; } }
        public int LastVideoFrameCount { get; private set; }
        public VisualMemoryStore.Record LatestVisualEvent { get { return _visualMemory.LatestEvent; } }

        void SampleVideoFrames()
        {
            if (WebcamEnabled && WebcamTexture != null && WebcamTexture.didUpdateThisFrame)
                _lastWebcamUpdate = Time.realtimeSinceStartup;
            if (!EnableTemporalVideo || !AnySourceEnabled) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextVideoSample) return;
            _nextVideoSample = now + Mathf.Clamp(VideoSampleInterval, 0.25f, 2f);
            _videoFrames.Prune(now, Mathf.Clamp(VideoBufferSeconds, 3f, 120f), Mathf.Clamp(VideoMaxFrames, 8, 512), Mathf.Clamp(VideoMaxMegabytes, 1, 64) * 1024L * 1024L);
            _visualMemory.Prune(now);
            _allowDarkVideoCapture = true;
            try
            {
                for (int source = 1; source <= 2; source++)
                {
                    if ((_sourceMask & source) == 0) continue;
                    float[] signature = null;
                    byte[] image = null;
                    if (source == 1) image = CaptureEye(GameImageWidth, GameImageHeight, JpegQuality, out signature);
                    else if (now - _lastWebcamUpdate < 1.5f) image = CaptureWebcam(384, 288, JpegQuality, out signature);
                    float mean = 0f;
                    if (signature != null)
                    {
                        foreach (float value in signature) mean += value;
                        mean /= signature.Length;
                    }
                    bool dark = signature != null && mean < MinFrameBrightness;
                    bool available = image != null && !dark;
                    bool wasAvailable = _sourceAvailable[source];
                    bool firstState = !_sourceStateKnown[source];
                    _sourceAvailable[source] = available;
                    _sourceStateKnown[source] = true;
                    if (image != null)
                        _videoFrames.Add(Time.realtimeSinceStartup, source, image, Mathf.Clamp(VideoBufferSeconds, 3f, 120f), Mathf.Clamp(VideoMaxFrames, 8, 512), Mathf.Clamp(VideoMaxMegabytes, 1, 64) * 1024L * 1024L, dark);
                    if (!available)
                    {
                        // P6：只在"首次记录/可用→不可用"时追加一条；持续不可用只刷新该来源的时间，
                        // 否则两路不可用时每 0.5s 交替追加，100 条时间轴约 25s 就被刷满。
                        if (firstState || wasAvailable)
                            AppendOrExtend(dark ? "[本地状态] 画面全黑或过暗，无法确认人或物；不能认定有人仍在，也不能认定已经离开。" : "[本地状态] 没有可用新帧，看不到当前画面，旧记录不能替代。", source, now, null);
                        else
                            RefreshUnavailableObservation(source, now);
                    }
                }
            }
            finally { _allowDarkVideoCapture = false; }
        }

        public void InvalidateCurrentView(int sourceMask, float after)
        {
            AppendObservation("[本地状态] 视角刚改变，尚未确认新方向，看不到当前细节。", sourceMask, after, null);
            RequestNow();
        }

        /// <summary>持续不可用时只刷新该来源本地状态的"最后确认时间"，不新增时间轴条目。</summary>
        void RefreshUnavailableObservation(int source, float now)
        {
            for (int index = _timeline.Count - 1; index >= 0; index--)
            {
                var observation = _timeline[index];
                if ((observation.SourceMask & source) == 0) continue;
                if (!IsOcclusionText(observation.Text)) continue;
                observation.LastSeenAt = now;
                observation.ReceivedAt = Time.realtimeSinceStartup;
                LastObservationTime = Mathf.Max(LastObservationTime, now);
                ApplyToChatSession();
                return;
            }
        }

        void ClearTemporalMemory()
        {
            _videoFrames.Clear();
            _lastCurrentAnswerCapturedAt = -1f;
            _lastCurrentAnswerMask = 0;
            _visualMemory.Clear();
            _sourceAvailable[1] = _sourceAvailable[2] = false;
            _sourceStateKnown[1] = _sourceStateKnown[2] = false;
            _lastAmbientClipEnd[1] = _lastAmbientClipEnd[2] = 0f;
            _nextVideoSample = _nextVideoRequest = 0f;
            LastVideoFrameCount = 0;
        }

        public void ClearVisualMemory()
        {
            CancelAsking("清空视觉记忆");
            _generation++;
            ClearChatContext();
            RequestNow();
            RaiseChanged();
        }

        IEnumerator VideoAmbientLoop()
        {
            while (EnableTemporalVideo)
            {
                // P7：视频模式也要消费 RequestNow() 的"尽快再观察"请求（例如云台转过之后），
                // 否则 _forceRefresh 只在旧 VisionLoop 生效，视频模式下会一直等到退避到期。
                if (_forceRefresh)
                {
                    _forceRefresh = false;
                    _nextVideoRequest = 0f;
                }
                if (!AnySourceEnabled || Asking || RequestInFlight || Time.realtimeSinceStartup < _nextVideoRequest)
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    continue;
                }
                int source = _nextVideoSource;
                _nextVideoSource = source == 1 ? 2 : 1;
                if ((_sourceMask & source) == 0 || !_sourceAvailable[source])
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    continue;
                }
                float duration = _lastAmbientClipEnd[source] > 0f ? Mathf.Max(VideoAmbientSeconds, Time.realtimeSinceStartup - _lastAmbientClipEnd[source] + VideoSampleInterval) : VideoAmbientSeconds;
                var clip = _videoFrames.Snapshot(source, Time.realtimeSinceStartup, Mathf.Min(VideoBufferSeconds, Mathf.Max(2f, duration)), 24, Mathf.Max(1.5f, VideoSampleInterval * 2.5f));
                if (!clip.Usable || !CanSendAmbientRequest())
                {
                    yield return new WaitForSecondsRealtime(0.25f);
                    continue;
                }
                int generation = _generation;
                string response = null, error = null;
                RequestInFlight = true;
                ApiCallCount++;
                RecordAmbientRequest();
                LastVideoFrameCount = clip.Frames.Count;
                try
                {
                    yield return Client.DescribeVideo(clip, value => response = value, value => error = value);
                }
                finally { RequestInFlight = false; }
                _nextVideoRequest = Time.realtimeSinceStartup + Mathf.Max(2f, VideoAmbientInterval);
                if (generation != _generation) continue;
                var evidence = VisualMemoryStore.Parse(response);
                if (evidence == null)
                {
                    LastError = string.IsNullOrEmpty(error) ? "视频观察未返回有效结构，不写入记忆" : error;
                    _nextVideoRequest += 4f;
                    RaiseChanged();
                    continue;
                }
                _lastAmbientClipEnd[source] = clip.End;
                _visualMemory.Add(evidence, clip);
                if (!string.IsNullOrWhiteSpace(_visualMemory.LastIssue))
                    Debug.LogWarning("[VisionVideo] " + _visualMemory.LastIssue);
                if (!string.IsNullOrWhiteSpace(evidence.current))
                    AppendOrExtend(evidence.current, source, clip.End, null);
                LastError = null;
                Debug.Log("[VisionVideo] source=" + source + " frames=" + clip.Frames.Count + " span=" + clip.Duration.ToString("F1") + "s events=" + VisualEventCount + " facts=" + VisualFactCount);
                RaiseChanged();
            }
        }

        public IEnumerator AskRecentVideo(string question, int requiredMask, float questionAt, Action<string> onAnswer)
        {
            if (Asking || !EnableTemporalVideo || !AnySourceEnabled)
            {
                if (onAnswer != null) onAnswer(null);
                yield break;
            }
            int source = requiredMask == 1 || requiredMask == 2 ? requiredMask : (WebcamEnabled ? 2 : 1);
            if (!HasSource(source))
            {
                if (onAnswer != null) onAnswer(null);
                yield break;
            }
            float anchor = Mathf.Min(questionAt, Time.realtimeSinceStartup);
            var clip = _videoFrames.Snapshot(source, anchor, Mathf.Clamp(VideoQuestionSeconds, 2f, VideoBufferSeconds), 48, Mathf.Max(1.5f, VideoSampleInterval * 2.5f));
            LastVideoFrameCount = clip.Frames.Count;
            if (!clip.Usable || !clip.HasVisibleFrames)
            {
                LastError = "近期没有至少4帧的可辨视频，不能靠当前一张图补造刚才的动作";
                if (onAnswer != null) onAnswer(null);
                yield break;
            }
            int generation = _generation, operation = ++_askOpId;
            Asking = true;
            Coroutine request = null;
            try
            {
                string answer = null, error = null;
                bool done = false;
                ApiCallCount++;
                string coverage = "本片段覆盖提问前" + (anchor - clip.Start).ToString("F1") + "秒至提问前" + (anchor - clip.End).ToString("F1") + "秒，并非无限历史。";
                request = StartCoroutine(Client.AskVideo(question + "。" + coverage, clip, _visualMemory.BuildContext(anchor, source),
                    value => { answer = value; done = true; }, value => { error = value; done = true; }, _visualMemory.SelectHistoricalFrames(source, clip.Start, question)));
                float deadline = Time.realtimeSinceStartup + Mathf.Max(3f, AskTimeoutSeconds + 8f);
                while (!done && operation == _askOpId && generation == _generation && Time.realtimeSinceStartup < deadline)
                    yield return null;
                if (operation != _askOpId || generation != _generation)
                {
                    if (onAnswer != null) onAnswer(null);
                    yield break;
                }
                if (done && !string.IsNullOrEmpty(answer))
                {
                    LastError = null;
                    LastRealtimeAnswer = answer;
                    if (onAnswer != null) onAnswer(coverage + "\n" + VisualMemoryStore.Clean(answer, 500));
                }
                else
                {
                    LastError = string.IsNullOrEmpty(error) ? "视频问答超时或已取消" : error;
                    if (onAnswer != null) onAnswer(null);
                }
            }
            finally
            {
                if (request != null) StopCoroutine(request);
                if (operation == _askOpId)
                {
                    if (Client != null) Client.AbortAskRequest();
                    Asking = false;
                }
            }
        }
    }
}
