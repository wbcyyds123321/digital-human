#if UNITY_EDITOR
// ============================================================================
//  ArmTwistRebuildTest —— 可靠路线的 twist 扫描
//
//  【为什么不热换 Avatar】
//  上一轮试过 `Animator.avatar = newAvatar` 做扫描，结果**连肩骨的位置都在变**
//  （−0.073 → +0.018），而肩骨不该受手臂 twist 影响
//  → 说明热换 Avatar 会打乱姿势，那两次扫描数据全部作废。
//
//  【本工具的做法】
//  改 `PmxImporter.upperArmTwist` 的**默认值** → 真正重新导入模型（重建整个层级）
//  → 替换场景里的模型 → 播动画 + 锁相位 → 测量。
//  慢（每个值约 10 秒），但每一步都是真实路径，不会自欺。
//
//  【可靠性的前置断言】
//  每个值测完后，检查「肩骨 x」是否与基线一致 ——
//  **肩骨不该受 twist 影响**，所以它必须稳定。不稳定就说明这一步无效，直接报错。
// ============================================================================

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.Motion
{
    public class ArmTwistRebuildTest : MonoBehaviour
    {
        public string ClipId = "idle_breath";
        public string Field = "upperArmTwist";
        public float[] Values = { 0f, 0.25f, 0.5f, 0.75f, 1f };

        public void Run() { StartCoroutine(CoRun()); }

        IEnumerator CoRun()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========== twist 扫描（★ 重建模型路线）==========");
            sb.AppendLine("clip=" + ClipId + "   field=" + Field);
            sb.AppendLine("判据：左手x（应 < 0，在身体左侧）；右x 应 > 0");
            sb.AppendLine();
            sb.AppendLine("值      左肩x    左手x    左横向   右肩x    右手x    右横向  肩断言");

            var imp = typeof(Yixuan.Core.YixuanCharacter).Assembly.GetType("Yixuan.ModelSwap.PmxImporter");
            var swap = typeof(Yixuan.Core.YixuanCharacter).Assembly.GetType("Yixuan.ModelSwap.SwapModel");
            var fld = imp.GetField(Field);
            float original = (float)fld.GetValue(null);

            float baseShoulderL = float.NaN;
            var results = new List<string>();

            foreach (var v in Values)
            {
                fld.SetValue(null, v);

                // --- 真正重建：Build 出临时模型，再装进场景 ---
                swap.GetMethod("Build").Invoke(null, null);
                var root = gameObject;
                for (int i = root.transform.childCount - 1; i >= 0; i--)
                {
                    var c = root.transform.GetChild(i);
                    if (c.name == "secondary") continue;
                    Object.DestroyImmediate(c.gameObject);
                }
                swap.GetMethod("Apply").Invoke(null, null);
                yield return null;
                yield return new WaitForSeconds(0.3f);

                // --- 测量 ---
                var ap = GetComponent<Yixuan.Motion.YixuanAnimationPlayer>();
                var mf = GetComponent<Yixuan.Motion.MixamoActionManifest>();
                var fixc = GetComponent<Yixuan.Motion.ArmClearanceFix>();
                if (fixc != null) fixc.Enabled = false;   // 扫描期间关掉修正

                string clip = mf.Resolve(ClipId);
                ap.Play(clip, true, 1f);

                var map = Yixuan.Model.MmdBoneNames.BuildIndex(root.transform);
                Transform shL, shR, hL, hR;
                map.TryGetValue("左腕", out shL); map.TryGetValue("左手首", out hL);
                map.TryGetValue("右腕", out shR); map.TryGetValue("右手首", out hR);

                float sxL = 0, sxR = 0, hxL = 0, hxR = 0, latL = 0, latR = 0;
                int N = 6;
                for (int i = 0; i < N; i++)
                {
                    ap.LockPhase((float)i / N);
                    sxL += shL.position.x; sxR += shR.position.x;
                    hxL += hL.position.x; hxR += hR.position.x;
                    latL += Mathf.Abs(hL.position.x - shL.position.x);
                    latR += Mathf.Abs(hR.position.x - shR.position.x);
                }
                sxL /= N; sxR /= N; hxL /= N; hxR /= N; latL /= N; latR /= N;

                // --- 前置断言：肩骨 x 必须稳定 ---
                string assert;
                if (float.IsNaN(baseShoulderL))
                {
                    baseShoulderL = sxL;
                    assert = "基准";
                }
                else
                {
                    float d = Mathf.Abs(sxL - baseShoulderL);
                    assert = d < 0.005f ? "✔稳" : ("✘漂移" + d.ToString("F3"));
                }

                string line = string.Format("{0,-6:F2} {1,7:F3} {2,8:F3} {3,8:F3}  {4,7:F3} {5,8:F3} {6,8:F3}  {7}",
                    v, sxL, hxL, latL, sxR, hxR, latR, assert);
                sb.AppendLine(line);
                results.Add(line);
            }

            fld.SetValue(null, original);   // 还原默认值
            sb.AppendLine();
            sb.AppendLine("（" + Field + " 已还原为 " + original.ToString("F2") + "）");
            sb.AppendLine("★ 若「肩断言」出现 ✘漂移，说明该行数据无效（姿势没对齐）");
            Debug.Log(sb.ToString());
        }
    }
}
#endif
