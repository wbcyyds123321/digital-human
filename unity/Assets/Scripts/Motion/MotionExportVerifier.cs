#if UNITY_EDITOR
using UnityEngine;
using System.Collections;
using System.IO;
using System.Text;

namespace Yixuan.Motion
{
    /// <summary>
    /// 一键验证：把几个动作的真实蒙皮网格导出成 OBJ，供离线渲染检查。
    ///
    /// 【为什么需要这个工具】
    /// 光看骨骼数值会被骗。踩过的两个坑：
    ///   1. 骨骼在动 ≠ 网格在动。本模型腿部有"控制骨/D骨"双链，
    ///      Unity 把 Humanoid 映射到了**零权重的控制骨**上，
    ///      所以骨骼数据完美但网格纹丝不动 → 用户看到"腿不弯"。
    ///   2. SkinnedMeshRenderer.BakeMesh(mesh) 无参重载拿到的是
    ///      **未蒙皮**的网格。必须用 BakeMesh(mesh, useScale) 重载。
    ///      用错重载时，坐姿导出和站姿一模一样（高 1.73 vs 1.73）。
    ///
    /// 【为什么要跨帧】
    /// 同一次调用里动画不会推进。必须 yield 等动画跑到位再 BakeMesh，
    /// 否则拿到的是上一帧（通常是站立）的姿势。
    ///
    /// 用法：菜单 UnityLITF/仪玄/⑥ 导出动作网格验证（Play 模式）
    /// </summary>
    public class MotionExportVerifier : MonoBehaviour
    {
        public static string OutDir = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "Tools", "_verify");

        /// <summary>要导出的动作与"等多久再导出"（秒）</summary>
        public struct Shot
        {
            public string Action;   // 语义 id：idle / sit / walk ...
            public string Tag;      // 输出文件名后缀
            public float Wait;      // 等多少秒（让动画播到稳定段）
            public Shot(string a, string t, float w) { Action = a; Tag = t; Wait = w; }
        }

        public static readonly Shot[] DefaultShots =
        {
            new Shot("",      "bind", 0.3f),   // 空 = 不播动作，用绑定姿态
            new Shot("idle",  "idle", 1.2f),
            new Shot("sit",   "sit",  1.2f),
            new Shot("walk",  "walk", 1.2f),
            new Shot("jog",   "jog",  1.2f),
            new Shot("kneel", "kneel",1.2f),
        };

        public void RunAll() { StartCoroutine(CoRun(DefaultShots)); }

        /// <summary>跑一组自定义镜头（供 AllActionsVerifier 一次性导出全部动作）</summary>
        public void RunShots(Shot[] shots) { StartCoroutine(CoRun(shots)); }

        public void RunOne(string action, string tag, float wait)
        {
            StartCoroutine(CoRun(new[] { new Shot(action, tag, wait) }));
        }

        IEnumerator CoRun(Shot[] shots)
        {
            Directory.CreateDirectory(OutDir);
            var yc = GetComponent<Yixuan.Core.YixuanCharacter>();
            var mv = System.Type.GetType("Yixuan.Motion.MotionVerifier, Assembly-CSharp");
            var restore = mv != null ? mv.GetMethod("RestoreBindPose",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) : null;

            var log = new StringBuilder();
            log.AppendLine("========== 动作网格导出 ==========");
            log.AppendLine("标签        高度      Y范围            腿(髋→膝)方向  膝→踝方向");

            foreach (var s in shots)
            {
                // 空 action = 绑定姿态
                if (string.IsNullOrEmpty(s.Action))
                {
                    if (restore != null) restore.Invoke(null, new object[] { gameObject });
                }
                else if (yc != null)
                {
                    yc.PlayGesture(s.Action);
                }

                // ★ 跨帧等动画跑到位 —— 同帧 BakeMesh 拿到的是旧姿势
                yield return new WaitForSeconds(s.Wait);
                yield return new WaitForEndOfFrame();

                string info = ExportOne(s.Tag);
                log.AppendLine(info);
            }

            Debug.Log(log.ToString());
        }

        /// <summary>导出当前蒙皮网格；返回一行摘要</summary>
        string ExportOne(string tag)
        {
            var smr = GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) return tag + "  ✘ 没有 SkinnedMeshRenderer";

            var m = new Mesh();
            // ★★ 必须用带 useScale 的重载，否则拿到未蒙皮的网格
            try { smr.BakeMesh(m, true); }
            catch { smr.BakeMesh(m); }

            var vs = m.vertices;
            var sb = new StringBuilder(vs.Length * 24);
            sb.AppendLine("# " + tag);
            for (int i = 0; i < vs.Length; i++)
                sb.AppendLine("v " + (-vs[i].x).ToString("F5") + " " +
                                    vs[i].y.ToString("F5") + " " +
                                    vs[i].z.ToString("F5"));
            for (int s = 0; s < m.subMeshCount; s++)
            {
                var tris = m.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3)
                    sb.AppendLine("f " + (tris[i] + 1) + " " + (tris[i + 1] + 1) + " " + (tris[i + 2] + 1));
            }

            float lo = 9999f, hi = -9999f;
            for (int i = 0; i < vs.Length; i++)
            {
                if (vs[i].y < lo) lo = vs[i].y;
                if (vs[i].y > hi) hi = vs[i].y;
            }
            Object.Destroy(m);

            File.WriteAllText(Path.Combine(OutDir, "V_" + tag + ".obj"), sb.ToString());

            // 顺带量腿骨方向，确认这一帧的姿势（★ 骨名走中央表）
            string legInfo = "";
            var h = Yixuan.Model.MmdBoneNames.Resolve("UpperLegL", transform);
            var k = Yixuan.Model.MmdBoneNames.Resolve("LowerLegL", transform);
            var a = Yixuan.Model.MmdBoneNames.Resolve("FootL", transform);
            if (h != null && k != null)
            {
                float d1 = Vector3.Angle((k.position - h.position).normalized, Vector3.down);
                legInfo = d1.ToString("F0").PadLeft(6) + "°";
                if (a != null)
                    legInfo += "      " + Vector3.Angle((a.position - k.position).normalized, Vector3.down).ToString("F0").PadLeft(4) + "°";
            }

            return tag.PadRight(10) + (hi - lo).ToString("F3").PadLeft(7) + "   " +
                   lo.ToString("F3") + "~" + hi.ToString("F3") + "   " + legInfo;
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem("UnityLITF/仪玄/⑥ 导出动作网格验证（Play 模式）")]
        public static void MenuRun()
        {
            var go = UnityEditor.Selection.activeGameObject;
            if (go == null) go = GameObject.Find("Yixuan_Test");
            if (go == null) { Debug.LogError("[ExportVerifier] 找不到角色"); return; }

            if (!Application.isPlaying)
            {
                Debug.LogWarning("[ExportVerifier] 请在 Play 模式下运行 —— 编辑模式动画不推进");
                return;
            }

            var v = go.GetComponent<MotionExportVerifier>();
            if (v == null) v = go.AddComponent<MotionExportVerifier>();
            v.RunAll();
            Debug.Log("[ExportVerifier] 已启动导出（约 7 秒），输出到 " + OutDir);
        }
#endif
    }
}
#endif
