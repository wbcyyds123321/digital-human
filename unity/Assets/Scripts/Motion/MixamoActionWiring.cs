#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Yixuan.Motion
{
    /// <summary>
    /// 把 Mixamo 动作灌进 YixuanAnimationPlayer，并建立"语义名 → clip"映射。
    ///
    /// 【为什么需要语义名】
    /// Mixamo 的原始名（"Waving Gesture"、"Lengthy Head Nod"）不方便代码调用，
    /// 而且同一个语义常有多个可选动作。这里建一张表：
    ///     idle     → Standing Idle
    ///     wave     → Waving Gesture
    ///     nod      → Lengthy Head Nod
    ///     ...
    /// 代码/UI 用 id 调用，换素材只改这张表。
    ///
    /// 【菜单 ⑧：补齐动作元数据】
    /// manifest 里每个动作除了 id / clip 名，还需要中文名、时长、姿态、部位、情绪标签、
    /// 能否循环/打断 —— 供 T2 的 ActionCatalog / PromptBuilder 用。
    /// 人工标注表在 ActionMetadata.cs，时长由这里从 clip.length 实测。
    ///
    /// 用法：
    ///   菜单 UnityLITF/仪玄/② 装配 Mixamo 动作到播放器   （生成映射表）
    ///   菜单 UnityLITF/仪玄/⑧ 补齐动作元数据              （填中文名/时长/姿态/情绪）
    /// </summary>
    public static class MixamoActionWiring
    {
        const string Folder = "Assets/Animations/Mixamo";

        /// <summary>语义 id → Mixamo 动作名（按优先级排列，取第一个存在的）</summary>
        public static readonly Dictionary<string, string[]> Map = new Dictionary<string, string[]>
        {
            { "idle",        new[]{ "Standing Idle", "Idle", "Breathing Idle" } },
            { "idle_breath", new[]{ "Breathing Idle", "Standing Idle" } },
            { "idle_shift",  new[]{ "Idle", "Standing Idle" } },
            { "wave",        new[]{ "Waving Gesture", "Waving" } },
            { "wave_both",   new[]{ "Waving", "Waving Gesture" } },
            // ⚠️ 这个文件其实是 "Standing Clap From Sitting"（从坐姿站起来拍手），
            //    不是站立拍手。原因：Mixamo 上真正的 "Clap While Standing"
            //    (motion_id c9c60ef2) 服务端导出固定失败，只能退而用这个。
            //    表现：动画里有"起身"过程，脚会短暂沉到地面下（实测脚Y=-0.24）。
            { "clap",        new[]{ "Standing Clap" } },
            { "nod",         new[]{ "Lengthy Head Nod" } },
            { "shake_head",  new[]{ "Shaking Head No" } },
            { "think",       new[]{ "Thinking" } },
            { "look_around", new[]{ "Nervously Look Around" } },
            { "walk",        new[]{ "Walking" } },
            { "jog",         new[]{ "Jogging" } },
            { "sit",         new[]{ "Sitting Idle" } },
            { "sit_point",   new[]{ "Sitting And Pointing" } },
            { "kneel",       new[]{ "Kneeling Idle" } },
        };

        [MenuItem("UnityLITF/仪玄/② 装配 Mixamo 动作到播放器")]
        public static void Wire()
        {
            var go = Selection.activeGameObject;
            if (go == null) go = GameObject.Find("Yixuan_Test");
            if (go == null) { Debug.LogError("[MixamoWire] 找不到角色，请先选中角色根节点"); return; }

            var player = go.GetComponent<YixuanAnimationPlayer>();
            if (player == null) player = go.AddComponent<YixuanAnimationPlayer>();

            // 收集所有 Mixamo clip
            var all = new List<AnimationClip>();
            var byName = new Dictionary<string, AnimationClip>();
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { Folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                if (!path.ToLower().EndsWith(".fbx")) continue;
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null) continue;
                all.Add(clip);
                if (!byName.ContainsKey(clip.name)) byName[clip.name] = clip;
            }

            player.Clips = all.ToArray();
            player.Animator = go.GetComponent<Animator>();
            EditorUtility.SetDirty(player);

            // 生成映射报告
            var sb = new StringBuilder();
            sb.AppendLine("========== Mixamo 动作装配 ==========");
            sb.AppendLine("共装入 " + all.Count + " 个 clip");
            sb.AppendLine();
            sb.AppendLine("语义 id           → 实际使用");
            sb.AppendLine("----------------------------------------");
            var idList = new List<string>(Map.Keys);
            idList.Sort();
            int miss = 0;
            foreach (var id in idList)
            {
                string used = null;
                foreach (var cand in Map[id])
                {
                    if (byName.ContainsKey(cand)) { used = cand; break; }
                }
                if (used == null) { miss++; sb.AppendLine("  " + id.PadRight(16) + " → ✘ 缺素材"); }
                else sb.AppendLine("  " + id.PadRight(16) + " → " + used);
            }
            sb.AppendLine();
            sb.AppendLine("缺失 " + miss + " 个语义");

            // 生成一个资源清单，方便非编辑器代码读取
            var manifest = go.GetComponent<MixamoActionManifest>();
            if (manifest == null) manifest = go.AddComponent<MixamoActionManifest>();
            var entries = new List<MixamoActionManifest.Entry>();
            foreach (var id in idList)
            {
                string used = null;
                foreach (var cand in Map[id])
                {
                    if (byName.ContainsKey(cand)) { used = cand; break; }
                }
                if (used != null)
                {
                    var entry = new MixamoActionManifest.Entry { Id = id, ClipName = used };
                    // 顺手补齐元数据 + 实测时长（菜单 ⑧ 也能单独补）
                    ActionMetadata.TryFill(entry);
                    AnimationClip clip;
                    if (byName.TryGetValue(used, out clip) && clip != null) entry.Duration = clip.length;
                    entries.Add(entry);
                }
            }
            manifest.Actions = entries.ToArray();
            EditorUtility.SetDirty(manifest);

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            Debug.Log(sb.ToString());
        }

        [MenuItem("UnityLITF/仪玄/③ 列出当前可用动作")]
        public static void ListWired()
        {
            var go = GameObject.Find("Yixuan_Test");
            if (go == null) return;
            var m = go.GetComponent<MixamoActionManifest>();
            var sb = new StringBuilder();
            sb.AppendLine("===== 已装配动作 =====");
            if (m == null || m.Actions == null) sb.AppendLine("  (未装配，先跑 ②)");
            else foreach (var e in m.Actions) sb.AppendLine("  " + e.Id.PadRight(16) + " → " + e.ClipName);
            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// 菜单 ⑧：给现有 manifest 补齐 T2 动作元数据（中文名/时长/姿态/部位/情绪/循环/可打断）。
        /// 不重新装配 clip，只改数据 —— 已有 manifest 的场景可以直接跑。
        /// </summary>
        [MenuItem("UnityLITF/仪玄/⑧ 补齐动作元数据（中文名/时长/姿态/情绪）")]
        public static void FillMetadata()
        {
            var go = GameObject.Find("Yixuan_Test");
            if (go == null) { Debug.LogError("[Metadata] 找不到 Yixuan_Test"); return; }

            var manifest = go.GetComponent<MixamoActionManifest>();
            if (manifest == null || manifest.Actions == null || manifest.Actions.Length == 0)
            {
                Debug.LogError("[Metadata] 语义表为空，先跑菜单 ② 装配 Mixamo 动作");
                return;
            }

            var byName = LoadClipsByFileName();

            int withMeta = 0, noMeta = 0, noClip = 0;
            var sb = new StringBuilder();
            sb.AppendLine("========== 动作元数据（菜单 ⑧）==========");
            sb.AppendLine(string.Format("{0,-14} {1,-10} {2,8}  {3,-5} {4,-8} {5,-4} {6,-6}  {7}",
                "id", "中文名", "时长", "姿态", "部位", "循环", "可打断", "情绪标签"));
            sb.AppendLine("------------------------------------------------------------------------------------------");

            foreach (var e in manifest.Actions)
            {
                if (e == null || string.IsNullOrEmpty(e.Id)) continue;

                if (ActionMetadata.TryFill(e)) withMeta++; else noMeta++;

                AnimationClip clip;
                if (!string.IsNullOrEmpty(e.ClipName) && byName.TryGetValue(e.ClipName, out clip) && clip != null)
                    e.Duration = clip.length;
                else
                    noClip++;

                sb.AppendLine(string.Format("{0,-14} {1,-10} {2,7:F2}s  {3,-5} {4,-8} {5,-4} {6,-6}  {7}",
                    e.Id,
                    string.IsNullOrEmpty(e.Label) ? "-" : e.Label,
                    e.Duration,
                    ActionMetadata.PostureCn(e.Posture),
                    ActionMetadata.BodyPartCn(e.BodyPart),
                    e.Loop ? "是" : "否",
                    e.Interruptible ? "是" : "否",
                    string.IsNullOrEmpty(e.EmotionTags) ? "-" : e.EmotionTags));
            }

            sb.AppendLine();
            sb.AppendLine("已标注 " + withMeta + " 个；未标注 " + noMeta + " 个；找不到 clip " + noClip + " 个");
            if (noMeta > 0)
                sb.AppendLine("⚠ 未标注的 id 请补到 Assets/Scripts/Motion/ActionMetadata.cs");

            EditorUtility.SetDirty(manifest);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(go.scene);
            Debug.Log(sb.ToString());
        }

        /// <summary>clip 名 → clip（只扫 Mixamo 目录下的 FBX）</summary>
        static Dictionary<string, AnimationClip> LoadClipsByFileName()
        {
            var byName = new Dictionary<string, AnimationClip>();
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { Folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                if (!path.ToLower().EndsWith(".fbx")) continue;
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null) continue;
                if (!byName.ContainsKey(clip.name)) byName[clip.name] = clip;
            }
            return byName;
        }
    }
}
#endif
