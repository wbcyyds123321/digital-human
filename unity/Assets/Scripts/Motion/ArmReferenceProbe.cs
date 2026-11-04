#if UNITY_EDITOR
// ============================================================================
//  ArmReferenceProbe —— 建立「手臂正确位置」的可信参照
//
//  【为什么需要】
//  前两轮修手臂失败，根因是**没有可信参照、凭推理下结论**
//  （把一个 3.7° 的差异误算成 35°，连改坏两版）。
//
//  【做法】
//  把 Mixamo 原版 FBX 的 `mixamorig` 骨架直接实例化到场景里播同一个 clip。
//  那是**未经任何重定向**的原生骨架 —— 它给出的手臂位置就是"正确答案"。
//
//  【怎么比】
//  直接比世界坐标没用（两套骨架比例/位置都不同）。
//  要比的是**手臂相对躯干的方向**，这个与骨骼命名、比例都无关：
//      · 躯干朝向 = 脊柱上游方向（Hips→Chest）
//      · 躯干侧向 = 两肩连线方向
//      · 躯干前后 = 前两者的叉积
//  然后把「上臂方向（肩→肘）」投影到这三个轴上，得到 (侧向, 上下, 前后) 三个分量。
//  这套分量在两个骨架之间**可以直接对比**。
// ============================================================================

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.Motion
{
    public class ArmReferenceProbe : MonoBehaviour
    {
        /// <summary>把一个骨架上「手臂相对躯干」的方向拆成三个分量</summary>
        public struct ArmFrame
        {
            public Vector3 UpperArmDir;   // 肩→肘（世界方向，单位）
            public float AlongSide;       // 在躯干侧向轴上的分量（+ = 往该侧张开）
            public float AlongUp;         // 在躯干上下轴上的分量（+ = 向上）
            public float AlongFwd;        // 在躯干前后轴上的分量（+ = 向前）
            public float BelowSpine;      // 肘相对躯干平面的前后偏移（负 = 身后）
            public override string ToString()
            {
                return string.Format("侧{0,6:F3} 上{1,6:F3} 前{2,6:F3}  肘前后{3,6:F3}",
                    AlongSide, AlongUp, AlongFwd, BelowSpine);
            }
        }

        /// <summary>
        /// 用「三个点」算手臂姿态：髋、胸、肩、肘。
        /// 这样同一套代码可以量任意骨架（Mixamo 的 mixamorig 或我们的 MMD 骨）。
        /// </summary>
        public static ArmFrame Measure(Transform hip, Transform chest, Transform shoulder, Transform elbow, bool isLeft)
        {
            var f = new ArmFrame();
            if (hip == null || chest == null || shoulder == null || elbow == null) return f;

            Vector3 up = (chest.position - hip.position).normalized;          // 躯干上游
            if (up.sqrMagnitude < 1e-6f) up = Vector3.up;

            // 侧向：用肩相对躯干中线的方向（比两肩连线更稳，单侧也能算）
            // 取「肩 - (髋+胸)/2」在垂直于 up 平面上的分量
            Vector3 mid = (hip.position + chest.position) * 0.5f;
            Vector3 toShoulder = shoulder.position - mid;
            Vector3 side = Vector3.ProjectOnPlane(toShoulder, up).normalized;
            if (side.sqrMagnitude < 1e-6f) side = Vector3.right;

            Vector3 fwd = Vector3.Cross(side, up).normalized;                 // 前后轴
            // Cross(side, up) 的手性取决于骨架朝向，用"鼻尖方向"校正：
            // 这里不假设，只用它做**相对比较**，两套骨架用同一公式即可。

            Vector3 d = (elbow.position - shoulder.position).normalized;
            f.UpperArmDir = d;
            f.AlongSide = Vector3.Dot(d, side) * (isLeft ? 1f : -1f);          // 归一到"往同侧张开为正"
            f.AlongUp = Vector3.Dot(d, up);
            f.AlongFwd = Vector3.Dot(d, fwd) * (isLeft ? 1f : -1f);

            // 肘相对躯干中线的前后偏移（负 = 身后）
            Vector3 toElbow = elbow.position - mid;
            f.BelowSpine = Vector3.Dot(toElbow, fwd) * (isLeft ? 1f : -1f);
            return f;
        }

        /// <summary>在任意骨架里按 3 段骨头名找 Transform</summary>
        public static Transform Find(Transform root, params string[] names)
        {
            var idx = Yixuan.Model.MmdBoneNames.BuildIndex(root);
            foreach (var n in names) { Transform t; if (idx.TryGetValue(n, out t)) return t; }
            // 模糊匹配（Mixamo 骨名带 mixamorig: 前缀）
            foreach (var kv in idx)
                foreach (var n in names)
                    if (kv.Key.EndsWith(n) || kv.Key.Contains(n)) return kv.Value;
            return null;
        }
    }
}
#endif
