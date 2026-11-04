using System;
using System.Collections;
using UnityEngine;

namespace Yixuan.Voice
{
    /// <summary>
    /// 语音播放器：一个常驻 2D AudioSource，播放 TTS 返回的 AudioClip，并「等它真的播完」。
    ///
    /// ★ 播放必须留在 Unity：uLipSync 要读 Unity 正在播放的 AudioSource 做口型（P4-C）；
    ///   sidecar 只负责合成，不碰播放。
    /// ★ 等待用逐帧 yield（协程），不是 while 空转。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AudioSource))]
    public class VoicePlayer : MonoBehaviour
    {
        [Header("播放（留空自动取本物体 AudioSource）")]
        public AudioSource Source;

        [Header("调试")]
        public bool VerboseLog = true;

        void Awake()
        {
            if (Source == null) Source = GetComponent<AudioSource>();
            if (Source != null)
            {
                Source.playOnAwake = false;
                Source.loop = false;
                Source.spatialBlend = 0f;   // 2D：像面对面说话，不随相机衰减
                Source.volume = 1f;
            }
        }

        public bool IsSpeaking { get { return Source != null && Source.isPlaying; } }

        public void Play(AudioClip clip, float volume)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(4))return;
            if (Source == null || clip == null) return;
            Source.Stop();
            Source.clip = clip;
            Source.volume = Mathf.Clamp01(volume);
            Source.Play();
            if (VerboseLog) Debug.Log("[Voice] ▶ 播放 " + clip.length.ToString("F2") + "s");
        }

        /// <summary>播放并等待结束（真实时长）。播完自动清理 clip 引用。</summary>
        public IEnumerator Speak(AudioClip clip, float volume, Action onDone)
        {
            Play(clip, volume);
            if (Source == null || clip == null)
            {
                if (onDone != null) onDone();
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + clip.length + 1.5f;
            while (Source != null && Source.isPlaying && Time.realtimeSinceStartup < deadline)
                yield return null;                      // 每帧让出主线程（不是空转）

            // 留一点尾音/口型收尾时间，避免"声音结束、表情瞬间跳回"
            if (Source != null && Source.isPlaying) yield return new WaitForSeconds(0.08f);

            if (Source != null)
            {
                Source.Stop();
                Source.clip = null;
            }
            if (VerboseLog) Debug.Log("[Voice] ■ 播放结束");
            if (onDone != null) onDone();
        }

        public void Stop()
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(4))return;
            if (Source == null) return;
            Source.Stop();
            Source.clip = null;
        }
    }
}
