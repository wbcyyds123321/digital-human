#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.Animations;
using UnityEngine.Playables;
using System.Collections.Generic;

namespace Yixuan.Motion
{
    /// <summary>
    /// 动作验证工具 —— 把 AnimationClip 真正作用到骨骼上并报告关键点坐标。
    ///
    /// 【为什么不用 SampleAnimation / PlayableGraph.Evaluate】
    /// 实测这两条路在编辑模式下都**不可靠**：
    ///   - AnimationClip.SampleAnimation() 读到的是陈旧姿态，曲线像是没生效
    ///   - PlayableGraph.Create + Evaluate() 同样不驱动骨骼
    /// 唯一可靠的办法是在 **Play 模式**下创建 graph 并让它走 PlayerLoop。
    /// 所以本工具提供"手动 apply 某一帧"的能力：
    ///   自己求值每条曲线 → 直接写 bone.localRotation。
    /// 这样不依赖任何 Unity 内部调度，结果可复现。
    /// </summary>
    public static class MotionVerifier
    {
        /// <summary>把 clip 在指定时间点"手动应用"到骨骼（自算曲线，不靠引擎调度）</summary>
        public static void ApplyAt(GameObject root, AnimationClip clip, float time)
        {
            var binds = AnimationUtility.GetCurveBindings(clip);
            var acc = new Dictionary<string, float[]>();

            foreach (var b in binds)
            {
                if (b.propertyName != "localRotation.x" && b.propertyName != "localRotation.y" &&
                    b.propertyName != "localRotation.z" && b.propertyName != "localRotation.w") continue;
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                if (curve == null) continue;

                float[] q;
                if (!acc.TryGetValue(b.path, out q)) { q = new float[] { 0, 0, 0, 1 }; acc[b.path] = q; }
                float v = curve.Evaluate(time);
                if (b.propertyName.EndsWith(".x")) q[0] = v;
                else if (b.propertyName.EndsWith(".y")) q[1] = v;
                else if (b.propertyName.EndsWith(".z")) q[2] = v;
                else q[3] = v;
            }

            foreach (var kv in acc)
            {
                var tr = string.IsNullOrEmpty(kv.Key) ? root.transform : root.transform.Find(kv.Key);
                if (tr == null) continue;
                tr.localRotation = new Quaternion(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]);
            }
        }

        /// <summary>恢复到 Avatar 绑定姿态</summary>
        public static int RestoreBindPose(GameObject root)
        {
            var anim = root.GetComponentInChildren<Animator>();
            if (anim == null || anim.avatar == null) return 0;
            var byName = new Dictionary<string, Transform>();
            foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(tr.name)) byName[tr.name] = tr;

            int n = 0;
            foreach (var s in anim.avatar.humanDescription.skeleton)
            {
                Transform tr;
                if (!byName.TryGetValue(s.name, out tr)) continue;
                tr.localRotation = s.rotation;
                tr.localPosition = s.position;
                n++;
            }
            return n;
        }

        /// <summary>采样一条 clip，输出若干关键骨的位置（用于肉眼判断动作是否合理）</summary>
        public static string SampleReport(GameObject root, AnimationClip clip,
                                          float[] times, string[] boneLabels, HumanBodyBones[] bones)
        {
            var anim = root.GetComponentInChildren<Animator>();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("clip=" + clip.name + "  length=" + clip.length.ToString("F2") + "s");

            var trs = new Transform[bones.Length];
            for (int i = 0; i < bones.Length; i++) trs[i] = anim.GetBoneTransform(bones[i]);

            sb.Append("time  ");
            foreach (var l in boneLabels) sb.Append(l.PadLeft(20));
            sb.AppendLine();

            foreach (var t in times)
            {
                RestoreBindPose(root);
                ApplyAt(root, clip, t);
                sb.Append(t.ToString("F2").PadLeft(5) + " ");
                for (int i = 0; i < trs.Length; i++)
                {
                    var p = trs[i] != null ? trs[i].position : Vector3.zero;
                    sb.Append(("(" + p.x.ToString("F2") + "," + p.y.ToString("F2") + "," + p.z.ToString("F2") + ")").PadLeft(20));
                }
                sb.AppendLine();
            }
            RestoreBindPose(root);
            return sb.ToString();
        }
    }
}
#endif
