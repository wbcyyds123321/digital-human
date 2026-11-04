#if UNITY_EDITOR
// ============================================================================
//  MotionVerifier —— 15 个动作的「腿骨真的在动吗」跨帧验证
//
//  【为什么需要专门验这个】
//  换模型后最大的风险是：Avatar 又映射回「0 权重的控制骨」，
//  于是动画在播、骨骼数据也在变，但**网格纹丝不动**（PROJECT_NOTES 第 1 节的坑）。
//  所以判据不能是"动画在播"，必须是**腿骨世界坐标逐帧真的变了**。
//
//  【为什么必须跨帧】
//  同一帧里动画不会推进。播完动作立刻采样，拿到的是上一帧（通常是站立）。
//  必须用协程 yield 等真实的帧（★ 绝不能空转等待 —— 会卡死编辑器）。
//
//  用法（Play 模式）：
//      var v = go.GetComponent<Yixuan.Motion.MotionVerifier>();
//      v.RunAllActions();
//      等几秒后看 Console
// ============================================================================

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.Motion
{
    public class AllActionsVerifier : MonoBehaviour
    {
        [Tooltip("每个动作采样几个时间点")]
        public int SamplesPerAction = 5;

        [Tooltip("每个动作总时长（秒）")]
        public float SecondsPerAction = 1.2f;

        /// <summary>腿骨在「绑定姿态」下的基准朝向（用来判断有没有偏离）</summary>
        Dictionary<string, Vector3> _restDir = new Dictionary<string, Vector3>();

        public void RunAllActions()
        {
            StartCoroutine(CoRunAll());
        }

        /// <summary>
        /// 量每个动作的**手臂外展程度**，用来标定 TPoseLiftDegrees。
        ///
        /// 判据（跑/走这类动作）：
        ///   · 手臂外展角（上臂方向偏离"向下"的角度）应该不大 —— 跑步时手臂是前后摆，
        ///     不是往两侧张开。实测偏大就说明抬升角给多了。
        ///   · 手腕不应该高过肩膀（除非动作本身是举手）。
        /// </summary>
        public void MeasureArms()
        {
            StartCoroutine(CoMeasureArms());
        }

        IEnumerator CoMeasureArms()
        {
            var ap = GetComponent<Yixuan.Motion.YixuanAnimationPlayer>();
            var mf = GetComponent<Yixuan.Motion.MixamoActionManifest>();
            if (ap == null || mf == null) { Debug.LogError("[AllActionsVerifier] 缺组件"); yield break; }

            var shL = Yixuan.Model.MmdBoneNames.Resolve("ShoulderL", transform);
            var aL = Yixuan.Model.MmdBoneNames.Resolve("ArmL", transform);
            var eL = Yixuan.Model.MmdBoneNames.Resolve("ElbowL", transform);
            var hL = Yixuan.Model.MmdBoneNames.Resolve("Head", transform);
            if (aL == null || eL == null) { Debug.LogError("[AllActionsVerifier] 缺手臂骨"); yield break; }

            var log = new System.Text.StringBuilder();
            log.AppendLine("========== 全 15 动作 · 手臂位置测量 ==========");
            log.AppendLine("动作           总偏离 侧向XY 矢状ZY   肘部z范围(前后)      判定");
            log.AppendLine("                                    (负=身后)");
            int bad = 0;
            var allIds = mf.Ids();
            foreach (var id in allIds)
            {
                string clip = mf.Resolve(id);
                if (string.IsNullOrEmpty(clip) || !ap.HasClip(clip)) continue;
                ap.Play(clip, true, 1f);
                yield return null;
                float sideMax = 0f, sagMax = 0f, totMax = 0f, dyMin = 999f, dyMax = -999f;
                float zMin = 999f, zMax = -999f;   // 手部的 z（负=身后）
                for (int i = 0; i < 6; i++)
                {
                    yield return new WaitForSeconds(0.18f);
                    var d = (eL.position - aL.position).normalized;
                    float side = Vector3.Angle(new Vector3(d.x, d.y, 0f), Vector3.down);
                    float sag = Vector3.Angle(new Vector3(0f, d.y, d.z), Vector3.down);
                    float tot = Vector3.Angle(d, Vector3.down);
                    if (side > sideMax) sideMax = side;
                    if (sag > sagMax) sagMax = sag;
                    if (tot > totMax) totMax = tot;
                    float dy = eL.position.y - aL.position.y;
                    dyMin = Mathf.Min(dyMin, dy); dyMax = Mathf.Max(dyMax, dy);
                    if (eL.position.z < zMin) zMin = eL.position.z;
                    if (eL.position.z > zMax) zMax = eL.position.z;
                }
                // 肘部跑到身后（z 明显为负）→ 手臂向后甩
                bool behind = zMin < -0.09f;
                if (behind) bad++;
                log.AppendLine(string.Format("{0,-14} {1,5:F1} {2,6:F1} {3,6:F1}  肘z {4,6:F3}~{5,6:F3}  {6}",
                    id, totMax, sideMax, sagMax, zMin, zMax,
                    behind ? "⚠ 肘甩到身后" : "✔"));
            }
            log.AppendLine();
            log.AppendLine("判据：肘部 z（前后）< −0.09 视为甩到身后。");
            log.AppendLine("甩到身后的动作数 = " + bad);
            Debug.Log(log.ToString());
        }

        /// <summary>
        /// 一次性把 15 个动作的蒙皮网格都导出成 OBJ（复用现有 MotionExportVerifier），
        /// 供 Blender 离线渲染后用眼睛确认。Play 模式调用。
        /// 判据见 PROJECT_NOTES 第 11 节：最终只信渲染出来的图。
        /// </summary>
        public void ExportAllActions()
        {
            var mf = GetComponent<Yixuan.Motion.MixamoActionManifest>();
            var ex = GetComponent<Yixuan.Motion.MotionExportVerifier>();
            if (mf == null || ex == null)
            {
                Debug.LogError("[AllActionsVerifier] 缺 Manifest / MotionExportVerifier");
                return;
            }

            var ids = mf.Ids();
            var shots = new List<Yixuan.Motion.MotionExportVerifier.Shot>();
            shots.Add(new Yixuan.Motion.MotionExportVerifier.Shot("", "bind", 0.35f));  // 绑定姿态（对照）
            foreach (var id in ids)
                shots.Add(new Yixuan.Motion.MotionExportVerifier.Shot(id, id, 1.1f));

            var arr = shots.ToArray();
            Debug.Log("[AllActionsVerifier] 开始导出 " + arr.Length + " 个姿态（含绑定姿态）");
            ex.RunShots(arr);
        }

        IEnumerator CoRunAll()
        {
            var yc = GetComponent<Yixuan.Core.YixuanCharacter>();
            var ap = GetComponent<Yixuan.Motion.YixuanAnimationPlayer>();
            var mf = GetComponent<Yixuan.Motion.MixamoActionManifest>();
            if (ap == null || mf == null) { Debug.LogError("[AllActionsVerifier] 缺 AnimPlayer / Manifest"); yield break; }

            var hipL = Yixuan.Model.MmdBoneNames.Resolve("UpperLegL", transform);
            var kneeL = Yixuan.Model.MmdBoneNames.Resolve("LowerLegL", transform);
            var ankL = Yixuan.Model.MmdBoneNames.Resolve("FootL", transform);
            var hipR = Yixuan.Model.MmdBoneNames.Resolve("UpperLegR", transform);
            var kneeR = Yixuan.Model.MmdBoneNames.Resolve("LowerLegR", transform);
            if (hipL == null || kneeL == null) { Debug.LogError("[AllActionsVerifier] 找不到腿骨"); yield break; }

            // 1) 先记绑定姿态（不播动画）
            if (ap != null) ap.Stop();
            yield return null; yield return null;
            var restHipL = hipL.position;
            var restKneeL = kneeL.position;
            float restThighLen = Vector3.Distance(restHipL, restKneeL);
            float restKneeAngle = Vector3.Angle((restKneeL - restHipL).normalized, Vector3.down);

            var ids = mf.Ids();
            var log = new System.Text.StringBuilder();
            log.AppendLine("========== 15 个动作 · 腿骨运动验证 ==========");
            log.AppendLine("绑定姿态: 大腿长=" + restThighLen.ToString("F4")
                + "  髋→膝方向=" + restKneeAngle.ToString("F1") + "° (相对向下)");
            log.AppendLine();
            log.AppendLine("动作           采样   clip内髋位移  clip内膝位移   膝角范围        结论");
            log.AppendLine("                      (越大=动作越大)");

            int pass = 0, fail = 0;
            foreach (var id in ids)
            {
                string clip = mf.Resolve(id);
                if (string.IsNullOrEmpty(clip) || !ap.HasClip(clip)) continue;

                ap.Play(clip, true, 1f);
                yield return null;

                var hipPos = new List<Vector3>();
                var kneePos = new List<Vector3>();
                var angs = new List<float>();
                int n = Mathf.Max(2, SamplesPerAction);
                for (int i = 0; i < n; i++)
                {
                    yield return new WaitForSeconds(SecondsPerAction / n);
                    hipPos.Add(hipL.position);
                    kneePos.Add(kneeL.position);
                    angs.Add(Vector3.Angle((kneeL.position - hipL.position).normalized, Vector3.down));
                }

                float hipMove = 0f, kneeMove = 0f;
                for (int i = 0; i < hipPos.Count; i++)
                {
                    hipMove = Mathf.Max(hipMove, Vector3.Distance(hipPos[i], restHipL));
                    kneeMove = Mathf.Max(kneeMove, Vector3.Distance(kneePos[i], restKneeL));
                }
                float aMin = float.MaxValue, aMax = float.MinValue;
                foreach (var a in angs) { aMin = Mathf.Min(aMin, a); aMax = Mathf.Max(aMax, a); }
                float angRange = aMax - aMin;
                float kneeDelta = Mathf.Max(Mathf.Abs(aMin - restKneeAngle), Mathf.Abs(aMax - restKneeAngle));

                // ★★ 真正该测的是「这个 clip 自己有没有在动」——即**采样点之间的变化**，
                //    而不是「相对绑定姿态偏了多少」。
                //    因为有些 clip（如 Standing Idle）姿态本来就接近绑定姿态，
                //    用"相对绑定姿态"当基准会误判成"腿不动"（实测在 0.002~0.003 反复横跳）。
                float intraKnee = 0f, intraHip = 0f;
                for (int i = 0; i < kneePos.Count; i++)
                    for (int j = i + 1; j < kneePos.Count; j++)
                    {
                        intraKnee = Mathf.Max(intraKnee, Vector3.Distance(kneePos[i], kneePos[j]));
                        intraHip = Mathf.Max(intraHip, Vector3.Distance(hipPos[i], hipPos[j]));
                    }

                // 判据：clip 内部骨骼有可测变化。
                // 真"映射到 0 权重控制骨"时，动画在播但**采样值完全相同** → intra = 0。
                bool moved = intraKnee > 0.0015f || intraHip > 0.0015f || angRange > 0.4f;
                if (moved) pass++; else fail++;

                log.AppendLine(string.Format(
                    "{0,-14} {1,4}   {2,7:F4}   {3,7:F4}   {4,5:F1}~{5,-5:F1}(Δ{6,4:F1})  {7}",
                    id, n, intraHip, intraKnee, aMin, aMax, angRange,
                    moved ? "✔ 在动" : "❌ clip 内位移≈0（疑似映射错骨）"));
            }

            log.AppendLine();
            log.AppendLine(string.Format("合计: 通过 {0} / 不动 {1}", pass, fail));
            if (fail > 0)
                log.AppendLine("⚠ 有动作腿骨不动 —— 检查 Avatar 是否又映射到了 0 权重的控制骨");
            Debug.Log(log.ToString());

            ap.Play(mf.Resolve("idle"), true, 1f);
        }
    }
}
#endif
