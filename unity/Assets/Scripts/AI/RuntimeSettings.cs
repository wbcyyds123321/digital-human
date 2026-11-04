using System;
using System.IO;
using UnityEngine;

namespace Yixuan.AI
{
    /// <summary>
    /// 用户可调设置（持久化到 persistentDataPath/settings.json）。
    /// 出厂默认来自 AIConfig.asset；用户改过的值覆盖默认，重启后自动加载。
    /// </summary>
    [Serializable]
    public class RuntimeSettings
    {
        public int maxReplyChars = 60;
        public int historyTokenBudget = 12000;
        public string streamMode = "full";     // off / light / full
        public bool bargeInEnabled = true;
        public bool thinkingEnabled = false;
        public string thinkingEffort = "low"; // low / high

        public static string FilePath
        {
            get { return Path.Combine(Application.persistentDataPath, "settings.json"); }
        }

        /// <summary>出厂默认（与 AIConfig.asset 当前默认一致；恢复默认用这个，不依赖运行时被改过的 Config）</summary>
        public static RuntimeSettings FactoryDefaults()
        {
            return new RuntimeSettings
            {
                maxReplyChars = 60,
                historyTokenBudget = 12000,
                streamMode = "full",
                bargeInEnabled = true,
                thinkingEnabled = false,
                thinkingEffort = "low",
            };
        }

        public static RuntimeSettings FromConfig(AIConfig c)
        {
            RuntimeSettings s = new RuntimeSettings();
            if (c != null)
            {
                s.maxReplyChars = c.MaxReplyChars;
                s.historyTokenBudget = c.HistoryTokenBudget;
                s.streamMode = c.StreamMode;
                s.bargeInEnabled = c.BargeInEnabled;
                s.thinkingEnabled = c.ThinkingEnabled;
                s.thinkingEffort = c.ThinkingEffort;
            }
            return s;
        }

        public RuntimeSettings Clone()
        {
            return new RuntimeSettings
            {
                maxReplyChars = maxReplyChars,
                historyTokenBudget = historyTokenBudget,
                streamMode = streamMode,
                bargeInEnabled = bargeInEnabled,
                thinkingEnabled = thinkingEnabled,
                thinkingEffort = thinkingEffort,
            };
        }

        public void ApplyTo(AIConfig c)
        {
            if (c == null) return;
            c.MaxReplyChars = Mathf.Clamp(maxReplyChars, 20, 400);
            c.HistoryTokenBudget = Mathf.Clamp(historyTokenBudget, 2000, 64000);
            c.StreamMode = NormalizeMode(streamMode);
            c.BargeInEnabled = bargeInEnabled;
            c.ThinkingEnabled = thinkingEnabled;
            c.ThinkingEffort = (thinkingEffort == "high") ? "high" : "low";
        }

        public static string NormalizeMode(string mode)
        {
            if (mode == "off" || mode == "light" || mode == "full") return mode;
            return "full";
        }

        public static RuntimeSettings Load(AIConfig config)
        {
            RuntimeSettings fallback = FromConfig(config);
            try
            {
                if (!File.Exists(FilePath)) return fallback;
                string json = File.ReadAllText(FilePath, System.Text.Encoding.UTF8);
                RuntimeSettings loaded = JsonUtility.FromJson<RuntimeSettings>(json);
                if (loaded == null) return fallback;

                // 旧文件/缺字段容错：非法值回退默认
                if (loaded.maxReplyChars < 20 || loaded.maxReplyChars > 400) loaded.maxReplyChars = fallback.maxReplyChars;
                if (loaded.historyTokenBudget < 2000 || loaded.historyTokenBudget > 64000) loaded.historyTokenBudget = fallback.historyTokenBudget;
                loaded.streamMode = NormalizeMode(loaded.streamMode);
                if (loaded.thinkingEffort != "high") loaded.thinkingEffort = "low";
                return loaded;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Settings] 读取失败，使用默认值：" + e.Message);
                return fallback;
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonUtility.ToJson(this, true), System.Text.Encoding.UTF8);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Settings] 保存失败：" + e.Message);
            }
        }
    }
}
