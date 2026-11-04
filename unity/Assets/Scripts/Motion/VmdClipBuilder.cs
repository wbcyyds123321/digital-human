#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Yixuan.Motion
{
    /// <summary>
    /// 把 vmd_to_anim.py 产出的动画 JSON 变成 Unity 原生 AnimationClip(.anim)。
    ///
    /// 【输入 JSON】
    ///   { "fps":30, "duration":3.2,
    ///     "bones":[ {"name":"RightLowerArm", "curves":[[frame,x,y,z,w], ...] } ] }
    ///
    /// 【关键设计：用 EditorCurveBinding 直接绑骨骼引用】
    /// 不写 "路径/骨名" 字符串。因为本模型是 MMD 日文命名（腕.R / ひじ.R / 手首.R），
    /// Humanoid 骨名并不是 Transform 名，硬编码路径一旦层级变动就全废。
    /// 这里先用 Animator 的 Humanoid 映射把骨名解析成 Transform，
    /// 再直接绑定该 Transform —— 层级怎么变都不会错。
    ///
    /// 用法：选中角色 → 菜单 UnityLITF → 仪玄 → 从 VMD JSON 生成动画
    /// </summary>
    public static class VmdClipBuilder
    {
        [System.Serializable] class ClipJson
        {
            public float fps = 30f;
            public float duration = 0f;
            public int frameCount = 0;
            public BoneJson[] bones;
        }

        [System.Serializable] class BoneJson
        {
            public string name;
            public string vmdName;
            public int frameCount;
            /// <summary>
            /// 摊平的帧数据：[f,x,y,z,w, f,x,y,z,w, ...]
            /// ★ 不能用 float[][]：Unity 的 JsonUtility 不支持交错数组，
            ///   实测会让整个 bones 解析为空、曲线一条都绑不上。
            /// </summary>
            public float[] flat;
        }

        // ------------------------------------------------------------ 骨名解析

        /// <summary>Humanoid 骨名 → 本模型的 Transform</summary>
        public static Dictionary<string, Transform> ResolveBones(GameObject root)
        {
            var map = new Dictionary<string, Transform>();
            var anim = root.GetComponentInChildren<Animator>();
            if (anim == null || anim.avatar == null || !anim.avatar.isHuman)
            {
                Debug.LogError("[VmdClipBuilder] 角色上没有可用的 Humanoid Animator");
                return map;
            }
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var b = (HumanBodyBones)i;
                var t = anim.GetBoneTransform(b);
                if (t != null) map[b.ToString()] = t;
            }
            return map;
        }

        /// <summary>相对角色根的层级路径（用于 EditorCurveBinding.path）</summary>
        static string RelativePath(Transform root, Transform bone)
        {
            if (bone == root) return "";
            var stack = new List<string>();
            var cur = bone;
            while (cur != null && cur != root)
            {
                stack.Add(cur.name);
                cur = cur.parent;
            }
            stack.Reverse();
            return string.Join("/", stack.ToArray());
        }

        // ------------------------------------------------------------ 构建

        public static AnimationClip Build(GameObject characterRoot, string jsonPath,
                                          string clipName, bool loop = true)
        {
            var data = JsonUtility.FromJson<ClipJson>(File.ReadAllText(jsonPath));
            if (data == null || data.bones == null || data.bones.Length == 0)
            {
                Debug.LogError("[VmdClipBuilder] JSON 解析失败或没有骨骼: " + jsonPath);
                return null;
            }

            var boneMap = ResolveBones(characterRoot);
            if (boneMap.Count == 0) return null;

            float fps = data.fps > 0f ? data.fps : 30f;
            var clip = new AnimationClip { name = clipName, frameRate = fps };
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            int bound = 0, missing = 0;
            float maxTime = 0f;
            var missingNames = new List<string>();

            foreach (var b in data.bones)
            {
                if (b == null || string.IsNullOrEmpty(b.name)) continue;
                Transform bone;
                if (!boneMap.TryGetValue(b.name, out bone) || bone == null)
                {
                    missing++;
                    if (missingNames.Count < 12) missingNames.Add(b.name);
                    continue;
                }
                if (b.flat == null || b.flat.Length < 10) continue;

                string path = RelativePath(characterRoot.transform, bone);
                int n = b.flat.Length / 5;
                var kx = new Keyframe[n];
                var ky = new Keyframe[n];
                var kz = new Keyframe[n];
                var kw = new Keyframe[n];
                float pxv = 0f, pyv = 0f, pzv = 0f, pwv = 0f;
                int m = 0;

                for (int i = 0; i < n; i++)
                {
                    int o = i * 5;
                    float t = b.flat[o] / fps;
                    float x = b.flat[o + 1], y = b.flat[o + 2], z = b.flat[o + 3], w = b.flat[o + 4];

                    // 符号连续化：四元数 q 与 -q 同一姿态，但插值会走远路
                    if (m > 0 && (x * pxv + y * pyv + z * pzv + w * pwv) < 0f)
                    { x = -x; y = -y; z = -z; w = -w; }
                    pxv = x; pyv = y; pzv = z; pwv = w;

                    kx[m] = new Keyframe(t, x);
                    ky[m] = new Keyframe(t, y);
                    kz[m] = new Keyframe(t, z);
                    kw[m] = new Keyframe(t, w);
                    if (t > maxTime) maxTime = t;
                    m++;
                }
                if (m == 0) continue;

                SetCurve(clip, path, "localRotation.x", kx);
                SetCurve(clip, path, "localRotation.y", ky);
                SetCurve(clip, path, "localRotation.z", kz);
                SetCurve(clip, path, "localRotation.w", kw);
                bound++;
            }

            Debug.Log("[VmdClipBuilder] " + clipName + "：绑定 " + bound + " 根骨；" +
                      "未匹配 " + missing + (missing > 0 ? "（" + string.Join(",", missingNames.ToArray()) + "）" : "") +
                      "；时长 " + maxTime.ToString("F2") + "s / " + Mathf.RoundToInt(maxTime * fps) + " 帧");
            return clip;
        }

        static void SetCurve(AnimationClip clip, string path, string prop, Keyframe[] keys)
        {
            var binding = EditorCurveBinding.FloatCurve(path, typeof(Transform), prop);
            AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(keys));
        }

        // ------------------------------------------------------------ 菜单

        [MenuItem("UnityLITF/仪玄/从 VMD JSON 生成 AnimationClip")]
        public static void BuildFromJsonMenu()
        {
            var go = Selection.activeGameObject;
            if (go == null) { Debug.LogError("请先在层级里选中角色根节点"); return; }

            string json = EditorUtility.OpenFilePanel("选择 vmd_to_anim.py 产出的 JSON", "", "json");
            if (string.IsNullOrEmpty(json)) return;

            string name = Path.GetFileNameWithoutExtension(json);
            var clip = Build(go, json, name);
            if (clip == null) return;
            SaveClip(clip, name);
        }

        /// <summary>批量：把 Assets/Animations/VMD/ 下所有 *.anim.json 转成 AnimationClip</summary>
        [MenuItem("UnityLITF/仪玄/批量导入 VMD JSON（Assets/Animations/VMD）")]
        public static void BatchBuildMenu()
        {
            var go = Selection.activeGameObject;
            if (go == null) { Debug.LogError("请先在层级里选中角色根节点"); return; }

            const string srcDir = "Assets/Animations/VMD";
            if (!AssetDatabase.IsValidFolder(srcDir))
            {
                Debug.LogError("没有 " + srcDir + " 目录（应放 vmd_to_anim.py 产出的 *.anim.json）");
                return;
            }
            var files = Directory.GetFiles(srcDir, "*.anim.json", SearchOption.AllDirectories);
            int ok = 0;
            foreach (var f in files)
            {
                string n = Path.GetFileName(f);
                n = n.Substring(0, n.Length - ".anim.json".Length);
                var clip = Build(go, f, n);
                if (clip != null) { SaveClip(clip, n); ok++; }
            }
            AssetDatabase.Refresh();
            Debug.Log("[VmdClipBuilder] 批量完成：" + ok + " / " + files.Length + " 个");
        }

        static void SaveClip(AnimationClip clip, string name)
        {
            const string dir = "Assets/Animations/Clips";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets/Animations", "Clips");
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(dir + "/" + name + ".anim");
            AssetDatabase.CreateAsset(clip, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorGUIUtility.PingObject(clip);
            Debug.Log("[VmdClipBuilder] 已生成: " + assetPath);
        }
    }
}
#endif
