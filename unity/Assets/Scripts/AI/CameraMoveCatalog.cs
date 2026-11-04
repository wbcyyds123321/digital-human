using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Yixuan.AI
{
    /// <summary>
    /// 相机云台动作表（对应 Actions/Expressions 的做法）：
    ///   - 这里是"云台能做什么"的唯一数据源；
    ///   - PromptBuilder 把它自动注入 system prompt（LLM 只能从这里选）；
    ///   - ResponseParser 用它校验 camera id / 角度范围；
    ///   - Director 用它执行（默认角度 + 可选 camera_angle 覆盖）。
    ///
    /// ★ 默认角度不再写死：由场景里的 <see cref="CameraGimbalSettings"/> 提供
    ///   （在 VisionSystem 上，Inspector 里直接调；也可加自定义预设）。
    ///   没绑定设置时退回保守默认值（60/28/180），保证离线测试/菜单预览也能用。
    ///
    /// 角度约定（和 EyeGimbalController.TurnRelative 一致）：
    ///   yaw 正 = 向右，负 = 向左；pitch 正 = 抬头，负 = 低头。
    /// </summary>
    public static class CameraMoveCatalog
    {
        public struct Move
        {
            public string Id;
            public string Label;       // 给 prompt 看的中文名
            public float DefaultYaw;   // 默认偏航（度，已乘 AngleScale）
            public float DefaultPitch; // 默认俯仰（度，已乘 AngleScale）
            public string Track;       // "user" / "store" / ""（空 = 用默认角度）
            public bool AngleYaw;      // 允许 camera_angle 覆盖（左右转）
            public bool AnglePitch;    // 允许 camera_angle 覆盖（抬低头）
        }

        // ---- 缺省值（没有绑 CameraGimbalSettings 时用）----
        const float FallbackLeftYaw = 60f;
        const float FallbackRightYaw = 60f;
        const float FallbackUpPitch = 28f;
        const float FallbackDownPitch = 28f;
        const float FallbackBehindYaw = 180f;
        const float FallbackMaxYaw = 180f;
        const float FallbackMaxPitch = 60f;
        const float FallbackMinAngle = 1f;

        static CameraGimbalSettings _settings;
        static List<Move> _moves = new List<Move>();

        static CameraMoveCatalog()
        {
            Rebuild();
        }

        /// <summary>CameraGimbalSettings.Awake 时调用；重新生成动作表。</summary>
        public static void Bind(CameraGimbalSettings settings)
        {
            _settings = settings;
            Rebuild();
        }

        public static void Unbind(CameraGimbalSettings settings)
        {
            if (ReferenceEquals(_settings, settings))
            {
                _settings = null;
                Rebuild();
            }
        }

        static float Scale { get { return _settings != null ? _settings.AngleScale : 1f; } }
        static float MaxYaw { get { return _settings != null ? Mathf.Max(1f, _settings.MaxYaw) : FallbackMaxYaw; } }
        static float MaxPitch { get { return _settings != null ? Mathf.Max(1f, _settings.MaxPitch) : FallbackMaxPitch; } }
        static float MinAngleDeg { get { return _settings != null ? Mathf.Max(0.1f, _settings.MinAngle) : FallbackMinAngle; } }
        static float AbsLeft { get { return _settings != null ? Mathf.Abs(_settings.DefaultLeftYaw) : FallbackLeftYaw; } }
        static float AbsRight { get { return _settings != null ? Mathf.Abs(_settings.DefaultRightYaw) : FallbackRightYaw; } }
        static float AbsUp { get { return _settings != null ? Mathf.Abs(_settings.DefaultUpPitch) : FallbackUpPitch; } }
        static float AbsDown { get { return _settings != null ? Mathf.Abs(_settings.DefaultDownPitch) : FallbackDownPitch; } }
        static float AbsBehind { get { return _settings != null ? Mathf.Abs(_settings.BehindYaw) : FallbackBehindYaw; } }

        static void Rebuild()
        {
            var list = new List<Move>(24)
            {
                new Move { Id = "recenter",  Label = "回正/看前面/回默认位置" },
                new Move { Id = "look_user", Label = "看用户/看镜头/对着我", Track = "user" },
                new Move { Id = "look_store",Label = "看便利店/柜台/货架/门口", Track = "store" },
                // 左右/抬低头：默认角度已乘 AngleScale 并 clamp
                new Move { Id = "left", Label = "往左看", DefaultYaw = -ClampDefault(AbsLeft * Scale, MaxYaw), AngleYaw = true },
                new Move { Id = "right", Label = "往右看", DefaultYaw = ClampDefault(AbsRight * Scale, MaxYaw), AngleYaw = true },
                new Move { Id = "behind", Label = "回头看后面", DefaultYaw = ClampDefault(AbsBehind * Scale, MaxYaw) },
                new Move { Id = "up", Label = "抬头看上面", DefaultPitch = ClampDefault(AbsUp * Scale, MaxPitch), AnglePitch = true },
                new Move { Id = "down", Label = "低头看下面", DefaultPitch = -ClampDefault(AbsDown * Scale, MaxPitch), AnglePitch = true },
            };

            // 自定义预设（Inspector 里加的），不覆盖内置 id
            if (_settings != null && _settings.CustomMoves != null)
            {
                for (int i = 0; i < _settings.CustomMoves.Count; i++)
                {
                    var c = _settings.CustomMoves[i];
                    if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                    string id = Normalize(c.Id);
                    if (string.IsNullOrEmpty(id) || HasInList(list, id)) continue;
                    list.Add(new Move
                    {
                        Id = id,
                        Label = string.IsNullOrEmpty(c.Label) ? id : c.Label,
                        DefaultYaw = Mathf.Clamp(c.Yaw * Scale, -MaxYaw, MaxYaw),
                        DefaultPitch = Mathf.Clamp(c.Pitch * Scale, -MaxPitch, MaxPitch),
                        Track = string.IsNullOrEmpty(c.Track) ? "" : c.Track.Trim().ToLowerInvariant(),
                    });
                }
            }

            _moves = list;
        }

        static float ClampDefault(float deg, float max)
        {
            return Mathf.Clamp(deg, 0f, max);
        }

        static string Normalize(string id)
        {
            return (id ?? "").Trim().ToLowerInvariant();
        }

        static bool HasInList(List<Move> list, string id)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Id == id) return true;
            return false;
        }

        public static IReadOnlyList<Move> Moves { get { return _moves.AsReadOnly(); } }

        public static bool Has(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            id = Normalize(id);
            for (int i = 0; i < _moves.Count; i++)
                if (_moves[i].Id == id) return true;
            return false;
        }

        public static Move Get(string id)
        {
            id = Normalize(id);
            for (int i = 0; i < _moves.Count; i++)
                if (_moves[i].Id == id) return _moves[i];
            return _moves.Count > 0 ? _moves[0] : new Move { Id = "recenter", Label = "回正" };
        }

        /// <summary>“往左看/抬头”等动作的默认角度（绝对值，已乘 AngleScale）。</summary>
        public static float DefaultMagnitude(string id)
        {
            if (!Has(id)) return 0f;
            Move m = Get(id);
            if (m.AnglePitch) return Mathf.Abs(m.DefaultPitch);
            return Mathf.Abs(m.DefaultYaw);
        }

        /// <summary>所有角度统一的函数式缩放（camera_angle 也走它）。</summary>
        public static float ScaleAngle(float degrees)
        {
            return Mathf.Abs(degrees) * Scale;
        }

        /// <summary>给 prompt 的自动清单（和 ActionCatalog.DescribeGestures 一个套路）。</summary>
        public static string Describe()
        {
            var sb = new StringBuilder(768);
            for (int i = 0; i < _moves.Count; i++)
            {
                var m = _moves[i];
                sb.Append("- ").Append(m.Id).Append("  ").Append(m.Label);
                if (m.Track == "user") sb.Append("（跟踪用户）");
                else if (m.Track == "store") sb.Append("（跟踪便利店）");
                else if (m.AngleYaw)
                    sb.Append("（默认 ").Append(Mathf.Abs(m.DefaultYaw).ToString("F0"))
                      .Append("°，可用 camera_angle 指定 ").Append(MinAngleDeg.ToString("F0"))
                      .Append("~").Append(MaxYaw.ToString("F0")).Append("）");
                else if (m.AnglePitch)
                    sb.Append("（默认 ").Append(Mathf.Abs(m.DefaultPitch).ToString("F0"))
                      .Append("°，可用 camera_angle 指定 ").Append(MinAngleDeg.ToString("F0"))
                      .Append("~").Append(MaxPitch.ToString("F0")).Append("）");
                else
                    sb.Append("（固定 ").Append(Mathf.Abs(m.DefaultYaw + m.DefaultPitch).ToString("F0")).Append("°）");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>校验并解析角度：camera + camera_angle → 实际 yaw/pitch（度）。</summary>
        public static bool TryResolve(string id, float angle, out float yaw, out float pitch)
        {
            yaw = 0f; pitch = 0f;
            if (!Has(id)) return false;
            Move m = Get(id);

            if (m.Track == "user" || m.Track == "store") return true;   // 由 Director 走 Track

            if (m.AngleYaw)
            {
                float a = angle > 0.01f ? Mathf.Clamp(ScaleAngle(angle), Mathf.Min(MinAngleDeg, MaxYaw), MaxYaw)
                                        : Mathf.Abs(m.DefaultYaw);
                yaw = m.Id == "left" ? -a : a;
            }
            else
            {
                yaw = m.DefaultYaw;
            }

            if (m.AnglePitch)
            {
                float a = angle > 0.01f ? Mathf.Clamp(ScaleAngle(angle), Mathf.Min(MinAngleDeg, MaxPitch), MaxPitch)
                                        : Mathf.Abs(m.DefaultPitch);
                pitch = m.Id == "down" ? -a : a;
            }
            else
            {
                pitch = m.DefaultPitch;
            }
            return true;
        }

        /// <summary>这个动作允许的角度上限（给校验器用；0 = 不支持 angle）。</summary>
        public static float MaxAngle(string id)
        {
            if (!Has(id)) return 0f;
            Move m = Get(id);
            if (m.AngleYaw) return MaxYaw;
            if (m.AnglePitch) return MaxPitch;
            return 0f;
        }
    }
}
