#if UNITY_EDITOR
using System;
using System.Text;
using UnityEngine;
using UnityEditor;

namespace Yixuan.AI
{
    /// <summary>
    /// AI 层的编辑器自测菜单（整个文件只在编辑器编译，运行时不会有任何代码）。
    ///
    /// 菜单：UnityLITF/AI/测试解析器
    /// 作用：用 6 条写死的模拟 LLM 回复跑 ResponseParser.Parse，验证 B2 验收要求：
    ///   ① 正常完整 JSON        → ok=true，字段原样保留
    ///   ② 缺字段（只有 reply）  → ok=true，缺省值补齐（neutral / 0.6 / gesture=null / gaze=user）
    ///   ③ 非法 id              → emotion 改 neutral、gesture 改 null、beats 非法条目丢弃，且打印警告
    ///   ④ 多余文字包着 JSON    → 能截取到中间的 JSON，ok=true
    ///   ⑤ 空字符串             → ok=false，不抛异常
    ///   ⑥ 纯非 JSON 文本       → ok=false，不抛异常
    /// 最后打印 "PARSER_TEST pass=6 fail=0"（或实际结果）。
    ///
    /// 【关于下面写死的 id】
    /// 这些是"模拟 LLM 返回的测试数据"，不是生产逻辑 —— 生产代码里不允许写死动作/表情 id，
    /// 校验一律走 ActionCatalog。测试数据固定才能对比每次改动。
    /// </summary>
    public static class AIEditorMenu
    {
        /// <summary>
        /// 把 AI 实际收到的 system prompt 写到 Tools/AI/system_prompt.txt。
        /// 用途：没有 API Key 时，粘进 DeepSeek 网页版人工试聊；调 prompt 时对比前后差异。
        /// </summary>
        [MenuItem("UnityLITF/AI/导出 System Prompt")]
        public static void ExportSystemPrompt()
        {
            try { ActionCatalog.Refresh(); }
            catch (Exception e) { Debug.LogWarning("[AI] ActionCatalog.Refresh 失败：" + e.Message); }

            string text = PromptBuilder.BuildSystemPrompt(AIConfig.FindDefault());
            string path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Tools/AI/system_prompt.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            Debug.Log("[AI] System Prompt 已导出（" + text.Length + " 字）→ " + path);
        }

        [MenuItem("UnityLITF/AI/测试解析器")]
        public static void TestResponseParser()
        {
            // 先刷新清单：菜单里的 PASS 判定才和运行时一致（Parse 内部也会刷，这里只为打印数量）
            try { ActionCatalog.Refresh(); }
            catch (Exception e) { Debug.LogWarning("[ParserTest] ActionCatalog.Refresh 失败：" + e.Message); }

            int gestureCount = CountOf(ActionCatalog.GestureIds());
            int emotionCount = CountOf(ActionCatalog.EmotionIds());

            string[] names = new string[]
            {
                "正常完整 JSON",
                "缺字段（只有 reply）",
                "非法 id（emotion/gesture/beat 都不存在）",
                "多余文字包着 JSON",
                "空字符串",
                "纯非 JSON 文本",
                "v2 契约（字段乱序，含 demeanor）",
                "v2 非法 demeanor（按表情反推 + 警告）",
            };

            string[] raws = new string[]
            {
                // ① 正常完整 JSON（beats 也用合法动作）
                "{\"reply\":\"你好呀，今天想聊点什么？\",\"emotion\":\"smile\",\"intensity\":0.7," +
                "\"gesture\":\"wave\",\"posture\":\"stand\",\"gaze\":\"user\"," +
                "\"beats\":[{\"at\":0,\"gesture\":\"wave\"},{\"at\":6,\"gesture\":\"nod\"}]}",

                // ② 缺字段：只有 reply，其他全缺
                "{\"reply\":\"今天天气不错。\"}",

                // ③ 非法 id：emotion / gesture / beats 里各有一个不存在的 id
                "{\"reply\":\"这个我不太确定。\",\"emotion\":\"confused_xyz\",\"intensity\":0.5," +
                "\"gesture\":\"moonwalk\",\"posture\":\"stand\",\"gaze\":\"user\"," +
                "\"beats\":[{\"at\":1,\"gesture\":\"wave\"},{\"at\":4,\"gesture\":\"moonwalk\"}]}",

                // ④ 前面有废话 + markdown 代码块包着 JSON
                "好的，这是结果：\n```json\n{\"reply\":\"当然可以，我把清单整理给你。\",\"emotion\":\"smile\"," +
                "\"intensity\":0.6,\"gesture\":\"nod\"}\n```\n以上。",

                // ⑤ 空字符串
                "",

                // ⑥ 纯非 JSON 文本（没有花括号）
                "抱歉，我这次没法回答这个问题。",

                // ⑦ v2 契约：字段顺序打乱（JsonUtility 按名映射，顺序无关），demeanor 合法
                "{\"reply\":\"哪有……你也太会说话了。\",\"emotion\":\"shy\",\"intensity\":0.6,\"demeanor\":\"shy\"," +
                "\"gaze\":\"down\",\"wish\":\"\",\"gesture\":null,\"posture\":null,\"feeling\":\"被夸了，有点不好意思\"}",

                // ⑧ v2 非法 demeanor：应降级为 attentive 并给出警告
                "{\"feeling\":\"有点紧张\",\"demeanor\":\"nervous_xyz\",\"gaze\":\"down\",\"emotion\":\"troubled\"," +
                "\"intensity\":0.5,\"gesture\":null,\"posture\":null,\"wish\":\"\",\"reply\":\"我……有点紧张。\"}",
            };

            var sb = new StringBuilder();
            sb.AppendLine("========== AI 解析器自测（8 条模拟 LLM 回复）==========");
            sb.AppendLine("Catalog：动作 " + gestureCount + " 个 / 表情 " + emotionCount + " 个");
            if (gestureCount == 0)
                sb.AppendLine("⚠ 动作清单为空：先打开含 Yixuan_Test 的场景再跑，否则合法 gesture 会被降级成 null（环境问题，不是解析器 bug）");
            sb.AppendLine();

            int pass = 0;
            int fail = 0;

            for (int i = 0; i < raws.Length; i++)
            {
                string raw = raws[i];
                PlanParseResult result = null;
                bool threw = false;
                string exception = null;

                // 逐条 try/catch：解析器就算有 bug 也不能让菜单中断（B2 起：全部条目不抛异常）
                try
                {
                    result = ResponseParser.Parse(raw);
                }
                catch (Exception e)
                {
                    threw = true;
                    exception = e.Message;
                }

                bool ok = !threw && result != null && CheckCase(i, result);
                if (ok) pass++; else fail++;

                sb.AppendLine("【" + (i + 1) + "】" + names[i] + "   →   " + (ok ? "PASS" : "FAIL"));
                sb.AppendLine("    输入 : " + Preview(raw));

                if (threw)
                {
                    sb.AppendLine("    异常 : ✗ " + exception + "（解析器不允许抛异常！）");
                }
                else if (result == null)
                {
                    sb.AppendLine("    结果 : ✗ Parse 返回 null（不允许）");
                }
                else
                {
                    sb.AppendLine("    ok   : " + result.ok);
                    sb.AppendLine("    plan : " + (result.plan != null ? result.plan.ToString() : "null"));
                    sb.AppendLine("    警告 : " + result.WarningText());
                }
                sb.AppendLine();
            }

            sb.AppendLine("PARSER_TEST pass=" + pass + " fail=" + fail);

            if (fail == 0) Debug.Log(sb.ToString());
            else Debug.LogError(sb.ToString());
        }

        /// <summary>每条用例的通过条件（与上面的 8 条一一对应）</summary>
        static bool CheckCase(int index, PlanParseResult r)
        {
            switch (index)
            {
                case 0:  // ① 正常完整 JSON：reply/emotion/intensity/posture/gaze 都应原样保留
                    return r.ok && r.plan != null
                           && !string.IsNullOrEmpty(r.plan.reply)
                           && r.plan.emotion == "smile"
                           && Mathf.Abs(r.plan.intensity - 0.7f) < 0.001f
                           && r.plan.posture == "stand"
                           && r.plan.gaze == "user";

                case 1:  // ② 缺字段：ok=true（有 reply），缺省值补齐
                    return r.ok && r.plan != null
                           && !string.IsNullOrEmpty(r.plan.reply)
                           && r.plan.emotion == "neutral"
                           && Mathf.Abs(r.plan.intensity - 0.6f) < 0.001f
                           && r.plan.gesture == null
                           && r.plan.posture == ""
                           && r.plan.gaze == "user";

                case 2:  // ③ 非法 id：全部修正/丢弃，且必须有警告
                    return r.ok && r.plan != null
                           && r.plan.emotion == "neutral"
                           && r.plan.gesture == null
                           && r.HasWarnings
                           && AllBeatsValid(r.plan.beats);   // 非法 beat 必须已被丢弃

                case 3:  // ④ 多余文字包着 JSON：能截出来，reply 正常
                    return r.ok && r.plan != null && !string.IsNullOrEmpty(r.plan.reply);

                case 4:  // ⑤ 空字符串：ok=false，不抛异常
                    return !r.ok;

                case 5:  // ⑥ 纯文本：ok=false，不抛异常
                    return !r.ok;

                case 6:  // ⑦ v2 契约（字段乱序、含 demeanor）：ok，demeanor/gaze 原样保留
                    return r.ok && r.plan != null
                           && !string.IsNullOrEmpty(r.plan.reply)
                           && r.plan.demeanor == "shy"
                           && r.plan.gaze == "down";

                case 7:  // ⑧ v2 非法 demeanor：按表情反推（troubled → tired）+ 必须有警告
                    return r.ok && r.plan != null
                           && r.plan.demeanor == "tired"
                           && r.HasWarnings;

                default:
                    return false;
            }
        }

        /// <summary>beats 里剩下的条目都必须是合法动作（空数组也算通过）</summary>
        static bool AllBeatsValid(Beat[] beats)
        {
            if (beats == null) return true;
            for (int i = 0; i < beats.Length; i++)
            {
                if (beats[i] == null) return false;
                if (!ActionCatalog.HasGesture(beats[i].gesture)) return false;
            }
            return true;
        }

        /// <summary>输入摘要（去掉换行、截断，Console 一行放得下）</summary>
        static string Preview(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "(空字符串)";
            string s = raw.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 110 ? s : s.Substring(0, 110) + "...";
        }

        /// <summary>
        /// Catalog 的 Ids() 现在返回 string[]，但这里统一按 IList 数个数 —— 万一以后改成
        /// List&lt;string&gt; 等实现也不影响这个测试菜单编译。
        /// </summary>
        static int CountOf(object ids)
        {
            var list = ids as System.Collections.IList;
            return list != null ? list.Count : -1;
        }
    }
}
#endif
