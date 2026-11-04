#if UNITY_EDITOR
// ============================================================================
//  SwapModel —— 把 PMX 导入并**构建成场景里的模型**（不改动 Yixuan_Test 上的脚本）
//
//  这一版只做「构建」：把庄方宜建成一个独立的 GameObject（含 Avatar / 材质 /
//  41 个表情），放在场景里 origin 上，方便先单独验证。
//  验证通过后再做「换装」——把 Yixuan_Test 的子物体换掉、重绑骨骼引用。
//
//  菜单：UnityLITF/换模型/① 导入庄方宜并构建
//        UnityLITF/换模型/② 删除构建的临时模型
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Yixuan.ModelSwap
{
    public static class SwapModel
    {
        public const string BuildName = "Zhuangfy_BUILD";
        public const string SrcPmx = "Assets/Models/YOUR_MODEL.pmx"; // 换成你自己的 PMX 模型路径

        public const string ProjFolder = "Assets/Models/Zhuangfy";
        public const string TexFolder = ProjFolder + "/Textures";

        [MenuItem("UnityLITF/换模型/① 导入庄方宜并构建", false, 1)]
        public static void Build()
        {
            string pmx = SrcPmx;
            if (!File.Exists(pmx))
            {
                // 允许把 pmx 直接拷进项目目录
                string local = ProjFolder + "/庄方宜.pmx";
                if (File.Exists(local)) pmx = Path.GetFullPath(local);
                else { Debug.LogError("[SwapModel] 找不到 PMX：" + SrcPmx); return; }
            }

            // 先清掉上一次的构建
            DestroyBuild();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            PmxImporter.Result res;
            try
            {
                res = PmxImporter.Import(pmx, TexFolder, ProjFolder, "Zhuangfy");
            }
            catch (Exception e)
            {
                Debug.LogError("[SwapModel] 导入失败：" + e
                    + "\n---------- 崩溃前的解析进度 ----------\n"
                    + string.Join("\n", PmxImporter.LastLog.ToArray()));
                return;
            }
            sw.Stop();

            var root = res.Prefab;
            root.name = BuildName;
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;

            // 保存 Mesh 成资产，否则重启 Unity 会丢
            string meshDir = ProjFolder + "/Mesh";
            EnsureFolder(meshDir);
            string meshPath = meshDir + "/Zhuangfy_Mesh.asset";
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(res.Mesh, meshPath);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========== 庄方宜 导入结果 ==========");
            sb.AppendLine(string.Format("耗时 {0:F1}s", sw.Elapsed.TotalSeconds));
            foreach (var l in res.Log) sb.AppendLine("  " + l);
            sb.AppendLine();

            // Avatar 验证
            var av = res.Avatar;
            sb.AppendLine("--- Avatar ---");
            sb.AppendLine(string.Format("  isValid={0}  isHuman={1}  humanBoneCount={2}",
                av != null && av.isValid, av != null && av.isHuman, av != null ? av.humanDescription.human.Length : -1));
            sb.AppendLine(string.Format("  映射成功 {0} 个 Humanoid 骨骼，缺失 {1} 个",
                res.MappedHumanBones, res.MissingHumanBones));
            foreach (var m in res.MissingBones) sb.AppendLine("    ✘ " + m);

            // 关键验证：腿链到底映射到了「有权重的骨」还是「0 权重的骨」
            sb.AppendLine();
            sb.AppendLine("--- 腿链映射验证（权重顶点数，0 = 网格不会动）---");
            var smr = root.GetComponentInChildren<SkinnedMeshRenderer>();
            var boneWeightCount = CountBoneWeights(smr);
            var anim = root.GetComponent<Animator>();
            foreach (var hn in new[] { "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes",
                                       "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes",
                                       "Hips", "Spine", "Chest", "Head",
                                       "LeftUpperArm", "LeftLowerArm", "LeftHand" })
            {
                var hb = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), hn);
                var t = anim.GetBoneTransform(hb);
                int wc = (t != null && boneWeightCount.ContainsKey(t.name)) ? boneWeightCount[t.name] : -1;
                sb.AppendLine(string.Format("  {0,-16} → {1,-12} 权重顶点 {2,6}  {3}",
                    hn, t != null ? t.name : "(无)", wc,
                    wc == 0 ? "  ❌ 网格不会变形！" : (wc > 0 ? "  ✔" : "")));
            }

            sb.AppendLine();
            sb.AppendLine("--- BlendShape（表情）---");
            sb.AppendLine("  共 " + res.Mesh.blendShapeCount + " 个");
            var names = new List<string>();
            for (int i = 0; i < res.Mesh.blendShapeCount; i++) names.Add(res.Mesh.GetBlendShapeName(i));
            sb.AppendLine("  " + string.Join(" / ", names.ToArray()));

            sb.AppendLine();
            sb.AppendLine("--- 子网格 / 材质 ---");
            sb.AppendLine(string.Format("  subMesh {0} 个", res.Mesh.subMeshCount));
            foreach (var m in smr.sharedMaterials)
                sb.AppendLine("    " + (m != null ? m.name : "null"));

            Debug.Log(sb.ToString());

            EditorSceneManager.MarkSceneDirty(root.scene);
            Selection.activeGameObject = root;
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        static Dictionary<string, int> CountBoneWeights(SkinnedMeshRenderer smr)
        {
            var counts = new Dictionary<string, int>();
            if (smr == null || smr.sharedMesh == null) return counts;
            foreach (var b in smr.bones) if (b != null) counts[b.name] = 0;
            var bws = smr.sharedMesh.boneWeights;
            for (int i = 0; i < bws.Length; i++)
            {
                var w = bws[i];
                Bump(counts, smr, w.boneIndex0, w.weight0);
                Bump(counts, smr, w.boneIndex1, w.weight1);
                Bump(counts, smr, w.boneIndex2, w.weight2);
                Bump(counts, smr, w.boneIndex3, w.weight3);
            }
            return counts;
        }

        static void Bump(Dictionary<string, int> c, SkinnedMeshRenderer smr, int idx, float w)
        {
            if (w <= 1e-4f || idx < 0 || idx >= smr.bones.Length || smr.bones[idx] == null) return;
            string n = smr.bones[idx].name;
            if (c.ContainsKey(n)) c[n]++; else c[n] = 1;
        }

        [MenuItem("UnityLITF/换模型/② 删除构建的临时模型", false, 2)]
        public static void DestroyBuild()
        {
            var existing = GameObject.Find(BuildName);
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
        }

        // ====================================================================
        //  正式换装
        // ====================================================================
        public const string CharRoot = "Yixuan_Test";

        /// <summary>
        /// 把 Yixuan_Test 下的旧模型换成构建好的庄方宜。
        ///
        /// 【为什么要先备份】换装是不可逆操作（会删掉旧的 仪玄 网格层级）。
        /// 先把旧层级改名隐藏（不删），确认没问题再用菜单④清理；
        /// 出问题可以用菜单⑤一键回退。
        /// </summary>
        [MenuItem("UnityLITF/换模型/④ 正式换装（保留根节点与组件）", false, 20)]
        public static void Apply()
        {
            var root = GameObject.Find(CharRoot);
            if (root == null) { Debug.LogError("[SwapModel] 找不到 " + CharRoot); return; }
            var build = GameObject.Find(BuildName);
            if (build == null)
            {
                Debug.LogError("[SwapModel] 找不到 " + BuildName + "，先跑菜单①");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========== 正式换装 ==========");

            // ---- 0) 记录换装前的状态（验收标准 1 要求根节点不变） ----
            Vector3 p0 = root.transform.position;
            Quaternion r0 = root.transform.rotation;
            Vector3 s0 = root.transform.localScale;
            var comps0 = root.GetComponents<Component>();
            sb.AppendLine(string.Format("根节点 {0}: pos={1} rot={2} scale={3}  组件 {4} 个",
                root.name, p0, r0.eulerAngles, s0, comps0.Length));

            // ---- 1) 备份旧的模型层级 ----
            // ★ 不能盲取 child[0] —— `secondary`（弹簧碰撞体，属于场景原有资产）
            //   有可能正好排在第 0 位，会被误改名/误禁用（实测踩过）。
            //   这里显式排除必须保留的对象。
            Transform oldModel = null;
            for (int i = 0; i < root.transform.childCount; i++)
            {
                var c = root.transform.GetChild(i);
                if (c.name == "secondary") continue;                 // 场景原有资产，绝不动
                if (c.name.StartsWith("__BACKUP_")) continue;        // 已经是备份
                if (c.GetComponent<SkinnedMeshRenderer>() != null) { oldModel = c; break; }  // 旧网格
                if (c.name.StartsWith("庄方宜") || c.name.StartsWith("仪玄")) { oldModel = c; break; }
                if (oldModel == null) oldModel = c;                  // 兜底：第一个非 secondary 的
            }
            if (oldModel != null)
            {
                // 连带删掉它引用的骨骼根（否则旧骨骼会留在场景里）
                string backupName = "__BACKUP_" + oldModel.name;
                oldModel.name = backupName;
                oldModel.gameObject.SetActive(false);
                sb.AppendLine("已备份旧模型 '" + backupName + "' 并禁用（未删除）");
            }

            // ---- 2) 把新模型的骨骼根节点挂到 root 下（保持世界变换） ----
            var newRoots = new List<Transform>();
            for (int i = build.transform.childCount - 1; i >= 0; i--)
            {
                var c = build.transform.GetChild(i);
                if (c.GetComponent<SkinnedMeshRenderer>() != null) continue;   // 网格稍后单独处理
                newRoots.Add(c);
            }
            foreach (var c in newRoots)
            {
                c.SetParent(root.transform, true);
                sb.AppendLine("  移入骨骼根: " + c.name);
            }

            // ---- 3) 网格 ----
            var smrSrc = build.GetComponentInChildren<SkinnedMeshRenderer>();
            var meshGo = smrSrc.gameObject;
            meshGo.transform.SetParent(root.transform, true);
            meshGo.name = "庄方宜_mesh";
            sb.AppendLine("  移入网格: " + meshGo.name + " verts=" + smrSrc.sharedMesh.vertexCount);

            // ---- 4) Avatar ----
            var animSrc = build.GetComponent<Animator>();
            var animDst = root.GetComponent<Animator>();
            if (animDst == null) animDst = root.AddComponent<Animator>();
            animDst.avatar = animSrc.avatar;
            sb.AppendLine("  Avatar 已赋给根节点 Animator: isValid=" + (animDst.avatar != null && animDst.avatar.isValid)
                + " isHuman=" + (animDst.avatar != null && animDst.avatar.isHuman));

            // ---- 5) 重绑 YixuanCharacter 上的骨骼引用 ----
            var find = new System.Collections.Generic.Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (!find.ContainsKey(t.name)) find[t.name] = t;
            System.Func<string, Transform> F = null;
            F = (n) => { Transform x; return find.TryGetValue(n, out x) ? x : null; };

            var yc = root.GetComponent<Yixuan.Core.YixuanCharacter>();
            if (yc != null)
            {
                // ★ 用组件自己的公共方法重绑（内部走 MmdBoneNames 中央表），
                //   不用反射 —— 反射 SetValue 在这个项目里出现过「看着设上了、
                //   存盘后又是 null」的情况，直接调用更可靠。
                yc.RebindBones();
                sb.AppendLine(string.Format(
                    "  YixuanCharacter 重绑: Head={0} ArmR={1} ArmL={2} ElbowR={3} WristR={4} Center={5}",
                    yc.HeadBone != null ? yc.HeadBone.name : "✘",
                    yc.ArmR != null ? yc.ArmR.name : "✘",
                    yc.ArmL != null ? yc.ArmL.name : "✘",
                    yc.ElbowR != null ? yc.ElbowR.name : "✘",
                    yc.WristR != null ? yc.WristR.name : "✘",
                    yc.Center != null ? yc.Center.name : "✘"));
                UnityEditor.EditorUtility.SetDirty(yc);
            }

            // ---- 5b) ★ 重建 UniHumanoid.Humanoid 的骨映射 ----
            //   必须做！Vrm10Instance 内部用的是 Humanoid 组件的映射（不是 Animator 的 Avatar）。
            //   不重建的话 Vrm10Runtime.cs:70 的
            //       if (!instance.TryGetBoneTransform(HumanBodyBones.Head, out m_head)) throw new Exception();
            //   会每帧抛异常刷屏（Play/LateUpdate/OnDrawGizmosSelected 都会触发）。
            var hum = root.GetComponent<UniHumanoid.Humanoid>();
            if (hum != null)
            {
                bool okBones = hum.AssignBonesFromAnimator();
                bool headOk = hum.GetBoneTransform(UnityEngine.HumanBodyBones.Head) != null;
                UnityEditor.EditorUtility.SetDirty(hum);
                sb.AppendLine(string.Format("  UniHumanoid.Humanoid 骨映射重建: AssignBonesFromAnimator={0}  Head={1}",
                    okBones, headOk ? "✔" : "✘"));
            }

            // ---- 5c) ★ 解除可能残留的"调试冻结"姿势 ----
            //   LockGesturePose=true 会让 UpdateGesture 不再推进 → 角色永远卡在一帧。
            //   这个字段很容易被 DebugFreezeGesture 改脏并序列化进场景。
            {
                var c = root.GetComponent<Yixuan.Core.YixuanCharacter>();
                if (c != null)
                {
                    if (c.LockGesturePose || !c.AutoPlay)
                        sb.AppendLine(string.Format("  解除调试冻结: LockGesturePose {0}→False, AutoPlay {1}→True",
                            c.LockGesturePose, c.AutoPlay));
                    c.LockGesturePose = false;
                    c.AutoPlay = true;
                    UnityEditor.EditorUtility.SetDirty(c);
                }
            }

            // ---- 6) GroundSnap 重新找接地点 ----
            var gs = root.GetComponent<Yixuan.Motion.GroundSnap>();
            if (gs != null)
            {
                gs.Relink();
                sb.AppendLine("  GroundSnap.Relink() -> " + gs.Status().Replace("\n", " | "));
            }

            // ---- 7) MmdDeformBridge 重新连接 ----
            var bridge = root.GetComponent<Yixuan.Motion.MmdDeformBridge>();
            if (bridge != null)
            {
                bridge.Relink();
                sb.AppendLine("  MmdDeformBridge 已连接 " + "（庄方宜腿链直映 D 骨，此组件为空操作）");
            }

            // ---- 8) 清掉临时构建物 ----
            UnityEngine.Object.DestroyImmediate(build);
            sb.AppendLine("已删除临时构建物 " + BuildName);

            // ---- 9) 验收标准 1：根节点必须没变 ----
            sb.AppendLine();
            sb.AppendLine("--- 根节点校验（验收标准 1）---");
            sb.AppendLine(string.Format("  pos   {0} -> {1}  {2}", p0, root.transform.position,
                (p0 - root.transform.position).magnitude < 1e-6f ? "✔ 未变" : "✘ 变了"));
            sb.AppendLine(string.Format("  rot   {0} -> {1}  {2}", r0.eulerAngles, root.transform.rotation.eulerAngles,
                Quaternion.Angle(r0, root.transform.rotation) < 1e-4f ? "✔ 未变" : "✘ 变了"));
            sb.AppendLine(string.Format("  scale {0} -> {1}  {2}", s0, root.transform.localScale,
                (s0 - root.transform.localScale).magnitude < 1e-6f ? "✔ 未变" : "✘ 变了"));
            sb.AppendLine("  组件 " + comps0.Length + " -> " + root.GetComponents<Component>().Length);
            foreach (var c in comps0)
                sb.AppendLine("    - " + c.GetType().Name + (root.GetComponent(c.GetType()) != null ? " ✔" : " ✘ 丢了"));

            Debug.Log(sb.ToString());
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.scene);
            UnityEditor.Selection.activeGameObject = root;
        }

        /// <summary>回退：把备份的旧模型恢复，删掉新模型</summary>
        [MenuItem("UnityLITF/换模型/⑤ 回退到换装前", false, 21)]
        public static void Revert()
        {
            var root = GameObject.Find(CharRoot);
            if (root == null) { Debug.LogError("[SwapModel] 找不到 " + CharRoot); return; }
            Transform backup = null;
            var toDelete = new List<GameObject>();
            for (int i = 0; i < root.transform.childCount; i++)
            {
                var c = root.transform.GetChild(i);
                if (c.name.StartsWith("__BACKUP_")) backup = c;
                else if (c.name != "secondary") toDelete.Add(c.gameObject);
            }
            foreach (var g in toDelete) UnityEngine.Object.DestroyImmediate(g);
            if (backup != null)
            {
                backup.name = backup.name.Substring("__BACKUP_".Length);
                backup.gameObject.SetActive(true);
                Debug.Log("[SwapModel] 已回退，恢复 " + backup.name);
            }
            else Debug.LogWarning("[SwapModel] 没有找到备份，无法回退");
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.scene);
        }

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            var leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
#endif
