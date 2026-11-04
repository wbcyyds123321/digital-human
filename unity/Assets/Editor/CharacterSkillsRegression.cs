using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Yixuan.AI;
using Yixuan.Vision;

namespace Yixuan.EditorTests
{
    public static class CharacterSkillsRegression
    {
        static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly List<string> Passed = new List<string>();

        static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + label);
            Passed.Add(label);
        }

        static void Set(object target, string field, object value) { target.GetType().GetField(field, Private).SetValue(target, value); }
        static object Get(object target, string field) { return target.GetType().GetField(field, Private).GetValue(target); }
        static object Invoke(object target, string method, params object[] arguments) { return target.GetType().GetMethod(method, Private).Invoke(target, arguments); }

        [MenuItem("UnityLITF/AI/人格与 Skills 离线回归")]
        public static void RunMenu() { Debug.Log(RunOffline()); }

        public static string RunOffline()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出 Play。");
            Passed.Clear();
            var fixture = new GameObject("CharacterSkillsRegression") { hideFlags = HideFlags.HideAndDontSave };
            fixture.SetActive(false);
            var config = ScriptableObject.CreateInstance<AIConfig>();
            string target = Path.Combine(Application.temporaryCachePath, "character-skills-test-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                string issue;
                var profile = CharacterProfile.FromConfig(config);
                Check(profile.TryValidate(out issue) && profile.CameraEnabled, "默认配置有效且云台开启");
                profile.CharacterName = "技能测试角色";
                profile.Persona = "PERSONA_UNIQUE：一位认真而简洁的讲解员。";
                profile.Skills.Add(new CharacterSkill { Id = "teaching", Title = "解释习惯", Instructions = "SKILL_UNIQUE：先给生活例子，再解释原理。" });
                var copy = profile.Clone();
                copy.Skills[2].Instructions = "独立草稿";
                Check(profile.Skills[2].Instructions.Contains("SKILL_UNIQUE"), "草稿与已应用配置深拷贝隔离");
                string prompt = PromptBuilder.BuildSystemPrompt(config, profile);
                Check(prompt.Contains(profile.CharacterName) && prompt.Contains("PERSONA_UNIQUE") && prompt.Contains("SKILL_UNIQUE"), "人格与启用 Skill 进入系统提示词");
                Check(prompt.Contains("JSON") && prompt.Contains(CameraTurnSkill.SectionTitle), "固定协议与能力目录仍然存在");
                profile.Skills[2].Enabled = false;
                Check(!PromptBuilder.BuildSystemPrompt(config, profile).Contains("SKILL_UNIQUE"), "关闭文字 Skill 后不再注入");
                profile.Skills[2].Enabled = true;
                var binding = new CameraCommandBinding { Phrase = "往左偏一点", MoveId = "left", Angle = 15f };
                profile.CameraBindings.Add(binding);
                BehaviorPlan plan;
                Check(CameraCommandBinding.TryResolve(profile, "往左偏一点。", out plan) && plan.camera == "left" && plan.camera_angle == 15f, "整句映射忽略句末标点且保留参数");
                Check(!CameraCommandBinding.TryResolve(profile, "不要往左偏一点", out plan), "映射不会对子串或否定句执行");
                Check(!CameraCommandBinding.TryResolve(profile, "我刚才说往左偏一点", out plan), "历史提及不会命中整句映射");
                Check(!new CameraCommandBinding { Phrase = "我低头了吗", MoveId = "left" }.TryValidate(out issue), "本人姿态问题不能配置为执行命令");
                Check(!new CameraCommandBinding { Phrase = "不要向左", MoveId = "left" }.TryValidate(out issue), "否定触发句被拒绝");
                Check(!new CameraCommandBinding { Phrase = "先往左，往右", MoveId = "left" }.TryValidate(out issue), "混合从句不能作为单条校准映射");
                Check(new CameraCommandBinding { Phrase = "看一下门口", MoveId = "door-view" }.TryValidate(out issue), "自定义目录 ID 可保留连字符");
                Check(new CameraCommandBinding { Phrase = "看一下店里", MoveId = "look_store" }.TryValidate(out issue), "店铺预设 ID 不被字母 s 误拒绝");
                Check(!new CameraCommandBinding { Phrase = "看一下门口", MoveId = "door view" }.TryValidate(out issue), "动作 ID 的空白符被拒绝");
                Check(!new CameraCommandBinding { Phrase = "转一点", MoveId = "left", Angle = float.NaN }.TryValidate(out issue), "非法角度被拒绝");
                Check(!new CameraCommandBinding { Phrase = "转一点", MoveId = "missing_move", Angle = 10f }.TryBuildPlan(out plan), "缺失执行器不会回退到其他动作");
                profile.Skills[0].Enabled = false;
                Check(!CameraCommandBinding.TryResolve(profile, "往左偏一点", out plan) && PromptBuilder.BuildSystemPrompt(config, profile).Contains("云台 Skill 已关闭"), "云台关闭同时影响映射和提示词");
                profile.Skills[0].Enabled = true;
                var session = fixture.AddComponent<ChatSession>();
                session.Config = config;
                Check(session.TryApplyCharacterProfile(profile, false, out issue), "配置可不落盘应用，供离线测试");
                var snapshot = session.CurrentCharacterProfile();
                Set(session, "_turnCharacterProfile", snapshot);
                var updated = profile.Clone();
                updated.Persona = "NEXT_TURN_ONLY";
                session.TryApplyCharacterProfile(updated, false, out issue);
                var first = (List<ChatMessage>)Invoke(session, "SafeBuildMessages", "测试");
                var fallback = (List<ChatMessage>)Invoke(session, "SafeBuildMessages", "测试");
                Check(first[0].content.Contains("PERSONA_UNIQUE") && fallback[0].content.Contains("PERSONA_UNIQUE") && !fallback[0].content.Contains("NEXT_TURN_ONLY"), "当前轮与回退沿用同一人格快照");
                Set(session, "_turnCharacterProfile", null);
                Check(((List<ChatMessage>)Invoke(session, "SafeBuildMessages", "测试"))[0].content.Contains("NEXT_TURN_ONLY"), "后续轮才采用新人格");
                Set(session, "_turnCharacterProfile", snapshot);
                Set(session, "_state", ChatSession.State.Thinking);
                session.CancelCurrentTurn();
                Check(Get(session, "_turnCharacterProfile") == null, "取消当前轮释放人格快照");
                Check(config.Persona != profile.Persona && config.CharacterName != profile.CharacterName, "人格编辑不修改 AIConfig 资产字段");
                session.TryApplyCharacterProfile(profile, false, out issue);
                var gaze = fixture.AddComponent<EyeGimbalController>();
                session.Gaze = gaze;
                var director = fixture.AddComponent<Director>();
                director.Config = config;
                director.Gaze = gaze;
                session.Director = director;
                float before = gaze.TargetYaw;
                CameraCommandBinding.Diagnose(profile, "往左偏一点");
                Check(gaze.TargetYaw == before, "解析测试不转动云台");
                Set(session, "_syntheticVisualTurn", true);
                Check(!(bool)Invoke(session, "TryHandleCameraInput", "往左偏一点") && gaze.TargetYaw == before, "synthetic 输入不能通过校准映射执行云台");
                Set(session, "_syntheticVisualTurn", false);
                var disabled = profile.Clone();
                disabled.Skills[0].Enabled = false;
                session.TryApplyCharacterProfile(disabled, false, out issue);
                Check(!(bool)Invoke(session, "TryHandleCameraInput", "往左看") && gaze.TargetYaw == before, "关闭云台 Skill 后本地规则也不执行");
                var planning = (IEnumerator)Invoke(session, "PlanCameraBeforeVision", "柜台有什么");
                try { Check(!planning.MoveNext(), "关闭云台 Skill 后不进入预规划"); }
                finally { ((IDisposable)planning).Dispose(); }
                session.TryApplyCharacterProfile(profile, false, out issue);
                float expectedYaw, expectedPitch;
                CameraMoveCatalog.TryResolve("left", 15f, out expectedYaw, out expectedPitch);
                Check((bool)Invoke(session, "TryHandleCameraInput", "往左偏一点") && Mathf.Abs(gaze.TargetYaw - expectedYaw) < 0.01f, "校准映射经 Director 实际设置夹具云台目标");
                Check(CharacterProfileStore.TrySave(profile, out issue, target), "首次保存配置文件");
                var loaded = CharacterProfileStore.Load(config, out issue, target);
                Check(issue == null && loaded.Persona == profile.Persona && loaded.CameraBindings.Count == 1 && loaded.Skills.Count == 3, "JSON 重载保留人格、Skills 和云台映射");
                Check(CharacterProfileStore.TrySave(updated, out issue, target) && CharacterProfileStore.Load(config, out issue, target).Persona == "NEXT_TURN_ONLY", "已存在文件通过原子替换保存");
                using (var locked = File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check(!CharacterProfileStore.TrySave(profile, out issue, target) && !string.IsNullOrEmpty(issue), "保存失败可见且不应用修改");
                Check(CharacterProfileStore.Load(config, out issue, target).Persona == "NEXT_TURN_ONLY", "保存失败后原文件仍完整");
                File.WriteAllText(target, "{broken");
                loaded = CharacterProfileStore.Load(config, out issue, target);
                Check(!string.IsNullOrEmpty(issue) && loaded.CharacterName == config.CharacterName, "损坏文件诊断后回退出厂，不覆盖原文件");
                var invalid = profile.Clone();
                invalid.Skills.Add(invalid.Skills[0]);
                Check(!invalid.TryValidate(out issue), "重复 Skill ID 被拒绝");
                invalid = profile.Clone();
                invalid.Persona = new string('字', CharacterProfile.MaxPersonaChars + 1);
                Check(!invalid.TryValidate(out issue), "超长人格被拒绝");
                var ui = fixture.AddComponent<ChatUI>();
                ui.Session = session;
                var root = new VisualElement();
                ui.MountCharacterSkills(root);
                Check(root.Q<TextField>("profile-persona") != null && root.Q<Button>("save-character-skills") != null, "人格编辑与保存控件实际构建");
                Check(root.Q<TextField>("camera-skill-test-input") != null && root.Q<Button>("camera-skill-dry-run") != null, "云台校准与无副作用测试控件实际构建");
                Check(root.Q<Button>("close-character-skills").style.display.value == DisplayStyle.None, "编辑器独立挂载不显示无出口的关闭按钮");
                int originalCount = ((CharacterProfile)Get(ui, "_profileDraft")).Skills.Count;
                Invoke(ui, "AddCharacterSkill");
                Check(((CharacterProfile)Get(ui, "_profileDraft")).Skills.Count == originalCount + 1 && session.CurrentCharacterProfile().Skills.Count == originalCount, "新增 Skill 只改变草稿");
                return "PASS " + Passed.Count + "\n" + string.Join("\n", Passed);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fixture);
                UnityEngine.Object.DestroyImmediate(config);
                if (File.Exists(target)) File.Delete(target);
                if (File.Exists(target + ".tmp")) File.Delete(target + ".tmp");
            }
        }
    }
}
