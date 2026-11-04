using UnityEngine;

namespace Yixuan.AI
{
    public partial class ChatSession
    {
        CharacterProfile _characterProfile;
        CharacterProfile _turnCharacterProfile;

        CharacterProfile ProfileForTurn
        {
            get { return _turnCharacterProfile ?? _characterProfile ?? CharacterProfile.FromConfig(Config); }
        }

        public CharacterProfile CurrentCharacterProfile()
        {
            return (_characterProfile ?? CharacterProfile.FromConfig(Config)).Clone();
        }

        void InitializeCharacterProfile()
        {
            string issue;
            _characterProfile = CharacterProfileStore.Load(Config, out issue);
            if (!string.IsNullOrEmpty(issue)) Debug.LogWarning("[CharacterSkills] " + issue + " 使用出厂配置，原文件未覆盖。");
            if (UI != null) UI.RefreshCharacterProfileView();
        }

        public bool TryApplyCharacterProfile(CharacterProfile profile, bool persist, out string issue)
        {
            issue = null;
            if (profile == null) { issue = "人格配置为空。"; return false; }
            if (!profile.TryValidate(out issue)) return false;
            var copy = profile.Clone();
            copy.CharacterName = copy.CharacterName.Trim();
            if (persist && !CharacterProfileStore.TrySave(copy, out issue)) return false;
            _characterProfile = copy;
            if (UI != null) UI.RefreshCharacterProfileView();
            Debug.Log("[CharacterSkills] 人格与 Skills 已应用，从下一轮对话生效。");
            return true;
        }

        bool TryHandleCameraInput(string text)
        {
            if (_syntheticVisualTurn || !ProfileForTurn.CameraEnabled) return false;
            // F3：先提取整句身体权限，整句映射和关键词路由都服从同一约束。
            bool allowBodyAssist = Yixuan.Vision.GazeVoiceIntent.AllowsBodyAssist(text);
            BehaviorPlan calibrated;
            if (CameraCommandBinding.TryResolve(ProfileForTurn, text, out calibrated) && Director != null &&
                Director.ExecuteObservationCamera(calibrated, allowBodyAssist))
            {
                Debug.Log("[CharacterSkills] 云台命中整句映射：" + calibrated.camera +
                          "（allowBodyAssist=" + allowBodyAssist + "）");
                return true;
            }
            return Gaze != null && Gaze.HandleUtterance(text);
        }
    }
}
