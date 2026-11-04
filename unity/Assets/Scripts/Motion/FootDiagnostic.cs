#if UNITY_EDITOR
using UnityEngine;
using System.Collections;
using System.IO;
using System.Text;

namespace Yixuan.Motion
{
    /// <summary>
    /// 脚部专项导出：把每个姿势的**左脚区域**单独切出来导出成 OBJ，
    /// 便于离线高分辨率渲染、逐个对比畸形出现在哪个姿势。
    ///
    /// 用法（Play 模式）：UnityLITF/仪玄/⑦ 导出脚部专项对比
    /// </summary>
    public class FootDiagnostic : MonoBehaviour
    {
        public static string OutDir = System.IO.Path.Combine(Application.dataPath, "..", "Tools", "_verify", "foot");
        public const float FootYMax = 0.40f;   // 脚部区域的 Y 上限

        public void Run()
        {
            StartCoroutine(CoRun());
        }

        IEnumerator CoRun()
        {
            Directory.CreateDirectory(OutDir);
            var yc = GetComponent<Yixuan.Core.YixuanCharacter>();
            var log = new StringBuilder();
            log.AppendLine("========== 脚部专项导出 ==========");
            log.AppendLine("标签            脚部顶点    Y范围            X范围            与绑定差异");

            // 绑定姿态基准
            var mv = System.Type.GetType("Yixuan.Motion.MotionVerifier, Assembly-CSharp");
            var restore = mv.GetMethod("RestoreBindPose",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            Vector3[] bindVerts = null;

            string[] tags = { "__BIND__", "idle", "sit", "walk", "jog", "kneel" };
            foreach (var tag in tags)
            {
                if (tag == "__BIND__")
                {
                    if (restore != null) restore.Invoke(null, new object[] { gameObject });
                }
                else if (yc != null && restore != null)
                {
                    restore.Invoke(null, new object[] { gameObject });
                    yc.PlayGesture(tag);
                }

                yield return new WaitForSeconds(1.3f);
                yield return new WaitForEndOfFrame();

                var verts = ExtractFoot(out string info, out Vector3[] allFoot);
                if (tag == "__BIND__") bindVerts = allFoot;

                string diff = "—";
                if (bindVerts != null && allFoot != null && bindVerts.Length == allFoot.Length)
                {
                    // 找两个姿势间位移最大的脚部顶点
                    float maxD = 0f;
                    for (int i = 0; i < allFoot.Length; i++)
                    {
                        float d = Vector3.Distance(allFoot[i], bindVerts[i]);
                        if (d > maxD) maxD = d;
                    }
                    diff = "最大顶点位移 " + maxD.ToString("F4") + "m";
                }

                WriteObj(tag, verts);
                log.AppendLine(tag.Replace("__", "").PadRight(14) + info + "   " + diff);
            }

            Debug.Log(log.ToString());
        }

        struct FootMesh
        {
            public Vector3[] All;      // 全部顶点（世界）
            public int[] Tris;         // 全部三角
            public bool[] IsFoot;      // 每个顶点是否属于脚
        }

        FootMesh ExtractFoot(out string info, out Vector3[] footVerts)
        {
            var smr = GetComponentInChildren<SkinnedMeshRenderer>();
            var m = new Mesh();
            // ★ 必须用 useScale 重载
            try { smr.BakeMesh(m, true); } catch { smr.BakeMesh(m); }

            var vs = m.vertices;
            var subs = new System.Collections.Generic.List<int>();
            for (int s = 0; s < m.subMeshCount; s++) subs.AddRange(m.GetTriangles(s));

            bool[] isFoot = new bool[vs.Length];
            var list = new System.Collections.Generic.List<Vector3>();
            foreach (var t in subs)
            {
                if (isFoot[t]) continue;
                if (vs[t].y < FootYMax)
                {
                    isFoot[t] = true;
                    list.Add(vs[t]);
                }
            }

            float lo = 9999f, hi = -9999f, xlo = 9999f, xhi = -9999f, zlo = 9999f, zhi = -9999f;
            foreach (var v in list)
            {
                if (v.y < lo) lo = v.y;
                if (v.y > hi) hi = v.y;
                if (v.x < xlo) xlo = v.x;
                if (v.x > xhi) xhi = v.x;
                if (v.z < zlo) zlo = v.z;
                if (v.z > zhi) zhi = v.z;
            }

            info = list.Count.ToString().PadLeft(6) + "   " +
                   lo.ToString("F3") + "~" + hi.ToString("F3") + "   " +
                   xlo.ToString("F3") + "~" + xhi.ToString("F3") +
                   "   Z " + zlo.ToString("F3") + "~" + zhi.ToString("F3");

            var fm = new FootMesh();
            fm.All = vs; fm.Tris = subs.ToArray(); fm.IsFoot = isFoot;
            footVerts = list.ToArray();
            Object.Destroy(m);
            return fm;
        }

        void WriteObj(string tag, FootMesh fm)
        {
            var sb = new StringBuilder();
            // 重映射索引
            var remap = new int[fm.All.Length];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            int n = 0;
            for (int i = 0; i < fm.All.Length; i++)
                if (fm.IsFoot[i]) { remap[i] = n++; sb.AppendLine("v " + (-fm.All[i].x).ToString("F5") + " " + fm.All[i].y.ToString("F5") + " " + fm.All[i].z.ToString("F5")); }

            for (int i = 0; i + 2 < fm.Tris.Length; i += 3)
            {
                int a = fm.Tris[i], b = fm.Tris[i + 1], c = fm.Tris[i + 2];
                if (remap[a] < 0 || remap[b] < 0 || remap[c] < 0) continue;
                sb.AppendLine("f " + (remap[a] + 1) + " " + (remap[b] + 1) + " " + (remap[c] + 1));
            }

            File.WriteAllText(Path.Combine(OutDir, "F_" + tag.Replace("__", "") + ".obj"), sb.ToString());
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem("UnityLITF/仪玄/⑦ 导出脚部专项对比（Play 模式）")]
        public static void MenuRun()
        {
            var go = UnityEditor.Selection.activeGameObject;
            if (go == null) go = GameObject.Find("Yixuan_Test");
            if (go == null) { Debug.LogError("[FootDiagnostic] 找不到角色"); return; }
            if (!Application.isPlaying) { Debug.LogWarning("[FootDiagnostic] 请在 Play 模式运行"); return; }
            var d = go.GetComponent<FootDiagnostic>();
            if (d == null) d = go.AddComponent<FootDiagnostic>();
            d.Run();
            Debug.Log("[FootDiagnostic] 已启动，输出到 " + OutDir);
        }
#endif
    }
}
#endif
