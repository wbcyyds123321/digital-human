#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

namespace Yixuan.Motion
{
    /// <summary>
    /// 把 Mixamo 的 FBX 批量设成 Humanoid，并把 clip 改成有意义的名字。
    ///
    /// 【为什么要改 clip 名】
    /// Mixamo 导出的 FBX 里，AnimationClip 一律叫 **"mixamo.com"**（15 个全同名）。
    /// 用它做动画索引会全部撞车，所以必须按文件名改成 "Idle" / "Walking" 之类。
    ///
    /// 【为什么设 Humanoid 就能重定向】
    /// Mixamo 骨架（mixamorig:Hips…）和仪玄的 VRM 骨架（センター/上半身/腕.R）
    /// 完全不同，但两边的 Avatar 都是 Humanoid，
    /// Unity 会按"人体语义"（Hips/Spine/LeftUpperArm…）做骨骼重定向。
    /// 所以这条路**不需要手写任何骨骼数学**。
    ///
    /// 用法：菜单 UnityLITF/仪玄/① 导入 Mixamo 动作（Humanoid + 改名）
    /// </summary>
    public static class MixamoRigSetup
    {
        const string Folder = "Assets/Animations/Mixamo";

        [MenuItem("UnityLITF/仪玄/① 导入 Mixamo 动作（Humanoid + 改名）")]
        public static void SetupAll()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                Debug.LogError("[MixamoRig] 没有目录: " + Folder);
                return;
            }

            var guids = AssetDatabase.FindAssets("t:Model", new[] { Folder });
            var report = new List<string>();
            int renamed = 0;

            foreach (var g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                if (!path.ToLower().EndsWith(".fbx")) continue;

                string baseName = Path.GetFileNameWithoutExtension(path);
                var imp = AssetImporter.GetAtPath(path) as ModelImporter;
                if (imp == null) continue;

                bool changed = false;

                // ① Rig → Humanoid
                if (imp.animationType != ModelImporterAnimationType.Human)
                {
                    imp.animationType = ModelImporterAnimationType.Human;
                    changed = true;
                }
                if (!imp.autoGenerateAvatarMappingIfUnspecified)
                {
                    imp.autoGenerateAvatarMappingIfUnspecified = true;
                    changed = true;
                }
                if (!imp.importAnimation)
                {
                    imp.importAnimation = true;
                    changed = true;
                }

                // ② 把 clip 名从 "mixamo.com" 改成文件名
                var defaults = imp.defaultClipAnimations;

                if (defaults != null && defaults.Length > 0)
                {
                    bool needRename = false;
                    foreach (var d in defaults)
                    {
                        if (d.name != baseName) { needRename = true; break; }
                    }
                    if (needRename)
                    {
                        for (int i = 0; i < defaults.Length; i++)
                        {
                            defaults[i].name = (defaults.Length > 1)
                                ? baseName + "_" + i
                                : baseName;
                        }
                        imp.clipAnimations = defaults;
                        changed = true;
                        renamed++;
                    }
                }

                if (changed)
                {
                    imp.SaveAndReimport();
                }
                report.Add(string.Format("  {0,-26} {1}", baseName,
                    changed ? "已处理" : "本来就对"));
            }

            AssetDatabase.Refresh();

            // ③ 验证：Avatar 有效性 + clip 名
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========== Mixamo 导入结果 ==========");
            sb.AppendLine(string.Join("\n", report.ToArray()));
            sb.AppendLine();
            sb.AppendLine("--- Avatar / Clip 验证 ---");

            int okA = 0, badA = 0;
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { Folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                if (!path.ToLower().EndsWith(".fbx")) continue;
                string name = Path.GetFileNameWithoutExtension(path);

                var av = AssetDatabase.LoadAssetAtPath<Avatar>(path);
                bool avOk = av != null && av.isValid && av.isHuman;
                if (avOk) okA++; else badA++;

                var names = new List<string>();
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    var c = o as AnimationClip;
                    if (c != null && !c.name.StartsWith("__preview__"))
                        names.Add(c.name + "(" + c.length.ToString("F2") + "s)");
                }
                sb.AppendLine(string.Format("  {0} {1,-26} {2}",
                    avOk ? "✔" : "✘", name, string.Join(", ", names.ToArray())));
            }
            sb.AppendLine();
            sb.AppendLine("Avatar 有效 " + okA + " / 无效 " + badA);
            sb.AppendLine("clip 改名 " + renamed + " 个");

            Debug.Log(sb.ToString());
        }

        /// <summary>只列出，不改动（调试用）</summary>
        [MenuItem("UnityLITF/仪玄/列出 Mixamo 动作")]
        public static void ListClips()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("===== " + Folder + " =====");
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { Folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                if (!path.ToLower().EndsWith(".fbx")) continue;
                var names = new List<string>();
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    var c = o as AnimationClip;
                    if (c != null && !c.name.StartsWith("__preview__"))
                        names.Add(c.name + " " + c.length.ToString("F2") + "s");
                }
                sb.AppendLine("  " + Path.GetFileNameWithoutExtension(path).PadRight(26) +
                              " → " + (names.Count > 0 ? string.Join(", ", names.ToArray()) : "(无 clip)"));
            }
            Debug.Log(sb.ToString());
        }
    }
}
#endif
