using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using System.Collections.Generic;

namespace Yixuan.Motion
{
    /// <summary>
    /// 外部动画播放器 —— 播放 Mixamo / Unity-chan / 任何 Humanoid AnimationClip。
    ///
    /// ============================================================
    ///  【为什么用 Playables 而不是 AnimatorController】
    /// ============================================================
    /// AnimatorController 要求每个动作预先建好 State 并连线，
    /// 加一个新动作就得手动改状态机。
    ///
    /// Playables 可以直接播放任意 AnimationClip，不需要 controller 里预先配置。
    /// 用户把 FBX/动画丢进 Assets/Animations/Clips/ 就能用。
    ///
    /// ============================================================
    ///  【前提】
    /// ============================================================
    /// 模型必须是 Humanoid（avatar.isHuman == true）。
    /// 实测本模型：avatar = humanoid, isHuman = True ✅
    /// 所以 Mixamo 的动作可以直接用 —— Unity 会自动 retarget 骨骼。
    ///
    /// ============================================================
    ///  【怎么用】
    /// ============================================================
    /// 1. 下载 Humanoid 动画（Mixamo FBX / Unity-chan AnimationClip）
    /// 2. 放进 Assets/Animations/Clips/
    /// 3. Inspector 里点「自动收集动画」，或手动拖进 Clips 列表
    /// 4. 代码调 Play("Wave") 即可
    /// </summary>
    [DisallowMultipleComponent]
    public class YixuanAnimationPlayer : MonoBehaviour
    {
        [Header("引用")]
        public Animator Animator;

        [Header("动画列表（Inspector 拖入，或用右键菜单自动收集）")]
        public AnimationClip[] Clips;

        [Header("默认")]
        [Tooltip("没有指定动画时是否回到程序的自然站姿")]
        public bool FallbackToProcedural = true;

        [Header("调试")]
        public bool VerboseLog = true;

        // ---- 运行时 ----
        PlayableGraph _graph;
        AnimationMixerPlayable _mixer;
        AnimationClipPlayable _currentClip;
        bool _graphReady;
        string _currentName;
        float _clipEndTime;
        bool _looping;

        readonly Dictionary<string, AnimationClip> _map = new Dictionary<string, AnimationClip>();

        /// <summary>是否正在播放外部动画</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>当前动画名</summary>
        public string CurrentName { get { return _currentName; } }

        /// <summary>已收集的动画数量</summary>
        public int ClipCount { get { return _map.Count; } }

        void Awake()
        {
            if (Animator == null) Animator = GetComponentInChildren<Animator>();
            BuildMap();
        }

        void BuildMap()
        {
            _map.Clear();
            if (Clips == null) return;
            for (int i = 0; i < Clips.Length; i++)
            {
                var c = Clips[i];
                if (c == null) continue;
                if (!_map.ContainsKey(c.name)) _map.Add(c.name, c);
            }
        }

        /// <summary>手动注册一个动画（运行时可调）</summary>
        public void Register(AnimationClip clip)
        {
            if (clip == null) return;
            _map[clip.name] = clip;
        }

        public bool HasClip(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (_map.Count == 0) BuildMap();
            return _map.ContainsKey(name);
        }

        public string[] ClipNames()
        {
            if (_map.Count == 0) BuildMap();
            var arr = new string[_map.Count];
            _map.Keys.CopyTo(arr, 0);
            return arr;
        }

        void EnsureGraph()
        {
            if (_graphReady) return;
            if (Animator == null) { Debug.LogError("[AnimPlayer] 没有 Animator"); return; }

            _graph = PlayableGraph.Create("YixuanAnimGraph");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);   // 我们自己在 Update 里推进

            _mixer = AnimationMixerPlayable.Create(_graph, 1);

            var output = AnimationPlayableOutput.Create(_graph, "YixuanAnim", Animator);
            output.SetSourcePlayable(_mixer);

            _graph.Play();
            _graphReady = true;
        }

        /// <summary>
        /// 播放一个动画。
        /// </summary>
        /// <param name="name">动画名（对应 AnimationClip.name）</param>
        /// <param name="loop">是否循环</param>
        /// <param name="speed">播放速度</param>
        /// <param name="fadeIn">淡入时间（秒），0 = 直接切</param>
        public bool Play(string name, bool loop = false, float speed = 1f, float fadeIn = 0.15f)
        {
            if (!HasClip(name))
            {
                if (VerboseLog) Debug.LogWarning("[AnimPlayer] 没有动画: " + name + "（已注册 " + _map.Count + " 个）");
                return false;
            }

            EnsureGraph();
            if (!_graphReady) return false;

            var clip = _map[name];

            // 断开旧的
            if (_currentClip.IsValid()) { _graph.Disconnect(_mixer, 0); _graph.DestroyPlayable(_currentClip); }

            _currentClip = AnimationClipPlayable.Create(_graph, clip);
            _currentClip.SetApplyFootIK(false);
            _currentClip.SetSpeed(speed);

            _graph.Connect(_currentClip, 0, _mixer, 0);
            _mixer.SetInputWeight(0, 1f);

            _currentName = name;
            _clipEndTime = clip.length / Mathf.Max(0.01f, speed);
            _looping = loop;
            IsPlaying = true;

            // ★ 换动作时时间轴要归零，否则新动作会从上一个动作停在的时间点开始
            _playTime = 0.0;
            _finishedNotified = false;

            if (VerboseLog)
                Debug.Log("[AnimPlayer] 播放 " + name + "  时长=" + clip.length.ToString("F2") + "s" +
                          (loop ? " (循环)" : ""));
            return true;
        }

        /// <summary>一次性动作播完时触发（供 YixuanCharacter 回到待机）</summary>
        public event System.Action<string> AnimationFinished;
        bool _finishedNotified;

        /// <summary>停止播放，回到程序化姿态</summary>
        public void Stop()
        {
            if (_graphReady && _currentClip.IsValid())
            {
                _graph.Disconnect(_mixer, 0);
                _graph.DestroyPlayable(_currentClip);
            }
            _currentClip = default(AnimationClipPlayable);
            IsPlaying = false;
            _currentName = null;
            _playTime = 0.0;
        }

        /// <summary>我们自己在 Update 里推进的时间轴（秒）</summary>
        double _playTime;

        /// <summary>
        /// 【测量专用】把动画锁定到指定相位并立刻求值一次。
        ///
        /// 为什么需要：做参数扫描 / A-B 对比时，如果让动画自由播，
        /// 每个参数值采到的**相位不同** → 数据根本不可比
        /// （实测同一设定下"手贴躯干"能测出 0.047 和 0.561 两个值）。
        /// 锁定相位后每次都在同一帧上比，数据才有意义。
        ///
        /// 用法：`LockPhase(0.5f)` 把播放头放到片段的 50% 处。
        /// </summary>
        public void LockPhase(float normalized)
        {
            if (!_graphReady || !_currentClip.IsValid()) return;
            double len = _clipEndTime > 0.0001 ? _clipEndTime : 1.0;
            _playTime = Mathf.Clamp01(normalized) * len;
            _currentClip.SetTime(_playTime);
            _graph.Evaluate();
        }

        /// <summary>【测量专用】当前片段的时长（秒）</summary>
        public double ClipLength { get { return _clipEndTime; } }

        void Update()
        {
            if (!_graphReady || !IsPlaying) return;

            // ★★ 关键：DirectorUpdateMode.Manual 下，PlayableGraph 不会自己累积时间。
            //    必须自己把时间加上去，再 SetTime 告诉 graph "现在几点"，然后求值。
            //
            //    踩坑记录：原代码写的是 _graph.Evaluate(Time.deltaTime)
            //    —— 那只是"用这个 delta 求值一次"，并不会推进时间轴，
            //    于是每次求值都停在第 0.016 秒那一帧：
            //    骨骼值完全不动（实测 5 次采样一模一样），
            //    表现出来就是"点了动作、腿不动 / 人只是被摆到某个固定姿势"。
            _playTime += Time.deltaTime;

            // 循环：时间绕回，避免一直累加到 double 精度丢失
            if (_looping && _clipEndTime > 0.0001 && _playTime >= _clipEndTime)
            {
                _playTime -= _clipEndTime * Mathf.Floor((float)(_playTime / _clipEndTime));
            }

            // 把时间设到"当前片段"上（不是 graph 上 —— graph 没有 SetTime）
            if (_currentClip.IsValid()) _currentClip.SetTime(_playTime);
            _graph.Evaluate();

            // 非循环动画播完后自动停止
            if (!_looping && _currentClip.IsValid())
            {
                double t = _currentClip.GetTime();
                if (t >= _clipEndTime - 0.001)
                {
                    if (VerboseLog) Debug.Log("[AnimPlayer] " + _currentName + " 播放结束");
                    string done = _currentName;
                    Stop();
                    if (!_finishedNotified)
                    {
                        _finishedNotified = true;
                        if (AnimationFinished != null) AnimationFinished(done);
                    }
                }
            }
        }

        void OnDisable()
        {
            if (_graphReady) { _graph.Destroy(); _graphReady = false; }
        }

        void OnDestroy()
        {
            if (_graphReady) { _graph.Destroy(); _graphReady = false; }
        }

        public string Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[AnimPlayer] 已注册 " + _map.Count + " 个动画");
            sb.AppendLine("  当前 = " + (IsPlaying ? _currentName : "(无，使用程序化姿态)"));
            if (_map.Count > 0)
            {
                sb.Append("  可用: ");
                int n = 0;
                foreach (var kv in _map) { if (n++ > 0) sb.Append(", "); sb.Append(kv.Key); if (n >= 20) { sb.Append("..."); break; } }
                sb.AppendLine();
            }
            return sb.ToString();
        }

#if UNITY_EDITOR
        // ============================================================
        //  编辑器工具：一键收集动画
        // ============================================================
        [ContextMenu("自动收集动画（Assets/Animations）")]
        public void AutoCollect()
        {
            string[] folders = { "Assets/Animations" };
            var guids = UnityEditor.AssetDatabase.FindAssets("t:AnimationClip", folders);

            var list = new List<AnimationClip>();
            var seen = new HashSet<string>();
            int skipped = 0;

            for (int i = 0; i < guids.Length; i++)
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
                var clip = UnityEditor.AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null) continue;
                // 跳过 preview 之类
                if (clip.name.StartsWith("__preview__")) { skipped++; continue; }
                if (seen.Contains(clip.name)) continue;
                seen.Add(clip.name);
                list.Add(clip);
            }

            Clips = list.ToArray();
            BuildMap();
            UnityEditor.EditorUtility.SetDirty(this);

            Debug.Log("[AnimPlayer] 自动收集完成：找到 " + list.Count + " 个动画" +
                      (skipped > 0 ? "（跳过 " + skipped + " 个预览）" : "") +
                      "\n  " + string.Join(", ", ClipNames()));
        }

        [ContextMenu("打印动画列表")]
        public void LogClips()
        {
            BuildMap();
            Debug.Log(Status());
        }
#endif
    }
}
