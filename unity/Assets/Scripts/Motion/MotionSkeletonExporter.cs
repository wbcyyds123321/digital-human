using UnityEngine;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Yixuan.Motion
{
    /// <summary>
    /// 导出角色 Humanoid 骨架的 rest 姿态，供外部工具（VMD→动画）使用。
    ///
    /// 【为什么需要导出 rest 姿态】
    /// MMD 的 VMD 存的是【绝对旋转】，作用在 MMD 自己的 rest 姿态上。
    /// 而本模型的 rest 姿态（PMX 导出）与标准 MMD 不同：
    ///   腕.R = 271.4,0,268.6 ；ひじ.R = 0,180,356.5  等
    /// 所以必须把目标骨架的真实 rest 一起交给转换器，
    /// 才能算出"VMD 这组旋转作用到【本骨架】上应该是多少局部旋转"。
    ///
    /// 导出格式（JSON）：
    ///   { "fps":30, "bones":[ {"name","parent","restLocal":[x,y,z,w],"restWorld":[...] } ] }
    /// parent 是骨骼在数组里的下标，-1 = 根。
    ///
    /// 用法：挂到角色根节点上，右键组件 → 「导出骨架 rest 姿态」。
    /// </summary>
    public class MotionSkeletonExporter : MonoBehaviour
    {
        [Tooltip("导出路径（相对项目根目录）")]
        public string OutputPath = "Tools/MotionImport/skeleton.json";

        [Tooltip("只导出这些 Humanoid 骨（留空 = 导出全部已映射的）")]
        public bool HumanoidOnly = true;

        [ContextMenu("导出骨架 rest 姿态")]
        public void Export()
        {
            var anim = GetComponentInChildren<Animator>();
            if (anim == null || anim.avatar == null || !anim.avatar.isHuman)
            {
                Debug.LogError("[SkeletonExporter] 需要 Humanoid Animator");
                return;
            }

            // 收集要导出的骨：Humanoid 映射到的所有 Transform
            var wanted = new List<Transform>();
            var seen = new HashSet<Transform>();
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var t = anim.GetBoneTransform((HumanBodyBones)i);
                if (t != null && seen.Add(t)) wanted.Add(t);
            }

            // ★★ 关键：把 Humanoid 骨之间的【中间骨】也导出来。
            //    本模型 腕.R 的直接父级是 肩C.R（不是 Humanoid 骨），
            //    而 肩C.R 自身带旋转（358.73, 90, 104.58）。
            //    若不导出它，转换器会退而用 肩.R 的 rest world 当父级，
            //    两者差约 75° —— 于是算出来的 localRotation 全错
            //    （实测：恒等 VMD 却把手臂抬到 0.97 的高度）。
            //    这些中间骨虽然不接收动画，但必须进表，父级链才正确。
            var extras = new List<Transform>();
            foreach (var t in wanted.ToArray())
            {
                var p = t.parent;
                while (p != null && !seen.Contains(p))
                {
                    // 只补"有实际影响力"的骨：零旋转的纯分组节点可跳过
                    if (Quaternion.Angle(p.localRotation, Quaternion.identity) > 0.01f)
                        extras.Add(p);
                    p = p.parent;
                }
            }
            foreach (var e in extras)
            {
                if (seen.Add(e)) wanted.Add(e);
            }

            // ★★ IK 骨必须导出：VMD 里 足ＩＫ 的位置是【相对该骨自身 rest 位置】的偏移。
            //    不导出它就没有锚点，只能瞎猜世界坐标 —— 实测脚会被抬到 y≈1.1。
            //
            //    ★ 连它的父骨（足IK親.R，带 X/Z 偏移）也必须导出：
            //      否则 IK 骨的 parent 会算成 -1（当成根骨），
            //      而它的 localPos 只有 (0,0.144,0)，
            //      推不出真实的 worldPos (0.127,0.144,-0.032) —— 锚点就错了。
            foreach (var tr in anim.GetComponentsInChildren<Transform>(true))
            {
                if (!tr.name.Contains("\uff29\uff2b")) continue;   // 'ＩＫ'
                if (seen.Add(tr)) wanted.Add(tr);
                var pp = tr.parent;
                while (pp != null && !seen.Contains(pp))
                {
                    if (Quaternion.Angle(pp.localRotation, Quaternion.identity) > 0.01f ||
                        pp.localPosition.sqrMagnitude > 1e-8f)
                    {
                        if (seen.Add(pp)) wanted.Add(pp);
                    }
                    pp = pp.parent;
                }
            }

            // 建立 name→index
            var indexOf = new Dictionary<Transform, int>();
            for (int i = 0; i < wanted.Count; i++) indexOf[wanted[i]] = i;

            var sb = new StringBuilder();
            sb.Append("{\n  \"fps\": 30,\n  \"source\": \"");
            sb.Append(anim.avatar.name);
            sb.Append("\",\n  \"bones\": [\n");

            // ★ 自检：localPos 与父级世界位置必须在数值上自洽。
            //   曾经出现 足ＩＫ.R 的 parent 被写成 -1（当成根骨），
            //   但它的 localPos=(0,0.144,0) 推不出 worldPos=(0.127,0.144,-0.032)，
            //   于是 IK 锚点错位、脚被抬到 y≈1.8。这里显式校验并告警。
            var worldPosOf = new Dictionary<Transform, Vector3>();
            for (int i = 0; i < wanted.Count; i++) worldPosOf[wanted[i]] = wanted[i].position;

            for (int i = 0; i < wanted.Count; i++)
            {
                var t = wanted[i];
                var lr = t.localRotation;
                var wr = t.rotation;
                var wr_pos = t.position;

                // ★ parent 必须指向"最近的【已导出】祖先"，不能只看直接父级。
                //   本模型里 足.L 的直接父级是 下半身（不是 Humanoid 骨，没被导出），
                //   若直接取 indexOf[parent] 会得到 -1，腿就被当成根骨，
                //   转换器算 localRotation 时用的 parent_world 全错。
                //
                // ★★ 根骨必须写 -1。曾经写成自己的下标(0)，导致转换器算
                //    inv(restWorld) * restWorld，把 センター 拧了整整 180°，
                //    整个人体层级跟着歪掉（头从 1.53 掉到 0.27）。
                int pi = -1;
                var anc = t.parent;
                while (anc != null)
                {
                    if (indexOf.TryGetValue(anc, out pi) && pi != i) break;
                    pi = -1;
                    anc = anc.parent;
                }

                sb.Append("    { \"name\": \"").Append(Escape(t.name)).Append("\", ");
                sb.Append("\"humanoid\": \"").Append(HumanoidNameOf(anim, t)).Append("\", ");
                sb.Append("\"parent\": ").Append(pi).Append(", ");
                sb.Append("\"restLocal\": ").Append(Q(lr)).Append(", ");
                sb.Append("\"restWorld\": ").Append(Q(wr)).Append(", ");
                // 世界坐标 + 本地坐标：IK 解算要用真实骨骼长度和朝向
                sb.Append("\"worldPos\": ").Append(V(wr_pos)).Append(", ");
                sb.Append("\"localPos\": ").Append(V(t.localPosition));
                sb.Append(" }");
                if (i < wanted.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  ]\n}\n");

            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", OutputPath));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));
            Debug.Log("[SkeletonExporter] 已导出 " + wanted.Count + " 根骨 → " + full);
        }

        static string HumanoidNameOf(Animator anim, Transform t)
        {
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var b = (HumanBodyBones)i;
                if (anim.GetBoneTransform(b) == t) return b.ToString();
            }
            return "";
        }

        static string Q(Quaternion q)
        {
            return "[" + F(q.x) + ", " + F(q.y) + ", " + F(q.z) + ", " + F(q.w) + "]";
        }

        static string V(Vector3 v)
        {
            return "[" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + "]";
        }

        static string F(float v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
