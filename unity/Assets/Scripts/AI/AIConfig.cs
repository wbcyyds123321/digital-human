using UnityEngine;

namespace Yixuan.AI
{
    /// <summary>
    /// AI 对话配置（ScriptableObject）。资产放 Assets/Settings/AIConfig.asset。
    ///
    /// 【API Key 绝不进资产、不进代码】
    /// 读取优先级：
    ///   1. 环境变量 DEEPSEEK_API_KEY
    ///   2. %LOCALAPPDATA%Low\DefaultCompany\Become human\apikey.txt（Application.persistentDataPath）
    /// 都没有 → HasApiKey = false → ChatSession 走 RuleBrain 离线兜底（Console 只警告一次）。
    /// </summary>
    [CreateAssetMenu(fileName = "AIConfig", menuName = "Yixuan/AI Config")]
    public class AIConfig : ScriptableObject
    {
        [Header("DeepSeek 接口（OpenAI 兼容）")]
        public string Endpoint = "https://api.deepseek.com/chat/completions";
        // 2026-10 官方 /models：deepseek-flash / deepseek-v4-pro（旧的 deepseek-chat 已下架）
        public string Model = "deepseek-flash";
        [Range(0f, 2f)] public float Temperature = 0.8f;
        [Tooltip("单次请求超时（秒）")]
        public int TimeoutSec = 20;

        [Header("思考模式（DeepSeek）")]
        [Tooltip("开启后允许模型先思考再回答；会更慢，长答/深问题可开")]
        public bool ThinkingEnabled = false;
        [Tooltip("思考强度：low / high（DeepSeek reasoning_effort）")]
        public string ThinkingEffort = "low";
        [Tooltip("历史保留轮数（已弃用：现在按 HistoryTokenBudget 的 token 预算裁剪）")]
        public int HistoryTurns = 6;

        [Header("对话记忆（2026-10-20）")]
        [Tooltip("每次请求最多带多少 token 的历史（估算：1 汉字≈1 token）；超出时从最旧开始丢")]
        public int HistoryTokenBudget = 12000;
        [Tooltip("要求 LLM 回复的字数上限（约束 prompt 用）")]
        public int MaxReplyChars = 60;

        [Header("人设")]
        public string CharacterName = "庄方宜";
        [TextArea(4, 10)]
        public string Persona =
            "来自《明日方舟：终末地》的干员，现在以数字人形象与用户面对面聊天。" +
            "性格沉稳、温和、认真，说话简洁自然，偶尔带一点轻松的俏皮；" +
            "不夸张、不卖萌、不油腻，不用书面腔。";

        [Header("行为编排（Director / ChatSession 用）")]
        [Range(0f, 1f)] public float DefaultIntensity = 0.6f;
        [Tooltip("LLM 没指定 gesture 时，按情绪默认表抽一个动作的概率")]
        [Range(0f, 1f)] public float EmotionGestureChance = 0.5f;
        [Tooltip("同一动作冷却（秒）—— 冷却期内不重复播同一个动作")]
        public float GestureCooldownSec = 8f;
        [Tooltip("说话时长估算：每字多少秒")]
        public float SpeechSecondsPerChar = 0.18f;
        [Tooltip("说完后过多少秒恢复 YixuanBrain 的主动行为")]
        public float ResumeBrainDelaySec = 3f;        [Tooltip("表情渐入时间（秒）—— 设计目标 0.25s")]
        public float ExpressionFadeInSec = 0.25f;
        [Tooltip("表情说完后的衰减时间（秒）—— 设计目标 1.5s")]
        public float ExpressionDecaySec = 1.5f;

        [Header("回复流式（P6）")]
        [Tooltip("off=旧整段；light=完整 JSON 后分句流式；full=LLM SSE + 增量 JSON + 分句流式")]
        public string StreamMode = "full";

        [Header("语音（M1：MiniMax 克隆音色 → 本地 TTS sidecar）")]
        public bool VoiceEnabled = true;
        [Tooltip("本地 TTS 小服务地址（Tools/Voice/start_tts.bat 启动）")]
        public string VoiceEndpoint = "http://127.0.0.1:8766";
        [Tooltip("MiniMax 克隆音色 voice_id（用 Tools/Voice/minimax_try.py 生成）")]
        public string VoiceId = "zhuangfy001";
        [Tooltip("minimax（当前唯一接入的云引擎）；edge 暂未接入")]
        public string VoiceEngine = "minimax";
        [Tooltip("TTS 请求超时（秒）")]
        public int VoiceTimeoutSec = 20;
        [Range(0f, 1f)] public float VoiceVolume = 1f;

        [Header("语音输入（P5 全双工：AEC + 可打断）")]
        public bool VoiceInputEnabled = true;
        [Tooltip("全双工语音服务（Tools/Voice/start_voice.bat → voice_server.py，8767）")]
        public string VoiceServerEndpoint = "http://127.0.0.1:8767";
        [Tooltip("duplex = sidecar 播放 + 可打断；unity = M1 的 Unity 播放（半双工）；off = 关闭")]
        public string VoiceInputMode = "duplex";
        [Tooltip("AI 说话时允许用户开口打断（barge-in）")]
        public bool BargeInEnabled = true;

        [Header("口型同步（P6 LipSync）")]
        [Tooltip("sidecar 播放时通过 UDP 驱动口型；关闭则嘴巴不动（表情仍正常）")]
        public bool LipSyncEnabled = true;

        // ---- API Key（运行时缓存，绝不序列化）----
        [System.NonSerialized] string _keyCache;
        [System.NonSerialized] bool _keyChecked;

        /// <summary>解析出的 API Key；没有返回空串（不会返回 null）</summary>
        public string ApiKey
        {
            get
            {
                if (_keyChecked) return _keyCache ?? "";
                _keyChecked = true;

                string key = null;
                try { key = System.Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"); }
                catch (System.Exception) { key = null; }

                if (string.IsNullOrEmpty(key))
                {
                    try
                    {
                        string path = System.IO.Path.Combine(Application.persistentDataPath, "apikey.txt");
                        if (System.IO.File.Exists(path)) key = System.IO.File.ReadAllText(path).Trim();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning("[AIConfig] 读取 apikey.txt 失败：" + e.Message);
                    }
                }

                _keyCache = string.IsNullOrEmpty(key) ? "" : key;
                return _keyCache;
            }
        }

        public bool HasApiKey { get { return !string.IsNullOrEmpty(ApiKey); } }

        /// <summary>让下一次访问重新读环境变量/文件（换 key 后调用）</summary>
        public void ClearKeyCache()
        {
            _keyCache = null;
            _keyChecked = false;
        }

        /// <summary>
        /// 找默认配置：优先 Resources/AIConfig，其次编辑器里第一个 AIConfig 资产。
        /// 场景里显式赋值优先于这个兜底。
        /// </summary>
        public static AIConfig FindDefault()
        {
            var cfg = Resources.Load<AIConfig>("AIConfig");
            if (cfg != null) return cfg;
#if UNITY_EDITOR
            var guids = UnityEditor.AssetDatabase.FindAssets("t:AIConfig");
            if (guids != null && guids.Length > 0)
                return UnityEditor.AssetDatabase.LoadAssetAtPath<AIConfig>(
                    UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]));
#endif
            return null;
        }
    }
}
