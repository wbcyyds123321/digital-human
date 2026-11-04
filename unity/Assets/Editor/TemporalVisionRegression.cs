using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Yixuan.AI;
using Yixuan.Vision;

namespace Yixuan.EditorTests
{
    public static class TemporalVisionRegression
    {
        static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly List<string> Passed = new List<string>();
        static GameObject _probeObject;
        static readonly Stack<IEnumerator> ProbeStack = new Stack<IEnumerator>();
        public static string ProbeResult = "尚未运行";

        static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + label);
            Passed.Add(label);
        }

        static void Set(object target, string field, object value)
        {
            Type type = target.GetType();
            while (type != null)
            {
                var info = type.GetField(field, Private);
                if (info != null) { info.SetValue(target, value); return; }
                type = type.BaseType;
            }
            throw new MissingFieldException(target.GetType().FullName, field);
        }

        static object Get(object target, string field)
        {
            return target.GetType().GetField(field, Private).GetValue(target);
        }

        static void SetAuto(object target, string property, object value)
        {
            target.GetType().GetField("<" + property + ">k__BackingField", Private).SetValue(target, value);
        }

        static void RunIterator(IEnumerator iterator)
        {
            while (iterator.MoveNext())
            {
                var nested = iterator.Current as IEnumerator;
                if (nested != null) RunIterator(nested);
            }
        }

        static bool MessagesContain(List<ChatMessage> messages, string needle)
        {
            if (messages == null || string.IsNullOrEmpty(needle)) return false;
            for (int i = 0; i < messages.Count; i++)
                if (messages[i].content != null && messages[i].content.Contains(needle)) return true;
            return false;
        }

        static object Invoke(object target, string method, params object[] arguments)
        {
            return target.GetType().GetMethod(method, Private).Invoke(target, arguments);
        }

        [MenuItem("UnityLITF/视觉/运行离线回归（不启摄像头、不调用API）")]
        public static void RunMenu() { Debug.Log(RunOffline()); }

        public static string RunOffline()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出Play");
            Passed.Clear();
            var fixture = new GameObject("TemporalVisionRegression") { hideFlags = HideFlags.HideAndDontSave };
            fixture.SetActive(false);
            var settingsField = typeof(CameraMoveCatalog).GetField("_settings", BindingFlags.Static | BindingFlags.NonPublic);
            var originalSettings = settingsField.GetValue(null) as CameraGimbalSettings;
            try
            {
                var observer = fixture.AddComponent<VisionObserver>();
                var session = fixture.AddComponent<ChatSession>();
                var client = fixture.AddComponent<QwenVisionClient>();
                Set(observer, "_session", session);
                observer.Client = client;
                Set(observer, "<GameViewEnabled>k__BackingField", true);
                Set(observer, "<WebcamEnabled>k__BackingField", true);
                Set(observer, "_sourceMask", 3);
                float now = Time.realtimeSinceStartup;
                Invoke(observer, "AppendObservation", "镜头被遮挡，画面中无人", 2, now - 1f, null);
                Invoke(observer, "AppendOrExtend", "镜头前有戴眼镜男子", 2, now - 6f, null);
                Check(observer.LastObservation.Contains("遮挡"), "R1：旧回包不覆盖新遮挡");
                Check((bool)Invoke(observer, "HasNewerUnavailableObservation", 2, now - 6f), "R1：在途当前问答也必须服从更新的遮挡状态");
                Check(!(bool)Invoke(observer, "HasNewerUnavailableObservation", 1, now - 6f), "R1：摄像头遮挡不取消游戏来源答案");
                Check(observer.TimelineCount == 2, "R1：不删除历史遮挡证据");
                Invoke(observer, "AppendObservation", "游戏里有货架", 1, now, null);
                string context = observer.BuildVisionContext();
                string current = context.Split(new[] { "[历史视觉记忆" }, StringSplitOptions.None)[0];
                Check(current.Contains("游戏里有货架") && current.Contains("镜头被遮挡"), "R1：两来源各保留自己的当前状态");
                Check(!current.Contains("镜头前有戴眼镜男子"), "R1：旧正面结果不能冒充当前");
                GazeIntent gaze;
                foreach (string command in new[] { "不要面向我", "不要朝向我", "不要转向柜台", "不要背过去", "不要仰起头", "不要垂下头", "不要顺时针转", "不要看上方", "我昨天回头看了一下" })
                    Check(!GazeVoiceIntent.TryParse(command, out gaze), "R3：" + command);
                Check(GazeVoiceIntent.TryParse("刚才没看清，现在往左看", out gaze) && gaze.Yaw < 0f, "R3：保留后半句当前命令");
                foreach (string question in new[] { "我现在低头了吗", "你看我现在有没有抬头", "看看我现在什么表情" })
                    Check(!GazeVoiceIntent.TryParse(question, out gaze), "R4：不把用户状态当云台命令：" + question);
                var visionQuestion = typeof(ChatSession).GetMethod("IsVisionQuestion", BindingFlags.Static | BindingFlags.NonPublic);
                foreach (string question in new[] { "我现在低头了吗", "你看我现在有没有抬头", "我在不在镜头面前", "往左看，那边有什么", "我刚刚做了什么" })
                    Check((bool)visionQuestion.Invoke(null, new object[] { question }), "问答路由：" + question);
                Check(!VisualQueryIntent.IsTemporalQuestion("我刚刚说了什么"), "路由：不把对话回忆当视频回顾");
                Check(VisualQueryIntent.NeedsCameraPlanning("柜台方向好像有人，你注意看一下什么情况"), "R2：LLM方向先进入预规划");
                var director = fixture.AddComponent<Director>();
                var gimbal = fixture.AddComponent<EyeGimbalController>();
                director.Gaze = gimbal;
                session.Gaze = gimbal; session.Director = director; session.Vision = observer;
                session.Client = new CameraPlanStub();
                var planning = (IEnumerator)Invoke(session, "PlanCameraBeforeVision", "柜台方向好像有人，你注意看一下什么情况");
                while (planning.MoveNext()) { }
                Check((bool)typeof(ChatSession).GetField("_gazeCommandPending", Private).GetValue(session), "R2：真实预规划迭代器先执行云台并标记等待新视觉");
                Check(director.SuppressCameraForTurn, "R2：预规划后禁止答案重复执行云台");
                Set(session, "_blockLateVisionCamera", true);
                var latePlan = new BehaviorPlan { camera = "right", camera_angle = 30f };
                Invoke(session, "SuppressLateVisionCamera", latePlan);
                Check(latePlan.camera == null, "R2：答案阶段不再偷偷转向");
                Set(session, "_usedCurrentVisionForTurn", true);
                Set(session, "<LastVisionAnswer>k__BackingField", "旧肯定答案");
                Set(session, "_visionRealtimeNote", "旧肯定答案");
                Set(observer, "_lastCurrentAnswerCapturedAt", now - 6f);
                Set(observer, "_lastCurrentAnswerMask", 2);
                var messages = (List<ChatMessage>)Invoke(session, "SafeBuildMessages", "我现在在镜头前吗");
                Check(messages.Exists(message => message.content.Contains("当前视觉不可确认")) && !messages.Exists(message => message.content == "旧肯定答案"), "R1：送到DeepSeek前再次剔除失效肯定答案");
                Set(session, "_usedCurrentVisionForTurn", false);
                Set(session, "_visionRealtimeNote", "历史过程可以回顾，不表示现在可见");
                messages = (List<ChatMessage>)Invoke(session, "SafeBuildMessages", "我刚才做了什么");
                Check(messages.Exists(message => message.content == "历史过程可以回顾，不表示现在可见"), "记忆：当前遮挡不妨碍回顾过去证据");
                Check(messages.Exists(message => message.content.Contains("camera必须为null")), "R2：最终消息明确禁用回答阶段云台动作");
                var settings = fixture.AddComponent<CameraGimbalSettings>();
                settings.MinAngle = 15f;
                CameraMoveCatalog.Bind(settings);
                float yaw, pitch;
                CameraMoveCatalog.TryResolve("left", 5f, out yaw, out pitch);
                Check(GazeVoiceIntent.TryParse("往左转5度", out gaze) && Mathf.Approximately(gaze.Yaw, yaw) && yaw == -15f, "R5：最小角度一致");
                settings.MinAngle = 1f; settings.AngleScale = 0.5f; settings.MaxYaw = 120f;
                CameraMoveCatalog.Bind(settings);
                var parsed = ResponseParser.Parse("{\"reply\":\"好\",\"camera\":\"right\",\"camera_angle\":200}");
                CameraMoveCatalog.TryResolve(parsed.plan.camera, parsed.plan.camera_angle, out yaw, out pitch);
                Check(GazeVoiceIntent.TryParse("往右转200度", out gaze) && Mathf.Approximately(gaze.Yaw, yaw) && yaw == 100f, "R5：模型与本地均先缩放后裁剪");
                settings.DefaultLeftYaw = 0f; settings.DefaultDownPitch = 0f; settings.AngleScale = 1f;
                CameraMoveCatalog.Bind(settings);
                Check(GazeVoiceIntent.TryParse("往左转30度", out gaze) && gaze.Yaw == -30f, "R5：零默认值不反转左向");
                Check(GazeVoiceIntent.TryParse("低头30度", out gaze) && gaze.Pitch == -30f, "R5：零默认值不反转低头");
                var buffer = new VisualFrameBuffer();
                for (int index = 0; index < 100; index++) buffer.Add(index * 0.5f, 2, new byte[10], 30f, 20, 180);
                Check(buffer.Count == 18 && buffer.Bytes == 180, "视频缓冲：数量及字节上限");
                var clip = buffer.Snapshot(2, 49.5f, 20f, 12, 1.5f);
                Check(clip.Usable && clip.Frames.Count == 12 && clip.Start < clip.End, "视频缓冲：有序采样，保留首尾");
                Check(buffer.Snapshot(1, 49.5f, 20f, 12, 1.5f).Frames.Count == 0, "视频缓冲：来源隔离");
                buffer.Add(70f, 2, new byte[10], 30f, 100, 1000);
                Check(!buffer.Snapshot(2, 70f, 30f, 48, 1.5f).Usable, "视频缓冲：断帧不伪装连续录像");
                var frames = new List<VisualFrame>();
                for (int index = 0; index < 4; index++) frames.Add(new VisualFrame(index * 0.5f, 2, new byte[] { 1, 2, 3 }));
                clip = new VisualClip(frames);
                var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
                try
                {
                    System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
                    string json = client.BuildVideoJson(clip, "动作？", 320);
                    Check(json.Contains("\"type\":\"video\"") && json.Contains("\"fps\":2") && json.Contains("image_url"), "视频协议：video序列加末帧，且fps不受系统语言影响");
                }
                finally { System.Threading.Thread.CurrentThread.CurrentCulture = culture; }
                var memory = new VisualMemoryStore();
                memory.Add(new VisualEvidence { events = new[] { new VisualEventEvidence { start = 0f, end = 1.5f, description = "拿起鼠标后移出画面", confidence = 0.95f } }, facts = new[] { new VisualFactEvidence { frame = 0, subject = "黑色鼠标", detail = "清晰可见罗技文字", confidence = 0.95f }, new VisualFactEvidence { subject = "鼠标", detail = "猜测是罗技", confidence = 0.2f } } }, clip);
                Check(memory.KeyframeCount == 1 && memory.SelectHistoricalFrames(2, 2f, "鼠标").Count == 1, "关键帧：保留事实对应的原始视觉证据");
                Check(memory.SelectHistoricalFrames(1, 2f, "鼠标").Count == 0, "关键帧：来源隔离");
                string historyJson = client.BuildVideoJson(new VisualClip(new List<VisualFrame> { new VisualFrame(2f, 2, new byte[] {1}), new VisualFrame(2.5f, 2, new byte[] {1}), new VisualFrame(3f, 2, new byte[] {1}), new VisualFrame(3.5f, 2, new byte[] {1}) }), "动作", 320, memory.SelectHistoricalFrames(2, 2f, "鼠标"));
                Check(historyJson.Contains("历史关键帧"), "关键帧：以历史图片输入模型而不是只靠文字");
                Check(memory.EventCount == 1 && memory.FactCount == 1, "记忆：分开保存事件与高置信历史事实");
                Check(memory.BuildContext(2f, 1).IndexOf("罗技", StringComparison.Ordinal) < 0, "记忆：游戏不能借用摄像头事实");
                Check(memory.BuildContext(2f, 2).Contains("不是当前所见"), "记忆：历史不冒充当前");
                Check(VisualMemoryStore.Parse("不是JSON") == null, "记忆：无效响应不入库");
                Check(VisualMemoryStore.Clean("a\nb", 20) == "a b", "记忆：过滤换行");
                memory.Prune(2000f);
                Check(memory.EventCount == 0 && memory.FactCount == 0, "记忆：时间上限清理");

                // ---- P1：本轮视觉证据要保留到回退重建消息（不能第一次构建后就清空） ----
                Set(session, "_visionRealtimeNote", "定向证据A");
                Set(session, "_visionPendingNote", "本轮状态B");
                var build1 = (List<ChatMessage>)Invoke(session, "SafeBuildMessages", "测试输入");
                var build2 = (List<ChatMessage>)Invoke(session, "SafeBuildMessages", "测试输入");
                Check(build1.Exists(m => m.content.Contains("定向证据A")) && build2.Exists(m => m.content.Contains("定向证据A")),
                      "P1：回退重建消息仍保留实时视觉证据");
                Check(build2.Exists(m => m.content.Contains("本轮状态B")), "P1：回退重建消息仍保留视觉状态提示");
                Set(session, "_visionRealtimeNote", null);
                Set(session, "_visionPendingNote", null);

                // ---- P2：时间路由（含交付文档里的杯子句） ----
                Check(VisualQueryIntent.IsTemporalQuestion("杯子刚才从哪边挪到哪边"), "P2：杯子挪动进入视频回顾");
                Check(VisualQueryIntent.IsTemporalQuestion("桌上刚才有个鼠标吗"), "P2：桌上鼠标属于过去过程");
                Check(VisualQueryIntent.IsTemporalQuestion("我刚才做了什么"), "P2：我刚才做了什么进入视频回顾");
                Check(!VisualQueryIntent.IsTemporalQuestion("我刚才说了什么"), "P2：纯对话回忆不进入视频");
                Check((bool)visionQuestion.Invoke(null, new object[] { "桌上刚才有个鼠标吗" }), "P2：桌上鼠标触发定向视觉问答");

                // ---- P5：混合句里的姿态问题不能吞掉当前云台指令 ----
                // 前面的 R5 用例为了验证"零默认值不反转"把 DefaultLeftYaw 改成了 0，
                // 这里先恢复默认角度，避免 P5 断言受测试顺序影响。
                settings.DefaultLeftYaw = 60f;
                settings.DefaultDownPitch = 28f;
                settings.AngleScale = 1f;
                settings.MaxYaw = 180f;
                settings.MaxPitch = 60f;
                settings.MinAngle = 1f;
                CameraMoveCatalog.Bind(settings);
                Check(GazeVoiceIntent.TryParse("我低头了吗，往左看", out gaze) && gaze.Yaw < 0f, "P5：混合句保留当前指令");
                Check(!GazeVoiceIntent.TryParse("我现在低头了吗", out gaze), "P5：纯姿态问题不转云台");
                Check(GazeVoiceIntent.TryParse("我现在有没有抬头，往右看", out gaze) && gaze.Yaw > 0f, "P5：混合句姿态问句不吞右侧指令");
                Check(GazeVoiceIntent.TryParse("我是不是低头了，往左看", out gaze) && gaze.Yaw < 0f, "P5：是不是混合句保留当前指令");
                Check(GazeVoiceIntent.TryParse("我低头了吗，抬头", out gaze) && gaze.Pitch > 0f, "P5：混合句保留独立抬头指令");

                // ---- P0：本轮用户输入不能重复进入请求 ----
                Set(session, "_currentUserInHistory", true);
                Invoke(session, "AddHistory", "user", "重复测试输入");
                var dupMessages = (List<ChatMessage>)Invoke(session, "SafeBuildMessages", "重复测试输入");
                int duplicateCount = 0;
                foreach (var m in dupMessages)
                    if (m.role == "user" && m.content.Contains("重复测试输入")) duplicateCount++;
                Check(duplicateCount == 1, "P0：本轮用户输入只出现一次（历史与本轮不重复）");
                Set(session, "_currentUserInHistory", false);

                // ---- P2 补充：当前时问题不能被当成过去回顾 ----
                Check(!VisualQueryIntent.IsTemporalQuestion("我现在在干嘛"), "P2：现在在干嘛不是过去回顾");
                Check(!VisualQueryIntent.IsTemporalQuestion("我现在在做什么"), "P2：现在在做什么不是过去回顾");
                Check(VisualQueryIntent.IsTemporalQuestion("我刚才在干嘛"), "P2：我刚才在干嘛仍进入视频回顾");
                Check((bool)visionQuestion.Invoke(null, new object[] { "我现在在做什么" }), "P2：现在在做什么走当前视觉问答");

                // ---- 短指代收紧：那怎么办不应触发视觉问答 ----
                Check(!(bool)visionQuestion.Invoke(null, new object[] { "那怎么办" }), "路由：那怎么办不再触发视觉问答");

                // ---- 移动协议：地点目标 / 坐标目标 / 非法 move ----
                Yixuan.World.WorldData.Refresh();
                string moveByAnchor = "{\"feeling\":\"\",\"demeanor\":\"attentive\",\"gaze\":\"user\",\"camera\":null,\"camera_angle\":0,\"camera_hold\":0,\"move\":\"walk_to\",\"move_target\":\"street_center\",\"move_x\":0,\"move_z\":0,\"emotion\":\"neutral\",\"intensity\":0.5,\"gesture\":null,\"posture\":null,\"wish\":\"\",\"reply\":\"好。\"}";
                var moveAnchorPlan = ResponseParser.Parse(moveByAnchor).plan;
                Check(moveAnchorPlan != null && moveAnchorPlan.move == "walk_to" && moveAnchorPlan.move_target == "street_center",
                      "移动：地点目标解析");
                string moveByCoord = "{\"feeling\":\"\",\"demeanor\":\"attentive\",\"gaze\":\"user\",\"camera\":null,\"camera_angle\":0,\"camera_hold\":0,\"move\":\"walk_to\",\"move_target\":null,\"move_x\":-2.4,\"move_z\":5.5,\"emotion\":\"neutral\",\"intensity\":0.5,\"gesture\":null,\"posture\":null,\"wish\":\"\",\"reply\":\"好。\"}";
                var moveCoordPlan = ResponseParser.Parse(moveByCoord).plan;
                Check(moveCoordPlan != null && moveCoordPlan.move == "walk_to" && Mathf.Abs(moveCoordPlan.move_x + 2.4f) < 0.01f,
                      "移动：坐标目标解析");
                string badMove = "{\"feeling\":\"\",\"demeanor\":\"attentive\",\"gaze\":\"user\",\"camera\":null,\"camera_angle\":0,\"camera_hold\":0,\"move\":\"fly\",\"emotion\":\"neutral\",\"intensity\":0.5,\"gesture\":null,\"posture\":null,\"wish\":\"\",\"reply\":\"好。\"}";
                var badMovePlan = ResponseParser.Parse(badMove).plan;
                Check(badMovePlan != null && string.IsNullOrEmpty(badMovePlan.move), "移动：非法 move 被忽略");

                // ---- A*：起点到店门口必须绕开栏杆，且路点都可走 ----
                var storeDoor = Yixuan.World.WorldData.GetAnchor("store_door");
                var detour = storeDoor != null
                    ? Yixuan.World.WorldData.FindPath(new Vector3(-2.4f, 0.03f, 3.8f), storeDoor.Position)
                    : null;
                Check(detour != null && detour.Count > 2, "A*：起点到店门口能绕开栏杆");
                bool allWalkable = detour != null;
                if (detour != null)
                    for (int i = 0; i < detour.Count; i++)
                        if (!Yixuan.World.WorldData.IsWalkable(detour[i])) { allWalkable = false; break; }
                Check(allWalkable, "A*：路点都在可走区");
                bool allSegments = detour != null;
                if (detour != null)
                    for (int i = 1; i < detour.Count; i++)
                        if (!Yixuan.World.WorldData.LineWalkable(detour[i - 1], detour[i])) { allSegments = false; break; }
                Check(allSegments, "A*：相邻路点连接段都可走（F2）");

                // F2 固定复现：要么整条路线每段可走，要么明确失败
                var f2start = new Vector3(5.937244f, 0.03f, 5.620171f);
                var f2goal = new Vector3(-6.13652325f, 0.03f, 2.227428f);
                var f2path = Yixuan.World.WorldData.FindPath(f2start, f2goal);
                bool f2valid = f2path == null || f2path.Count == 0;
                if (f2path != null)
                {
                    f2valid = true;
                    for (int i = 1; i < f2path.Count; i++)
                        if (!Yixuan.World.WorldData.LineWalkable(f2path[i - 1], f2path[i])) { f2valid = false; break; }
                }
                Check(f2valid, "A*：F2 固定坐标不返回不可走连接段（或明确失败）");

                // F4：越界目标必须拒绝，不能悄悄 Clamp 后接受
                var locomotion = UnityEngine.Object.FindObjectOfType<Yixuan.Motion.CharacterLocomotion>();
                if (locomotion != null)
                {
                    Vector3 before = locomotion.transform.position;
                    bool accepted = locomotion.MoveTo(new Vector3(999f, 0f, 5.5f));
                    Check(!accepted && locomotion.LastStatus == "out_of_bounds" &&
                          (locomotion.transform.position - before).sqrMagnitude < 0.0001f,
                          "移动：越界目标明确拒绝（F4）");
                }

                // F1：缺 move_z 的坐标目标必须拒绝；字段在 reply 后也要能被完整解析
                string missingZ = "{\"feeling\":\"\",\"demeanor\":\"attentive\",\"gaze\":\"user\",\"camera\":null,\"camera_angle\":0,\"camera_hold\":0,\"move\":\"walk_to\",\"move_x\":2.9,\"emotion\":\"neutral\",\"intensity\":0.5,\"gesture\":null,\"posture\":null,\"wish\":\"\",\"reply\":\"好。\"}";
                var missingZPlan = ResponseParser.Parse(missingZ).plan;
                Check(missingZPlan != null && string.IsNullOrEmpty(missingZPlan.move),
                      "移动：缺 move_z 的 walk_to 被拒绝（F1）");
                string afterReply = "{\"reply\":\"好。\",\"move\":\"walk_to\",\"move_target\":\"store_door\"}";
                var afterReplyPlan = ResponseParser.Parse(afterReply).plan;
                Check(afterReplyPlan != null && afterReplyPlan.move == "walk_to" &&
                      afterReplyPlan.move_target == "store_door",
                      "移动：move 在 reply 后仍被完整解析（F1）");

                // F5：身体转身/相对移动字段与云台路由分离
                var bodyTurnPlan = ResponseParser.Parse("{\"feeling\":\"\",\"demeanor\":\"attentive\",\"body_turn\":180,\"reply\":\"好。\"}").plan;
                Check(bodyTurnPlan != null && Mathf.Abs(bodyTurnPlan.body_turn - 180f) < 0.01f, "身体：body_turn 解析");
                var relativePlan = ResponseParser.Parse("{\"feeling\":\"\",\"demeanor\":\"attentive\",\"move_forward_m\":1.5,\"move_right_m\":-1,\"reply\":\"好。\"}").plan;
                Check(relativePlan != null && Mathf.Abs(relativePlan.move_forward_m - 1.5f) < 0.01f &&
                      Mathf.Abs(relativePlan.move_right_m + 1f) < 0.01f, "身体：相对移动解析");
                var conflictPlan = ResponseParser.Parse("{\"feeling\":\"\",\"demeanor\":\"attentive\",\"move\":\"walk_to\",\"move_target\":\"store_door\",\"move_forward_m\":1,\"reply\":\"好。\"}").plan;
                Check(conflictPlan != null && string.IsNullOrEmpty(conflictPlan.move) &&
                      Mathf.Abs(conflictPlan.move_forward_m) < 0.0001f, "身体：绝对/相对移动冲突被拒绝");
                Yixuan.Vision.GazeIntent gazeIntent;
                Check(!Yixuan.Vision.GazeVoiceIntent.TryParse("往左走", out gazeIntent), "路由：往左走不再被云台截获");
                Check(!Yixuan.Vision.GazeVoiceIntent.TryParse("往后转身", out gazeIntent), "路由：往后转身不再被云台截获");
                Check(Yixuan.Vision.GazeVoiceIntent.TryParse("往左看", out gazeIntent), "路由：往左看仍走云台");
                Check(!Yixuan.Vision.GazeVoiceIntent.TryParse("身体向左转90度", out gazeIntent), "路由：身体向左转不进入云台");
                Check(!Yixuan.Vision.GazeVoiceIntent.TryParse("向左转身", out gazeIntent), "路由：向左转身不进入云台");
                Check(Yixuan.Vision.GazeVoiceIntent.TryParse("往左走一步，然后往右看", out gazeIntent) && gazeIntent.Yaw > 0f,
                      "路由：混合句只保留往右看（R2）");
                Check(Yixuan.Vision.GazeVoiceIntent.TryParse("先往右看，再往左走一步", out gazeIntent) && gazeIntent.Yaw > 0f,
                      "路由：混合句不被往左走抢走观察（R2）");

                // R1：同网格提前返回不能输出穿障路线
                var r1start = new Vector3(3.07235241f, 0.03f, 1.84376466f);
                var r1goal = new Vector3(3.46125054f, 0.03f, 2.05582142f);
                bool r1Direct = Yixuan.World.WorldData.LineWalkable(r1start, r1goal);
                var r1path = Yixuan.World.WorldData.FindPath(r1start, r1goal);
                bool r1valid = r1path == null || r1path.Count == 0;
                if (r1path != null && r1path.Count > 0)
                {
                    r1valid = true;
                    for (int i = 1; i < r1path.Count; i++)
                        if (!Yixuan.World.WorldData.LineWalkable(r1path[i - 1], r1path[i])) { r1valid = false; break; }
                    if (!r1Direct && r1path.Count == 1 && r1path[0] == r1goal) r1valid = false;
                }
                Check(!r1Direct && r1valid, "A*：同网格直线不可走时不输出穿障路线（R1）");

                // ============================================================
                //  3509d9e 复核回归：F1 流式身体副作用 / F2 观察等待 /
                //  F3 只转镜头权限 / C1 身体配合任务所有权
                // ============================================================
                var reviewRoot = new GameObject("Review350_Observation") { hideFlags = HideFlags.HideAndDontSave };
                reviewRoot.transform.SetParent(fixture.transform, false);
                reviewRoot.SetActive(false);

                var reviewCharObj = new GameObject("Review350_Character") { hideFlags = HideFlags.HideAndDontSave };
                reviewCharObj.transform.SetParent(reviewRoot.transform, false);
                var reviewChar = reviewCharObj.AddComponent<Yixuan.Core.YixuanCharacter>();
                var reviewLoco = reviewCharObj.AddComponent<Yixuan.Motion.CharacterLocomotion>();
                reviewLoco.Character = reviewChar;
                reviewLoco.VerboseLog = false;

                var reviewGazeObj = new GameObject("Review350_Gaze") { hideFlags = HideFlags.HideAndDontSave };
                reviewGazeObj.transform.SetParent(reviewRoot.transform, false);
                var reviewGaze = reviewGazeObj.AddComponent<EyeGimbalController>();
                reviewGaze.Character = reviewChar.transform;
                reviewGaze.Locomotion = reviewLoco;
                reviewGaze.YawPivot = reviewGazeObj.transform;
                reviewGaze.BaseYaw = 0f;
                reviewGaze.HomeYaw = 0f;
                reviewGaze.LimitRelativeRange = true;
                reviewGaze.MaxRelativeYaw = 60f;
                reviewGaze.MaxRelativePitch = 30f;
                reviewGaze.BodyAssist = true;
                reviewGaze.AutoRecenter = false;

                var reviewDirectorObj = new GameObject("Review350_Director") { hideFlags = HideFlags.HideAndDontSave };
                reviewDirectorObj.transform.SetParent(reviewRoot.transform, false);
                var reviewDirector = reviewDirectorObj.AddComponent<Director>();
                reviewDirector.Character = reviewChar;
                reviewDirector.Locomotion = reviewLoco;
                reviewDirector.Gaze = reviewGaze;

                // F1：未完成流式前缀的 camera=behind 只能转云台，不能启动身体。
                var reviewStreamParser = new StreamingPlanParser();
                reviewStreamParser.Feed("{\"camera\":\"behind\",\"emotion\":\"neutral\",\"reply\":\"我看看");
                BehaviorPlan streamPrefix = reviewStreamParser.BuildPlan();
                Check(streamPrefix != null && streamPrefix.camera == "behind",
                      "F1：未完成前缀能构造 camera=behind 计划（复现输入）");
                int reviewTurnBeforeStream = reviewLoco.TurnCount;
                reviewDirector.ExecuteStreaming(streamPrefix);
                Check(reviewLoco.TurnCount == reviewTurnBeforeStream && !reviewLoco.IsTurning,
                      "F1：未完成/未校验的流式前缀不启动身体转向");
                Check(reviewGaze.LastObservationLimited,
                      "F1：流式前缀只允许无身体副作用的云台限幅");
                reviewDirector.ExecuteStreaming(streamPrefix);
                Check(reviewLoco.TurnCount == reviewTurnBeforeStream && !reviewLoco.IsTurning,
                      "F1：重复流式前缀也不能启动身体（回退不累积身体任务）");

                // F1：完整合法计划在最终提交点只兑现一次身体配合。
                string reviewFullJson = "{\"feeling\":\"\",\"demeanor\":\"attentive\",\"gaze\":\"user\",\"camera\":\"behind\",\"camera_angle\":0,\"camera_hold\":0,\"move\":null,\"move_target\":null,\"move_x\":0,\"move_z\":0,\"body_turn\":0,\"move_forward_m\":0,\"move_right_m\":0,\"emotion\":\"neutral\",\"intensity\":0.5,\"gesture\":null,\"posture\":null,\"wish\":\"\",\"reply\":\"好。\"}";
                var reviewFull = ResponseParser.Parse(reviewFullJson);
                Check(reviewFull.ok && reviewFull.plan != null && reviewFull.plan.camera == "behind",
                      "F1：完整合法 JSON 仍保留 camera=behind");
                int reviewTurnBeforeFinal = reviewLoco.TurnCount;
                reviewDirector.ExecuteFinal(reviewFull.plan);
                Check(reviewLoco.TurnCount == reviewTurnBeforeFinal + 1,
                      "F1：完整合法 camera=behind 只启动一次身体转向");
                Check(reviewLoco.IsTurningOwnedBy(EyeGimbalController.BodyAssistOwner),
                      "F1：最终观察的身体任务带 gaze.body-assist owner");
                Check(reviewGaze.LastBodyAssistOutcome == "pending",
                      "F1：最终观察记录身体配合 pending，供视觉等待结算");
                reviewLoco.Stop("回归重置");
                reviewGaze.Center(true);
                int reviewTurnBeforeDeniedFinal = reviewLoco.TurnCount;
                reviewDirector.ExecuteFinal(reviewFull.plan, false);
                Check(reviewLoco.TurnCount == reviewTurnBeforeDeniedFinal && !reviewLoco.IsTurning &&
                      reviewGaze.LastObservationLimited,
                      "F3：最终 LLM camera 也服从整句禁止身体权限");
                reviewLoco.Stop("回归重置");

                // F3：整句权限必须先于从句过滤提取；只转镜头绝不启动身体。
                GazeIntent reviewGazeIntent;
                Check(Yixuan.Vision.GazeVoiceIntent.TryParse("不要转身，只往后看", out reviewGazeIntent) &&
                      !reviewGazeIntent.AllowBodyAssist &&
                      Mathf.Abs(Mathf.Abs(reviewGazeIntent.Yaw) - 180f) < 1f,
                      "F3：不要转身，只往后看 → 允许云台、禁止身体");
                Check(Yixuan.Vision.GazeVoiceIntent.TryParse("只转镜头，身体不要动，往后看", out reviewGazeIntent) &&
                      !reviewGazeIntent.AllowBodyAssist &&
                      Mathf.Abs(Mathf.Abs(reviewGazeIntent.Yaw) - 180f) < 1f,
                      "F3：只转镜头 + 身体不要动 → 禁止身体配合");
                Check(Yixuan.Vision.GazeVoiceIntent.TryParse("只转镜头，向右看90度", out reviewGazeIntent) &&
                      !reviewGazeIntent.AllowBodyAssist && reviewGazeIntent.Yaw > 0f,
                      "F3：只转镜头，向右看90度 → 禁止身体配合");
                Check(Yixuan.Vision.GazeVoiceIntent.TryParse("身体不要动，只把镜头往后转", out reviewGazeIntent) &&
                      !reviewGazeIntent.AllowBodyAssist &&
                      Mathf.Abs(Mathf.Abs(reviewGazeIntent.Yaw) - 180f) < 1f,
                      "F3：镜头往后转的本地路由 + 禁止身体");
                Check(Yixuan.Vision.GazeVoiceIntent.TryParse("回头看", out reviewGazeIntent) &&
                      reviewGazeIntent.AllowBodyAssist,
                      "F3：普通回头看仍允许身体配合（对照组）");

                reviewLoco.Stop("回归重置");
                reviewGaze.Center(true);
                int reviewTurnBeforeCameraOnly = reviewLoco.TurnCount;
                Check(reviewGaze.HandleUtterance("只转镜头，向右看90度"),
                      "F3：只转镜头命令被执行（不是解析失败）");
                Check(reviewLoco.TurnCount == reviewTurnBeforeCameraOnly && !reviewLoco.IsTurning,
                      "F3：只转镜头时身体确实没有动");
                Check(reviewGaze.LastObservationLimited && !string.IsNullOrEmpty(reviewGaze.LastObservationNote),
                      "F3：只转镜头超范围时明确回报仅限幅");

                reviewLoco.Stop("回归重置");
                reviewGaze.Center(true);
                int reviewTurnBeforeNormal = reviewLoco.TurnCount;
                Check(reviewGaze.HandleUtterance("回头看") &&
                      reviewLoco.TurnCount == reviewTurnBeforeNormal + 1 &&
                      reviewLoco.IsTurningOwnedBy(EyeGimbalController.BodyAssistOwner),
                      "F3：普通回头看仍启动身体配合");
                reviewLoco.Stop("回归重置");
                reviewGaze.Center(true);

                // C1：回正只取消本观察拥有的身体配合，不误杀独立导航转身。
                reviewGaze.TurnRelative(180f, 0f);
                Check(reviewLoco.IsTurningOwnedBy(EyeGimbalController.BodyAssistOwner),
                      "C1：观察任务能拿到自己的身体转身");
                reviewGaze.Center();
                Check(!reviewLoco.IsTurning,
                      "C1：回正取消自己的观察身体转身");
                Check(reviewGaze.LastBodyAssistOutcome == "cancelled",
                      "C1：回正取消后保留 cancelled，供视觉等待判失败");
                reviewLoco.Stop("回归重置");
                reviewLoco.TurnRelative(90f, "navigation");
                reviewGaze.Center();
                Check(reviewLoco.IsTurningOwnedBy("navigation"),
                      "C1：回正不误杀独立导航转身");
                reviewLoco.Stop("回归重置");

                // F2：视觉等待必须等本次身体配合完成；取消/限幅不注入“已转向”。
                session.Gaze = reviewGaze;
                session.Vision = observer;
                Set(observer, "<GameViewEnabled>k__BackingField", true);
                Set(observer, "<WebcamEnabled>k__BackingField", true);
                Set(observer, "_sourceMask", 3);
                SetAuto(reviewGaze, "CurrentYaw", 0f);
                SetAuto(reviewGaze, "TargetYaw", 0f);
                SetAuto(reviewGaze, "CurrentPitch", 0f);
                SetAuto(reviewGaze, "TargetPitch", 0f);
                Set(session, "_gazeCommandPending", true);
                Set(session, "_visionPendingNote", null);
                Check(reviewGaze.HandleUtterance("回头看"), "F2：先发起会配合身体的观察");
                var reviewWait = (IEnumerator)Invoke(session, "WaitForFreshVisionIfNeeded", "后面有什么东西");
                Check(reviewWait.MoveNext(),
                      "F2：身体转向未完成时视觉等待必须 yield 而不是直接结束");

                reviewLoco.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                reviewLoco.StopTurning(EyeGimbalController.BodyAssistOwner);
                reviewGaze.RefreshBodyAssistOutcome();
                SetAuto(reviewGaze, "CurrentYaw", 0f);
                SetAuto(reviewGaze, "TargetYaw", 0f);
                Check(!reviewWait.MoveNext(), "F2：身体配合完成后视觉等待结束");
                string reviewSuccessNote = (string)Get(session, "_visionPendingNote");
                Check(!string.IsNullOrEmpty(reviewSuccessNote) && reviewSuccessNote.Contains("已转向"),
                      "F2：到位后才注入已转向的新方向提示");

                reviewLoco.Stop("回归重置");
                reviewGaze.Center(true);
                SetAuto(reviewGaze, "CurrentYaw", 0f);
                SetAuto(reviewGaze, "TargetYaw", 0f);
                Set(session, "_gazeCommandPending", true);
                Set(session, "_visionPendingNote", null);
                Check(reviewGaze.HandleUtterance("回头看"), "F2：再次发起身体配合观察");
                var reviewCancelWait = (IEnumerator)Invoke(session, "WaitForFreshVisionIfNeeded", "后面有什么东西");
                Check(reviewCancelWait.MoveNext(), "F2：取消前等待仍在 yield");
                reviewLoco.Stop("回归取消");
                reviewGaze.RefreshBodyAssistOutcome();
                Check(!reviewCancelWait.MoveNext(), "F2：身体配合被取消后等待结束");
                string reviewCancelNote = (string)Get(session, "_visionPendingNote");
                Check(!string.IsNullOrEmpty(reviewCancelNote) &&
                      (reviewCancelNote.Contains("取消") || reviewCancelNote.Contains("未确认")),
                      "F2：取消身体配合时不注入已转向成功提示");

                reviewLoco.Stop("回归重置");
                reviewGaze.Center(true);
                SetAuto(reviewGaze, "CurrentYaw", 0f);
                SetAuto(reviewGaze, "TargetYaw", 0f);
                Set(session, "_gazeCommandPending", true);
                Set(session, "_visionPendingNote", null);
                Check(reviewGaze.HandleUtterance("只转镜头，往后看"), "F2：发起仅限幅的观察");
                var reviewLimitedWait = (IEnumerator)Invoke(session, "WaitForFreshVisionIfNeeded", "后面有什么东西");
                Check(reviewLimitedWait.MoveNext(), "F2：限幅云台尚未到位时等待");
                SetAuto(reviewGaze, "CurrentYaw", 60f);
                SetAuto(reviewGaze, "TargetYaw", 60f);
                Check(!reviewLimitedWait.MoveNext(), "F2：限幅云台到位后等待结束");
                string reviewLimitedNote = (string)Get(session, "_visionPendingNote");
                Check(!string.IsNullOrEmpty(reviewLimitedNote) &&
                      (reviewLimitedNote.Contains("限幅") || reviewLimitedNote.Contains("没有覆盖")),
                      "F2：仅限幅时明确说明视野没有覆盖完整方向");

                reviewLoco.Stop("回归重置");
                reviewGaze.Center(true);
                SetAuto(reviewGaze, "CurrentYaw", 0f);
                SetAuto(reviewGaze, "TargetYaw", 0f);
                Set(session, "_gazeCommandPending", true);
                Set(session, "_visionPendingNote", null);
                session.VisionTurnWaitSeconds = 0f;   // 强制超时分支
                Check(reviewGaze.HandleUtterance("回头看"), "F2：超时前先发起身体配合观察");
                var reviewTimeoutWait = (IEnumerator)Invoke(session, "WaitForFreshVisionIfNeeded", "后面有什么东西");
                Check(!reviewTimeoutWait.MoveNext(), "F2：超时后等待结束而不阻塞");
                string reviewTimeoutNote = (string)Get(session, "_visionPendingNote");
                Check(!string.IsNullOrEmpty(reviewTimeoutNote) && reviewTimeoutNote.Contains("未确认"),
                      "F2：超时明确回报新方向未确认，不注入成功提示");
                session.VisionTurnWaitSeconds = 4.5f;
                reviewLoco.Stop("回归重置");
                reviewGaze.Center(true);

                // ============================================================
                //  P2：观察执行失败必须贯穿 AskNow 回包，最终消息仍保留方向限制
                // ============================================================
                var p2SessionObj = new GameObject("ReviewP2_Session") { hideFlags = HideFlags.HideAndDontSave };
                p2SessionObj.transform.SetParent(fixture.transform, false);
                var p2Session = p2SessionObj.AddComponent<ChatSession>();
                var p2VisionObj = new GameObject("ReviewP2_Vision") { hideFlags = HideFlags.HideAndDontSave };
                p2VisionObj.transform.SetParent(fixture.transform, false);
                var p2Vision = p2VisionObj.AddComponent<P2VisionStub>();
                p2Vision.Answer = "测试回包：货架上有饮料";
                p2Vision.AskCount = 0;
                Set(p2Vision, "<GameViewEnabled>k__BackingField", true);
                Set(p2Vision, "<WebcamEnabled>k__BackingField", true);
                Set(p2Vision, "_sourceMask", 3);
                p2Session.Gaze = reviewGaze;
                p2Session.Vision = p2Vision;
                const string p2Input = "后面有什么东西";

                System.Action resetP2Turn = delegate
                {
                    reviewLoco.Stop("P2 重置");
                    reviewGaze.Center(true);
                    reviewLoco.transform.rotation = Quaternion.Euler(0f, 0f, 0f);
                    p2Vision.ClearVisualMemory();
                    p2Vision.Answer = "测试回包：货架上有饮料";
                    p2Vision.AskCount = 0;
                    p2Session.VisionTurnWaitSeconds = 4.5f;
                    Set(p2Session, "_gazeCommandPending", true);
                    Set(p2Session, "_gazeExecutionStatus", ChatSession.GazeExecutionStatus.None);
                    Set(p2Session, "_visionPendingNote", null);
                    Set(p2Session, "_visionRealtimeNote", null);
                    Set(p2Session, "_usedCurrentVisionForTurn", false);
                    Set(p2Session, "_visionPreparedForTurn", false);
                    Set(p2Session, "_gazeVisionMask", 0);
                    Set(p2Session, "<LastVisionAnswer>k__BackingField", "");
                    Set(p2Session, "<LastVisionAnswerSeconds>k__BackingField", -1f);
                };

                // 1) 正常到位 → 成功回包 → 最终消息可以回答该方向。
                resetP2Turn();
                reviewGaze.TurnRelative(180f, 0f);
                reviewLoco.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                reviewLoco.StopTurning(EyeGimbalController.BodyAssistOwner);
                reviewGaze.RefreshBodyAssistOutcome();
                SetAuto(reviewGaze, "CurrentYaw", 0f);
                SetAuto(reviewGaze, "TargetYaw", 0f);
                RunIterator((IEnumerator)Invoke(p2Session, "PrepareVisionForTurn", p2Input));
                Check(p2Vision.AskCount == 1 && p2Session.TurnGazeExecutionStatus == ChatSession.GazeExecutionStatus.Reached,
                      "P2：正常到位后执行一次定向识别");
                Set(p2Vision, "_lastCurrentAnswerCapturedAt", Time.realtimeSinceStartup);
                Set(p2Vision, "_lastCurrentAnswerMask", 1);
                var p2Messages = (List<ChatMessage>)Invoke(p2Session, "SafeBuildMessages", p2Input);
                Check(MessagesContain(p2Messages, "测试回包：货架上有饮料") &&
                      !MessagesContain(p2Messages, "新方向未确认"),
                      "P2：正常成功回包可以进入最终消息");

                // 2) 超时 → 非空识别回包（此处不应被调用）→ 最终消息仍保留未确认。
                resetP2Turn();
                p2Session.VisionTurnWaitSeconds = 0f;
                Check(reviewGaze.HandleUtterance("回头看"), "P2：超时前发起身体配合观察");
                RunIterator((IEnumerator)Invoke(p2Session, "PrepareVisionForTurn", p2Input));
                Check(p2Vision.AskCount == 0 &&
                      p2Session.TurnGazeExecutionStatus == ChatSession.GazeExecutionStatus.TimedOut,
                      "P2：超时不继续把普通 AskNow 当作目标方向证据");
                p2Messages = (List<ChatMessage>)Invoke(p2Session, "SafeBuildMessages", p2Input);
                Check(MessagesContain(p2Messages, "未确认") && !MessagesContain(p2Messages, "测试回包：货架上有饮料"),
                      "P2：超时后最终消息仍保留方向未确认");
                var p2Rebuilt = (List<ChatMessage>)Invoke(p2Session, "SafeBuildMessages", p2Input);
                Check(MessagesContain(p2Rebuilt, "未确认"), "P2：流式回退重建消息仍保留方向未确认");

                // 3) 取消 → 非空识别回包（不应被调用）→ 不注入已转向。
                resetP2Turn();
                Check(reviewGaze.HandleUtterance("回头看"), "P2：取消前发起身体配合观察");
                reviewGaze.Center();
                RunIterator((IEnumerator)Invoke(p2Session, "PrepareVisionForTurn", p2Input));
                Check(p2Vision.AskCount == 0 &&
                      p2Session.TurnGazeExecutionStatus == ChatSession.GazeExecutionStatus.Cancelled,
                      "P2：取消后不继续执行目标方向识别");
                p2Messages = (List<ChatMessage>)Invoke(p2Session, "SafeBuildMessages", p2Input);
                Check(MessagesContain(p2Messages, "取消") || MessagesContain(p2Messages, "未确认"),
                      "P2：取消后最终消息明确未确认");
                Check(!MessagesContain(p2Messages, "测试回包：货架上有饮料"),
                      "P2：取消后不把识别回包当目标方向证据");

                // 4) 仅限幅 → 非空回包 → 始终保留“没有覆盖完整方向”，且结果不被清掉。
                resetP2Turn();
                Check(reviewGaze.HandleUtterance("只转镜头，往后看"), "P2：发起仅限幅观察");
                SetAuto(reviewGaze, "CurrentYaw", 60f);
                SetAuto(reviewGaze, "TargetYaw", 60f);
                RunIterator((IEnumerator)Invoke(p2Session, "PrepareVisionForTurn", p2Input));
                Check(p2Vision.AskCount == 1 &&
                      p2Session.TurnGazeExecutionStatus == ChatSession.GazeExecutionStatus.Limited,
                      "P2：限幅仍允许识别，但保留执行限制状态");
                Set(p2Vision, "_lastCurrentAnswerCapturedAt", Time.realtimeSinceStartup);
                Set(p2Vision, "_lastCurrentAnswerMask", 1);
                p2Messages = (List<ChatMessage>)Invoke(p2Session, "SafeBuildMessages", p2Input);
                Check(MessagesContain(p2Messages, "方向限制") &&
                      MessagesContain(p2Messages, "没有覆盖") &&
                      MessagesContain(p2Messages, "测试回包：货架上有饮料"),
                      "P2：成功回包不能清掉限幅说明，最终消息同时包含限制和识别");
                var p2LimitedRebuild = (List<ChatMessage>)Invoke(p2Session, "SafeBuildMessages", p2Input);
                Check(MessagesContain(p2LimitedRebuild, "没有覆盖"),
                      "P2：流式回退重建仍保留限幅说明");
                int p2AskBeforeReplay = p2Vision.AskCount;
                RunIterator((IEnumerator)Invoke(p2Session, "PrepareVisionForTurn", p2Input));
                Check(p2Vision.AskCount == p2AskBeforeReplay,
                      "P2：回退重建不重复执行观察/识别任务");

                // 5) 无视觉回包 → 原执行失败/限幅说明仍在。
                resetP2Turn();
                Check(reviewGaze.HandleUtterance("只转镜头，往后看"), "P2：无回包前发起仅限幅观察");
                SetAuto(reviewGaze, "CurrentYaw", 60f);
                SetAuto(reviewGaze, "TargetYaw", 60f);
                p2Vision.Answer = null;
                RunIterator((IEnumerator)Invoke(p2Session, "PrepareVisionForTurn", p2Input));
                p2Messages = (List<ChatMessage>)Invoke(p2Session, "SafeBuildMessages", p2Input);
                Check(p2Vision.AskCount == 1 && MessagesContain(p2Messages, "没有覆盖"),
                      "P2：没有视觉回包时仍保留限幅说明");

                // 6) 下一轮开始清除上一轮失败状态。
                Set(p2Session, "_state", ChatSession.State.Thinking);
                p2Session.CancelCurrentTurn();
                Check(p2Session.TurnGazeExecutionStatus == ChatSession.GazeExecutionStatus.None &&
                      string.IsNullOrEmpty((string)Get(p2Session, "_visionPendingNote")) &&
                      string.IsNullOrEmpty((string)Get(p2Session, "_visionRealtimeNote")),
                      "P2：下一轮/取消重置观察执行状态");

                // 7) 上一轮失败后，新的一轮重新正常到位，不沿用失败状态。
                resetP2Turn();
                reviewGaze.TurnRelative(180f, 0f);
                reviewLoco.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                reviewLoco.StopTurning(EyeGimbalController.BodyAssistOwner);
                reviewGaze.RefreshBodyAssistOutcome();
                SetAuto(reviewGaze, "CurrentYaw", 0f);
                SetAuto(reviewGaze, "TargetYaw", 0f);
                RunIterator((IEnumerator)Invoke(p2Session, "PrepareVisionForTurn", p2Input));
                Set(p2Vision, "_lastCurrentAnswerCapturedAt", Time.realtimeSinceStartup);
                Set(p2Vision, "_lastCurrentAnswerMask", 1);
                var p2NextMessages = (List<ChatMessage>)Invoke(p2Session, "SafeBuildMessages", p2Input);
                string p2NextPending = (string)Get(p2Session, "_visionPendingNote");
                string p2NextRealtime = (string)Get(p2Session, "_visionRealtimeNote");
                Check(p2Session.TurnGazeExecutionStatus == ChatSession.GazeExecutionStatus.Reached &&
                      p2Vision.AskCount == 1 &&
                      MessagesContain(p2NextMessages, "测试回包：货架上有饮料") &&
                      string.IsNullOrEmpty(p2NextPending) &&
                      !string.IsNullOrEmpty(p2NextRealtime) &&
                      !p2NextRealtime.Contains("定向观察执行失败") &&
                      !p2NextRealtime.Contains("方向限制"),
                      "P2：夹具重置后新观察成功（生产轮次入口见下方 F01/F03 用例）");

                // ============================================================
                //  687e41b 复核（2026-10-23）：F01 旧 cancelled 阻断新回正 /
                //  F02 问句路由绕过方向屏障 / F03 真实轮次入口 / R01·R02
                // ============================================================
                var reviewBrainObj = new GameObject("Review687_Brain") { hideFlags = HideFlags.HideAndDontSave };
                reviewBrainObj.transform.SetParent(fixture.transform, false);
                reviewDirector.Brain = reviewBrainObj.AddComponent<Yixuan.Brain.YixuanBrain>();
                p2Session.Director = reviewDirector;
                p2Session.Locomotion = reviewLoco;

                // F01 + F03：旧观察 pending → 用户取消 → 真实新轮次入口（Submit 的同步主体
                // BeginTurn）→ 新命令“回正，看看前面有什么”。新观察不得继承旧 cancelled。
                resetP2Turn();
                reviewLoco.Stop("F01 重置");
                reviewGaze.Center(true);
                SetAuto(reviewGaze, "CurrentYaw", 0f);
                SetAuto(reviewGaze, "TargetYaw", 0f);
                p2Vision.Answer = "测试回包：前面没人";
                p2Vision.AskCount = 0;
                Check(reviewGaze.HandleUtterance("回头看"), "F01：旧轮次观察发起身体配合");
                Set(p2Session, "_state", ChatSession.State.Thinking);
                p2Session.CancelCurrentTurn();   // 真实取消入口：旧观察作废
                // 只把待测字段设脏，不手工清；由新的真实轮次入口自己重置。
                Set(p2Session, "_visionPreparedForTurn", true);
                Set(p2Session, "_usedCurrentVisionForTurn", true);
                Set(p2Session, "_gazeVisionMask", 1);
                Set(p2Session, "_gazeExecutionStatus", ChatSession.GazeExecutionStatus.Cancelled);
                Set(p2Session, "_visionPendingNote", "旧轮次失败提示");
                Set(p2Session, "_visionRealtimeNote", "旧轮次识别结果");
                Set(p2Session, "<LastVisionAnswer>k__BackingField", "旧答案");
                Invoke(p2Session, "BeginTurn", "回正，看看前面有什么");
                Check(p2Session.TurnGazeExecutionStatus == ChatSession.GazeExecutionStatus.None &&
                      string.IsNullOrEmpty((string)Get(p2Session, "_visionPendingNote")) &&
                      string.IsNullOrEmpty((string)Get(p2Session, "_visionRealtimeNote")) &&
                      !(bool)Get(p2Session, "_visionPreparedForTurn") &&
                      !(bool)Get(p2Session, "_usedCurrentVisionForTurn") &&
                      (int)Get(p2Session, "_gazeVisionMask") == 0 &&
                      string.IsNullOrEmpty((string)Get(p2Session, "<LastVisionAnswer>k__BackingField")),
                      "F03：真实新轮次入口重置视觉状态（不靠夹具手清）");
                Check((bool)Get(p2Session, "_gazeCommandPending"),
                      "F01：新轮次回正命令已登记等待新观察");
                RunIterator((IEnumerator)Invoke(p2Session, "PrepareVisionForTurn", "回正，看看前面有什么"));
                Set(p2Vision, "_lastCurrentAnswerCapturedAt", Time.realtimeSinceStartup);
                Set(p2Vision, "_lastCurrentAnswerMask", 1);
                Check(p2Session.TurnGazeExecutionStatus == ChatSession.GazeExecutionStatus.Reached &&
                      p2Vision.AskCount == 1,
                      "F01：新回正不继承旧 cancelled，执行一次定向识别");
                var f01Messages = (List<ChatMessage>)Invoke(p2Session, "SafeBuildMessages", "回正，看看前面有什么");
                Check(MessagesContain(f01Messages, "测试回包：前面没人") &&
                      !MessagesContain(f01Messages, "定向观察执行失败"),
                      "F01：最终消息按新方向识别结果回答");
                p2Session.CancelCurrentTurn();

                // F02：旧关键词表漏掉的问法也必须进入方向等待，限幅约束不能被识别回包清掉。
                resetP2Turn();
                reviewLoco.Stop("F02 重置");
                reviewGaze.Center(true);
                SetAuto(reviewGaze, "CurrentYaw", 0f);
                SetAuto(reviewGaze, "TargetYaw", 0f);
                p2Vision.Answer = "测试回包：右边有人";
                p2Vision.AskCount = 0;
                const string f02Input = "不要转身，向右看90度，那里有人吗？";
                Check(reviewGaze.HandleUtterance(f02Input),
                      "F02：只转镜头 90 度命令被执行");
                Check(reviewGaze.LastObservationLimited,
                      "F02：命令实际只到云台限幅 60 度");
                Set(p2Session, "_gazeCommandPending", true);
                SetAuto(reviewGaze, "CurrentYaw", 60f);
                SetAuto(reviewGaze, "TargetYaw", 60f);
                RunIterator((IEnumerator)Invoke(p2Session, "PrepareVisionForTurn", f02Input));
                Check(p2Session.TurnGazeExecutionStatus == ChatSession.GazeExecutionStatus.Limited &&
                      p2Vision.AskCount == 1,
                      "F02：方向依赖的问答进入等待并保留限幅状态");
                Set(p2Vision, "_lastCurrentAnswerCapturedAt", Time.realtimeSinceStartup);
                Set(p2Vision, "_lastCurrentAnswerMask", 1);
                var f02Messages = (List<ChatMessage>)Invoke(p2Session, "SafeBuildMessages", f02Input);
                Check(MessagesContain(f02Messages, "方向限制") &&
                      MessagesContain(f02Messages, "没有覆盖") &&
                      MessagesContain(f02Messages, "测试回包：右边有人"),
                      "F02：限幅状态不被识别回包清掉，最终消息同时保留方向限制和识别结果");
                var directionDependent = typeof(ChatSession).GetMethod("DirectionDependentVisionQuestion", BindingFlags.Static | BindingFlags.NonPublic);
                Check(directionDependent != null &&
                      (bool)directionDependent.Invoke(null, new object[] { "不要转身，向右看90度，那里有人吗？" }) &&
                      (bool)directionDependent.Invoke(null, new object[] { "往后看60度，看得清吗？" }),
                      "F02：命令 + “有人吗/看得清吗”问法都判定为依赖方向");
                Check(!(bool)directionDependent.Invoke(null, new object[] { "回头看，我现在的表情是什么？" }) &&
                      !(bool)directionDependent.Invoke(null, new object[] { "我刚才做了什么" }),
                      "F02/R01：纯用户来源、历史回顾不依赖新方向");

                // R01：纯摄像头/用户问题（mask=2）不依赖游戏方向，不能被方向失败一并阻断。
                resetP2Turn();
                reviewLoco.Stop("R01 重置");
                reviewGaze.Center(true);
                SetAuto(reviewGaze, "CurrentYaw", 0f);
                SetAuto(reviewGaze, "TargetYaw", 0f);
                p2Vision.Answer = "测试回包：表情有点紧张";
                p2Vision.AskCount = 0;
                p2Session.VisionTurnWaitSeconds = 0f;
                Check(reviewGaze.HandleUtterance("回头看"), "R01：先发起会启动身体配合的方向命令");
                Set(p2Session, "_gazeCommandPending", true);
                RunIterator((IEnumerator)Invoke(p2Session, "PrepareVisionForTurn", "回头看，我现在的表情是什么？"));
                Check(p2Session.TurnGazeExecutionStatus == ChatSession.GazeExecutionStatus.None &&
                      p2Vision.AskCount == 1,
                      "R01：纯摄像头问题不被游戏方向失败降级");

                // R02：身体已取消但云台仍在平滑时，原因应报取消而不是超时。
                resetP2Turn();
                reviewLoco.Stop("R02 重置");
                reviewGaze.Center(true);
                SetAuto(reviewGaze, "CurrentYaw", 0f);
                SetAuto(reviewGaze, "TargetYaw", 0f);
                p2Session.VisionTurnWaitSeconds = 0.05f;
                Check(reviewGaze.HandleUtterance("回头看"), "R02：发起身体配合观察");
                Set(p2Session, "_gazeCommandPending", true);
                var r02Wait = (IEnumerator)Invoke(p2Session, "WaitForFreshVisionIfNeeded", "后面有什么东西");
                Check(r02Wait.MoveNext(), "R02：等待身体/云台稳定");
                reviewLoco.Stop("R02 取消身体");
                reviewGaze.RefreshBodyAssistOutcome();
                SetAuto(reviewGaze, "CurrentYaw", 30f);   // 云台仍未平滑到位
                SetAuto(reviewGaze, "TargetYaw", 0f);
                float r02Until = Time.realtimeSinceStartup + 1f;
                while (r02Wait.MoveNext() && Time.realtimeSinceStartup < r02Until) { }
                Check(p2Session.TurnGazeExecutionStatus == ChatSession.GazeExecutionStatus.Cancelled,
                      "R02：取消优先于超时，失败原因不被误报");
                p2Session.VisionTurnWaitSeconds = 4.5f;

                // F6：红绿灯语义地标必须映到可站、可达的靠近点
                var trafficAnchor = Yixuan.World.WorldData.GetAnchor("traffic_light_main");
                Check(trafficAnchor != null && Yixuan.World.WorldData.IsWalkable(trafficAnchor.Position) &&
                      Yixuan.World.WorldData.FindPath(new Vector3(-2.4f, 0.03f, 3.8f), trafficAnchor.Position) != null,
                      "地标：红绿灯映射到可站、可达的靠近点（F6）");

                // ---- 主动轮次：允许空 reply 的解析入口 ----
                var emptyReplyParsed = ResponseParser.Parse("{\"emotion\":\"surprised\",\"reply\":\"\"}", true);
                Check(emptyReplyParsed.ok && emptyReplyParsed.plan != null && !emptyReplyParsed.plan.HasReply,
                      "主动路径：allowEmptyReply 允许只做动作不说话");
                var normalEmptyReply = ResponseParser.Parse("{\"emotion\":\"surprised\",\"reply\":\"\"}");
                Check(!normalEmptyReply.ok, "普通路径：空 reply 仍视为解析失败（走兜底）");


                // ---- P3：字符串数组结构必须给出明确诊断，不能静默丢 ----
                var badEvidence = VisualMemoryStore.Parse("{\"current\":\"红块在右侧\",\"events\":[\"0.00秒：红块在左侧\"],\"facts\":[\"红块是实心的\"]}");
                var badMemory = new VisualMemoryStore();
                badMemory.Add(badEvidence, clip);
                Check(badMemory.EventCount == 0 && badMemory.FactCount == 0 && !string.IsNullOrEmpty(badMemory.LastIssue),
                      "P3：字符串数组结构被明确诊断而不是静默丢弃");
                var emptyMemory = new VisualMemoryStore();
                emptyMemory.Add(new VisualEvidence { current = "正常", events = new VisualEventEvidence[0], facts = new VisualFactEvidence[0] }, clip);
                Check(string.IsNullOrEmpty(emptyMemory.LastIssue), "P3：合法的空数组不误报");

                // ---- P6：两路持续不可用只保留每来源一条本地状态（不能 25s 刷满 100 条） ----
                Invoke(observer, "ClearChatContext");
                Set(observer, "_sourceMask", 3);
                Set(observer, "<GameViewEnabled>k__BackingField", true);
                Set(observer, "<WebcamEnabled>k__BackingField", true);
                for (int cycle = 0; cycle < 20; cycle++)
                {
                    Set(observer, "_nextVideoSample", 0f);
                    Invoke(observer, "SampleVideoFrames");
                }
                Check(observer.TimelineCount == 2, "P6：持续不可用只保留每来源一条本地状态");

                // ---- P7：忙碌早退要清理主动接话标记 ----
                Set(session, "_state", ChatSession.State.Thinking);
                Set(session, "_nextTurnIsProactive", true);
                session.Submit("忙碌时的测试输入");
                Check(!(bool)Get(session, "_nextTurnIsProactive"), "P7：忙碌早退清理主动接话标记");
                Set(session, "_state", ChatSession.State.Idle);

                observer.ClearVisualMemory();
                Check(observer.BufferedVideoFrames == 0 && observer.TimelineCount == 0 && observer.VisualFactCount == 0, "生命周期：清空聊天同时清空视觉记忆");
                return "PASS " + Passed.Count + "\n" + string.Join("\n", Passed);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fixture);
                CameraMoveCatalog.Bind(originalSettings);
            }
        }

        sealed class CameraPlanStub : ILLMClient
        {
            public bool IsBusy { get { return false; } }
            public string LastError { get { return null; } }
            public System.Threading.Tasks.Task<string> ChatAsync(List<ChatMessage> messages, System.Threading.CancellationToken token)
            {
                return System.Threading.Tasks.Task.FromResult("{\"camera\":\"look_store\",\"camera_angle\":0}");
            }
        }

        public static string StartCancellationProbe(string endpoint)
        {
            if (_probeObject != null) return "已有测试在运行";
            _probeObject = new GameObject("LocalCancellationProbe") { hideFlags = HideFlags.HideAndDontSave };
            _probeObject.SetActive(false);
            var client = _probeObject.AddComponent<QwenVisionClient>();
            client.Endpoint = endpoint;
            client.SetApiKey("local-regression-placeholder");
            var frames = new List<VisualFrame>();
            for (int index = 0; index < 4; index++) frames.Add(new VisualFrame(index * 0.5f, 2, new byte[] { 1, 2, 3 }));
            ProbeResult = "running";
            ProbeStack.Push(client.AskVideo("本地取消测试", new VisualClip(frames), "", value => ProbeResult = value, error => ProbeResult = error));
            EditorApplication.update += TickProbe;
            return "已开始本地HTTP取消测试";
        }

        public static void CancelProtocolProbe()
        {
            if (_probeObject != null) _probeObject.GetComponent<QwenVisionClient>().AbortAskRequest();
        }

        public static string StartProtocolProbe()
        {
            if (_probeObject != null) return "已有测试在运行";
            _probeObject = new GameObject("SyntheticVideoProtocolProbe") { hideFlags = HideFlags.HideAndDontSave };
            _probeObject.SetActive(false);
            var client = _probeObject.AddComponent<QwenVisionClient>();
            client.TimeoutSec = 30f;
            var frames = new List<VisualFrame>();
            for (int index = 0; index < 4; index++)
            {
                var texture = new Texture2D(384, 288, TextureFormat.RGB24, false);
                var pixels = new Color32[384 * 288];
                for (int offset = 0; offset < pixels.Length; offset++) pixels[offset] = new Color32(230, 230, 230, 255);
                int left = 20 + index * 80;
                for (int vertical = 100; vertical < 180; vertical++)
                    for (int horizontal = left; horizontal < left + 60; horizontal++) pixels[vertical * 384 + horizontal] = new Color32(220, 30, 30, 255);
                texture.SetPixels32(pixels); texture.Apply();
                frames.Add(new VisualFrame(index * 0.5f, 2, texture.EncodeToJPG(75)));
                UnityEngine.Object.DestroyImmediate(texture);
            }
            ProbeResult = "running";
            ProbeStack.Push(client.DescribeVideo(new VisualClip(frames), value => ProbeResult = value, error => ProbeResult = "ERROR: " + error));
            EditorApplication.update += TickProbe;
            return "已开始4帧合成视频测试，不访问真实摄像头";
        }

        static void TickProbe()
        {
            try
            {
                if (ProbeStack.Count > 0)
                {
                    var current = ProbeStack.Peek();
                    if (!current.MoveNext()) { (current as IDisposable)?.Dispose(); ProbeStack.Pop(); }
                    else if (current.Current is IEnumerator) ProbeStack.Push((IEnumerator)current.Current);
                    return;
                }
                EditorApplication.update -= TickProbe;
                if (_probeObject != null) UnityEngine.Object.DestroyImmediate(_probeObject);
                _probeObject = null;
            }
            catch (Exception exception)
            {
                ProbeResult = "ERROR: " + exception.Message;
                while (ProbeStack.Count > 0) (ProbeStack.Pop() as IDisposable)?.Dispose();
                EditorApplication.update -= TickProbe;
                if (_probeObject != null) UnityEngine.Object.DestroyImmediate(_probeObject);
                _probeObject = null;
            }
        }
    }

    /// <summary>P2 回归：模拟 AskNow 成功回包，不抓帧、不请求网络。</summary>
    public sealed class P2VisionStub : VisionObserver
    {
        public string Answer;
        public int AskCount;

        public override IEnumerator AskNow(string question, int requiredMask, Action<string> onAnswer)
        {
            AskCount++;
            if (!string.IsNullOrEmpty(Answer))
            {
                var append = typeof(VisionObserver).GetMethod("AppendRealtimeAnswer",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (append != null) append.Invoke(this, new object[] { Answer, requiredMask == 0 ? 1 : requiredMask, Time.realtimeSinceStartup });
            }
            if (onAnswer != null) onAnswer(Answer);
            yield break;
        }
    }
}
