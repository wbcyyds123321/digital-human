namespace Yixuan.AI
{
    /// <summary>
    /// 给 DeepSeek 的"云台转向"技能说明（塞进 system prompt）。
    ///
    /// 为什么单独写成一段：
    ///   LLM 很容易只在回复里说"好，我去看看"，但 JSON 契约里的 camera 字段留空 ——
    ///   对用户来说就是"嘴上答应、镜头没动"，指令跟随很差。
    ///   这里把"转相机"和"转头"合并成同一件事。
    ///
    /// ★ 云台能做什么、默认多少度，不写死在这里 —— 由 <see cref="CameraMoveCatalog"/> 提供
    ///   （和动作/表情的清单注入同一个套路）。这里只写"怎么用"的规则和例子。
    /// </summary>
    public static class CameraTurnSkill
    {
        public const string SectionTitle = "【云台转向 —— 和\"转头\"是同一件事，必须执行】";

        /// <summary>拼进 system prompt 的技能段（纯字符串，方便离线测试/复用）。</summary>
        public static string BuildPromptSection()
        {
            return SectionTitle + "\n"
                 + "camera 字段会转动整个相机视角（她看哪）；gaze 只是头部/眼睛的微动。\n"
                 + "用户让你\"转过去 / 转头 / 看那边 / 看看某处 / 把镜头转到… / 回头看 / 回正\"时，\n"
                 + "你**必须在 camera 里填对应值**。只在回复里答应、camera 留空 = 没有执行。\n"
                 + "【可用云台动作】（camera 只能从这里选；同一行括号里是默认角度）\n"
                 + CameraMoveCatalog.Describe()
                 + "角度写法：camera 选 left/right/up/down 时，可以再用 camera_angle 指定角度（正数，方向由 camera 决定）：\n"
                 + "- \"往左转 30 度\" → \"camera\":\"left\", \"camera_angle\":30\n"
                 + "- \"往右转 90 度\" → \"camera\":\"right\", \"camera_angle\":90\n"
                 + "- \"抬头 45 度\" → \"camera\":\"up\", \"camera_angle\":45\n"
                 + "- \"低头 20 度\" → \"camera\":\"down\", \"camera_angle\":20\n"
                 + "不写 camera_angle（或填 0）就用默认角度。\n"
                 + "【坐标系】从这个角色的视角看：顺时针 = 往右（camera:\"right\"），逆时针 = 往左（camera:\"left\"）。\n"
                 + "【同义说法归一化】用户不会总说标准词，先理解意思再映射到下面的枚举，\n"
                 + "不要因为不是原词就不动；实在判断不了方向时 camera 填 null，并在 reply 里问清楚：\n"
                 + "- 往左偏 / 朝左 / 左手边 / 左边 / 逆时针 / 左转 → \"left\"\n"
                 + "- 往右偏 / 朝右 / 右手边 / 右边 / 顺时针 / 右转 → \"right\"\n"
                 + "- 扭头 / 转头 / 转视角 / 把镜头转到… → 就是转相机（用 camera 表达）\n"
                 + "- 转过身 / 向后转 / 背过去 → **身体转身，用 body_turn，不要填 camera**（camera 只改观察方向）\n"
                 + "- 面向柜台 / 转向柜台 / 看入口 / 看店门 / 面向店铺 / 看便利店 → \"look_store\"\n"
                 + "- 仰起头 / 抬一下头 / 看天花板 / 向上看 → \"up\"\n"
                 + "- 俯视 / 看地面 / 向下看 / 低下头 → \"down\"\n"
                 + "- 转回来 / 回默认 / 回到原位 / 看前面 → \"recenter\"\n"
                 + "例：\"你逆时针转45度\" → \"camera\":\"left\", \"camera_angle\":45；\n"
                 + "    \"顺时针偏30度\" → \"camera\":\"right\", \"camera_angle\":30。\n"
                 + "规则：\n"
                 + "1. 只是聊天/表达情绪时 camera 填 null，不要乱转；\n"
                 + "2. camera_hold 一般填 0 —— 转过去后会一直保持，**不会自动回正**，只有用户说\"回正\"才回默认；\n"
                 + "3. 回复可以简短说\"好，我看看\"，但 camera 字段必须同时设置；\n"
                 + "4. 用户没说方向/地点、只是问情绪或闲聊时，不要猜方向；\n"
                 + "5. camera_angle 只能是正数（1~180 或 1~60），不要写负数，方向交给 camera。\n"
                 + "示例（只示意 camera 相关字段）：\n"
                 + "  用户：\"你转过去看看便利店\" → \"camera\":\"look_store\", \"camera_angle\":0, \"camera_hold\":0\n"
                 + "  用户：\"转过身\" → \"camera\":null, \"body_turn\":180（身体转，不是云台）\n"
                 + "  用户：\"往左走两步\" → \"camera\":null, \"move_right_m\":-2.0（相对身体往左走）\n"
                 + "  用户：\"回头看\" → \"camera\":\"behind\"（观察方向）\n"
                 + "  用户：\"往右转30度看看\" → \"camera\":\"right\", \"camera_angle\":30, \"camera_hold\":0\n"
                 + "  用户：\"好了，转回来吧\" → \"camera\":\"recenter\", \"camera_angle\":0, \"camera_hold\":0\n"
                 + "  用户：\"今天天气不错\" → \"camera\":null, \"camera_angle\":0, \"camera_hold\":0\n";
        }
    }
}
