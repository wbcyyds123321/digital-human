using UnityEngine;
using System;
using System.IO;
using System.Collections.Generic;

namespace Yixuan.Core
{
    /// <summary>
    /// 预设持久化：把用户在 UI 里微调出来的表情保存到磁盘，重启后自动加载。
    ///
    /// 存放位置：Application.persistentDataPath/presets.json
    ///   Windows: C:/Users/&lt;用户&gt;/AppData/LocalLow/&lt;公司&gt;/&lt;产品&gt;/presets.json
    ///
    /// 【为什么用 JsonUtility 而不是 Newtonsoft】
    /// 项目已装 com.unity.nuget.newtonsoft-json，但 JsonUtility 零依赖、
    /// 对这种扁平结构完全够用，且不会因为包版本变化出问题。
    ///
    /// 【数据结构】
    /// 自定义表情用"形态键名 → 权重"的列表保存（不是字典），
    /// 因为 Unity 的 JsonUtility 不支持 Dictionary 序列化。
    /// </summary>
    public class PresetStore : MonoBehaviour
    {
        public const string FileName = "presets.json";

        [Header("自动加载 / 保存")]
        public bool AutoLoadOnStart = true;
        public bool AutoSaveOnChange = false;

        [Header("调试")]
        public bool VerboseLog = true;

        // ---- 序列化结构 ----
        [Serializable]
        public class MorphPair
        {
            public string name;
            public float weight;
        }

        [Serializable]
        public class CustomExpressionData
        {
            public string id;
            public string label;
            public List<MorphPair> morphs = new List<MorphPair>();
        }

        [Serializable]
        public class SettingsData
        {
            public float exposure = -0.15f;
            public int backgroundIndex = 0;
            public float expressionInterval = 2.4f;
            public float gestureInterval = 4.5f;
            public bool autoPlay = true;
            public bool blinkEnabled = true;
        }

        [Serializable]
        public class RootData
        {
            public int version = 1;
            public List<CustomExpressionData> customExpressions = new List<CustomExpressionData>();
            public SettingsData settings = new SettingsData();
        }

        public RootData Data { get; private set; }
        public string FilePath { get { return Path.Combine(Application.persistentDataPath, FileName); } }

        void Awake()
        {
            Data = new RootData();
            if (AutoLoadOnStart) Load();
        }

        // ============================================================
        //  存取
        // ============================================================

        public bool Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    if (VerboseLog) Debug.Log("[PresetStore] 无预设文件，使用默认值: " + FilePath);
                    Data = new RootData();
                    return false;
                }

                string json = File.ReadAllText(FilePath);
                var loaded = JsonUtility.FromJson<RootData>(json);
                if (loaded == null) { Data = new RootData(); return false; }

                Data = loaded;
                if (Data.customExpressions == null) Data.customExpressions = new List<CustomExpressionData>();
                if (Data.settings == null) Data.settings = new SettingsData();

                RegisterAllToLibrary();

                if (VerboseLog)
                    Debug.Log($"[PresetStore] 已加载 {Data.customExpressions.Count} 个自定义表情 <- {FilePath}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[PresetStore] 加载失败: " + e.Message);
                Data = new RootData();
                return false;
            }
        }

        public bool Save()
        {
            try
            {
                string json = JsonUtility.ToJson(Data, true);
                File.WriteAllText(FilePath, json);
                if (VerboseLog)
                    Debug.Log($"[PresetStore] 已保存 {Data.customExpressions.Count} 个自定义表情 -> {FilePath}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[PresetStore] 保存失败: " + e.Message);
                return false;
            }
        }

        // ============================================================
        //  自定义表情
        // ============================================================

        /// <summary>把一个表情定义保存为自定义预设（覆盖同 id）</summary>
        public void SaveCustomExpression(string id, string label, List<MorphPair> morphs)
        {
            if (string.IsNullOrEmpty(id)) return;

            var found = Data.customExpressions.Find(delegate(CustomExpressionData d) { return d.id == id; });
            if (found == null)
            {
                found = new CustomExpressionData { id = id, label = label };
                Data.customExpressions.Add(found);
            }
            found.label = label;
            found.morphs = morphs != null ? morphs : new List<MorphPair>();

            RegisterCustomToLibrary(found);
            Save();
        }

        /// <summary>删除一个自定义预设</summary>
        public void RemoveCustomExpression(string id)
        {
            Data.customExpressions.RemoveAll(delegate(CustomExpressionData d) { return d.id == id; });
            Save();
        }

        public bool HasCustom(string id)
        {
            return Data.customExpressions.Exists(delegate(CustomExpressionData d) { return d.id == id; });
        }

        // ============================================================
        //  设置
        // ============================================================

        public void SaveSettings(SettingsData s)
        {
            if (s == null) return;
            Data.settings = s;
            Save();
        }

        // ============================================================
        //  注册到表达式库
        // ============================================================

        void RegisterAllToLibrary()
        {
            for (int i = 0; i < Data.customExpressions.Count; i++)
                RegisterCustomToLibrary(Data.customExpressions[i]);
        }

        static void RegisterCustomToLibrary(CustomExpressionData d)
        {
            if (d == null || string.IsNullOrEmpty(d.id)) return;

            var keys = new Yixuan.Face.ExpressionLibrary.MorphKey[
                d.morphs != null ? d.morphs.Count : 0];
            for (int i = 0; i < keys.Length; i++)
                keys[i] = new Yixuan.Face.ExpressionLibrary.MorphKey(d.morphs[i].name, d.morphs[i].weight);

            Yixuan.Face.ExpressionLibrary.Register(new Yixuan.Face.ExpressionLibrary.ExpressionDef {
                Id = d.id,
                Label = string.IsNullOrEmpty(d.label) ? d.id : d.label,
                Morphs = keys,
            });
        }

        /// <summary>从当前表情库导出（用于首次生成默认预设文件）</summary>
        public void ExportAllBuiltins()
        {
            Data.customExpressions.Clear();
            var all = Yixuan.Face.ExpressionLibrary.All;
            for (int i = 0; i < all.Count; i++)
            {
                var def = all[i];
                if (def.Morphs == null || def.Morphs.Length == 0) continue;
                var ce = new CustomExpressionData { id = def.Id, label = def.Label };
                for (int k = 0; k < def.Morphs.Length; k++)
                    ce.morphs.Add(new MorphPair { name = def.Morphs[k].Morph, weight = def.Morphs[k].Weight });
                Data.customExpressions.Add(ce);
            }
            Save();
        }

        public string Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[PresetStore] " + FilePath);
            sb.AppendLine("   自定义表情 " + Data.customExpressions.Count + " 个");
            for (int i = 0; i < Data.customExpressions.Count && i < 8; i++)
                sb.AppendLine("     " + Data.customExpressions[i].id + " (" + Data.customExpressions[i].label + ") " +
                              Data.customExpressions[i].morphs.Count + " 键");
            sb.AppendLine("   设置: 曝光=" + Data.settings.exposure.ToString("F2") +
                          " 背景=" + Data.settings.backgroundIndex +
                          " 表情间隔=" + Data.settings.expressionInterval.ToString("F1") +
                          " 动作间隔=" + Data.settings.gestureInterval.ToString("F1"));
            return sb.ToString();
        }
    }
}
