#if UNITY_EDITOR
// ============================================================================
//  RetargetProbe —— 重定向基准检查（只读诊断，T1 的固定验收工具）
//
//  【为什么要它】
//  Unity 的 Humanoid 重定向要求 Avatar 的 `HumanDescription.skeleton` 是 **T-pose**。
//  如果 skeleton 里抄的是模型的**绑定姿态**（这个模型是 A-pose，上臂下垂 37°），
//  肌肉值 0 的位置就被算错了 —— 所有 Mixamo 动作都会整体偏一个角度。
//
//  【判据（确定性、不播动画）】
//  1. 参照：实例化 `Assets/Animations/Mixamo/Standing Idle.fbx`
//     （Mixamo 原生骨架，它的 Avatar 就是标准 T-pose）
//  2. 两边各建一个 HumanPoseHandler，把肌肉**全部置 0** 后 SetHumanPose
//     —— 骨架会被摆成"该 Avatar 认为的肌肉 0 位置"
//  3. 量 8 个骨段的方向：
//       左右上臂（UpperArm→LowerArm）、左右前臂（LowerArm→Hand）
//       左右大腿（UpperLeg→LowerLeg）、左右小腿（LowerLeg→Foot）
//  4. 两个模型朝向不同，**不能直接比世界方向** ——
//     各自把方向换算到**自己 Hips 的局部坐标系**里再比，得到每段夹角
//  5. Console 出表，最后一行 `RETARGET_PROBE max_deg=<最大夹角>`
//
//  【只读保证】
//  角色所有 Transform 的 localPosition / localRotation 先快照，结束时（含异常）
//  一定还原；参照实例销毁、两个 handler Dispose。
//
//  用法：菜单 UnityLITF/仪玄/⑦ 重定向基准检查（编辑模式）
// ============================================================================

using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEditor;

namespace Yixuan.Motion
{
    public static class RetargetProbe
    {
        const string ReferencePath = "Assets/Animations/Mixamo/T-Pose.fbx";   // Mixamo 官方 X Bot T-pose（With Skin），左右完全对称
        const string RootName = "Yixuan_Test";

        // ================================================================
        //  菜单
        // ================================================================
        [MenuItem("UnityLITF/仪玄/⑦ 重定向基准检查（编辑模式）")]
        public static void Run()
        {
            var root = GameObject.Find(RootName);
            if (root == null) { Debug.LogError("[RetargetProbe] 场景里找不到 " + RootName); return; }
            var anim = root.GetComponent<Animator>();
            if (anim == null || anim.avatar == null)
            {
                Debug.LogError("[RetargetProbe] " + RootName + " 上没有 Animator/Avatar");
                return;
            }
            if (Application.isPlaying)
                Debug.LogWarning("[RetargetProbe] 建议在**编辑模式**跑（Play 模式下场景里已有动画在驱动骨骼）。");

            Debug.Log(Measure(root, anim.avatar));
        }

        // ================================================================
        //  测量
        // ================================================================
        static string Measure(GameObject character, Avatar charAvatar)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========== 重定向基准检查（肌肉全 0）==========");

            // ---- 参照：Mixamo 原生骨架 ----
            GameObject reference = null;
            HumanPoseHandler hRef = null, hChar = null;
            List<Transform> owner = new List<Transform>();
            List<Vector3> pos = new List<Vector3>();
            List<Quaternion> rot = new List<Quaternion>();

            try
            {
                reference = LoadReference();
                if (reference == null)
                {
                    sb.AppendLine("✘ 参照实例化失败: " + ReferencePath);
                    return sb.ToString();
                }
                var refAnim = reference.GetComponentInChildren<Animator>(true);
                Avatar refAvatar = refAnim != null ? refAnim.avatar : null;
                if (refAvatar == null || !refAvatar.isValid || !refAvatar.isHuman)
                    refAvatar = AvatarOf(ReferencePath);          // 兜底：直接从 FBX 资产上取
                if (refAvatar == null || !refAvatar.isValid || !refAvatar.isHuman)
                {
                    sb.AppendLine("✘ 参照 Avatar 无效（检查 " + ReferencePath + " 的 Rig 是否 Humanoid）");
                    return sb.ToString();
                }
                sb.AppendLine("参照: " + ReferencePath + "  Avatar=" + refAvatar.name
                    + " isValid=" + refAvatar.isValid + " isHuman=" + refAvatar.isHuman);
                sb.AppendLine("被测: " + character.name + "  Avatar=" + charAvatar.name
                    + " isValid=" + charAvatar.isValid + " isHuman=" + charAvatar.isHuman
                    + "  assetPath=" + PathOf(charAvatar));

                // ---- 快照角色的全部 Transform（结束时还原）----
                foreach (var t in character.GetComponentsInChildren<Transform>(true))                {
                    owner.Add(t);
                    pos.Add(t.localPosition);
                    rot.Add(t.localRotation);
                }

                hRef = new HumanPoseHandler(refAvatar, reference.transform);
                hChar = new HumanPoseHandler(charAvatar, character.transform);

                ApplyZeroMuscles(hRef);
                ApplyZeroMuscles(hChar);

                // ---- 8 个骨段 ----
                var names = new[]
                {
                    "Left UpperArm", "Left LowerArm",
                    "Right UpperArm", "Right LowerArm",
                    "Left UpperLeg", "Left LowerLeg",
                    "Right UpperLeg", "Right LowerLeg",
                };
                var bones = new[]
                {
                    HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm,
                    HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm,
                    HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg,
                    HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg,
                };
                var nexts = new[]
                {
                    HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                    HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                    HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
                    HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
                };

                var refHip = HipOf(refAnim);
                var charHip = HipOf(character.GetComponent<Animator>());
                if (refHip == null || charHip == null)
                {
                    sb.AppendLine("✘ 取不到 Hips（" + (refHip == null ? "参照 " : "") + (charHip == null ? "被测" : "") + "）");
                    return sb.ToString();
                }

                sb.AppendLine();
                sb.AppendLine(string.Format("{0,-16} {1,10} {2,10} {3,10}",
                    "骨段", "参照°", "被测°", "夹角°"));
                sb.AppendLine("--------------------------------------------------");

                float maxDeg = 0f, armMax = 0f;
                var bad = new List<string>();

                for (int i = 0; i < names.Length; i++)
                {
                    var dRef = DirInHip(refHip, hRef, refAnim, bones[i], nexts[i]);
                    var dChr = DirInHip(charHip, hChar, character.GetComponent<Animator>(), bones[i], nexts[i]);
                    if (dRef == Vector3.zero || dChr == Vector3.zero)
                    {
                        sb.AppendLine(string.Format("{0,-16} {1,10} {2,10} {3,10}   ✘ 取不到骨",
                            names[i], "—", "—", "—"));
                        continue;
                    }
                    float ang = Vector3.Angle(dRef, dChr);
                    maxDeg = Mathf.Max(maxDeg, ang);
                    bool isArm = i < 4;
                    if (isArm) armMax = Mathf.Max(armMax, ang);
                    if (ang > (isArm ? 5f : 8f)) bad.Add(names[i]);

                    sb.AppendLine(string.Format("{0,-16} {1,10:F1} {2,10:F1} {3,10:F1}   {4}",
                        names[i], DegFromHorizontal(dRef), DegFromHorizontal(dChr), ang,
                        ang > (isArm ? 5f : 8f) ? "✘" : "✔"));
                }

                sb.AppendLine("--------------------------------------------------");
                sb.AppendLine("（参照°/被测° = 该段方向距水平面的角度，只作参考；判据是末尾的 max_deg）");
                if (bad.Count > 0) sb.AppendLine("超阈值骨段: " + string.Join(", ", bad.ToArray()));
                sb.AppendLine("RETARGET_PROBE max_deg=" + maxDeg.ToString("F1")
                    + " arm_max_deg=" + armMax.ToString("F1"));
            }
            finally
            {
                // ---- 还原角色骨骼（无论如何都还原）----
                int n = Mathf.Min(owner.Count, Mathf.Min(pos.Count, rot.Count));
                for (int i = 0; i < n; i++)
                {
                    if (owner[i] == null) continue;
                    owner[i].localPosition = pos[i];
                    owner[i].localRotation = rot[i];
                }
                if (hRef != null) hRef.Dispose();
                if (hChar != null) hChar.Dispose();
                if (reference != null) UnityEngine.Object.DestroyImmediate(reference);
            }

            return sb.ToString();
        }

        // ================================================================
        //  工具
        // ================================================================

        static GameObject LoadReference()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(ReferencePath);
            if (asset == null) return null;
            var inst = (GameObject)UnityEngine.Object.Instantiate(asset);
            inst.name = "__RetargetProbe_Ref";
            inst.hideFlags = HideFlags.HideAndDontSave;
            foreach (var t in inst.GetComponentsInChildren<Transform>(true))
                t.gameObject.hideFlags = HideFlags.HideAndDontSave;
            inst.transform.position = Vector3.zero;
            inst.transform.rotation = Quaternion.identity;

            var a = inst.GetComponentInChildren<Animator>(true);
            if (a != null)
            {
                a.applyRootMotion = false;
                a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                if (a.avatar == null) a.avatar = AvatarOf(ReferencePath);
                a.enabled = false;      // 不让它自己动；姿势由 HumanPoseHandler 写
            }
            return inst;
        }

        static Avatar AvatarOf(string fbxPath)
        {
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
            {
                var a = o as Avatar;
                if (a != null && a.isValid && a.isHuman) return a;
            }
            return null;
        }

        static string PathOf(Avatar av)
        {
            if (av == null) return "(null)";
            string p = AssetDatabase.GetAssetPath(av);
            return string.IsNullOrEmpty(p) ? "(嵌在场景里，没有资产路径)" : p;
        }

        /// <summary>取到肌肉全 0 的姿势：先 Get 拿到结构（长度/body 字段），再只清肌肉。</summary>
        static void ApplyZeroMuscles(HumanPoseHandler h)
        {
            var p = new HumanPose();
            h.GetHumanPose(ref p);
            p.muscles = new float[p.muscles != null ? p.muscles.Length : 0];
            h.SetHumanPose(ref p);
        }

        static Transform HipOf(Animator a)
        {
            if (a == null) return null;
            if (a.isHuman)
            {
                var t = a.GetBoneTransform(HumanBodyBones.Hips);
                if (t != null) return t;
            }
            // 兜底：按名字找（Avatar 映射不全时也不至于整张表都取不到骨）
            Transform byName = Yixuan.Model.MmdBoneNames.Resolve("Center", a.transform);
            if (byName != null) return byName;
            return a.transform;   // 最后兜底：用根节点当局部坐标系原点
        }

        static Vector3 BoneDir(Animator a, HumanPoseHandler h, HumanBodyBones b, HumanBodyBones n)
        {
            var t0 = ResolveBone(a, h, b);
            var t1 = ResolveBone(a, h, n);
            if (t0 == null || t1 == null) return Vector3.zero;
            var d = t1.position - t0.position;
            return d.sqrMagnitude > 1e-10f ? d.normalized : Vector3.zero;
        }

        /// <summary>取 Humanoid 骨：优先走 HumanPoseHandler / Animator，取不到再按 MMD 骨名兜底。</summary>
        static Transform ResolveBone(Animator a, HumanPoseHandler h, HumanBodyBones b)
        {
            if (a != null && a.isHuman)
            {
                var t = a.GetBoneTransform(b);
                if (t != null) return t;
            }
            if (a == null) return null;
            string semantic = SemanticOf(b);
            if (semantic == null) return null;
            return Yixuan.Model.MmdBoneNames.Resolve(semantic, a.transform);
        }

        /// <summary>HumanBodyBones → MmdBoneNames 的语义名（只列本探针用到的）。</summary>
        static string SemanticOf(HumanBodyBones b)
        {
            switch (b)
            {
                case HumanBodyBones.Hips: return "Center";
                case HumanBodyBones.LeftUpperArm: return "ArmL";
                case HumanBodyBones.LeftLowerArm: return "ElbowL";
                case HumanBodyBones.LeftHand: return "WristL";
                case HumanBodyBones.RightUpperArm: return "ArmR";
                case HumanBodyBones.RightLowerArm: return "ElbowR";
                case HumanBodyBones.RightHand: return "WristR";
                case HumanBodyBones.LeftUpperLeg: return "UpperLegL";
                case HumanBodyBones.LeftLowerLeg: return "LowerLegL";
                case HumanBodyBones.LeftFoot: return "FootL";
                case HumanBodyBones.RightUpperLeg: return "UpperLegR";
                case HumanBodyBones.RightLowerLeg: return "LowerLegR";
                case HumanBodyBones.RightFoot: return "FootR";
                default: return null;
            }
        }

        /// <summary>把骨段方向换算到各自 Hips 的局部坐标系里（两个模型朝向不同，必须这样才可比）。</summary>
        static Vector3 DirInHip(Transform hip, HumanPoseHandler h, Animator a,
                                HumanBodyBones b, HumanBodyBones n)
        {
            var d = BoneDir(a, h, b, n);
            if (d == Vector3.zero) return Vector3.zero;
            // 用角色根节点的坐标系，不用 Hips 骨：Mixamo 和 MMD 的 Hips 局部轴朝向本来就不同，
            // 换进 Hips 局部系反而不可比。两个模型都面朝 +Z，根节点系可以直接比。
            return a.transform.InverseTransformDirection(d).normalized;
        }

        /// <summary>方向距水平面的角度（度），只作肉眼参考。</summary>
        static float DegFromHorizontal(Vector3 dir)
        {
            float horiz = new Vector2(dir.x, dir.z).magnitude;
            return Mathf.Atan2(Mathf.Abs(dir.y), horiz) * Mathf.Rad2Deg;
        }
    }
}
#endif
