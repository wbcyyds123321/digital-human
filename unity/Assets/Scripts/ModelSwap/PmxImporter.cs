#if UNITY_EDITOR
// ============================================================================
//  PmxImporter —— 把 MMD 的 PMX 模型导入为 Unity 原生层级（网格 + 骨骼 + 表情）
//
//  【为什么要自己写，而不是走 Blender / MMD4Mecanim】
//  这个项目依赖一条非常特定的约束：Humanoid Avatar 必须**显式映射到 MMD 的
//  「D骨」（变形骨）**，而不是 Unity 自动映射挑中的「控制骨」。
//  控制骨 0 个顶点权重（左足/左ひざ/左足首 = 0 顶点），映射到它网格就不会动
//  —— 这正是 PROJECT_NOTES 第 1 节记录的、仪玄踩了最久的坑。
//  自己解析 PMX 才能 100% 控制骨骼命名、绑定姿态和 Avatar 映射。
//
//  【坐标系】★★ 实测结论，别靠推断 ★★
//    PMX/MMD  : X 右 / Y 上 / Z **后**，模型面朝 **−Z**
//    Unity    : X 右 / Y 上 / Z 前
//
//    「面朝 −Z」是**用几何数据实测**出来的，不是猜的：
//      在头部区域（原始 y > 17）按材质统计平均 Z：
//          面(脸)   z = −0.73      睫眉  z = −0.97
//          目(眼)   z = −0.93      口内  z = −0.84
//          发(头发) z = +1.35      ← 头发在背后
//      脸和五官全在 −Z 侧、头发在 +Z 侧 ⇒ 模型面朝 −Z。
//      （我一开始按「MMD 面朝 +Z」处理，结果渲染出来永远是后脑勺。）
//
//    转换：Unity = (−x, y, −z) × Scale
//      这是**绕 Y 轴转 180°**，属于旋转（保持手性），所以：
//        · 三角形绕序**不用改**
//        · 法线**不用取反**
//      若只取反 Z 则是镜像、会翻面，必须改绕序 —— 那条路更容易错。
//
//  【骨骼位置】PMX 存的是「模型空间绝对坐标」，**不是相对父级的局部坐标**。
//    见骨骼构建那段的长注释。
//
//    【几何验证结论（2026-09 实测）】
//    最终渲染与 Blender mmd_tools 的参考渲染在**轮廓上完全一致**
//    （角/头发/手臂/裙长/靴子位置逐一对齐），说明网格、骨架、蒙皮、UV 都正确。
//    踩过的三个坑，全部已修，记录在此避免重犯：
//      ① 骨骼位置当成了局部坐标（实际是模型空间绝对坐标）→ 骨架被拉长 6.9 倍
//      ② 面朝方向当成 +Z（实测 −Z）→ 渲染出来是后脑勺
//      ③ UV 的 V 轴没翻转 → 衣服采样到贴图的错误区域，颜色全错（绿裙变灰白）
//
//  【权重】
//    PMX 的 BDEF1/2/4 与 Unity 的 BoneWeight 一一对应；
//    SDEF 没有 Unity 对应物，按 BDEF2 的权重降级（丢掉 C/R0/R1 球面修正）。
//
//  【绑定姿态】
//    PMX 不存 rest rotation（MMD 骨骼初始旋转恒为单位四元数），
//    所以绑定姿态**完全由骨骼位置 + 父子关系决定** —— 可以用位置直接算出来，
//    不需要像仪玄那样把绑定姿态序列化进场景。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Yixuan.ModelSwap
{
    /// <summary>PMX 二进制读取器</summary>
    internal class PmxReader
    {
        readonly byte[] _d;
        int _p;
        public Encoding Enc;
        public bool IsUtf16;
        public int AddUv;
        public int SzVertex, SzTex, SzMat, SzBone, SzMorph, SzRigid;

        public PmxReader(byte[] data) { _d = data; _p = 0; }

        public int Position { get { return _p; } set { _p = value; } }

        public byte U8() { return _d[_p++]; }
        public sbyte I8() { return (sbyte)_d[_p++]; }
        public ushort U16() { var v = BitConverter.ToUInt16(_d, _p); _p += 2; return v; }
        public short I16() { var v = BitConverter.ToInt16(_d, _p); _p += 2; return v; }
        public int I32() { var v = BitConverter.ToInt32(_d, _p); _p += 4; return v; }
        public float F32() { var v = BitConverter.ToSingle(_d, _p); _p += 4; return v; }

        public Vector2 V2() { float x = F32(), y = F32(); return new Vector2(x, y); }
        public Vector3 V3() { float x = F32(), y = F32(), z = F32(); return new Vector3(x, y, z); }
        public Vector4 V4() { float x = F32(), y = F32(), z = F32(), w = F32(); return new Vector4(x, y, z, w); }

        /// <summary>
        /// 读一个 PMX 字符串。
        ///
        /// ★★ 这里有一个非常容易踩的坑：
        ///   PMX 的长度前缀是**字节数**，而 .NET 的 <see cref="Encoding.GetString(byte[],int,int)"/>
        ///   要的是**字符数**。UTF-16 下两者差 2 倍 ——
        ///   直接传字节数会让每个字符串多读一倍的字符，整个字节流从第一个字符串起就错位。
        ///   （症状：材质表从第 7 项开始 surf 值变成天文数字，骨骼/表情段彻底解析失败。）
        ///   所以 UTF-16 必须除以 2；UTF-8 下字节数 == 字符数，直接用。
        /// </summary>
        public string Text()
        {
            int byteLen = I32();
            if (byteLen < 0 || _p + byteLen > _d.Length)
                throw new Exception("字符串长度越界: byteLen=" + byteLen + " @" + _p + " (fileLen=" + _d.Length + ")");

            string s;
            if (IsUtf16)
            {
                // ★ 自己按 UTF-16LE 码元解码，**不要**用 Encoding.Unicode.GetString。
                //   实测 .NET 的 UnicodeEncoding 在这里会丢掉高位码元
                //   （'目白' 只解出 '目'），虽然前缀字节数是对的、位置也对，
                //   但文字内容会烂掉，而且后续读取逻辑会连带出错。
                int n = byteLen / 2;
                var chars = new char[n];
                for (int i = 0; i < n; i++)
                    chars[i] = (char)(_d[_p + i * 2] | (_d[_p + i * 2 + 1] << 8));
                s = new string(chars);
            }
            else
            {
                s = Encoding.UTF8.GetString(_d, _p, byteLen);
            }

            _p += byteLen;
            return s;
        }

        /// <summary>诊断：读字符串并把原始十六进制一并吐出来</summary>
        public string TextHex(out string hex)
        {
            int p0 = _p;
            int byteLen = I32();
            var sbh = new StringBuilder();
            for (int i = 0; i < byteLen && _p + i < _d.Length; i++)
                sbh.Append(_d[_p + i].ToString("X2")).Append(' ');
            hex = "len=" + byteLen + " @" + p0 + " : " + sbh.ToString().Trim();
            string s;
            if (IsUtf16)
            {
                int n = byteLen / 2;
                var chars = new char[n];
                for (int i = 0; i < n; i++)
                    chars[i] = (char)(_d[_p + i * 2] | (_d[_p + i * 2 + 1] << 8));
                s = new string(chars);
            }
            else s = Encoding.UTF8.GetString(_d, _p, byteLen);
            _p += byteLen;
            return s;
        }

        /// <summary>按索引宽度读一个有符号索引（-1 表示无）</summary>
        public int Idx(int size)
        {
            switch (size)
            {
                case 1: return I8();
                case 2: return I16();
                default: return I32();
            }
        }

        public void Skip(int n) { _p += n; }
    }

    internal class PmxBone
    {
        public string Name;
        public string NameEn;
        public Vector3 Pos;             // MMD 坐标（原始单位）
        public int Parent;
        public int Flag;
        public Vector3 TailPos;
        public int TailBone = -1;
        public int InheritParent = -1;  // 付与親
        public float InheritWeight = 1f;
        public int WeightCount;         // 有该骨权重的顶点数（后面统计）
    }

    internal class PmxMaterial
    {
        public string Name, NameEn;
        public Color Diffuse;
        public Vector3 Specular;
        public float Shininess;
        public Vector3 Ambient;
        public byte Flag;
        public Color EdgeColor;
        public float EdgeSize;
        public int Tex, Sphere, Toon;
        public byte SphereMode, ToonShared;
        public int SurfaceCount;
    }

    internal class PmxMorph
    {
        public string Name;
        public byte Panel, Type;
        public int[] Indices;
        public Vector3[] Offsets;       // type 0=group(用 Offsets.x 存索引), 1=vertex
        public List<KeyValuePair<int, float>> MaterialOffsets;  // type 8=material
    }

    public static class PmxImporter
    {
        // ---- 缩放：MMD 单位 → 米 ----
        // 由实测决定：仪玄綁定姿态「髋→头」= 0.5334m，庄方宜 = 6.6907 MMD 单位。
        // 取 0.0792 让两者髋高比一致（1.0071m vs 仪玄 1.0013m，差 0.6%），
        // 保证 Mixamo 重定向后的肢体比例正确。
        // 模型总高 23.458 × 0.078 ≈ 1.83 m。
        public const float Scale = 0.078f;

        /// <summary>
        /// 【扫描专用】用指定的 twist 值，对**场景里已有的角色**重建一份 Avatar。
        ///
        /// 为什么需要它：HumanDescription 的 twist 参数只影响**重定向**，
        /// 不影响绑定姿态 —— 所以扫描时不用重导模型，只要换 `Animator.avatar`。
        /// 这样一轮扫描几秒就能跑完，而且**完全不碰场景**（风险为零）。
        /// </summary>
        public static Avatar BuildAvatarForTest(GameObject characterRoot, string field, float value)
        {
            // 备份 → 改值
            float oldU = upperArmTwist, oldL = lowerArmTwist;
            if (field == "upperArmTwist") upperArmTwist = value;
            else if (field == "lowerArmTwist") lowerArmTwist = value;

            try
            {
                var bones = new List<PmxBone>();
                var boneTf = new List<Transform>();
                var rootTf = characterRoot.transform;
                var map = Yixuan.Model.MmdBoneNames.BuildIndex(rootTf);

                // 用「HumanMap 里列出的骨名」构建骨架数组 —— 不需要原始 PMX 数据
                var skeleton = new List<SkeletonBone>();
                foreach (var t in rootTf.GetComponentsInChildren<Transform>(true))
                {
                    skeleton.Add(new SkeletonBone
                    {
                        name = t.name,
                        position = t.localPosition,
                        rotation = t.localRotation,
                        scale = t.localScale,
                    });
                }

                var human = new List<HumanBone>();
                var res = new Result();
                foreach (var pair in HumanMap)
                {
                    string humanName = pair[0], pmxName = pair[1];
                    if (string.IsNullOrEmpty(pmxName)) continue;
                    Transform t;
                    if (!map.TryGetValue(pmxName, out t)) continue;
                    if (Array.IndexOf(HumanTrait.BoneName, humanName) < 0)
                    {
                        string fx = NormalizeHumanName(humanName);
                        if (fx == null) continue;
                        humanName = fx;
                    }
                    human.Add(new HumanBone
                    {
                        boneName = pmxName,
                        humanName = humanName,
                        limit = new HumanLimit { useDefaultValues = true },
                    });
                }

                var desc = new HumanDescription
                {
                    human = human.ToArray(),
                    skeleton = skeleton.ToArray(),
                    armStretch = armStretch,
                    legStretch = legStretch,
                    upperArmTwist = upperArmTwist,
                    lowerArmTwist = lowerArmTwist,
                    upperLegTwist = upperLegTwist,
                    lowerLegTwist = lowerLegTwist,
                    feetSpacing = feetSpacing,
                    hasTranslationDoF = false,
                };
                var av = AvatarBuilder.BuildHumanAvatar(characterRoot, desc);
                av.name = string.Format("Test_{0}_{1:F2}", field, value);
                return av;
            }
            finally
            {
                upperArmTwist = oldU; lowerArmTwist = oldL;   // 一定还原
            }
        }

        /// <summary>
        /// T-pose 骨骼 rest 朝向覆盖开关。
        ///
        /// ★★ **默认关闭，且应当保持关闭。**
        ///
        /// 曾经的（错误）推理：以为"网格是 T-pose、骨骼是 A-pose，差 35°"。
        /// 重新核算后发现**差只有 3.7°** —— 骨架与网格本来就是对得上的：
        ///   网格手臂（肩→手）    方向 (0.582, −0.381) → 距水平 33.2° 向下
        ///   骨骼链（左腕→左ひじ） 方向 (0.160, −0.120) → 距水平 36.9° 向下
        /// 之前把一个方向向量算错了，才误判出"35° 不匹配"。
        ///
        /// 打开这个开关会把手臂**抬成接近水平**（像张开的树枝），是**错误的**。
        /// 保留开关只为记录这段弯路，不要再打开。
        /// </summary>
        public static bool ApplyTPoseSkeletonFix = false;

        /// <summary>手臂链抬升角度（度）。**默认 0** —— 见 ApplyTPoseSkeletonFix 的说明。</summary>
        public static float TPoseLiftDegrees = 0f;

        // ====================================================================
        //  HumanDescription 的可调参数（给"参数扫描"用）
        //
        //  Unity 用这些值决定**怎么在骨链上分配扭转**。
        //  因为 15 个动作的手臂**系统性地整体偏后**，怀疑就是这里分配错了 ——
        //  这类"所有 clip 同方向偏移"的现象，典型成因就是重定向的 twist 参数。
        //
        //  扫描方法：改这里的值 → 重新导入 → 跑 AllActionsVerifier.MeasureArms()
        //  看「肘部 z < −0.09 的动作数」哪个值最少。
        // ====================================================================
        public static float armStretch = 0.05f;
        public static float legStretch = 0.05f;
        public static float upperArmTwist = 0.5f;
        public static float lowerArmTwist = 0.5f;
        public static float upperLegTwist = 0.5f;
        public static float lowerLegTwist = 0.5f;
        public static float feetSpacing = 0f;

        /// <summary>
        /// 取一根骨在 rest 下的「骨方向」：优先用它的第一个子骨指向，没有子骨则用尾巴。
        /// MMD 的骨骼方向由 head→tail（或 head→子骨）决定。
        /// </summary>
        static Vector3 GetBoneDir(Transform t)
        {
            if (t == null) return Vector3.zero;
            // 优先找"真正的下一节"骨骼（子骨里挑一个名字不像辅助骨的）
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                var d = c.position - t.position;
                if (d.sqrMagnitude > 1e-6f) return d;
            }
            // 没有子骨 → 用父→自身方向（末梢骨）
            if (t.parent != null) return t.position - t.parent.position;
            return Vector3.zero;
        }

        /// <summary>
        /// 三角形绕序是否翻转。
        ///
        /// 【为什么需要这个开关】
        ///   MMD/DirectX 系的正面绕序与 Unity(OpenGL 系) 相反，理论上要翻。
        ///   但这里我们对位置做了**绕 Y 180° 的旋转**（不是镜像），手性不变，
        ///   所以绕序理论上**不用翻**。
        ///   实测判据：若衣服外面看不见、只看到内衬（或整体"透"），就是绕序反了。
        /// </summary>
        public static bool FlipWinding = false;

        /// <summary>
        /// UV 的 V 轴是否翻转。
        ///
        /// 判据（实测）：衣服材质 Cloth1 的平均 UV 是 (0.47, 0.217)。
        ///   不翻 → 采样到贴图的灰白区域（渲染出来衣服是灰的）
        ///   翻   → 采样到 (0.47, 0.783)，正好是贴图里的**绿色裙摆**区域 ✔
        /// 所以 PMX 的 UV 需要 V 取反才能对上 Unity 的贴图坐标。
        /// </summary>
        public static bool FlipV = true;

        /// <summary>
        /// 坐标约定。0 = 面朝 −Z（实测当前模型），1 = 面朝 +Z。
        /// 只用于 A/B 对照实验。
        /// </summary>
        public static int CoordMode = 0;

        /// <summary>
        /// PMX 模型空间 → Unity 模型空间。
        /// 见文件头「坐标系」说明。
        ///   CoordMode 0：Unity = (−x, y, −z)   绕 Y 180°，保持手性
        ///   CoordMode 1：Unity = ( x, y, −z)   只翻 Z（镜像）
        /// </summary>
        public static Vector3 ToUnity(Vector3 mmd)
        {
            if (CoordMode == 1) return new Vector3(mmd.x * Scale, mmd.y * Scale, -mmd.z * Scale);
            return new Vector3(-mmd.x * Scale, mmd.y * Scale, -mmd.z * Scale);
        }

        // ---- Unity Humanoid → PMX 骨骼名 映射 ----
        // ★ 关键：腿链映射到 **D骨（变形骨）**，因为控制骨 0 顶点权重。
        //   手臂没有 D 骨，直接映射到本体。
        static readonly string[][] HumanMap =
        {
            new[]{"Hips",                  "センター"},
            new[]{"Spine",                 "上半身"},
            new[]{"Chest",                 "上半身2"},
            new[]{"UpperChest",            ""},          // 该模型无对应骨，留空
            new[]{"Neck",                  "首"},
            new[]{"Head",                  "頭"},

            new[]{"LeftShoulder",          "左肩"},
            new[]{"LeftUpperArm",          "左腕"},
            new[]{"LeftLowerArm",          "左ひじ"},
            new[]{"LeftHand",              "左手首"},
            new[]{"RightShoulder",         "右肩"},
            new[]{"RightUpperArm",         "右腕"},
            new[]{"RightLowerArm",         "右ひじ"},
            new[]{"RightHand",             "右手首"},

            // ★★ 腿：映射到 D 骨（变形骨）。控制骨同名去掉 D 后 0 权重。
            new[]{"LeftUpperLeg",          "左足D"},
            new[]{"LeftLowerLeg",          "左ひざD"},
            new[]{"LeftFoot",              "左足首D"},
            new[]{"LeftToes",              "左足先EX"},
            new[]{"RightUpperLeg",         "右足D"},
            new[]{"RightLowerLeg",         "右ひざD"},
            new[]{"RightFoot",             "右足首D"},
            new[]{"RightToes",             "右足先EX"},

            new[]{"LeftEye",               "左目"},
            new[]{"RightEye",              "右目"},

            // 手指
            // 手指
            // ★ 注意：HumanTrait.BoneName / HumanBone.humanName 用的是**带空格**的写法
            //   （"Left Thumb Proximal"），而不是 HumanBodyBones 枚举的驼峰写法
            //   （LeftThumbProximal）。写错不会报错，只会静默不映射 —— 17 根手指骨全丢。
            new[]{"Left ThumbProximal",     "左親指０"},
            new[]{"Left ThumbIntermediate", "左親指１"},
            new[]{"Left ThumbDistal",       "左親指２"},
            new[]{"Left IndexProximal",     "左人指１"},
            new[]{"Left IndexIntermediate", "左人指２"},
            new[]{"Left IndexDistal",       "左人指３"},
            new[]{"Left MiddleProximal",    "左中指１"},
            new[]{"Left MiddleIntermediate","左中指２"},
            new[]{"Left MiddleDistal",      "左中指３"},
            new[]{"Left RingProximal",      "左薬指１"},
            new[]{"Left RingIntermediate",  "左薬指２"},
            new[]{"Left RingDistal",        "左薬指３"},
            new[]{"Left LittleProximal",    "左小指１"},
            new[]{"Left LittleIntermediate","左小指２"},
            new[]{"Left LittleDistal",      "左小指３"},
            new[]{"Right ThumbProximal",     "右親指０"},
            new[]{"Right ThumbIntermediate", "右親指１"},
            new[]{"Right ThumbDistal",       "右親指２"},
            new[]{"Right IndexProximal",     "右人指１"},
            new[]{"Right IndexIntermediate", "右人指２"},
            new[]{"Right IndexDistal",       "右人指３"},
            new[]{"Right MiddleProximal",    "右中指１"},
            new[]{"Right MiddleIntermediate","右中指２"},
            new[]{"Right MiddleDistal",      "右中指３"},
            new[]{"Right RingProximal",      "右薬指１"},
            new[]{"Right RingIntermediate",  "右薬指２"},
            new[]{"Right RingDistal",        "右薬指３"},
            new[]{"Right LittleProximal",    "右小指１"},
            new[]{"Right LittleIntermediate","右小指２"},
            new[]{"Right LittleDistal",      "右小指３"},
        };

        /// <summary>
        /// 把 "Left ThumbProximal" 这种写法转成 Unity 认的 "Left Thumb Proximal"。
        /// 判据是 <see cref="HumanTrait.BoneName"/> 数组（它是权威来源），
        /// 转不出来就返回 null，调用处会记一条警告。
        /// </summary>
        static string NormalizeHumanName(string s)
        {
            if (Array.IndexOf(HumanTrait.BoneName, s) >= 0) return s;
            // 在 LEFT/RIGHT 之后、以及常见单词边界前插空格
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && char.IsUpper(c))
                {
                    string prev = s.Substring(0, i);
                    // Left/Right 之后插一个空格
                    if (prev == "Left" || prev == "Right") { sb.Append(' '); }
                    else if (char.IsLower(s[i - 1])) { sb.Append(' '); }
                }
                sb.Append(c);
            }
            var cand = sb.ToString();
            if (Array.IndexOf(HumanTrait.BoneName, cand) >= 0) return cand;
            return null;
        }

        /// <summary>最近一次导入的日志（导入中途崩溃时也能拿到，用于定位崩在哪一段）</summary>
        public static List<string> LastLog = new List<string>();

        /// <summary>最近一次导入的 Result（含部分日志），即使中途崩溃也已填充</summary>
        public static Result Last;

        public class Result
        {
            public List<string> Log = new List<string>();            public GameObject Prefab;
            public Avatar Avatar;
            public Mesh Mesh;
            public int VertexCount, BoneCount, MorphCount, MaterialCount;
            public int MappedHumanBones, MissingHumanBones;
            public List<string> MissingBones = new List<string>();
            public List<string> SkippedMorphs = new List<string>();
        }

        // ====================================================================
        //  主入口
        // ====================================================================
        public static Result Import(string pmxPath, string texDir, string outFolder, string objName)
        {
            try
            {
                return ImportInner(pmxPath, texDir, outFolder, objName);
            }
            catch (Exception e)
            {
                throw new Exception("PMX 解析崩溃: " + e.GetType().Name + " " + e.Message, e);
            }
        }

        /// <summary>诊断用：逐段记录字节位置，出错时能定位到哪一段</summary>
        public static string Trace(string pmxPath)
        {
            var data = File.ReadAllBytes(pmxPath);
            var r = new PmxReader(data);
            var sb = new StringBuilder();
            try
            {
                r.Position = 4;
                float ver = r.F32(); int gc = r.U8();
                var g = new byte[gc]; for (int i = 0; i < gc; i++) g[i] = r.U8();
                r.Enc = g[0] == 0 ? Encoding.Unicode : Encoding.UTF8; r.IsUtf16 = g[0] == 0;
                r.AddUv = g[1]; r.SzVertex = g[2]; r.SzTex = g[3]; r.SzMat = g[4];
                r.SzBone = g[5]; r.SzMorph = g[6]; r.SzRigid = g[7];
                sb.AppendLine(string.Format("ver={0} enc={1} addUv={2} sizes v/t/m/b/mo/r={3}/{4}/{5}/{6}/{7}/{8} pos={9} len={10}",
                    ver, g[0], g[1], g[2], g[3], g[4], g[5], g[6], g[7], r.Position, data.Length));
                r.Text(); r.Text(); r.Text(); r.Text();
                sb.AppendLine("  after names pos=" + r.Position);

                int vcount = r.I32();
                sb.AppendLine("  vertexCount=" + vcount + " pos=" + r.Position);
                for (int i = 0; i < vcount; i++)
                {
                    r.V3(); r.V3(); r.V2();
                    for (int k = 0; k < r.AddUv; k++) r.V4();
                    byte wt = r.U8();
                    switch (wt)
                    {
                        case 0: r.Idx(r.SzBone); break;
                        case 1: r.Idx(r.SzBone); r.Idx(r.SzBone); r.F32(); break;
                        case 2: for (int k = 0; k < 4; k++) r.Idx(r.SzBone); for (int k = 0; k < 4; k++) r.F32(); break;
                        case 3: r.Idx(r.SzBone); r.Idx(r.SzBone); r.F32(); r.V3(); r.V3(); r.V3(); break;
                        case 4: for (int k = 0; k < 4; k++) r.Idx(r.SzBone); for (int k = 0; k < 4; k++) r.F32(); break;
                        default: throw new Exception("未知权重类型 " + wt + " @vertex " + i);
                    }
                    r.F32();
                }
                sb.AppendLine("  after vertices pos=" + r.Position);

                int icount = r.I32();
                sb.AppendLine("  indexCount=" + icount + " pos=" + r.Position);
                for (int i = 0; i < icount; i++) r.Idx(r.SzVertex);
                sb.AppendLine("  after faces pos=" + r.Position);

                int tcount = r.I32();
                sb.AppendLine("  textureCount=" + tcount + " pos=" + r.Position);
                for (int i = 0; i < tcount; i++) sb.AppendLine("    tex[" + i + "]=" + r.Text());
                sb.AppendLine("  after textures pos=" + r.Position);

                int matCount = r.I32();
                sb.AppendLine("  materialCount=" + matCount + " pos=" + r.Position);
                int faceSum = 0;
                for (int i = 0; i < matCount; i++)
                {
                    int q0 = r.Position;
                    var n1 = r.Text(); int q1 = r.Position;
                    var n2 = r.Text(); int q2 = r.Position;
                    r.V4(); r.V3(); r.F32(); r.V3();
                    r.U8(); r.V4(); r.F32();
                    int pB = r.Position;
                    int tex = r.Idx(r.SzTex); int sph = r.Idx(r.SzTex);
                    byte smode = r.U8(); byte shared = r.U8();
                    int toon = r.Idx(r.SzTex);   // ★ 见 ImportInner：必须按纹理索引宽度读
                    var memo = r.Text();
                    int sc = r.I32();
                    faceSum += sc;
                    sb.AppendLine(string.Format(
                        "    mat[{0}] name='{1}'(chars={2},bytes={3}) en='{4}'(chars={5},bytes={6}) tex={7} sph={8} smode={9} shared={10} toon={11} memo={12}(chars={13},bytes={14}) surf={15} pos={16}",
                        i, n1, n1.Length, q1 - q0 - 4, n2, n2.Length, q2 - q1 - 4,
                        tex, sph, smode, shared, toon, memo.Length > 0 ? "yes" : "empty", memo.Length, r.Position - pB, sc, r.Position));
                }
                sb.AppendLine("  ★ 材质面数合计 = " + faceSum + "  (应等于 indexCount " + icount + ")");

                int bcount = r.I32();
                sb.AppendLine("  boneCount=" + bcount + " pos=" + r.Position);
                for (int i = 0; i < bcount; i++)
                {
                    int p0 = r.Position;
                    var bn = r.Text(); r.Text(); r.V3(); r.Idx(r.SzBone); r.I32();
                    int f = r.U16();
                    if ((f & 0x0001) != 0) r.Idx(r.SzBone); else r.V3();
                    if ((f & (0x0100 | 0x0200)) != 0) { r.Idx(r.SzBone); r.F32(); }
                    if ((f & 0x0400) != 0) r.V3();
                    if ((f & 0x0800) != 0) { r.V3(); r.V3(); }
                    if ((f & 0x2000) != 0) r.I32();
                    int ikLinks = 0;
                    if ((f & 0x0020) != 0)
                    {
                        r.Idx(r.SzBone); r.I32(); r.F32();
                        int lc = r.I32(); ikLinks = lc;
                        for (int k = 0; k < lc; k++) { r.Idx(r.SzBone); if (r.U8() == 1) { r.V3(); r.V3(); } }
                    }
                    if (i < 6 || i == 63 || i == 64 || i == 127 || i == 255 || i == 511 || i == 543 ||
                        f != 0x0b || (f & 0x0020) != 0)
                        sb.AppendLine(string.Format("    bone[{0,3}] {1,-22} flag={2:X4} start={3} end={4} len={5} ik={6}",
                            i, bn, f, p0, r.Position, r.Position - p0, ikLinks));
                }
                sb.AppendLine("  after bones pos=" + r.Position + "  fileLen=" + data.Length + "  DIFF=" + (data.Length - r.Position));
            }
            catch (Exception e)
            {
                sb.AppendLine("  !! 崩溃于 pos=" + r.Position + " : " + e.GetType().Name + " " + e.Message);
            }
            return sb.ToString();
        }

        /// <summary>诊断：只解析到材质段，逐个材质输出「字段→位置」轨迹，找出与 Python 的分歧点</summary>
        public static string TraceMat(string pmxPath)
        {
            var data = File.ReadAllBytes(pmxPath);
            var r = new PmxReader(data);
            var sb = new StringBuilder();
            r.Position = 4;
            float ver = r.F32(); int gc = r.U8();
            var g = new byte[gc]; for (int i = 0; i < gc; i++) g[i] = r.U8();
            r.Enc = g[0] == 0 ? Encoding.Unicode : Encoding.UTF8; r.IsUtf16 = g[0] == 0;
            r.AddUv = g[1]; r.SzVertex = g[2]; r.SzTex = g[3]; r.SzMat = g[4];
            r.SzBone = g[5]; r.SzMorph = g[6]; r.SzRigid = g[7];
            r.Text(); r.Text(); r.Text(); r.Text();
            int vc = r.I32();
            for (int i = 0; i < vc; i++)
            {
                r.V3(); r.V3(); r.V2();
                for (int k = 0; k < r.AddUv; k++) r.V4();
                byte wt = r.U8();
                switch (wt)
                {
                    case 0: r.Idx(r.SzBone); break;
                    case 1: r.Idx(r.SzBone); r.Idx(r.SzBone); r.F32(); break;
                    case 2: for (int k = 0; k < 4; k++) r.Idx(r.SzBone); for (int k = 0; k < 4; k++) r.F32(); break;
                    case 3: r.Idx(r.SzBone); r.Idx(r.SzBone); r.F32(); r.V3(); r.V3(); r.V3(); break;
                    case 4: for (int k = 0; k < 4; k++) r.Idx(r.SzBone); for (int k = 0; k < 4; k++) r.F32(); break;
                }
                r.F32();
            }
            int ic = r.I32(); r.Skip(ic * r.SzVertex);
            int tc = r.I32(); for (int i = 0; i < tc; i++) r.Text();
            int mc = r.I32();
            sb.AppendLine("mats start " + r.Position + " count " + mc + "  SzTex=" + r.SzTex);

            int faceSum = 0;
            for (int i = 0; i < mc; i++)
            {
                int q0 = r.Position;
                string hex1, hex2;
                string n1 = r.TextHex(out hex1);
                string n2 = r.TextHex(out hex2);
                int q1 = r.Position;
                var dif = r.V4(); int q3 = r.Position;
                r.V3(); float sh = r.F32(); r.V3();
                byte fl = r.U8(); r.V4(); r.F32();
                int q4 = r.Position;
                int tex = r.Idx(r.SzTex); int sph = r.Idx(r.SzTex);
                byte smode = r.U8(); byte shared = r.U8();
                int q5 = r.Position;
                int toon = r.Idx(r.SzTex);   // ★ 见 ImportInner 里的说明：这里必须按纹理索引宽度读
                string memo = r.Text();
                int sc = r.I32();
                faceSum += sc;
                sb.AppendLine(string.Format(
                    "mat[{0,2}] '{1}' [{2}] en[{3}] q3={4} q4={5} q5={6} tex={7} sph={8} smode={9} shared={10} toon={11} memoLen={12} surf={13} end={14}",
                    i, n1, hex1, hex2, q3, q4, q5, tex, sph, smode, shared, toon,
                    memo.Length, sc, r.Position));
            }
            sb.AppendLine("★ faceSum=" + faceSum + "  indexCount=" + ic + "  -> " + (faceSum == ic ? "MATCH" : "MISMATCH"));
            sb.AppendLine("after mats pos=" + r.Position);
            return sb.ToString();
        }

        static Result ImportInner(string pmxPath, string texDir, string outFolder, string objName)
        {
            var res = new Result();
            LastLog = res.Log;      // ★ 让崩溃时也能拿到已解析的进度
            Last = res;
            var data = File.ReadAllBytes(pmxPath);
            var r = new PmxReader(data);

            // ---- header ----
            if (data[0] != 'P' || data[1] != 'M' || data[2] != 'X' || data[3] != ' ')
                throw new Exception("不是 PMX 文件（签名不符）");
            r.Position = 4;
            float version = r.F32();
            int gc = r.U8();
            var g = new byte[gc];
            for (int i = 0; i < gc; i++) g[i] = r.U8();
            r.Enc = g[0] == 0 ? Encoding.Unicode : Encoding.UTF8; r.IsUtf16 = g[0] == 0;
            r.AddUv = g[1];
            r.SzVertex = g[2]; r.SzTex = g[3]; r.SzMat = g[4];
            r.SzBone = g[5]; r.SzMorph = g[6]; r.SzRigid = g[7];
            res.Log.Add(string.Format("PMX v{0}  编码={1}  附加UV={2}  索引宽度 v/t/m/b/mo/r = {3}/{4}/{5}/{6}/{7}/{8}",
                version, g[0] == 0 ? "UTF-16" : "UTF-8", r.AddUv,
                r.SzVertex, r.SzTex, r.SzMat, r.SzBone, r.SzMorph, r.SzRigid));

            r.Text(); r.Text(); r.Text(); r.Text();   // name / nameEn / comment / commentEn

            // ---- 顶点 ----
            int vcount = r.I32();
            var positions = new Vector3[vcount];
            var normals = new Vector3[vcount];
            var uvs = new Vector2[vcount];
            var boneIdx = new int[vcount][];
            var boneW = new float[vcount][];
            var wTypeCensus = new int[5];

            for (int i = 0; i < vcount; i++)
            {
                positions[i] = r.V3();
                normals[i] = r.V3();
                uvs[i] = r.V2();
                for (int k = 0; k < r.AddUv; k++) r.V4();

                byte wt = r.U8();
                wTypeCensus[wt]++;
                var bi = new int[4];
                var bw = new float[4];
                switch (wt)
                {
                    case 0: // BDEF1
                        bi[0] = r.Idx(r.SzBone); bw[0] = 1f;
                        break;
                    case 1: // BDEF2
                        bi[0] = r.Idx(r.SzBone); bi[1] = r.Idx(r.SzBone);
                        bw[0] = r.F32(); bw[1] = 1f - bw[0];
                        break;
                    case 2: // BDEF4
                        for (int k = 0; k < 4; k++) bi[k] = r.Idx(r.SzBone);
                        for (int k = 0; k < 4; k++) bw[k] = r.F32();
                        break;
                    case 3: // SDEF —— Unity 无对应，降级为 BDEF2
                        bi[0] = r.Idx(r.SzBone); bi[1] = r.Idx(r.SzBone);
                        bw[0] = r.F32(); bw[1] = 1f - bw[0];
                        r.V3(); r.V3(); r.V3();   // C / R0 / R1 球面修正，丢弃
                        break;
                    case 4: // QDEF —— 与 BDEF4 同构
                        for (int k = 0; k < 4; k++) bi[k] = r.Idx(r.SzBone);
                        for (int k = 0; k < 4; k++) bw[k] = r.F32();
                        break;
                    default:
                        throw new Exception("未知权重类型 " + wt + " @顶点 " + i);
                }
                r.F32();   // edge scale
                boneIdx[i] = bi;
                boneW[i] = bw;
            }
            res.VertexCount = vcount;
            res.Log.Add(string.Format("顶点 {0}  (BDEF1={1} BDEF2={2} BDEF4={3} SDEF={4} QDEF={5})",
                vcount, wTypeCensus[0], wTypeCensus[1], wTypeCensus[2], wTypeCensus[3], wTypeCensus[4]));
            // ★ 一致性自检：网格顶点与骨骼位置必须处在同一个尺度坐标系里
            {
                float vmax = -1e9f, vmin = 1e9f;
                for (int i = 0; i < vcount; i++) { if (positions[i].y > vmax) vmax = positions[i].y; if (positions[i].y < vmin) vmin = positions[i].y; }
                res.Log.Add(string.Format("  原始顶点 Y 范围 {0:F4} .. {1:F4}  ×Scale({2}) = {3:F4} .. {4:F4}",
                    vmin, vmax, Scale, vmin * Scale, vmax * Scale));
            }

            // ---- 面 ----
            int icount = r.I32();
            var tris = new int[icount];
            for (int i = 0; i < icount; i++) tris[i] = r.Idx(r.SzVertex);
            res.Log.Add(string.Format("三角形 {0}", icount / 3));

            // ---- 贴图路径 ----
            int tcount = r.I32();
            var texPaths = new string[tcount];
            for (int i = 0; i < tcount; i++) texPaths[i] = r.Text();

            // ---- 材质 ----
            int matCount = r.I32();
            var pmxMats = new List<PmxMaterial>();
            for (int i = 0; i < matCount; i++)
            {
                var m = new PmxMaterial();
                m.Name = r.Text(); m.NameEn = r.Text();
                m.Diffuse = r.V4();
                m.Specular = r.V3();
                m.Shininess = r.F32();
                m.Ambient = r.V3();
                m.Flag = r.U8();
                m.EdgeColor = r.V4();
                m.EdgeSize = r.F32();
                m.Tex = r.Idx(r.SzTex);
                m.Sphere = r.Idx(r.SzTex);
                m.SphereMode = r.U8();
                // ★★ toon 字段：**读 4 字节的纹理索引**。
                //
                //   mmd_tools 的源码写的是「shared 标志为真时读 1 字节」：
                //       self.is_shared_toon_texture = (fs.readSignedByte() == 1)
                //       if self.is_shared_toon_texture: self.toon_texture = fs.readSignedByte()
                //       else: self.toon_texture = __tex_index(fs.readTextureIndex())
                //   但**实测这个文件必须按 4 字节纹理索引读**。
                //
                //   判据（两条同时成立才算对）：
                //     ① sum(material.surface_count) == index_count  →  248967 == 248967 ✅
                //     ② 材质段结束位置 == 4689022，与骨骼段起点 4689026 严丝合缝 ✅
                //   按 1 字节读时：面数合计对不上，且第 7 项起 surf 变成 13396992 这种垃圾值。
                //
                //   所以这里不再做 shared 分支，一律按纹理索引宽度读。
                m.ToonShared = r.U8();
                m.Toon = r.Idx(r.SzTex);
                r.Text();   // memo
                m.SurfaceCount = r.I32();
                pmxMats.Add(m);
            }
            res.MaterialCount = pmxMats.Count;
            res.Log.Add(string.Format("材质 {0}  贴图槽 {1}", pmxMats.Count, tcount));

            // ---- 骨骼 ----
            int bcount = r.I32();
            var bones = new List<PmxBone>();
            for (int i = 0; i < bcount; i++)
            {
                var b = new PmxBone();
                b.Name = r.Text(); b.NameEn = r.Text();
                b.Pos = r.V3();
                b.Parent = r.Idx(r.SzBone);
                r.I32();                 // 变形阶层
                b.Flag = r.U16();
                if ((b.Flag & 0x0001) != 0) b.TailBone = r.Idx(r.SzBone);
                else b.TailPos = r.V3();
                if ((b.Flag & (0x0100 | 0x0200)) != 0)
                {
                    b.InheritParent = r.Idx(r.SzBone);
                    b.InheritWeight = r.F32();
                }
                if ((b.Flag & 0x0400) != 0) r.V3();                  // 轴固定
                if ((b.Flag & 0x0800) != 0) { r.V3(); r.V3(); }      // 局部轴
                if ((b.Flag & 0x2000) != 0) r.I32();                 // 外部父变形
                if ((b.Flag & 0x0020) != 0)                          // IK
                {
                    r.Idx(r.SzBone); r.I32(); r.F32();
                    int lc = r.I32();
                    for (int k = 0; k < lc; k++)
                    {
                        r.Idx(r.SzBone);
                        if (r.U8() == 1) { r.V3(); r.V3(); }
                    }
                }
                bones.Add(b);
            }
            res.BoneCount = bones.Count;
            res.Log.Add(string.Format("骨骼 {0}", bones.Count));

            // 统计每根骨头的权重顶点数（诊断用，也是「哪根是变形骨」的铁证）
            var wcount = new int[bones.Count];
            for (int i = 0; i < vcount; i++)
                for (int k = 0; k < 4; k++)
                    if (boneW[i][k] > 1e-4f && boneIdx[i][k] >= 0 && boneIdx[i][k] < bones.Count)
                        wcount[boneIdx[i][k]]++;
            for (int i = 0; i < bones.Count; i++) bones[i].WeightCount = wcount[i];

            // ---- 表情（morph）----
            int morphCount = r.I32();
            var morphs = new List<PmxMorph>();
            res.Log.Add("morph 段起点 pos=" + r.Position + "  count=" + morphCount);
            for (int i = 0; i < morphCount; i++)
            {
                int mp0 = r.Position;
                var mo = new PmxMorph();
                try
                {
                    mo.Name = r.Text(); r.Text();
                    mo.Panel = r.U8(); mo.Type = r.U8();
                    int oc = r.I32();
                    mo.Indices = new int[oc];
                    mo.Offsets = new Vector3[oc];
                    switch (mo.Type)
                    {
                        case 0: // 组
                            for (int k = 0; k < oc; k++) { mo.Indices[k] = r.Idx(r.SzMorph); mo.Offsets[k] = new Vector3(r.F32(), 0, 0); }
                            break;
                        case 1: // 顶点
                            for (int k = 0; k < oc; k++) { mo.Indices[k] = r.Idx(r.SzVertex); mo.Offsets[k] = r.V3(); }
                            break;
                        case 2: // 骨骼
                            for (int k = 0; k < oc; k++) { mo.Indices[k] = r.Idx(r.SzBone); mo.Offsets[k] = r.V3(); r.V4(); }
                            break;
                        case 3: case 4: case 5: case 6: case 7: // UV 1..4
                            for (int k = 0; k < oc; k++) { r.Idx(r.SzVertex); r.V4(); }
                            break;
                        case 8: // 材质
                            // ★ 权威结构照抄 mmd_tools 的 MaterialMorphOffset.load()：
                            //     materialIndex, offsetType(s8),
                            //     diffuse(V4), specular(V3), shininess(F), ambient(V3),
                            //     edgeColor(V4), edgeSize(**F**),        ← 是 float，不是 V4！
                            //     textureFactor(V4), sphereFactor(V4), toonFactor(V4)
                            //   合计 1+1+16+12+4+12+16+4+16+16+16 = 114 字节/条。
                            //   （读成 V4 会每条多 12 字节，导致整段错位。）
                            mo.MaterialOffsets = new List<KeyValuePair<int, float>>();
                            for (int k = 0; k < oc; k++)
                            {
                                int mi = r.Idx(r.SzMat);
                                byte op = r.U8();
                                var diff = r.V4();
                                r.V3(); r.F32(); r.V3();
                                r.V4(); r.F32();
                                r.V4(); r.V4(); r.V4();
                                mo.MaterialOffsets.Add(new KeyValuePair<int, float>(mi, diff.x));
                            }
                            break;
                        case 9: // UV 追加 1..4
                            for (int k = 0; k < oc; k++) { r.Idx(r.SzVertex); r.V4(); }
                            break;
                        case 10: // 材质追加
                            for (int k = 0; k < oc; k++) { r.Idx(r.SzMat); r.U8(); r.V4(); r.V4(); }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    res.Log.Add(string.Format("  ✘ morph[{0}] name='{1}' type={2} 崩于 pos={3}（本段起 {4}）: {5}",
                        i, mo.Name, mo.Type, r.Position, mp0, ex.Message));
                    var hex = new StringBuilder();
                    int from = Math.Max(0, mp0 - 16);
                    int to = Math.Min(mp0 + 40, data.Length);
                    for (int p = from; p < to; p++)
                    {
                        if (p == mp0) hex.Append(" |");
                        hex.Append(' ').Append(data[p].ToString("X2"));
                    }
                    res.Log.Add("     原始字节 @" + from + " :" + hex);
                    throw;   // 不再猜测跳过 —— 出错就是要修结构
                }
                res.Log.Add(string.Format("  morph[{0,2}] '{1}' type={2} count={3}  pos {4}→{5}",
                    i, mo.Name, mo.Type, mo.Indices.Length, mp0, r.Position));
                morphs.Add(mo);
            }
            res.MorphCount = morphs.Count;
            res.Log.Add(string.Format("表情(morph) {0}  -> pos={1}", morphs.Count, r.Position));

            // ---- 显示枠 / 刚体 / 关节 ----
            // 这三段本项目用不到（数字人展示不需要 MMD 物理），
            // 但**必须按格式读完**，否则无法用「是否恰好读到文件末尾」来验证前面解析正确。
            int displayCount = r.I32();
            for (int i = 0; i < displayCount; i++)
            {
                r.Text(); r.Text();
                r.U8();                       // 特殊枠标志
                int n = r.I32();
                for (int k = 0; k < n; k++)
                {
                    byte dt = r.U8();
                    if (dt == 0) r.Idx(r.SzBone);
                    else r.Idx(r.SzMorph);
                }
            }
            res.Log.Add("显示枠 " + displayCount + " -> pos=" + r.Position);

            int rigidCount = r.I32();
            for (int i = 0; i < rigidCount; i++)
            {
                // 照抄 mmd_tools Rigid.load()：
                //   name, nameEn, boneIdx, group(s8), mask(u16), type(s8),
                //   size(V3), location(V3), rotation(V3),
                //   mass(F), velocityAtten(F), rotationAtten(F), bounce(F), friction(F),
                //   mode(s8)     ← ★ 末尾还有一个字节，漏了就会整段错位
                r.Text(); r.Text();
                r.Idx(r.SzBone);
                r.U8();                       // 碰撞组号
                r.U16();                      // 非冲突组掩码
                r.I8();                       // 形状类型（s8）
                r.V3(); r.V3(); r.V3();       // 尺寸 / 位置 / 旋转
                r.F32(); r.F32(); r.F32();    // 质量 / 移动衰减 / 旋转衰减
                r.F32(); r.F32();             // 反弹 / 摩擦
                r.I8();                       // 物理模式
            }
            res.Log.Add("刚体 " + rigidCount + " -> pos=" + r.Position);

            int jointCount = r.I32();
            for (int i = 0; i < jointCount; i++)
            {
                // 照抄 mmd_tools Joint._load()
                r.Text(); r.Text();
                r.I8();                       // 模式（s8）
                r.Idx(r.SzRigid); r.Idx(r.SzRigid);
                r.V3(); r.V3();               // 位置 / 旋转
                r.V3(); r.V3();               // 移动上限 / 下限
                r.V3(); r.V3();               // 旋转上限 / 下限
                r.V3(); r.V3();               // 弹簧 移动/旋转
            }
            res.Log.Add("关节 " + jointCount + " -> pos=" + r.Position);

            res.Log.Add(string.Format("★ 解析终点 pos={0}  文件长度={1}  {2}",
                r.Position, data.Length,
                r.Position == data.Length ? "完全吻合 ✅" : ("差 " + (data.Length - r.Position) + " 字节 ❌")));

            // ================================================================
            //  构建
            // ================================================================
            var root = new GameObject(objName);

            // ---- 骨骼 Transform ----
            //
            // ★★★ 最容易搞错的一点：PMX 里存的骨骼坐标是「模型空间绝对坐标」，
            //      **不是相对父级的局部坐标**。
            //
            //      实测证据（庄方宜.pmx）：
            //        センター / センター2 / グルーブ / グルーブ2  的原始 Y **全都是 9.8127**
            //        它们互为父子关系，所以世界位置必须**全都一样**（局部偏移为 0）。
            //
            //      Blender 的 mmd_tools 也是这样解释的（权威对照，scale=0.08）：
            //        操作中心 (0, 0, 0.0181) / センター (0, 0, 0.7850)
            //        センター2 (0, 0, 0.7850) / グルーブ2 (0, 0, 0.7850)
            //        腰 (0, 0.0238, 1.0663) / 頭 (0, -0.0113, 1.5526)
            //
            //      若直接当成 localPosition 用，每一级都会**重复累加**一次父级偏移：
            //        グルーブ2 会跑到 3.08，腰跑到 4.12，头跑到 10.49 —— 整个骨架被拉长 6.9 倍。
            //      而且因为绑定姿态用同一套（错误的）世界矩阵算，蒙皮看起来「自洽」，
            //      很难从数据上发现，只有渲染出来才看得出人是散的。
            //
            //      正确做法：世界位置 = 原始坐标 × Scale；
            //                localPosition = 自己的世界位置 − 父级的世界位置。
            var worldPos = new Vector3[bones.Count];
            for (int i = 0; i < bones.Count; i++)
                worldPos[i] = ToUnity(bones[i].Pos);

            // ---- ★ 手臂 rest 朝向修正（同时作用于骨骼和 bindposes）----
            //
            // 【问题】网格是 T-pose（手臂水平），骨骼 rest 是 A-pose（斜下约 37°）。
            //   Unity 按骨骼 rest 推导 T-pose，于是所有 Mixamo 动作都被拧了约 35°。
            //
            // 【曾经错误的做法】只改 SkeletonBone 的旋转。
            //   那样 Unity 以为 T-pose 是水平的，但**实际骨骼/网格还在 A-pose**，
            //   偏差并没有消除，只是换了个方向 —— 表现为托腮时手穿进胸口。
            //
            // 【正确做法】**把抬升真正施加到骨骼上**（改 worldPos 的父链朝向），
            //   并让 bindposes 跟着更新，这样绑定姿态下蒙皮矩阵仍是单位阵、
            //   网格保持形状；同时 Avatar 也从"已抬升"的骨骼推导 T-pose，
            //   两边就一致了。
            //
            //   实现：先按父链算出每根骨的 local 旋转（只改手臂链），
            //   再用它累加出真正的世界位置 → bindposes。
            var localRot = new Quaternion[bones.Count];
            for (int i = 0; i < bones.Count; i++) localRot[i] = Quaternion.identity;

            if (ApplyTPoseSkeletonFix)
            {
                float liftDeg = TPoseLiftDegrees;
                // 按 PMX 骨名找索引（此时 Transform 还没建，只能查名字表）
                var nameToIdx = new Dictionary<string, int>();
                for (int i = 0; i < bones.Count; i++)
                    if (!nameToIdx.ContainsKey(bones[i].Name)) nameToIdx[bones[i].Name] = i;

                int lifted = 0;
                foreach (var sem in new[] { "ArmL", "ElbowL", "WristL", "ArmR", "ElbowR", "WristR" })
                {
                    // 借中央表的候选名，再用 PMX 骨名查索引
                    var cands = Yixuan.Model.MmdBoneNames.Candidates(sem);
                    for (int c = 0; c < cands.Length; c++)
                    {
                        int idx;
                        if (!nameToIdx.TryGetValue(cands[c], out idx)) continue;
                        // 抬升符号按「骨骼在模型里的左右」：+X 侧抬 +，−X 侧抬 −
                        float sign = worldPos[idx].x >= 0f ? 1f : -1f;
                        localRot[idx] = Quaternion.AngleAxis(sign * liftDeg, Vector3.forward);
                        lifted++;
                        break;
                    }
                }
                res.Log.Add(string.Format("★ 手臂 rest 抬升 {0}°（骨骼旋转 + bindposes 同步修正，共 {1} 根）",
                    liftDeg, lifted));
            }

            var boneTf = new Transform[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                var go = new GameObject(bones[i].Name);
                boneTf[i] = go.transform;
            }
            for (int i = 0; i < bones.Count; i++)
            {
                int p = bones[i].Parent;
                bool hasParent = p >= 0 && p < bones.Count;
                boneTf[i].SetParent(hasParent ? boneTf[p] : root.transform, false);
                Vector3 parentWorld = hasParent ? worldPos[p] : Vector3.zero;
                boneTf[i].localPosition = worldPos[i] - parentWorld;
                boneTf[i].localRotation = localRot[i];
            }
            res.Log.Add("骨骼挂载完成，抽查 world 位置（应与 Blender mmd_tools 的 scale=0.08 结果成比例）：");
            foreach (var nm in new[] { "操作中心", "全ての親", "センター", "センター2", "グルーブ", "グルーブ2", "腰", "上半身", "頭" })
            {
                for (int i = 0; i < bones.Count; i++)
                {
                    if (bones[i].Name != nm) continue;
                    var chain = new StringBuilder();
                    var t = boneTf[i];
                    int d = 0;
                    while (t != null && d < 9) { chain.Append(t.name).Append(" < "); t = t.parent; d++; }
                    res.Log.Add(string.Format("  {0,-8} local={1,-24} world={2}",
                        nm, boneTf[i].localPosition.ToString("F5"), boneTf[i].position.ToString("F5")));
                    break;
                }
            }

            // ---- Mesh ----
            var mesh = new Mesh();
            mesh.name = objName;
            mesh.indexFormat = vcount > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;

            var mv = new Vector3[vcount];
            for (int i = 0; i < vcount; i++) mv[i] = ToUnity(positions[i]);
            mesh.vertices = mv;

            // 法线：与位置用同一个变换（CoordMode 1 是镜像，手性翻转 → 绕序也必须翻）
            var mn = new Vector3[vcount];
            for (int i = 0; i < vcount; i++) mn[i] = new Vector3(-normals[i].x, normals[i].y, -normals[i].z);
            mesh.normals = mn;
            if (FlipV)
            {
                var fuv = new Vector2[vcount];
                for (int i = 0; i < vcount; i++) fuv[i] = new Vector2(uvs[i].x, 1f - uvs[i].y);
                mesh.uv = fuv;
            }
            else
            {
                mesh.uv = uvs;
            }

            var t2 = new int[icount];
            if (FlipWinding)
            {
                for (int i = 0; i + 2 < icount; i += 3)
                {
                    t2[i] = tris[i]; t2[i + 1] = tris[i + 2]; t2[i + 2] = tris[i + 1];
                }
            }
            else
            {
                Array.Copy(tris, t2, icount);
            }
            mesh.triangles = t2;
            res.Log.Add("绕序翻转 = " + FlipWinding);

            // ---- 权重 ----
            var bw4 = new BoneWeight[vcount];
            for (int i = 0; i < vcount; i++)
            {
                var w = new BoneWeight();
                var bi = boneIdx[i]; var ww = boneW[i];
                // 归一化（PMX 数据偶尔不严格归一）
                float sum = ww[0] + ww[1] + ww[2] + ww[3];
                if (sum > 1e-6f) for (int k = 0; k < 4; k++) ww[k] /= sum;
                w.boneIndex0 = Mathf.Max(0, bi[0]); w.weight0 = ww[0];
                w.boneIndex1 = bi[1] >= 0 ? bi[1] : 0; w.weight1 = ww[1];
                w.boneIndex2 = bi[2] >= 0 ? bi[2] : 0; w.weight2 = ww[2];
                w.boneIndex3 = bi[3] >= 0 ? bi[3] : 0; w.weight3 = ww[3];
                bw4[i] = w;
            }
            mesh.boneWeights = bw4;

            // ---- 绑定姿态 ----
            // ★ 必须用「真实的世界矩阵」的逆（考虑上面给手臂加的 localRot），
            //   而不是 Matrix4x4.TRS(worldPos, identity)。
            //   因为骨骼已经带着 localRot 了 —— 绑定姿态必须与之一致，
            //   这样 rest 下蒙皮矩阵才等于单位阵（网格保持原始形状）。
            //
            //   坐标系：worldMat = parentWorldMat × TRS(localPos, localRot)
            //   而 localPos 当初就是用 (worldPos − parentWorldPos) 算的。
            var restWorld = new Matrix4x4[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                int p = bones[i].Parent;
                bool hasParent = p >= 0 && p < bones.Count;
                Vector3 parentWorld = hasParent ? worldPos[p] : Vector3.zero;
                var local = Matrix4x4.TRS(worldPos[i] - parentWorld, localRot[i], Vector3.one);
                restWorld[i] = hasParent ? restWorld[p] * local : local;
            }
            var bindposes = new Matrix4x4[bones.Count];
            for (int i = 0; i < bones.Count; i++) bindposes[i] = restWorld[i].inverse;
            mesh.bindposes = bindposes;

            // ---- 子网格 ----
            var subMeshes = new List<int[]>();
            var matNames = new List<string>();
            int cursor = 0;
            foreach (var m in pmxMats)
            {
                int n = m.SurfaceCount;
                if (n <= 0) { matNames.Add(m.Name); continue; }
                var sub = new int[n];
                Array.Copy(t2, cursor, sub, 0, n);
                subMeshes.Add(sub);
                matNames.Add(m.Name);
                cursor += n;
            }
            mesh.subMeshCount = subMeshes.Count;
            for (int i = 0; i < subMeshes.Count; i++) mesh.SetTriangles(subMeshes[i], i, false);

            // ---- 表情（BlendShape）----
            foreach (var mo in morphs)
            {
                if (mo.Type != 1 || mo.Indices == null || mo.Indices.Length == 0) continue;
                if (string.IsNullOrEmpty(mo.Name)) continue;
                var dv = new Vector3[vcount];
                var dn = new Vector3[vcount];
                var dt = new Vector3[vcount];
                for (int k = 0; k < mo.Indices.Length; k++)
                {
                    int vi = mo.Indices[k];
                    if (vi < 0 || vi >= vcount) continue;
                    dv[vi] = mo.Offsets[k] * Scale;
                }
                mesh.AddBlendShapeFrame(mo.Name, 100f, dv, dn, dt);
            }
            res.Log.Add(string.Format("BlendShape {0} 个", mesh.blendShapeCount));

            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            res.Mesh = mesh;

            // ---- SkinnedMeshRenderer ----
            var meshGo = new GameObject(objName + "_mesh");
            meshGo.transform.SetParent(root.transform, false);
            var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = boneTf;
            smr.rootBone = boneTf.Length > 0 ? boneTf[0] : null;
            smr.updateWhenOffscreen = true;
            smr.quality = SkinQuality.Bone4;

            // ---- 材质 ----
            var mats = new Material[subMeshes.Count];
            for (int i = 0, si = 0; i < pmxMats.Count; i++)
            {
                if (pmxMats[i].SurfaceCount <= 0) continue;
                mats[si++] = BuildMaterial(pmxMats[i], texPaths, texDir, outFolder, res);
            }
            smr.sharedMaterials = mats;

            // ---- Avatar ----
            var animator = root.AddComponent<Animator>();
            var avatar = BuildAvatar(root, bones, boneTf, res);
            animator.avatar = avatar;
            res.Avatar = avatar;
            res.Prefab = root;

            return res;
        }

        // ====================================================================
        //  Avatar：显式映射到 D 骨
        // ====================================================================

        /// <summary>
        /// SkeletonBone 列表 = 层次结构里每个 Transform 的**局部**值照抄一份。
        /// ★ 注意是 localPosition / localRotation：`SkeletonBone.rotation`
        ///   和 Transform.localRotation 是同一个空间，不是"相对根的世界旋转"。
        ///   （`BuildTPoseAvatar` 里改这个字段时也必须按局部旋转来改。）
        /// </summary>
        static List<SkeletonBone> BuildSkeletonBones(GameObject root)
        {
            var skeleton = new List<SkeletonBone>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                skeleton.Add(new SkeletonBone
                {
                    name = t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                });
            }
            return skeleton;
        }

        /// <summary>
        /// 按中央表 <see cref="HumanMap"/> 生成 HumanBone 列表（不做任何姿态处理）。
        /// 找不到的骨记进 res，Unity 不认的 humanName 试一次自动补空格。
        /// </summary>
        static List<HumanBone> BuildHumanBones(Dictionary<string, Transform> byName, Result res)
        {
            var human = new List<HumanBone>();
            foreach (var pair in HumanMap)
            {
                string humanName = pair[0], pmxName = pair[1];
                if (string.IsNullOrEmpty(pmxName)) continue;
                Transform t;
                if (!byName.TryGetValue(pmxName, out t))
                {
                    res.MissingBones.Add(humanName + " ← " + pmxName);
                    res.MissingHumanBones++;
                    continue;
                }
                if (Array.IndexOf(HumanTrait.BoneName, humanName) < 0)
                {
                    string fixedName = NormalizeHumanName(humanName);
                    if (fixedName == null)
                    {
                        res.Log.Add("  ⚠ Unity 不认这个 humanName: " + humanName);
                        continue;
                    }
                    humanName = fixedName;
                }
                human.Add(new HumanBone
                {
                    boneName = pmxName,
                    humanName = humanName,
                    limit = new HumanLimit { useDefaultValues = true },
                });
                res.MappedHumanBones++;
            }
            return human;
        }

        static Avatar BuildAvatar(GameObject root, List<PmxBone> bones, Transform[] boneTf, Result res)
        {
            var byName = new Dictionary<string, Transform>();
            for (int i = 0; i < bones.Count; i++)
                if (!byName.ContainsKey(bones[i].Name)) byName[bones[i].Name] = boneTf[i];

            var human = BuildHumanBones(byName, res);

            // ================================================================
            //  SkeletonBone 直接照抄骨骼 Transform 的实际值。
            //
            //  ★ 手臂的 rest 抬升已经在**建骨骼 Transform 时**就施加了
            //    （连同 bindposes 一起重算），所以这里不需要再动 ——
            //    SkeletonBone 和骨骼本来就是一致的。
            //
            //  ⚠️ 曾经只改 SkeletonBone、不改骨骼，结果 Unity 以为 T-pose 是水平的
            //     而网格还在 A-pose，偏差没消除只是换了方向 → 托腮时手穿进胸口。
            // ================================================================
            var skeleton = BuildSkeletonBones(root);
            var desc = new HumanDescription
            {
                human = human.ToArray(),
                skeleton = skeleton.ToArray(),
                armStretch = armStretch,
                legStretch = legStretch,
                upperArmTwist = upperArmTwist,
                lowerArmTwist = lowerArmTwist,
                upperLegTwist = upperLegTwist,
                lowerLegTwist = lowerLegTwist,
                feetSpacing = feetSpacing,
                hasTranslationDoF = false,
            };
            res.Log.Add(string.Format(
                "HumanDescription: armStretch={0:F3} legStretch={1:F3} upperArmTwist={2:F2} lowerArmTwist={3:F2} upperLegTwist={4:F2} lowerLegTwist={5:F2} feetSpacing={6:F3}",
                armStretch, legStretch, upperArmTwist, lowerArmTwist, upperLegTwist, lowerLegTwist, feetSpacing));

            var avatar = AvatarBuilder.BuildHumanAvatar(root, desc);
            avatar.name = root.name + "_Avatar";
            return avatar;
        }

        // ====================================================================
        //  ★ T1：构建 T-pose 版 Avatar（只改 SkeletonBone 的旋转）
        // ====================================================================

        [MenuItem("UnityLITF/换模型/④ 重建 T-pose Avatar", false, 40)]
        public static void RebuildTPoseAvatarMenu()
        {
            var root = GameObject.Find("Yixuan_Test");
            if (root == null) { Debug.LogError("[TPose] 场景里找不到 Yixuan_Test"); return; }
            Debug.Log(BuildTPoseAvatar(root));
        }

        [MenuItem("UnityLITF/换模型/④b 恢复原 Avatar（回滚）", false, 41)]
        public static void RestoreOrigAvatarMenu()
        {
            var root = GameObject.Find("Yixuan_Test");
            if (root == null) { Debug.LogError("[TPose] 场景里找不到 Yixuan_Test"); return; }
            var av = AssetDatabase.LoadAssetAtPath<Avatar>(OrigAvatarPath);
            if (av == null) { Debug.LogError("[TPose] 没有找到备份 " + OrigAvatarPath + "，无法回滚"); return; }
            var anim = root.GetComponent<Animator>();
            if (anim == null) { Debug.LogError("[TPose] 没有 Animator"); return; }
            anim.avatar = av;
            EditorUtility.SetDirty(anim);
            EnsureFolder(FolderZhuangfy);
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(root.scene);
            EditorSceneManager.SaveScene(root.scene);
            Debug.Log("[TPose] 已回滚为 " + OrigAvatarPath + "（记得把 ArmClearanceFix.Enabled 设回 true）");
        }

        const string FolderZhuangfy = "Assets/Models/Zhuangfy";
        const string OrigAvatarPath = FolderZhuangfy + "/Zhuangfy_Avatar_ORIG.asset";
        const string TPoseAvatarPath = FolderZhuangfy + "/Zhuangfy_Avatar_TPose.asset";

        /// <summary>
        /// 构建 T-pose 版 Avatar —— **不重导模型、不动骨骼 Transform、不动 bindposes**。
        ///
        /// 【根因】Unity Humanoid 重定向要求 `HumanDescription.skeleton` 是 T-pose。
        /// `BuildAvatar()` 把绑定姿态（A-pose，上臂下垂约 37°）原样抄进 skeleton，
        /// Unity 就当成 T-pose 用 → 所有肌肉值都相对一个错误基准 →
        /// 肩/肘的"肌肉 0"位置偏了 37°，表现为**两手交叉到身体中线**（13.11 ③h）。
        ///
        /// 【做法】skeleton 照抄当前 Transform，然后**只把上臂/前臂/手腕/腿链的
        /// SkeletonBone.rotation 摆成标准 T-pose**：
        ///   · 上臂 → 水平指向 ±X（左 −X、右 +X）
        ///   · 前臂、手腕 → 与上臂共线
        ///   · 大腿 → 小腿 → 脚踝 → 竖直向下
        /// 每个骨段用「当前方向 → 目标方向」算增量旋转，再沿父链换算成局部旋转，
        /// **从根往叶依次处理**（父骨改了，子骨当前的世界方向要重新算）。
        /// </summary>
        public static string BuildTPoseAvatar(GameObject root)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========== 重建 T-pose Avatar ==========");
            if (root == null) return sb.AppendLine("✘ root 为 null").ToString();

            var anim = root.GetComponent<Animator>();
            if (anim == null) { sb.AppendLine("✘ 没有 Animator"); return sb.ToString(); }

            // ★ 必须在编辑模式下做：Play 模式热换 Avatar 会打乱姿势（PROJECT_NOTES 13.11 ③i），
            //   且这里要存资产 + 存场景。放在最前面，避免"做到一半才中止"。
            if (Application.isPlaying)
            {
                sb.AppendLine("✘ 当前在 Play 模式 —— 请退出 Play 再跑（13.11 ③i：热换 Avatar 会打乱姿势）");
                return sb.ToString();
            }

            try
            {
                EnsureFolder(FolderZhuangfy);

                // ---- 1) 先备份当前 Avatar ------------------------------------
                //   当前 Avatar 是嵌在场景里的（没有资产路径），不先存成资产就没法回滚。
                if (AssetDatabase.LoadAssetAtPath<Avatar>(OrigAvatarPath) == null)
                {
                    if (anim.avatar == null) { sb.AppendLine("✘ 当前 Animator.avatar 为空，无备份可言"); return sb.ToString(); }
                    AssetDatabase.CreateAsset(anim.avatar, OrigAvatarPath);
                    AssetDatabase.SaveAssets();
                    sb.AppendLine("  已备份原 Avatar → " + OrigAvatarPath);
                }
                else
                {
                    sb.AppendLine("  原 Avatar 备份已存在（保留最初那份）: " + OrigAvatarPath);
                }

                // ---- 2) human 映射：照搬 BuildAvatar() 的同一张 HumanMap -------
                var byName = Yixuan.Model.MmdBoneNames.BuildIndex(root.transform);
                var res = new Result();
                var human = BuildHumanBones(byName, res);
                sb.AppendLine(string.Format("  HumanBone 映射 {0} 个，缺失 {1} 个",
                    res.MappedHumanBones, res.MissingHumanBones));

                // ---- 3) skeleton：照抄 Transform，再只改旋转 -------------------
                var skeleton = BuildSkeletonBones(root);
                int bonesChanged = ApplyTPoseToSkeleton(root.transform, skeleton, byName, sb);
                sb.AppendLine("  已摆成 T-pose 的 SkeletonBone: " + bonesChanged + " 根");

                // ---- 4) 构建 + 校验 + 存资产 ---------------------------------
                var desc = new HumanDescription
                {
                    human = human.ToArray(),
                    skeleton = skeleton.ToArray(),
                    armStretch = armStretch,
                    legStretch = legStretch,
                    upperArmTwist = upperArmTwist,
                    lowerArmTwist = lowerArmTwist,
                    upperLegTwist = upperLegTwist,
                    lowerLegTwist = lowerLegTwist,
                    feetSpacing = feetSpacing,
                    hasTranslationDoF = false,
                };
                var av = AvatarBuilder.BuildHumanAvatar(root, desc);
                av.name = "Zhuangfy_Avatar_TPose";
                sb.AppendLine(string.Format("  BuildHumanAvatar: isValid={0} isHuman={1}", av.isValid, av.isHuman));
                if (!av.isValid || !av.isHuman)
                {
                    sb.AppendLine("✘ 构建出来的 Avatar 无效，**不改场景、不存资产**（原 Avatar 原样保留）");
                    UnityEngine.Object.DestroyImmediate(av);
                    return sb.ToString();
                }

                if (AssetDatabase.LoadAssetAtPath<Avatar>(TPoseAvatarPath) != null)
                    AssetDatabase.DeleteAsset(TPoseAvatarPath);
                AssetDatabase.CreateAsset(av, TPoseAvatarPath);
                AssetDatabase.SaveAssets();

                // ---- 5) ★ 编辑模式下赋值（Play 模式已在最前面挡掉）------------
                anim.avatar = av;
                EditorUtility.SetDirty(anim);

                // ---- 6) 重建 UniHumanoid.Humanoid 的骨映射（13.11 ①）----------
                var hum = root.GetComponent<UniHumanoid.Humanoid>();
                if (hum != null)
                {
                    bool ok = hum.AssignBonesFromAnimator();
                    bool headOk = hum.GetBoneTransform(UnityEngine.HumanBodyBones.Head) != null;
                    EditorUtility.SetDirty(hum);
                    sb.AppendLine(string.Format("  Humanoid.AssignBonesFromAnimator={0}  Head={1}",
                        ok, headOk ? "✔" : "✘"));
                }
                else sb.AppendLine("  （没有 UniHumanoid.Humanoid 组件，跳过骨映射重建）");

                // ---- 7) 存场景 ----------------------------------------------
                AssetDatabase.SaveAssets();
                if (root.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(root.scene);
                    EditorSceneManager.SaveScene(root.scene);
                    sb.AppendLine("  场景已保存: " + root.scene.path);
                }

                sb.AppendLine("✔ T-pose Avatar 已赋值并保存 → " + TPoseAvatarPath);
                sb.AppendLine("  下一步：菜单 ⑦ 重定向基准检查，arm_max_deg 应 ≤ 5");
            }
            catch (Exception e)
            {
                sb.AppendLine("✘ 异常: " + e.GetType().Name + " " + e.Message);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 把 skeleton 里的手臂/腿链旋转摆成 T-pose，返回实际改动的骨数。
        /// ★ SkeletonBone.rotation 是**局部**旋转，和 Transform.localRotation 同一空间。
        /// </summary>
        static int ApplyTPoseToSkeleton(Transform root, List<SkeletonBone> skeleton,
                                        Dictionary<string, Transform> byName, StringBuilder sb)
        {
            // 骨名：场景 → (Transform, SkeletonBone 下标)
            var idx = new Dictionary<string, int>();
            for (int i = 0; i < skeleton.Count; i++)
                if (!idx.ContainsKey(skeleton[i].name)) idx[skeleton[i].name] = i;
            var tfOf = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (!tfOf.ContainsKey(t.name)) tfOf[t.name] = t;

            // skeleton 的局部旋转（改这个副本，最后写回 skeleton）
            var localRot = new Dictionary<string, Quaternion>();
            foreach (var b in skeleton) localRot[b.name] = b.rotation;

            // 父骨名
            var parentOf = new Dictionary<string, string>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (!parentOf.ContainsKey(t.name))
                    parentOf[t.name] = t.parent != null ? t.parent.name : null;

            Func<string, Quaternion> RotOf = null;
            RotOf = name =>
            {
                // 世界旋转 = 父链世界旋转 × 自己的局部旋转（局部旋转优先取 skeleton 里已修改的值）。
                // ⚠ 曾经写成"skeleton 里有就直接返回局部值"，结果子骨看不到父骨的抬升，前臂被抬了两次。
                if (string.IsNullOrEmpty(name)) return Quaternion.identity;
                string p; Quaternion pq = Quaternion.identity;
                if (parentOf.TryGetValue(name, out p) && !string.IsNullOrEmpty(p)) pq = RotOf(p);
                if (localRot.ContainsKey(name)) return pq * localRot[name];
                Transform t;
                if (tfOf.TryGetValue(name, out t)) return pq * t.localRotation;
                return pq;
            };

            // ---- 脊柱方向：髋→肩（用来把"水平"方向摆稳）----
            Transform armL = bone(byName, "左腕");
            Transform hip = bone(byName, "センター");
            Vector3 spineUp = Vector3.up;
            if (hip != null && armL != null)
            {
                var s = armL.position - hip.position;
                s.y = 0f;
                if (s.sqrMagnitude > 1e-8f) spineUp = s.normalized;
            }

            // ---- 目标姿态表（世界方向）----
            //   手臂：上臂水平指向 ±X；前臂、手腕与上臂共线
            //   腿  ：大腿→小腿→脚踝竖直向下
            var targets = new Dictionary<string, Vector3>();
            // 模型面朝 +Z、左臂在 −X（实测绑定姿态左上臂 dir.x = −0.80），直接用 ±X。
            // （spineUp 取的是髋→左肩的水平分量，指向侧面而非上方，拿它做修正会把目标算成 +Z。）
            AddTarget(targets, "左腕", -Vector3.right);
            AddTarget(targets, "左ひじ", -Vector3.right);
            AddTarget(targets, "左手首", -Vector3.right);
            AddTarget(targets, "右腕", Vector3.right);
            AddTarget(targets, "右ひじ", Vector3.right);
            AddTarget(targets, "右手首", Vector3.right);
            AddTarget(targets, "左足D", Down());
            AddTarget(targets, "左ひざD", Down());
            AddTarget(targets, "右足D", Down());
            AddTarget(targets, "右ひざD", Down());

            // ---- 处理顺序：根 → 叶（父骨先改，子骨的当前方向才正确）----
            var order = new[]
            {
                "左腕", "左ひじ", "左手首",
                "右腕", "右ひじ", "右手首",
                "左足D", "左ひざD",   // ⚠ 脚踝不动：T-pose 的脚朝前，设成"向下"会让 Unity 把踮脚当中立 → 一直踮脚尖
                "右足D", "右ひざD",
            };

            int changed = 0, missing = 0;
            foreach (var name in order)
            {
                Transform t;
                if (!byName.TryGetValue(name, out t)) { missing++; continue; }
                Vector3 goal;
                if (!targets.TryGetValue(name, out goal)) continue;

                Transform child = pickChild(t, idx);
                // 当前方向必须用 skeleton 里（已含父骨修改）的世界旋转推，不能用 Transform 的实际位置 ——
                // 否则父骨抬平后子骨还会按原 A-pose 方向再抬一次。
                Vector3 cur = child != null ? (RotOf(name) * child.localPosition).normalized : Vector3.zero;
                if (cur.sqrMagnitude < 1e-8f) { sb.AppendLine("    ⚠ 取不到骨方向: " + name); missing++; continue; }

                var qDelta = Quaternion.FromToRotation(cur, goal);
                var qWorld = qDelta * RotOf(name);
                string pn; Quaternion pq = Quaternion.identity;
                if (parentOf.TryGetValue(name, out pn) && !string.IsNullOrEmpty(pn)) pq = RotOf(pn);
                localRot[name] = Quaternion.Inverse(pq) * qWorld;
                changed++;
            }

            // ---- 写回 skeleton ----
            for (int i = 0; i < skeleton.Count; i++)
            {
                Quaternion q;
                if (localRot.TryGetValue(skeleton[i].name, out q))
                {
                    var b = skeleton[i];
                    b.rotation = q;
                    skeleton[i] = b;
                }
            }
            if (missing > 0) sb.AppendLine("    ⚠ 有 " + missing + " 根骨没处理（见上）");
            return changed;
        }

        static void AddTarget(Dictionary<string, Vector3> dict, string boneName, Vector3 dir)
        {
            if (!string.IsNullOrEmpty(boneName)) dict[boneName] = dir;
        }

        static Vector3 Down() { return Vector3.down; }

        /// <summary>把 ±X 摆成"与脊柱垂直的水平方向"——纯 ±X 在数值上不稳，用法线修正一下。</summary>
        static Vector3 Horizontal(Vector3 want, Vector3 spineUp)
        {
            var n = Vector3.Cross(want, spineUp);
            if (n.sqrMagnitude < 1e-6f) n = Vector3.Cross(want, Vector3.forward);
            if (n.sqrMagnitude < 1e-6f) n = Vector3.up;
            n.Normalize();
            var r = Vector3.Cross(n, spineUp);
            if (r.sqrMagnitude < 1e-6f) return want.normalized;
            r.Normalize();
            return Vector3.Dot(r, want) < 0f ? -r : r;
        }

        /// <summary>
        /// 取 t 的骨方向所指的那根子骨 —— 优先挑"在 skeleton 里有条目、且名字像下一节"的，
        /// 兜底挑第一根在 skeleton 里有条目的直接子骨（辅助骨不在 skeleton 里的概率很低）。
        /// </summary>
        static Transform pickChild(Transform t, Dictionary<string, int> idx)
        {
            Transform fallback = null;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (!idx.ContainsKey(c.name)) continue;
                if (c.name.Contains("ひじ") || c.name.Contains("肘") ||
                    c.name.Contains("手首") || c.name.Contains("足首") ||
                    c.name.Contains("ひざ") || c.name.Contains("膝") ||
                    c.name.Contains("先EX") || c.name.Contains("中指"))
                    return c;
                if (fallback == null) fallback = c;
            }
            return fallback;
        }

        static Transform childNamed(Transform parent, string token, string exclude)
        {
            foreach (var t in parent.GetComponentsInChildren<Transform>(true))
            {
                if (t == parent || t.name == exclude) continue;
                if (t.name.Contains(token)) return t;
            }
            return null;
        }

        static Transform bone(Dictionary<string, Transform> byName, string name)
        {
            Transform t;
            return byName.TryGetValue(name, out t) ? t : null;
        }

        static Material BuildMaterial(PmxMaterial m, string[] texPaths, string texDir, string outFolder, Result res)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var mat = new Material(shader);
            mat.name = m.Name;

            // 主贴图
            var baseTex = LoadTex(texPaths, m.Tex, texDir);
            if (baseTex != null)
            {
                mat.SetTexture("_BaseMap", baseTex);
                mat.SetTexture("_MainTex", baseTex);
            }
            mat.SetColor("_BaseColor", m.Diffuse);
            mat.SetColor("_Color", m.Diffuse);

            // 法线贴图：PMX 未引用，但包里给了同名 _N
            var nrm = LoadNormalFor(texPaths, m.Tex, texDir);
            if (nrm != null)
            {
                mat.SetTexture("_BumpMap", nrm);
                mat.SetFloat("_BumpScale", 1f);
            }

            // PMX 的 diffuse.a 用来标记「这个材质要半透明」。
            // ★ 但实测：**只有 diffuse.a < 0.5 才是真的透明材质**（如 目影/发影/照れ = 0.0）。
            //   衣服的 diffuse.a = 1.0，不该当透明处理 —— 尽管它的贴图里带 alpha 通道
            //   （动漫模型常见：D 图 alpha 是杂讯/备用，不能当透明度用）。
            //   曾经用 `a < 0.999` 判定，结果整件衣服变半透明，能看到内衬 → 像"衣服穿帮"。
            bool alpha = m.Diffuse.a < 0.5f;
            if (alpha)
            {
                mat.SetFloat("_Surface", 1f);              // Transparent
                mat.SetFloat("_Blend", 0f);                // Alpha
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetFloat("_ZWrite", 0f);
                mat.SetFloat("_AlphaClip", 0f);
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.DisableKeyword("_ALPHATEST_ON");
            }
            else
            {
                mat.SetFloat("_Surface", 0f);
                mat.SetFloat("_ZWrite", 1f);
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            }

            // 双面：PMX flag bit0 = 双面绘制
            bool doubleSided = (m.Flag & 0x01) != 0;
            mat.SetFloat("_Cull", doubleSided ? (float)UnityEngine.Rendering.CullMode.Off
                                              : (float)UnityEngine.Rendering.CullMode.Back);

            // 粗糙度：动漫模型用较光滑的皮肤 / 布料
            float smooth = 0.35f;
            string n = m.Name;
            if (n.Contains("肌") || n.Contains("面")) smooth = 0.45f;
            else if (n.Contains("目") || n.Contains("HL")) smooth = 0.85f;
            else if (n.Contains("Cloth")) smooth = 0.30f;
            else if (n.Contains("发")) smooth = 0.42f;
            mat.SetFloat("_Smoothness", smooth);
            mat.SetFloat("_Glossiness", smooth);
            mat.SetFloat("_Metallic", 0f);

            // 存成资产
            string safe = Sanitize(m.Name);
            string path = outFolder + "/Materials/" + safe + ".mat";
            EnsureFolder(outFolder + "/Materials");
            AssetDatabase.CreateAsset(mat, path);
            res.Log.Add(string.Format("  材质 {0,-12} → {1}  tex={2} alpha={3} double={4}",
                m.Name, safe, baseTex != null ? baseTex.name : "无", alpha, doubleSided));
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            var r = sb.ToString();
            return string.IsNullOrEmpty(r) ? "mat" : r;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            var leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        static Texture2D LoadTex(string[] texPaths, int idx, string texDir)
        {
            if (idx < 0 || idx >= texPaths.Length) return null;
            string fn = texPaths[idx].Replace('\\', '/');
            fn = fn.Substring(fn.LastIndexOf('/') + 1);
            string p = texDir + "/" + fn;
            return AssetDatabase.LoadAssetAtPath<Texture2D>(p);
        }

        /// <summary>给贴图找同名 _N（法线）贴图</summary>
        static Texture2D LoadNormalFor(string[] texPaths, int idx, string texDir)
        {
            if (idx < 0 || idx >= texPaths.Length) return null;
            string fn = texPaths[idx].Replace('\\', '/');
            fn = fn.Substring(fn.LastIndexOf('/') + 1);
            if (!fn.EndsWith("_D.png")) return null;
            string n = fn.Substring(0, fn.Length - 6) + "_N.png";
            return AssetDatabase.LoadAssetAtPath<Texture2D>(texDir + "/" + n);
        }
    }
}
#endif
