#if UNITY_EDITOR
// ============================================================================
//  ArmTwistSweep —— 参数扫描：找出让手臂回到正确位置的 HumanDescription 参数
//
//  【为什么要扫描而不是推理】
//  前两轮凭推理改手臂，连改坏两版（37°→穿胸口，20°→手臂像树枝）。
//  这次改成**纯经验法**：逐个值拿到同一套测量下比数字。
//
//  【为什么必须热换 Avatar，而不是重导模型】
//  HumanDescription 的 twist 参数影响的是**重定向**，不影响绑定姿态。
//  所以在场景里重建模型是白费功夫 —— 要换的是 **Animator.avatar**。
//  本工具直接 `AvatarBuilder.BuildHumanAvatar` 造出多个 Avatar，
//  依次套到当前角色上，播同一个 clip，量手臂位置。
//  这样一轮扫描只要几秒，且完全不碰场景。
//
//  【判据】肘部 z（相对躯干平面，负=身后）。越接近 0 越好。
//
//  用法（**Play 模式**）：
//      var s = go.AddComponent<Yixuan.Motion.ArmTwistSweep>();
//      s.RunSweep();
// ============================================================================

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.Motion
{
    public class ArmTwistSweep : MonoBehaviour
    {
        public string ClipId = "jog";          // 用哪个动作测
        public int SamplesPerValue = 8;        // 每个参数值采样几帧
        public float SampleInterval = 0.09f;

        public void RunSweep() { StartCoroutine(CoSweep()); }

        /// <summary>
        /// ★ 正确的判据：慢跑手臂本来就该**一只前一只后**，
        /// 所以不能把"肘在身后"当错误。真正该看的是：
        ///   ① 前后摆幅（好的跑步：两肘 z 的最大值−最小值 都很大）
        ///   ② 左右是否**反相**（一只前时另一只在后）
        ///   ③ 有没有落到身体里（|z| 很大且 |x| 很小 = 插进躯干）
        /// </summary>
        IEnumerator CoSweep()
        {
            var anim = GetComponent<Animator>();
            var ap = GetComponent<Yixuan.Motion.YixuanAnimationPlayer>();
            var mf = GetComponent<Yixuan.Motion.MixamoActionManifest>();
            if (anim == null || ap == null || mf == null) { Debug.LogError("缺组件"); yield break; }

            string clipName = mf.Resolve(ClipId);
            if (string.IsNullOrEmpty(clipName) || !ap.HasClip(clipName)) { Debug.LogError("没有 clip " + ClipId); yield break; }

            var orig = anim.avatar;
            var map = Yixuan.Model.MmdBoneNames.BuildIndex(transform);
            Transform cen, ch, shL, elL, shR, elR, hL, hR;
            map.TryGetValue("センター", out cen);
            map.TryGetValue("上半身2", out ch);
            map.TryGetValue("左腕", out shL); map.TryGetValue("左ひじ", out elL);
            map.TryGetValue("右腕", out shR); map.TryGetValue("右ひじ", out elR);
            map.TryGetValue("左手首", out hL); map.TryGetValue("右手首", out hR);

            var log = new System.Text.StringBuilder();
            log.AppendLine("========== 手臂参数扫描（clip=" + clipName + "，★ 相位锁定）==========");
            log.AppendLine("★ 判据：手相对肩的【横向离开距离】—— 实测问题是两手 x≈0（都跑到身体中线）");
            log.AppendLine("参数值   左手横向  右手横向  左手外侧  右手外侧  肘铰链沿左右轴");

            string[] fields = { "upperArmTwist", "lowerArmTwist", "armStretch" };
            float[] values = { 0f, 0.25f, 0.5f, 0.75f, 1f };

            foreach (var fname in fields)
            {
                log.AppendLine();
                log.AppendLine("--- 只变 " + fname + " ---");
                foreach (var v in values)
                {
                    var av = BuildAvatarWith(anim, fname, v);
                    if (av == null) { log.AppendLine("  " + v.ToString("F2") + "  ✘ 建 Avatar 失败"); continue; }
                    anim.avatar = av;
                    ap.Play(clipName, true, 1f);
                    yield return null;

                    float latL = 0, latR = 0, outL = 0, outR = 0, hinge = 0;
                    int N = 8;
                    for (int i = 0; i < N; i++)
                    {
                        ap.LockPhase((float)i / N);
                        latL += Mathf.Abs(hL.position.x - shL.position.x);
                        latR += Mathf.Abs(hR.position.x - shR.position.x);
                        outL += Mathf.Abs(hL.position.x) - Mathf.Abs(shL.position.x);
                        outR += Mathf.Abs(hR.position.x) - Mathf.Abs(shR.position.x);
                        Vector3 ua = (elL.position - shL.position).normalized;
                        Vector3 la = (hL.position - elL.position).normalized;
                        Vector3 hg = Vector3.Cross(ua, la);
                        if (hg.sqrMagnitude > 1e-8f) hinge += Mathf.Abs(Vector3.Dot(hg.normalized, Vector3.right));
                    }
                    log.AppendLine(string.Format("  {0,-6:F2}  {1,7:F3}   {2,7:F3}   {3,7:F3}   {4,7:F3}   {5,7:F3}",
                        v, latL / N, latR / N, outL / N, outR / N, hinge / N));
                    Object.Destroy(av);
                }
            }

            anim.avatar = orig;
            ap.Play(clipName, true, 1f);
            log.AppendLine();
            log.AppendLine("（已还原原 Avatar）");
            Debug.Log(log.ToString());
        }

        /// <summary>点到【线段】的距离（脊柱 = センター→上半身2）</summary>
        static float DistToSpine(Vector3 p, Transform a, Transform b)
        {
            if (a == null || b == null) return 999f;
            Vector3 A = a.position, B = b.position;
            Vector3 AB = B - A;
            float len2 = AB.sqrMagnitude;
            if (len2 < 1e-8f) return Vector3.Distance(p, A);
            float t = Mathf.Clamp01(Vector3.Dot(p - A, AB) / len2);
            return Vector3.Distance(p, A + AB * t);
        }

        /// <summary>用指定的 twist 值重建一份 Avatar（不动场景）</summary>
        Avatar BuildAvatarWith(Animator anim, string field, float value)
        {
            var imp = typeof(Yixuan.Core.YixuanCharacter).Assembly.GetType("Yixuan.ModelSwap.PmxImporter");
            if (imp == null) return null;
            var bm = imp.GetMethod("BuildAvatarForTest",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (bm == null) { Debug.LogError("PmxImporter 缺 BuildAvatarForTest"); return null; }
            return (Avatar)bm.Invoke(null, new object[] { gameObject, field, value });
        }
    }
}
#endif
