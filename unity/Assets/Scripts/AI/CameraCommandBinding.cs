using System;
using System.Text.RegularExpressions;
using Yixuan.Vision;

namespace Yixuan.AI
{
    [Serializable]
    public sealed class CameraCommandBinding
    {
        public string Phrase = "";
        public string MoveId = "left";
        public float Angle;

        public static string NormalizePhrase(string text)
        {
            return (text ?? "").Trim().TrimEnd('，', ',', '。', '！', '!', '？', '?', '；', ';').Trim();
        }

        public bool TryValidate(out string issue)
        {
            issue = null;
            string phrase = NormalizePhrase(Phrase);
            if (phrase.Length == 0 || phrase.Length > 100) issue = "云台触发句需要 1～100 字。";
            else if (phrase.IndexOfAny(new[] { '，', ',', '。', '！', '!', '？', '?', '；', ';', '\n', '\r' }) >= 0)
                issue = "整句映射只支持一个从句，不要混合问题与多个命令。";
            else if (VisualQueryIntent.IsUserPostureQuestion(phrase) || VisualQueryIntent.ContainsAny(phrase,
                "不要", "别", "不用", "不必", "禁止", "不许", "不准", "不能", "不想", "不需要", "不是", "是不是", "是否",
                "刚才", "刚刚", "昨天", "以前", "之前", "曾经", "上次")) issue = "不能把否定、过去描述或本人姿态问题设成执行命令。";
            else if (string.IsNullOrWhiteSpace(MoveId) || MoveId.Length > 80 || Regex.IsMatch(MoveId, @"\s")) issue = "云台动作 ID 无效。";
            else if (float.IsNaN(Angle) || float.IsInfinity(Angle) || Angle < 0f || Angle > 180f) issue = "角度需要是 0～180 的有限数字；0 使用默认角度。";
            return issue == null;
        }

        public bool TryBuildPlan(out BehaviorPlan plan)
        {
            plan = null;
            string issue;
            if (!TryValidate(out issue) || !CameraMoveCatalog.Has(MoveId)) return false;
            plan = new BehaviorPlan { camera = MoveId, camera_angle = Angle };
            return true;
        }

        public static bool TryResolve(CharacterProfile profile, string text, out BehaviorPlan plan)
        {
            plan = null;
            if (profile == null || !profile.CameraEnabled || profile.CameraBindings == null) return false;
            string phrase = NormalizePhrase(text);
            foreach (var binding in profile.CameraBindings)
                if (binding != null && NormalizePhrase(binding.Phrase) == phrase) return binding.TryBuildPlan(out plan);
            return false;
        }

        public static string Diagnose(CharacterProfile profile, string text)
        {
            if (profile == null || !profile.CameraEnabled) return "云台 Skill 已关闭：对话不会执行转向。";
            BehaviorPlan plan;
            if (TryResolve(profile, text, out plan))
            {
                var move = CameraMoveCatalog.Get(plan.camera);
                if (!string.IsNullOrEmpty(move.Track)) return "命中整句映射：" + plan.camera + "；跟踪目标 " + move.Track + "，角度由目标位置决定。未执行。";
                float yaw, pitch;
                CameraMoveCatalog.TryResolve(plan.camera, plan.camera_angle, out yaw, out pitch);
                return "命中整句映射：" + plan.camera + "；执行角度 yaw=" + yaw.ToString("F1") + "°，pitch=" + pitch.ToString("F1") + "°（含增益和限幅）。未执行。";
            }
            GazeIntent intent;
            if (GazeVoiceIntent.TryParse(text, out intent))
                return "命中现有本地规则：" + intent.Type + "；yaw=" + intent.Yaw.ToString("F1") + "°，pitch=" + intent.Pitch.ToString("F1") + "°。未执行。";
            return "未命中本地规则；仍可能走 DeepSeek 语义判断。可添加整句映射，避免依赖关键词。未调用模型、未执行。";
        }
    }
}
