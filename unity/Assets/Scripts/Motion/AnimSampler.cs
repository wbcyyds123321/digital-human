#if UNITY_EDITOR
using UnityEngine;
using System.Collections;

namespace Yixuan.Motion
{
    /// <summary>
    /// 跨帧采样动画 —— 验证"动画到底有没有在推进"。
    ///
    /// 【为什么不能用空转循环】
    /// 在编辑器/游戏主线程上写 while(Time.realtimeSinceStartup - t0 &lt; 0.2f){} 会：
    ///   ① 堵死主线程 → Update() 跑不了 → 时间不推进 → 采样永远同一帧
    ///   ② 严重时把整个编辑器卡死（本会话真实发生过，只能强杀 Unity）
    /// 正确做法是用协程 yield，把控制权交回引擎，下一帧继续采。
    /// </summary>
    public class AnimSampler : MonoBehaviour
    {
        public int Frames = 8;
        public float Interval = 0.12f;
        public string Label = "";

        public void Run(string label)
        {
            Label = label;
            Frames = 8;
            Interval = 0.12f;
            StartCoroutine(CoSample());
        }

        /// <summary>
        /// 等动画稳定后导出当前蒙皮网格为 OBJ（离线渲染验证用）。
        ///
        /// ★ 为什么必须跨帧等：
        ///   动画在同一次调用的同一帧里不会推进，立刻 BakeMesh 拿到的是上一帧姿势。
        ///   实测踩过：调用 PlayGesture("sit") 后立刻导出，拿到的还是站立姿势。
        /// </summary>
        public void RunExportAfter(string action, string tag, float waitSec)
        {
            StartCoroutine(CoExportAfter(action, tag, waitSec));
        }

        IEnumerator CoExportAfter(string action, string tag, float waitSec)
        {
            var yc = GetComponent<Yixuan.Core.YixuanCharacter>();
            if (yc != null && !string.IsNullOrEmpty(action)) yc.PlayGesture(action);
            yield return new WaitForSeconds(waitSec);

            var smr = GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) { Debug.LogError("[Export] 没有 SkinnedMeshRenderer"); yield break; }

            // ★ BakeMesh 的坑：
            //   无参重载在某些情况下拿到的是"未蒙皮"的网格 ——
            //   实测：骨骼位置明明已经是坐姿（足.L→ひざ.L 方向 85° 水平），
            //   但导出的网格高度和待机一样（1.725 vs 1.726），明显没跟随。
            //   改用带 SkinnedMeshRenderer.BakeMesh(mesh, useScale) 的重载，
            //   并显式等一帧，确保蒙皮已经算过。
            yield return new WaitForEndOfFrame();

            var m = new Mesh();
            try { smr.BakeMesh(m, true); }
            catch { smr.BakeMesh(m); }
            var vs = m.vertices;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < vs.Length; i++)
                sb.AppendLine("v " + (-vs[i].x).ToString("F5") + " " + vs[i].y.ToString("F5") + " " + vs[i].z.ToString("F5"));
            for (int s = 0; s < m.subMeshCount; s++)
            {
                var tris = m.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3)
                    sb.AppendLine("f " + (tris[i] + 1) + " " + (tris[i + 1] + 1) + " " + (tris[i + 2] + 1));
            }

            float lo = 9999f, hi = -9999f, zmin = 9999f, zmax = -9999f;
            for (int i = 0; i < vs.Length; i++)
            {
                if (vs[i].y < lo) lo = vs[i].y;
                if (vs[i].y > hi) hi = vs[i].y;
                if (vs[i].z < zmin) zmin = vs[i].z;
                if (vs[i].z > zmax) zmax = vs[i].z;
            }
            UnityEngine.Object.Destroy(m);

            string dir = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "Tools", "_verify");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(dir + "\\EXP_" + tag + ".obj", sb.ToString());

            Debug.Log("### EXPORT " + tag + " ###  Y " + lo.ToString("F3") + "~" + hi.ToString("F3") +
                      " (高" + (hi - lo).ToString("F3") + ")   Z " + zmin.ToString("F3") + "~" + zmax.ToString("F3") +
                      " (跨度" + (zmax - zmin).ToString("F3") + ")  → EXP_" + tag + ".obj");

            // 顺便打印腿骨状态 —— 确认这一帧到底是哪个姿势
            // ★ 骨名走中央表（仪玄 足D.L / 庄方宜 左足D 都认）
            var hip = Yixuan.Model.MmdBoneNames.Resolve("UpperLegL", transform);
            var knee = Yixuan.Model.MmdBoneNames.Resolve("LowerLegL", transform);
            var ankle = Yixuan.Model.MmdBoneNames.Resolve("FootL", transform);
            Transform ctrlHip = null, ctrlKnee = null;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "足.L" || t.name == "左足") ctrlHip = t;
                else if (t.name == "ひざ.L" || t.name == "左ひざ") ctrlKnee = t;
            }
            if (hip != null && knee != null)
            {
                Vector3 v = knee.position - hip.position;
                string s = "    腿骨D链: 髋→膝 长度=" + v.magnitude.ToString("F3") +
                           "  方向(相对向下)=" + Vector3.Angle(v.normalized, Vector3.down).ToString("F1") + "°";
                if (ankle != null)
                {
                    Vector3 v2 = ankle.position - knee.position;
                    s += "  膝→踝=" + Vector3.Angle(v2.normalized, Vector3.down).ToString("F1") + "°";
                }
                Debug.Log(s);
            }
            if (ctrlHip != null && ctrlKnee != null)
            {
                Vector3 v = ctrlKnee.position - ctrlHip.position;
                Debug.Log("    腿骨控制链: 髋→膝 方向=" + Vector3.Angle(v.normalized, Vector3.down).ToString("F1") + "°");
            }
        }

        /// <summary>逐个动作检查 GroundSnap 后脚是否贴地（跨帧，每个动作等 1.3s）</summary>
        public void RunGroundCheck(string[] ids)
        {
            StartCoroutine(CoGroundCheck(ids));
        }

        IEnumerator CoGroundCheck(string[] ids)
        {
            var toeL = Yixuan.Model.MmdBoneNames.Resolve("ToeL", transform);
            var toeR = Yixuan.Model.MmdBoneNames.Resolve("ToeR", transform);

            var yc = GetComponent<Yixuan.Core.YixuanCharacter>();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("### GroundSnap 逐动作验证 ###");
            sb.AppendLine("动作".PadRight(10) + "根节点Y    脚趾最低   判定");

            foreach (var id in ids)
            {
                if (yc != null) yc.PlayGesture(id);
                yield return new WaitForSeconds(1.3f);

                float rootY = transform.position.y;
                float lo = 99f;
                if (toeL != null) lo = Mathf.Min(lo, toeL.position.y);
                if (toeR != null) lo = Mathf.Min(lo, toeR.position.y);

                string verdict;
                if (Mathf.Abs(lo) < 0.03f) verdict = "OK 贴地";
                else if (lo > 0f) verdict = "悬空 " + lo.ToString("F3") + "m";
                else verdict = "穿地 " + (-lo).ToString("F3") + "m";

                sb.AppendLine(id.PadRight(10) + rootY.ToString("F4").PadLeft(9) +
                              lo.ToString("F4").PadLeft(11) + "   " + verdict);
            }
            sb.AppendLine("### 验证结束 ###");
            Debug.Log(sb.ToString());
        }

        IEnumerator CoSample()
        {
            var anim = GetComponentInChildren<Animator>();
            var hips = anim != null ? anim.GetBoneTransform(HumanBodyBones.Hips) : null;
            var lul = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg) : null;
            var lll = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftLowerLeg) : null;
            var lf = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftFoot) : null;
            var head = anim != null ? anim.GetBoneTransform(HumanBodyBones.Head) : null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("### AnimSampler [" + Label + "] ###");
            sb.AppendLine("  帧  髋localY  大腿倾角   膝角    左脚Y    头Y      小腿localRotW");

            for (int i = 0; i < Frames; i++)
            {
                float thigh = (lul != null && lll != null)
                    ? Vector3.Angle((lll.position - lul.position).normalized, Vector3.down) : 0f;
                float knee = (lul != null && lll != null && lf != null)
                    ? Vector3.Angle(lul.position - lll.position, lf.position - lll.position) : 0f;

                sb.AppendLine("  " + i.ToString().PadLeft(2) + "  " +
                    (hips != null ? hips.localPosition.y.ToString("F4") : "-").PadLeft(9) +
                    thigh.ToString("F1").PadLeft(9) + "°" +
                    knee.ToString("F1").PadLeft(7) + "°" +
                    (lf != null ? lf.position.y.ToString("F4") : "-").PadLeft(9) +
                    (head != null ? head.position.y.ToString("F4") : "-").PadLeft(9) +
                    (lll != null ? lll.localRotation.w.ToString("F4") : "-").PadLeft(9));

                yield return new WaitForSeconds(Interval);
            }

            sb.AppendLine("### AnimSampler 结束 [" + Label + "] ###");
            Debug.Log(sb.ToString());
        }
    }
}
#endif
