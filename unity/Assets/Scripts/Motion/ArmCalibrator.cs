using UnityEngine;

namespace Yixuan.Motion
{
    /// <summary>
    /// 手臂基础校准器 —— 一次性把"哪根轴管什么、往哪个方向、什么角度合理"测清楚。
    ///
    /// ============================================================
    ///  【为什么要单独做这个】
    /// ============================================================
    /// 之前反复出错，根本原因是**测量方法本身有问题**：
    ///   1. 用"手首世界位置"判断，但那个位置同时受上臂/肘/手腕影响，分不清是谁造成的
    ///   2. 在错误的基准姿态下测（センター 被污染，角色是倒立的）
    ///   3. 只看"位置对不对"，没检查"手臂朝向对不对"
    ///
    /// 本校准器只测一件事：**上臂指向**。
    ///   肘保持伸直 → 手首-肩 的向量就是手臂指向，不受肘/腕干扰。
    ///
    /// 输出一张表，直接告诉我们：
    ///   - 哪个轴能让手臂【向外侧抬起】
    ///   - 哪个轴是纯扭转（指向不变）
    ///   - 每个角度的实际指向向量
    /// ============================================================
    /// </summary>
    public class ArmCalibrator : MonoBehaviour
    {
        [Header("引用")]
        public Transform Shoulder;   // 腕.R（上臂根）
        public Transform Elbow;      // ひじ.R
        public Transform Wrist;      // 手首.R

        [Header("调试")]
        public bool VerboseLog = true;

        /// <summary>肘伸直，返回"手臂指向"（从肩到腕的单位向量）</summary>
        public Vector3 ArmDirection()
        {
            if (Shoulder == null || Wrist == null) return Vector3.zero;
            return (Wrist.position - Shoulder.position).normalized;
        }

        /// <summary>手首相对肩的位置</summary>
        public Vector3 WristRel() { return Wrist.position - Shoulder.position; }

        /// <summary>
        /// 完整扫描：逐轴旋转上臂，输出手臂指向的变化。
        /// 返回可直接阅读的表格文本。
        /// </summary>
        public string ScanUpperArm()
        {
            var sb = new System.Text.StringBuilder();
            if (Shoulder == null || Elbow == null || Wrist == null)
            {
                return "❌ ArmCalibrator 引用不完整（Shoulder/Elbow/Wrist）";
            }

            var baseArmRot = Shoulder.localRotation;
            var baseElbowRot = Elbow.localRotation;
            var baseDir = ArmDirection();
            float baseLen = WristRel().magnitude;

            sb.AppendLine("############ 上臂轴扫描（肘伸直，测手臂指向）############");
            sb.AppendLine("基准手臂指向 = " + baseDir.ToString("F3") + "   长度=" + baseLen.ToString("F3"));
            sb.AppendLine("判定：指向变化越大 = 该轴越是'运动轴'；指向几乎不变 = 纯扭转轴");
            sb.AppendLine();

            string[] axName = { "X", "Y", "Z" };
            for (int a = 0; a < 3; a++)
            {
                sb.AppendLine("--- 绕 " + axName[a] + " ---");
                for (int d = -180; d <= 180; d += 30)
                {
                    if (d == 0) continue;
                    Elbow.localRotation = baseElbowRot;   // 肘保持伸直

                    Quaternion q = baseArmRot;
                    if (a == 0) q = baseArmRot * Quaternion.Euler(d, 0, 0);
                    if (a == 1) q = baseArmRot * Quaternion.Euler(0, d, 0);
                    if (a == 2) q = baseArmRot * Quaternion.Euler(0, 0, d);
                    Shoulder.localRotation = q;

                    var dir = ArmDirection();
                    float ang = Vector3.Angle(dir, baseDir);
                    float len = WristRel().magnitude;

                    sb.AppendLine("  " + axName[a] + (d > 0 ? "+" : "") + d.ToString().PadLeft(5) + "°" +
                                  "  指向=(" + dir.x.ToString("F2") + "," + dir.y.ToString("F2") + "," + dir.z.ToString("F2") + ")" +
                                  "  偏离=" + ang.ToString("F0").PadLeft(3) + "°" +
                                  "  长度比=" + (len / baseLen).ToString("F3") +
                                  (ang < 5f ? "   ← 纯扭转" : ""));
                }
                // 恢复
                Shoulder.localRotation = baseArmRot;
                sb.AppendLine();
            }

            Shoulder.localRotation = baseArmRot;
            Elbow.localRotation = baseElbowRot;
            return sb.ToString();
        }

        /// <summary>
        /// 找"手臂向侧面抬起指定角度"的正确参数。
        /// </summary>
        /// <param name="sideSign">右侧 = -1（手在 -X 侧），左侧 = +1</param>
        /// <param name="liftDegrees">要从下垂抬起多少度（0=下垂, 90=水平, 180=举过头顶）</param>
        public string FindSideLift(float sideSign, float liftDegrees)
        {
            var sb = new System.Text.StringBuilder();
            if (Shoulder == null || Elbow == null || Wrist == null) return "❌ 引用不完整";

            var baseArmRot = Shoulder.localRotation;
            var baseElbowRot = Elbow.localRotation;

            sb.AppendLine("=== 找参数：手臂向" + (sideSign < 0 ? "右" : "左") + "侧抬起 " + liftDegrees + "° ===");
            sb.AppendLine("目标指向：dX=" + (sideSign * Mathf.Sin(liftDegrees * Mathf.Deg2Rad)).ToString("F2") +
                          " dY=" + Mathf.Cos(liftDegrees * Mathf.Deg2Rad).ToString("F2") + " dZ≈0");
            sb.AppendLine();

            float bestErr = 999f;
            int bA = 0, bB = 0, bAVal = 0, bBVal = 0;
            Vector3 bestDir = Vector3.zero;

            // 扫描两个主要轴（X 和 Z）
            for (int a = -180; a <= 180; a += 15)
            {
                for (int b = -90; b <= 90; b += 15)
                {
                    Elbow.localRotation = baseElbowRot;
                    Shoulder.localRotation = baseArmRot * Quaternion.Euler(a, 0f, b);
                    var dir = ArmDirection();

                    float tX = sideSign * Mathf.Sin(liftDegrees * Mathf.Deg2Rad);
                    float tY = Mathf.Cos(liftDegrees * Mathf.Deg2Rad);
                    float err = Mathf.Abs(dir.x - tX) + Mathf.Abs(dir.y - tY) + Mathf.Abs(dir.z) * 2f;
                    if (err < bestErr)
                    {
                        bestErr = err; bA = a; bB = b; bestDir = dir;
                        bAVal = a; bBVal = b;
                    }
                }
            }

            // 用最优参数验证
            Elbow.localRotation = baseElbowRot;
            Shoulder.localRotation = baseArmRot * Quaternion.Euler(bAVal, 0f, bBVal);
            var vd = ArmDirection();

            sb.AppendLine("★ 最优: 上臂 Euler(X=" + bAVal + ", Y=0, Z=" + bBVal + ")");
            sb.AppendLine("   实际指向=(" + vd.x.ToString("F3") + "," + vd.y.ToString("F3") + "," + vd.z.ToString("F3") + ")");
            sb.AppendLine("   误差=" + bestErr.ToString("F3"));

            Shoulder.localRotation = baseArmRot;
            return sb.ToString();
        }

        /// <summary>体检：检查手臂链的完整性（骨骼长度是否正常）</summary>
        public string HealthCheck()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== 手臂链长度体检 ===");
            if (Shoulder == null || Elbow == null || Wrist == null) return "❌ 引用不完整";

            float upperLen = (Elbow.position - Shoulder.position).magnitude;
            float foreLen = (Wrist.position - Elbow.position).magnitude;
            float total = (Wrist.position - Shoulder.position).magnitude;

            sb.AppendLine("  上臂长 = " + upperLen.ToString("F4"));
            sb.AppendLine("  小臂长 = " + foreLen.ToString("F4"));
            sb.AppendLine("  肩→腕 直线距离 = " + total.ToString("F4"));
            sb.AppendLine("  上臂+小臂 = " + (upperLen + foreLen).ToString("F4"));

            float ratio = total / (upperLen + foreLen);
            sb.AppendLine("  比值 = " + ratio.ToString("F4") +
                          (ratio > 1.02f ? "   ❌ 被拉长（关节异常）" :
                           ratio > 0.98f ? "   ✅ 完全伸直" : "   ✅ 正常弯曲"));
            return sb.ToString();
        }
    }
}
