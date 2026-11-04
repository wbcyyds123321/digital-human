using System.Collections.Generic;

namespace Yixuan.AI
{
    /// <summary>
    /// LLM 输出的结构化行为计划 —— 契约 v2：**先感受与神态，最后才说话**。
    ///
    /// LLM 是按顺序逐字生成的，字段顺序就是它"思考"的顺序：
    /// {
    ///   "feeling":  "被夸了，有点开心又不好意思",   ← 内心感受（不说出口）
    ///   "demeanor": "shy",                          ← 神态（DemeanorCatalog）
    ///   "gaze":     "down",                         ← 视线
    ///   "emotion":  "shy", "intensity": 0.6,        ← 表情（ExpressionLibrary）
    ///   "gesture":  null,  "posture": null,         ← 动作（MixamoActionManifest）
    ///   "wish":     "",                             ← 清单里没有、但想做的表情/动作（中文描述）
    ///   "reply":    "哪有……你也太会说话了。"        ← 最后才是说的话
    /// }
    ///
    /// 【JsonUtility 限制】不支持交错数组 / Dictionary，所以 beats 用「对象数组」。
    /// JsonUtility 按字段名映射，与 JSON 里的字段顺序无关；顺序只对 LLM 有意义（见 PromptBuilder）。
    /// </summary>
    [System.Serializable]
    public class BehaviorPlan
    {
        public string feeling;    // 内心感受，≤20 字，不说出口（UI 上可显示成"内心想法"）
        public string demeanor;   // DemeanorCatalog id；空 → 按 emotion 反推
        public string gaze;       // user / away / down / up；空 → 该神态的默认视线（只动头/颈）
        /// <summary>
        /// 相机云台（角色眼睛看向哪里）。空/null = 不动。
        /// recenter（回正）/ look_user（看用户/镜头）/ look_store（看便利店）/
        /// left / right / behind / up / down（相对转视角）。
        /// 和 gaze 的区别：gaze 只动头颈，camera 动整个相机云台。
        /// </summary>
        public string camera;
        /// <summary>
        /// 转向角度（可选，正数；方向由 camera 决定）：
        /// left/right → 偏航 1~180°，up/down → 俯仰 1~60°；
        /// 填 0 用 CameraMoveCatalog 的默认角度；recenter/look_* 忽略。
        /// </summary>
        public float camera_angle;
        /// <summary>云台保持秒数（0 = 用默认；AutoRecenter 关闭时一直保持）</summary>
        public float camera_hold;
        /// <summary>移动：null / "walk_to" / "stop"；目标可以是地点 id，也可以是世界坐标。</summary>
        public string move;
        /// <summary>可去地点 id（WorldData.Anchors）；与 move_x/move_z 二选一。</summary>
        public string move_target;
        /// <summary>目标世界坐标 x（move_target 为空时使用）</summary>
        public float move_x;
        /// <summary>目标世界坐标 z</summary>
        public float move_z;
        /// <summary>身体原地相对转身（有符号角度：右正左负；0=不转）。和 camera 云台分开。</summary>
        public float body_turn;
        /// <summary>相对当前身体前方走多少米（正=前，负=后）；与 move_target/move_x 二选一。</summary>
        public float move_forward_m;
        /// <summary>相对当前身体右方走多少米（正=右，负=左）</summary>
        public float move_right_m;
        public string emotion;    // ExpressionLibrary id；空/null → neutral
        public float intensity;   // 0~1
        public string gesture;    // MixamoActionManifest id；空/null = 不指定（Director 按情绪默认表补）
        public string posture;    // stand / sit / kneel；空 = 保持当前
        public string wish;       // 清单里没有、但想做的表情/动作（自由描述，用来补库）
        public string reply;      // 要说的话（必填；空 = 不说话）
        public Beat[] beats;      // 可省略（P2 编排用）

        // ---- 解析器填写：LLM 原本要求的值（校验修正之前），给行为面板显示"想要什么 / 实际做了什么" ----
        [System.NonSerialized] public string askedEmotion;
        [System.NonSerialized] public string askedGesture;
        [System.NonSerialized] public string askedDemeanor;
        [System.NonSerialized] public string source = "LLM";   // LLM / RuleBrain / 保底

        public bool HasReply { get { return !string.IsNullOrEmpty(reply); } }
        public bool HasGesture { get { return !string.IsNullOrEmpty(gesture); } }

        public override string ToString()
        {
            return "feeling=\"" + (feeling ?? "") + "\" demeanor=" + (demeanor ?? "-") +
                   " reply=\"" + (reply ?? "") + "\" emotion=" + (emotion ?? "-") +
                   " intensity=" + intensity.ToString("F2") +
                   " gesture=" + (gesture ?? "null") +
                   " posture=" + (posture ?? "-") + " gaze=" + (gaze ?? "-") +
                   " camera=" + (string.IsNullOrEmpty(camera) ? "-" : camera + (camera_angle > 0f ? "(" + camera_angle.ToString("F0") + "°)" : "") + (camera_hold > 0 ? "[" + camera_hold.ToString("F0") + "s]" : "")) +
                   " move=" + (string.IsNullOrEmpty(move) ? "-" : move + (string.IsNullOrEmpty(move_target) ? "(" + move_x.ToString("F1") + "," + move_z.ToString("F1") + ")" : "→" + move_target)) +
                   " bodyTurn=" + body_turn.ToString("F0") + " rel=(" + move_forward_m.ToString("F1") + "," + move_right_m.ToString("F1") + ")" +
                   (string.IsNullOrEmpty(wish) ? "" : " wish=\"" + wish + "\"") +
                   " beats=" + (beats != null ? beats.Length : 0);
        }
    }

    /// <summary>句内动作节拍：reply 的第 at 个字时触发 gesture（P2 编排用，T2 先只解析）</summary>
    [System.Serializable]
    public class Beat
    {
        public int at;
        public string gesture;
    }

    /// <summary>
    /// 解析结果：成功/失败 + 修正后的 plan + 警告列表。
    /// 解析器**永远不抛异常**（B2 验收要求），坏输入也返回一个可用的 PlanParseResult。
    /// </summary>
    public class PlanParseResult
    {
        public BehaviorPlan plan;
        public List<string> warnings = new List<string>();
        public bool ok;
        public string raw;

        public bool HasWarnings { get { return warnings != null && warnings.Count > 0; } }

        public string WarningText()
        {
            if (warnings == null || warnings.Count == 0) return "(无警告)";
            return string.Join(" | ", warnings.ToArray());
        }

        public override string ToString()
        {
            return "[PlanParseResult ok=" + ok + "] " + (plan != null ? plan.ToString() : "plan=null") +
                   " warnings=" + WarningText();
        }
    }
}
