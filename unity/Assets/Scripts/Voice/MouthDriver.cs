using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace Yixuan.Voice
{
    /// <summary>
    /// 口型驱动（P6 LipSync）：sidecar（voice_server.py）在播放音频时每 20ms 通过 UDP 8770
    /// 发来一帧：seq + rms + 五元音权重 [A,I,U,E,O]（28 字节）。
    ///
    /// 本组件在后台线程收包、LateUpdate 平滑写 YixuanBlendShapes 的 あ/い/う/え/お，
    /// Unity 本地播放的任务 TTS 直接读取 AudioSource 音量，作为基础张口兜底（非逐字口型）。
    /// 无本地播放、UDP 超时后自动闭嘴。与 YixuanExpressions 配合：开启 ExternalMouthControl 后，
    /// 表情系统不再清/写这五个口型键，避免每帧被归零。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(12100)]
    public class MouthDriver : MonoBehaviour
    {
        [Header("目标（留空自动查找）")]
        public Yixuan.Core.YixuanCharacter Character;

        [Header("开关")]
        public bool Enabled = true;
        public int Port = 8770;
        [Tooltip("Unity 内播放的任务语音用实际音量驱动基础张口；UDP 仍用于外部播放口型")]
        public bool UseUnityAudioFallback = true;
        public VoicePlayer UnityVoice;
        [Tooltip("收到新包后平滑到目标权重的速度（秒）")]
        public float SmoothTime = 0.055f;
        [Tooltip("超过这么久没收到包就闭嘴（秒）")]
        public float TimeoutSec = 0.2f;
        [Tooltip("口型 O 的上限（避免与惊讶表情冲突）")]
        [Range(0f, 1f)] public float MaxO = 0.85f;
        [Tooltip("口型整体输出增益（视觉更明显）")]
        [Range(0.5f, 2.5f)] public float OutputGain = 1.6f;

        [Header("调试（只读）")]
        public float LastRms;
        public float[] LastWeights = new float[5];
        public int Packets;
        public string LastError = "";
        public string ActiveInput = "silent";
        readonly float[] _audioSamples = new float[512];
        readonly float[] _smoothed = new float[5];

        Yixuan.Face.YixuanBlendShapes _shapes;
        UdpClient _client;
        Thread _thread;
        readonly object _lock = new object();
        readonly float[] _latest = new float[5];
        volatile bool _running;
        long _lastPacketTicks;
        bool _hasPacket;

        void Awake()
        {
            if (Character == null) Character = FindObjectOfType<Yixuan.Core.YixuanCharacter>();
            EnsureShapes();
            if (Character != null && Character.Expressions != null)
                Character.Expressions.ExternalMouthControl = true;
            StartClient();
        }

        void Start()
        {
            EnsureShapes();
        }

        void EnsureShapes()
        {
            if (Character == null) Character = FindObjectOfType<Yixuan.Core.YixuanCharacter>();
            if (Character == null || Character.Expressions == null) return;
            Character.Expressions.EnsureShapes();
            _shapes = Character.Expressions.Shapes;
            if (Character.Expressions != null) Character.Expressions.ExternalMouthControl = true;
        }

        void StartClient()
        {
            if (_running) return;
            try
            {
                _client = new UdpClient(Port);
                _client.Client.ReceiveTimeout = 500;
                _running = true;
                _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "MouthUdp" };
                _thread.Start();
                Debug.Log("[Mouth] UDP 口型接收已启动 port=" + Port);
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Debug.LogWarning("[Mouth] UDP 启动失败（口型不可用）：" + e.Message);
                _client = null;
                _running = false;
            }
        }

        void ReceiveLoop()
        {
            IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            while (_running && _client != null)
            {
                try
                {
                    byte[] data = _client.Receive(ref ep);
                    if (data == null || data.Length < 28) continue;
                    float rms = BitConverter.ToSingle(data, 4);
                    float[] w = new float[5];
                    for (int i = 0; i < 5; i++) w[i] = BitConverter.ToSingle(data, 8 + i * 4);
                    lock (_lock)
                    {
                        LastRms = rms;
                        for (int i = 0; i < 5; i++) _latest[i] = w[i];
                        LastWeights = (float[])w.Clone();
                        _lastPacketTicks = DateTime.UtcNow.Ticks;
                        _hasPacket = true;
                        Packets++;
                    }
                }
                catch (SocketException)
                {
                    if (!_running) break;
                }
                catch (Exception)
                {
                    // 忽略单帧错误
                }
            }
        }

        void LateUpdate()
        {
            if (!Enabled) return;
            EnsureShapes();
            if (_shapes == null) return;

            bool fresh;
            float[] target = new float[5];
            lock (_lock)
            {
                double age = (DateTime.UtcNow.Ticks - _lastPacketTicks) / (double)TimeSpan.TicksPerSecond;
                fresh = _hasPacket && age < TimeoutSec;
                if (fresh)
                    for (int i = 0; i < 5; i++) target[i] = _latest[i];
            }

            ActiveInput = fresh ? "udp" : "silent";
            if (UseUnityAudioFallback)
            {
                if (UnityVoice == null) UnityVoice = FindObjectOfType<VoicePlayer>();
                var source = UnityVoice != null ? UnityVoice.Source : null;
                if (source != null && source.isPlaying)
                {
                    source.GetOutputData(_audioSamples, 0);
                    double sum = 0; foreach (var sample in _audioSamples) sum += sample * sample;
                    LastRms = (float)Math.Sqrt(sum / _audioSamples.Length);
                    // Audio amplitude drives one open-mouth shape; it does not infer phonemes.
                    Array.Clear(target, 0, target.Length);
                    target[0] = Mathf.Clamp01((LastRms - 0.004f) / 0.09f);
                    fresh = true; ActiveInput = "unity_audio";
                    Array.Copy(target, LastWeights, 5);
                }
            }
            if (!fresh) { LastRms = 0; Array.Clear(LastWeights, 0, LastWeights.Length); }
            float k = SmoothTime <= 0.001f ? 1f : 1f - Mathf.Exp(-Time.unscaledDeltaTime / SmoothTime);
            // ★ 增益只乘目标值，不能乘当前值：否则表情写入 1.0 后会被反馈锁死
            float ta = fresh ? Mathf.Clamp01(target[0] * OutputGain) : 0f;
            float ti = fresh ? Mathf.Clamp01(target[1] * OutputGain) : 0f;
            float tu = fresh ? Mathf.Clamp01(target[2] * OutputGain) : 0f;
            float te = fresh ? Mathf.Clamp01(target[3] * OutputGain) : 0f;
            float to = fresh ? Mathf.Min(target[4] * OutputGain, MaxO) : 0f;

            float a = _smoothed[0] = Mathf.Lerp(_smoothed[0], ta, k);
            float i2 = _smoothed[1] = Mathf.Lerp(_smoothed[1], ti, k);
            float u = _smoothed[2] = Mathf.Lerp(_smoothed[2], tu, k);
            float e = _smoothed[3] = Mathf.Lerp(_smoothed[3], te, k);
            float o = _smoothed[4] = Mathf.Lerp(_smoothed[4], to, k);
            o = Mathf.Min(o, MaxO);

            _shapes.Set(_shapes.A, a);
            _shapes.Set(_shapes.I, i2);
            _shapes.Set(_shapes.U, u);
            _shapes.Set(_shapes.E, e);
            _shapes.Set(_shapes.O, o);
        }

        public void SetEnabled(bool on)
        {
            Enabled = on;
            if (!on) { MouthSilence(); ClearOutput(); }
        }

        public void MouthSilence()
        {
            lock (_lock)
            {
                for (int i = 0; i < 5; i++) _latest[i] = 0f;
                _hasPacket = false;
            }
        }

        void ClearOutput()
        {
            Array.Clear(_smoothed, 0, _smoothed.Length);
            if (_shapes == null) return;
            _shapes.Set(_shapes.A, 0); _shapes.Set(_shapes.I, 0); _shapes.Set(_shapes.U, 0); _shapes.Set(_shapes.E, 0); _shapes.Set(_shapes.O, 0);
        }
        void OnDisable() { ClearOutput(); }

        void OnDestroy()
        {
            _running = false;
            try { if (_client != null) _client.Close(); } catch (Exception) { }
            try { if (_thread != null && _thread.IsAlive) _thread.Join(200); } catch (Exception) { }
        }
    }
}
