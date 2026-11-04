using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Yixuan.AI
{
    /// <summary>一条对话消息（OpenAI 兼容格式：role = system / user / assistant）</summary>
    [System.Serializable]
    public class ChatMessage
    {
        public string role;
        public string content;

        public ChatMessage() { }
        public ChatMessage(string role, string content)
        {
            this.role = role;
            this.content = content;
        }

        public override string ToString()
        {
            return "[" + role + "] " + content;
        }
    }

    /// <summary>
    /// LLM 客户端接口。实现方负责网络、超时、重试；调用方只关心"给消息、拿回复原文"。
    ///
    /// 约定：
    /// - 返回的是 LLM 的原始文本（可能是包着废话的 JSON），交给 ResponseParser 解析；
    /// - 失败（网络错/超时/HTTP 非 200/空回复）返回 null，不抛异常 —— 由 ChatSession 回退 RuleBrain；
    /// - 实现必须**不阻塞主线程**（Unity 主线程卡死会冻住编辑器）。
    /// </summary>
    public interface ILLMClient
    {
        /// <summary>是否有请求正在进行</summary>
        bool IsBusy { get; }

        /// <summary>最近一次失败原因（成功时清空）</summary>
        string LastError { get; }

        /// <summary>发一轮对话，返回 LLM 原始文本；失败返回 null</summary>
        Task<string> ChatAsync(List<ChatMessage> messages, CancellationToken ct);
    }
}
