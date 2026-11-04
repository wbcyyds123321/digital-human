using UnityEngine;
using System.Collections.Generic;

namespace Yixuan.Motion
{
    /// <summary>
    /// 控制骨 → 变形骨 桥接（MMD "D骨" 体系专用）。
    ///
    /// 【问题】
    /// 这个 MMD 模型的腿部有**两套并行的骨骼链**，挂在同一个父级下：
    ///
    ///     腰キャンセル.L
    ///     ├── 足.L  → ひざ.L  → 足首.L  → つま先.L      ← 控制骨（0 个顶点权重）
    ///     └── 足D.L → ひざD.L → 足首D.L → 足先EX.L     ← 变形骨（D = Deform，真正蒙皮）
    ///
    /// Unity 的 Humanoid Avatar 自动映射把 LeftUpperLeg/LeftLowerLeg/LeftFoot/LeftToes
    /// 映射到了 **控制骨**（足.L / ひざ.L / 足首.L / つま先.L）。
    /// 实测权重：
    ///     足.L   0 顶点      足D.L   1112 顶点
    ///     ひざ.L 0 顶点      ひざD.L  385 顶点
    ///     足首.L 0 顶点      足首D.L  531 顶点
    ///     つま先.L 0 顶点    足先EX.L 155 顶点
    ///
    /// 结果：动画确实在驱动控制骨（骨骼旋转是真的），
    /// **但网格一个顶点都不受影响** → 表现为"腿完全不弯、上半身带着腿晃"。
    ///
    /// 【为什么上肢没事】
    /// 手臂没有 D 骨，Unity 映射到的 腕/ひじ/手首 本身就是变形骨，所以正常。
    ///
    /// 【解法】
    /// 每帧把控制骨的**世界旋转**同步到对应的 D 骨上。
    /// 好在这些 D 骨的 localRotation 与恒等只差 0.00°（完全跟随父级），
    /// 所以直接写 localRotation = 控制骨的 localRotation 即可，不需要补偿。
    /// （足先EX 差 180°，但它是最末端的脚趾尖，跟随 足首D 就够）
    ///
    /// 【备选方案（更彻底但更重）】
    /// 重新生成 Avatar，把 LeftUpperLeg 等直接映射到 D 骨。
    /// 但那样要重建整个 Humanoid 映射，风险大；桥接是零风险的加法。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(200)]   // LateUpdate 里早于 GroundSnap(10000)
    public class MmdDeformBridge : MonoBehaviour
    {
        [Header("开关")]
        public bool Enabled = true;

        [Header("调试")]
        public bool VerboseLog = false;

        /// <summary>控制骨 → 变形骨 的名字对</summary>
        static readonly string[][] Pairs =
        {
            // 腿（左）—— 仪玄的命名（`足.L` → `足D.L`）
            new[] { "足.L",      "足D.L"    },
            new[] { "ひざ.L",    "ひざD.L"  },
            new[] { "足首.L",    "足首D.L"  },
            // 腿（右）
            new[] { "足.R",      "足D.R"    },
            new[] { "ひざ.R",    "ひざD.R"  },
            new[] { "足首.R",    "足首D.R"  },
        };

        /// <summary>
        /// 庄方宜（`左足` → `左足D`）的命名。
        ///
        /// ★ 但在庄方宜上这个桥接是**不需要的** —— 因为
        ///   `PmxImporter.HumanMap` 已经把 Humanoid 的
        ///   LeftUpperLeg/LowerLeg/Foot/Toes **直接映射到 D 骨**，
        ///   控制骨从头到尾都没人动，也就没有「控制骨在动、网格不动」的问题。
        ///
        ///   保留这张表 + 组件本身，是为了：
        ///     ① 万一以后要回退到仪玄（`__BACKUP_仪玄`）不用重配
        ///     ② 万一以后想把 Avatar 改回映射控制骨，可以直接启用
        /// </summary>
        static readonly string[][] PairsZhuangfy =
        {
            new[] { "左足",   "左足D"   },
            new[] { "左ひざ", "左ひざD" },
            new[] { "左足首", "左足首D" },
            new[] { "右足",   "右足D"   },
            new[] { "右ひざ", "右ひざD" },
            new[] { "右足首", "右足首D" },
        };

        /// <summary>
        /// 启用庄方宜命名（默认关闭）。
        /// 只有在「Avatar 映射回控制骨」时才需要打开 —— 见上面说明。
        /// </summary>
        public bool UseZhuangfyNaming = false;

        string[][] ActivePairs()
        {
            return UseZhuangfyNaming ? PairsZhuangfy : Pairs;
        }

        /// <summary>
        /// 【辅助变形骨】—— 默认**不桥接**。
        ///
        /// 试过桥接它们（`足先EX` / `足首補助` / `足2` / `ひざ2` / `ひざ補助`，
        /// 共 561 个顶点），结果**更糟**：绑定姿态从正常变成扭曲的腿 + 碎片，
        /// 导出的 OBJ 从 63KB 涨到 166KB。
        ///
        /// 原因：这些骨虽然是 D 骨的子级，但它们带**自身的绑定偏移旋转**
        /// （例如 `足先EX.L` 的 localRot 是 (270,180,0)，与恒等差 180°），
        /// 而且它们的父级关系和主控制骨不对应
        /// （`ひざ2.L` 的父是 `ひざD.L` 而不是 `ひざ.L`）。
        /// 直接套同一个 Δ 会转错方向。
        ///
        /// **所以留空。** 实测不桥接时 idle / sit / jog 的脚渲染是正常的。
        /// 如果以后要处理更复杂的模型（上半身也有 D 骨），需要为辅助骨
        /// 单独推导映射关系，不能套用主骨的公式。
        /// </summary>
        static readonly string[][] AuxPairs =
        {
            // 故意留空 —— 见上面的说明
        };

        readonly List<Transform> _src = new List<Transform>();
        readonly List<Transform> _dst = new List<Transform>();
        /// <summary>控制骨的绑定世界朝向（rest world rotation）</summary>
        readonly List<Quaternion> _srcRestWorld = new List<Quaternion>();
        /// <summary>D 骨的绑定世界朝向（rest world rotation）</summary>
        readonly List<Quaternion> _dstRestWorld = new List<Quaternion>();
        bool _ready;
        bool _restCached;

        /// <summary>
        /// 绑定姿态（序列化保存）。
        ///
        /// ★ 为什么必须序列化，不能用静态字段/运行时抓取：
        ///   1. 进 Play 模式会触发 domain reload，静态字段全清空
        ///   2. Play 后动画立刻开始跑，Awake 时读到的 rotation 已被动画改过
        ///   两条加起来 → 运行期拿不到真实绑定姿态 → Δ 算错 → 腿转反。
        ///   所以绑定姿态在**编辑模式**下算好、写进这两个数组存进场景。
        ///   菜单：UnityLITF/仪玄/⑤ 缓存绑定姿态（桥接用）
        /// </summary>
        [HideInInspector] public Vector4[] RestSrc;   // 控制骨绑定世界旋转 (x,y,z,w)
        [HideInInspector] public Vector4[] RestDst;   // D 骨绑定世界旋转 (x,y,z,w)

        void Awake()
        {
            Relink();
        }

        /// <summary>查找骨骼对，并载入序列化的绑定姿态</summary>
        public void Relink()
        {
            _src.Clear(); _dst.Clear();
            _srcRestWorld.Clear(); _dstRestWorld.Clear();
            _restCached = false;

            var all = GetComponentsInChildren<Transform>(true);
            var map = new Dictionary<string, Transform>();
            foreach (var t in all) if (!map.ContainsKey(t.name)) map[t.name] = t;

            int missing = 0;
            foreach (var p in ActivePairs())
            {
                Transform s, d;
                if (!map.TryGetValue(p[0], out s) || !map.TryGetValue(p[1], out d))
                {
                    missing++;
                    if (VerboseLog) Debug.LogWarning("[MmdBridge] 找不到骨骼对: " + p[0] + " → " + p[1]);
                    continue;
                }
                _src.Add(s);
                _dst.Add(d);
                // 占位，稍后由序列化数据覆盖
                _srcRestWorld.Add(Quaternion.identity);
                _dstRestWorld.Add(Quaternion.identity);
            }

            // 辅助变形骨：一起桥接（它们的 Δ 来自所属主控制骨）
            int auxOk = 0;
            foreach (var p in AuxPairs)
            {
                Transform s, d;
                if (!map.TryGetValue(p[0], out s) || !map.TryGetValue(p[1], out d)) continue;
                _src.Add(s);
                _dst.Add(d);
                _srcRestWorld.Add(Quaternion.identity);
                _dstRestWorld.Add(Quaternion.identity);
                auxOk++;
            }
            if (VerboseLog && auxOk > 0)
                Debug.Log("[MmdBridge] 额外桥接辅助变形骨 " + auxOk + " 根");

            _ready = _dst.Count > 0;
            LoadRestFromSerialized();

            if (VerboseLog)
                Debug.Log("[MmdBridge] 已连接 " + _dst.Count + " 对" +
                          (_restCached ? "（绑定姿态已载入）" :
                           "（⚠ 没有绑定姿态，请跑菜单 ⑤ 缓存绑定姿态）") +
                          (missing > 0 ? ("，缺 " + missing + " 对") : ""));
        }

        /// <summary>把序列化的绑定姿态读进运行时数组</summary>
        void LoadRestFromSerialized()
        {
            if (RestSrc == null || RestDst == null) return;
            if (RestSrc.Length != _src.Count || RestDst.Length != _dst.Count) return;

            for (int i = 0; i < _src.Count; i++)
            {
                _srcRestWorld[i] = new Quaternion(RestSrc[i].x, RestSrc[i].y, RestSrc[i].z, RestSrc[i].w);
                _dstRestWorld[i] = new Quaternion(RestDst[i].x, RestDst[i].y, RestDst[i].z, RestDst[i].w);
            }
            _restCached = true;
        }

        /// <summary>
        /// 记录绑定姿态到序列化字段。**必须在编辑模式、模型处于绑定姿态时调用。**
        /// </summary>
        public void CacheRestPose()
        {
            if (_src.Count == 0) Relink();

            RestSrc = new Vector4[_src.Count];
            RestDst = new Vector4[_dst.Count];

            for (int i = 0; i < _src.Count; i++)
            {
                Quaternion sr = _src[i] != null ? _src[i].rotation : Quaternion.identity;
                Quaternion dr = _dst[i] != null ? _dst[i].rotation : Quaternion.identity;
                RestSrc[i] = new Vector4(sr.x, sr.y, sr.z, sr.w);
                RestDst[i] = new Vector4(dr.x, dr.y, dr.z, dr.w);
                _srcRestWorld[i] = sr;
                _dstRestWorld[i] = dr;
            }
            _restCached = true;
            if (VerboseLog) Debug.Log("[MmdBridge] 绑定姿态已写入序列化字段（" + _src.Count + " 对）");
        }

        /// <summary>
        /// 在 LateUpdate 跑 —— 必须在**动画播放器之后**。
        ///
        /// ★ 踩过的坑：原本写在 Update 里，而 YixuanAnimationPlayer 也在 Update 推进
        ///   动画。两者的执行顺序不确定，桥接常常读到"还没被动画更新"的控制骨旋转，
        ///   于是把旧的（甚至绑定姿态的）旋转抄给 D 骨 →
        ///   实测现象：坐下时髋骨数据确实变了（髋 0.90→0.45、大腿 3.7°→84.6°），
        ///   但导出的网格和待机几乎一样（高 1.730 vs 1.727）→ 用户看到"腿一动不动"。
        ///   LateUpdate 保证动画已经算完。
        /// </summary>
        void LateUpdate()
        {
            if (!Enabled || !_ready) return;
            if (!_restCached) return;   // 没有绑定姿态就别乱动骨骼

            // ★★ 不能直接把控制骨的 localRotation 抄给 D 骨。
            //    两条链的**绑定朝向完全不同**（实测 足.L 的 Y 是 +0.4156 朝上，
            //    足D.L 是 -0.4147 朝下），直接抄会把腿转反 ——
            //    第一次就是这么错的，结果站姿高度从 1.757 变成 1.157。
            //
            //    正确做法：算"控制骨相对其绑定姿态转了多少"（世界空间增量 Δ），
            //    再把同一个 Δ 施加到 D 骨的绑定朝向上：
            //        Δ = srcWorld × srcRestWorld⁻¹
            //        dstWorld = Δ × dstRestWorld
            for (int i = 0; i < _dst.Count; i++)
            {
                var s = _src[i];
                var d = _dst[i];
                if (s == null || d == null) continue;

                Quaternion delta = s.rotation * Quaternion.Inverse(_srcRestWorld[i]);
                Quaternion targetWorld = delta * _dstRestWorld[i];

                d.rotation = targetWorld;
            }
        }

        /// <summary>验证：统计每根 D 骨的权重顶点数，确认桥接的是真变形骨</summary>
        public string Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[MmdBridge] " + (Enabled ? "开" : "关") + "  已连接 " + _dst.Count + " 对");

            var smr = GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null || smr.sharedMesh == null) return sb.ToString();

            var bws = smr.sharedMesh.boneWeights;
            var counts = new Dictionary<string, int>();
            for (int i = 0; i < smr.bones.Length; i++)
            {
                if (smr.bones[i] == null) continue;
                counts[smr.bones[i].name] = 0;
            }
            foreach (var w in bws)
            {
                if (w.boneIndex0 >= 0 && w.weight0 > 0.001f) Bump(counts, smr, w.boneIndex0);
                if (w.boneIndex1 >= 0 && w.weight1 > 0.001f) Bump(counts, smr, w.boneIndex1);
                if (w.boneIndex2 >= 0 && w.weight2 > 0.001f) Bump(counts, smr, w.boneIndex2);
                if (w.boneIndex3 >= 0 && w.weight3 > 0.001f) Bump(counts, smr, w.boneIndex3);
            }

            for (int i = 0; i < _src.Count; i++)
            {
                string sn = _src[i] != null ? _src[i].name : "?";
                string dn = _dst[i] != null ? _dst[i].name : "?";
                int sc = counts.ContainsKey(sn) ? counts[sn] : -1;
                int dc = counts.ContainsKey(dn) ? counts[dn] : -1;
                sb.AppendLine("  " + sn.PadRight(10) + " 权重 " + sc.ToString().PadLeft(6) +
                              "   →   " + dn.PadRight(10) + " 权重 " + dc.ToString().PadLeft(6));
            }
            return sb.ToString();
        }

        static void Bump(Dictionary<string, int> c, SkinnedMeshRenderer smr, int idx)
        {
            if (idx < 0 || idx >= smr.bones.Length || smr.bones[idx] == null) return;
            string n = smr.bones[idx].name;
            if (c.ContainsKey(n)) c[n]++; else c[n] = 1;
        }

#if UNITY_EDITOR
        /// <summary>
        /// 菜单：把当前（必须是绑定姿态）的骨骼朝向写进序列化字段。
        /// 重建场景 / 换模型后需要重跑一次。
        /// </summary>
        [UnityEditor.MenuItem("UnityLITF/仪玄/⑤ 缓存绑定姿态（桥接用）")]
        public static void MenuCacheRestPose()
        {
            var go = UnityEditor.Selection.activeGameObject;
            if (go == null) go = GameObject.Find("Yixuan_Test");
            if (go == null) { Debug.LogError("[MmdBridge] 找不到角色"); return; }

            var br = go.GetComponent<MmdDeformBridge>();
            if (br == null) br = go.AddComponent<MmdDeformBridge>();

            // 先把角色复位到绑定姿态，否则缓存到的是当前动画姿势
            var mv = System.Type.GetType("Yixuan.Motion.MotionVerifier, Assembly-CSharp");
            if (mv != null)
            {
                var m = mv.GetMethod("RestoreBindPose",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (m != null) m.Invoke(null, new object[] { go });
            }

            // 缓存期间要先关掉桥接，避免它把姿态改了
            bool wasEnabled = br.Enabled;
            br.Enabled = false;
            br.VerboseLog = true;
            br.Relink();
            br.CacheRestPose();
            br.Enabled = wasEnabled;

            UnityEditor.EditorUtility.SetDirty(br);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            Debug.Log("[MmdBridge] 绑定姿态已缓存到序列化字段。\n" + br.Status());
        }

        [UnityEditor.MenuItem("UnityLITF/仪玄/查看腿骨权重（诊断）")]
        public static void MenuShowWeights()
        {
            var go = UnityEditor.Selection.activeGameObject;
            if (go == null) go = GameObject.Find("Yixuan_Test");
            if (go == null) return;
            var br = go.GetComponent<MmdDeformBridge>();
            if (br == null) { Debug.LogError("[MmdBridge] 角色上没有 MmdDeformBridge"); return; }
            br.Relink();
            Debug.Log(br.Status());
        }
#endif
    }
}
