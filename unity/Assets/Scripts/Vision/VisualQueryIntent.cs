using System.Text.RegularExpressions;

namespace Yixuan.Vision
{
    public static class VisualQueryIntent
    {
        static readonly Regex UserPosture = new Regex(@"我(?:现在|刚才|刚刚|有没有|是不是|是否|在|还|已经|刚|正|有|没|不)*(?:低头|抬头|转头|举手|抬手|姿势|动作|表情)", RegexOptions.Compiled);

        public static bool IsUserPostureQuestion(string text)
        {
            if (string.IsNullOrEmpty(text) || !UserPosture.IsMatch(text)) return false;
            return ContainsAny(text, "吗", "？", "?", "有没有", "是不是", "什么", "是否", "怎么样");
        }

        public static bool IsTemporalQuestion(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (ContainsAny(text, "说了", "说过", "问了", "问过", "聊了", "提过", "告诉") && !ContainsAny(text, "画面", "视频", "动作")) return false;
            bool past = ContainsAny(text, "刚才", "刚刚", "之前", "先后", "顺序", "过程", "变化", "演示", "做了什么", "干了什么", "拿过", "挪到", "放到哪里", "先拿", "后拿", "又拿");
            if (!past) return false;
            // 过去词必须和"视觉对象/动作/方向"同时出现，避免把"我刚才很开心"这类纯对话回忆当视频回顾。
            return ContainsAny(text, "看到", "看见", "看", "画面", "视频", "镜头", "摄像头", "手", "手机", "鼠标", "键盘",
                                     "杯", "瓶", "桌", "柜", "货架", "门", "窗", "书", "包", "衣", "眼镜", "头发",
                                     "数字", "手势", "比划", "表情", "动作", "姿势", "东西", "物品",
                                     "拿", "放", "挪", "移", "动", "换", "掉", "捡", "扔",
                                     "先", "后", "左", "右", "上", "下", "前", "后", "变化", "顺序", "过程", "演示",
                                     "做了什么", "干了什么", "干嘛", "做什么", "干什么");
        }

        public static bool NeedsCameraPlanning(string text)
        {
            if (string.IsNullOrEmpty(text) || IsUserPostureQuestion(text) || IsTemporalQuestion(text)) return false;
            return ContainsAny(text, "柜台", "收银台", "货架", "门口", "入口", "店门", "左边", "右边", "身后", "后方")
                && ContainsAny(text, "看", "注意", "观察", "转", "情况", "有什么");
        }

        public static bool ContainsAny(string text, params string[] words)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int index = 0; index < words.Length; index++)
                if (text.Contains(words[index])) return true;
            return false;
        }
    }
}
