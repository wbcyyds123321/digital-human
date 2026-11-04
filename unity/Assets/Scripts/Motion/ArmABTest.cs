#if UNITY_EDITOR
// ============================================================================
//  ArmABTest —— 手臂参照对比 + twist 参数 A/B 测试（Play 模式用）
//
//  用法：
//      var p = go.GetComponent<Yixuan.Motion.ArmABTest>();
//      p.RunReferenceCompare();     // 建立 Mixamo 参照并和当前角色对比
//      p.RunTwistSweep();           // 逐个 twist 值重导模型并复测（较慢）
// ============================================================================

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;          // AnimationClipPlayableExtensions.SetTime / AnimationPlayableOutputExtensions.SetSourcePlayable
using UnityEngine.Playables;

namespace Yixuan.Motion
{
    public class ArmABTest : MonoBehaviour
    {
        /// <summary>
        /// 【第一步】建立参照：把 Mixamo 原版 FBX 的骨架实例化，用 SampleAnimation 直接采样
        /// 它自己的 clip（零重定向），量出手臂相对躯干的方向。
        ///
        /// ★ 用 SampleAnimation 而不是 Animator：Animator 在这个 FBX 上会自动套 Humanoid 重定向，
        ///   那就不是"原生"了。SampleAnimation 直接把 FK 曲线写进骨骼，才是真正的正确答案。
        /// </summary>
        public void RunReferenceCompare()
        {
            var log = new System.Text.StringBuilder();
            log.AppendLine("========== 手臂参照对比 ==========");

            // ---- 我们的角色：先量一次（不切 clip，读当前帧） ----
            log.AppendLine("—— 我们的角色（当前 clip）——");
            log.AppendLine("  左臂: " + MeasureOurCharacter(null));
            log.AppendLine("  右臂: " + MeasureOurCharacterRight());

            // ---- Mixamo 原版 ----
            var fbx = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Animations/Mixamo/Jogging.fbx");
            if (fbx == null) { log.AppendLine("✘ 找不到 Jogging.fbx"); Debug.Log(log.ToString()); return; }

            GameObject refGo = null;
            System.Exception err = null;
            try
            {
                refGo = Instantiate(fbx);
                refGo.name = "__MIXAMO_REF";
                refGo.transform.position = new Vector3(6f, 0f, 0f);

                // ★ 保留 Animator —— PlayableGraph 需要一个 Animator 当输出端口。
                //   但我们自己建 graph 手动驱动，不用它的 runtimeAnimatorController，
                //   所以不会走"自动重定向"那条路。
                var a = refGo.GetComponent<Animator>();
                if (a == null) a = refGo.AddComponent<Animator>();
                a.runtimeAnimatorController = null;
                a.enabled = false;   // 不让它自己跑

                var mHip   = ArmReferenceProbe.Find(refGo.transform, "mixamorig:Hips", "Hips");
                var mChest = ArmReferenceProbe.Find(refGo.transform, "mixamorig:Spine2", "mixamorig:Spine1", "Spine2");
                var mShL   = ArmReferenceProbe.Find(refGo.transform, "mixamorig:LeftArm", "LeftArm");
                var mElL   = ArmReferenceProbe.Find(refGo.transform, "mixamorig:LeftForeArm", "LeftForeArm");
                var mShR   = ArmReferenceProbe.Find(refGo.transform, "mixamorig:RightArm", "RightArm");
                var mElR   = ArmReferenceProbe.Find(refGo.transform, "mixamorig:RightForeArm", "RightForeArm");

                log.AppendLine("骨: 髋=" + N(mHip) + " 胸=" + N(mChest)
                    + " 左肩=" + N(mShL) + " 左肘=" + N(mElL));

                // 找到它自己的 clip 并采样
                AnimationClip clip = null;
                foreach (var o in UnityEditor.AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/Mixamo/Jogging.fbx"))
                {
                    var c = o as AnimationClip;
                    if (c != null && !c.name.StartsWith("__preview__")) { clip = c; break; }
                }
                log.AppendLine("clip = " + (clip != null ? clip.name + " (" + clip.length.ToString("F2") + "s)" : "✘"));

                if (clip != null && mShL != null && mElL != null)
                {
                    // ★★ 不能用 clip.SampleAnimation()！
                    //   Humanoid clip 存的是 muscle 曲线（绑在 typeof(Animator) 上），
                    //   不是 m_LocalRotation。SampleAnimation 只对普通 FK 曲线有效 ——
                    //   实测每次采样结果**完全相同**（PROJECT_NOTES 第 2 节坑 B 原样复现）。
                    //   必须用 PlayableGraph，而且 SetTime 要设在**片段**上（graph 没有 SetTime）。
                    var graph = UnityEngine.Playables.PlayableGraph.Create("__ArmRefProbe");
                    graph.SetTimeUpdateMode(UnityEngine.Playables.DirectorUpdateMode.Manual);
                    var pb = UnityEngine.Animations.AnimationClipPlayable.Create(graph, clip);
                    var outPort = UnityEngine.Animations.AnimationPlayableOutput.Create(graph, "out", a);
                    outPort.SetSourcePlayable(pb);
                    graph.Play();

                    float[] times = { 0.0f, 0.4f, 0.8f, 1.2f, 1.6f, 2.0f, 2.4f };
                    foreach (var t in times)
                    {
                        pb.SetTime(t);        // ★ 设在【片段】上
                        graph.Evaluate();
                        var fL = ArmReferenceProbe.Measure(mHip, mChest, mShL, mElL, true);
                        var fR = ArmReferenceProbe.Measure(mHip, mChest, mShR, mElR, false);
                        log.AppendLine(string.Format("  t={0:F1}  左[{1}]  右[{2}]", t, fL, fR));
                    }
                    graph.Destroy();
                    log.AppendLine("  ★ 上面是【未经重定向的正确答案】");
                }
            }
            catch (System.Exception e) { err = e; }

            if (refGo != null) DestroyImmediate(refGo);
            if (err != null) log.AppendLine("异常: " + err.Message + "\n" + err.StackTrace);

            Debug.Log(log.ToString());
        }

        ArmReferenceProbe.ArmFrame MeasureOurCharacterRight()
        {
            var map = Yixuan.Model.MmdBoneNames.BuildIndex(transform);
            Transform hip, chest, sh, el;
            map.TryGetValue("センター", out hip);
            map.TryGetValue("上半身2", out chest);
            map.TryGetValue("右腕", out sh); map.TryGetValue("右ひじ", out el);
            return ArmReferenceProbe.Measure(hip, chest, sh, el, false);
        }

        string N(Transform t) { return t != null ? t.name : "✘"; }

        /// <summary>量我们角色当前的手臂</summary>
        ArmReferenceProbe.ArmFrame MeasureOurCharacter(string clip)
        {
            var map = Yixuan.Model.MmdBoneNames.BuildIndex(transform);
            Transform hip, chest, shL, elL, shR, elR;
            map.TryGetValue("センター", out hip);
            map.TryGetValue("上半身2", out chest);
            map.TryGetValue("左腕", out shL); map.TryGetValue("左ひじ", out elL);
            map.TryGetValue("右腕", out shR); map.TryGetValue("右ひじ", out elR);
            return ArmReferenceProbe.Measure(hip, chest, shL, elL, true);
        }

        public void RunPolicy() { StartCoroutine(CoPolicy()); }

        IEnumerator CoPolicy()
        {
            Debug.Log("（占位）");
            yield return null;
        }
    }
}
#endif
