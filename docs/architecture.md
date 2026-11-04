# 虚拟数字人 / AI 角色交互系统 — 技术架构总览（2026-11-04）

> 用途：记录当前架构、数据流和实测指标，方便 review 与后续迭代。所有数字来自当前仓库实扫（2026-11-04），
> 不是宣传口径；已知限制单列，避免把“正确拒绝”讲成“执行成功”。

---

## 0. 一句话定位

一个 **Unity 内的 AI 数字人（3D 虚拟角色）实时交互系统**：
用户用自然语言/语音说话 → 多模型分工理解与对话 → 角色在真实 3D 场景里走路、转身、做动作、
看画面、说话（TTS + 口型）→ 用真实回执校验任务是否完成，并把结果播报出来。

不是“聊天机器人换了张皮”，而是把 **LLM 的语义能力** 接到 **Unity 的真实身体、相机、视觉、语音、导航** 上，
并用契约/回执/验收把“模型说了什么”与“世界实际发生了什么”分开。

## 1. 项目规模（实扫）

| 项 | 数字 |
|---|---|
| Unity 工程 | 2022.3.62f3c1（中国版），URP 14.0.12 |
| C# 脚本 | **185 个文件 / 39,403 行**（14 个系统目录） |
| 场景 | 6 个（主场景 `SovietBlueHour_TaskPlan.unity`；旧雨夜便利店场景保留回退） |
| 材质 / 动画 FBX / VRM | 100 个材质、16 个 Mixamo FBX、1 个 VRM 角色（45.8 MB） |
| 地图资产 | Blender 程序化生成：48 个街区模块、38,454 个对象、1,421,322 三角面、构建 1,247 秒（Blender 5.2.3 LTS）；导出 GLB 112 MB → Unity 51 个合并网格 / 2,047 submesh / 1.417M 三角面 / ~571 材质槽 |
| 地图运行时数据 | 91 个碰撞 AABB（运行时 88）、3,486 个导航节点 / 8,551 条边、46 个中文语义锚点、401×305 的 1m 地面高度网格 |
| Python 工具链 | 155 个文件 / 33,564 行（地图生成、集成数据、语音服务、验收辅助） |
| 文档 | 55 篇 Markdown（架构、交接、验收、踩坑记录 1,237 行 PROJECT_NOTES） |
| 编辑器菜单 | 27 个 `MenuItem`（一键重建场景、QA、验收、地图工具等） |
| 自动化验收 | 14 个 C# 验收套件 + batchmode QA + 证据 JSON |
| 性能（batchmode QA，1280×720） | 约 **143 FPS**（676→4,890 帧 / 29.3s） |

## 2. 技术栈全景

### 引擎 / 渲染
- **Unity 2022.3 LTS + URP 14.0.12**（Forward、自定义 SkyGradient 半球天空、URP Volume 后处理）。
- **Shader/材质**：lilToon（角色卡通渲染）、自定义 SkyGradient、DioramaToon/Glass/Unlit（旧场景保留）、
  URP/Lit（地图 571 材质槽）、Bloom / Tonemap / ColorAdjustments / Vignette。
- **三光源分层**：KeyLight（地图+角色，阴影）、MoonLight（只照地图，cullingMask 排除 Layer 8）、
  FillLight/RimLight（只照角色 Layer 8）；shadowDistance 70m / 2 cascades / soft shadows。
- **性能**：地图静态合批、LOD/合并网格、103 万像素级相机；QA 约 143 FPS。

### 角色 / 动作
- **VRM 1.0**（UniVRM / UniGLTF）导入；模型为 **MMD 血统骨架**（日文骨名 + 控制骨/变形 D 骨两套链）。
- **MmdDeformBridge**：每帧把控制骨世界旋转同步到真正蒙皮的 D 骨，修复“腿骨在动但网格不动”的 MMD 映射问题。
- **Mixamo 动作重定向**：MixamoRigSetup / MixamoActionWiring / MixamoActionManifest（语义 id → clip 名，
  带中文名/时长/姿态/身体部位/情绪标签/是否循环/可打断），16 个 FBX、15 个可用语义动作。
- **表情**：ExpressionLibrary 15 种表情（blendshape 组合，含微笑/大笑/生气/悲伤/惊讶/害羞/眨眼等）；
  DemeanorCatalog 10 种神态（专注/害羞/思考/自信/疲惫/开心/低落/惊讶/警觉/放松），带视线/头部俯仰/眨眼频率。
- **身体表现**：SpringBone 调参 + Automatic Dynamic Bone（ADB）头发物理试点 `ZhuangfyHairDynamics`；
  BlinkController 眨眼；ProceduralIdleMotion 程序化待机；GroundSnap 动态贴地（1m 地面高度图，脚底误差 ≈0.1mm）。
- **口型**：TTS sidecar 经 UDP 8770 每 20ms 发 `seq/rms/5 个元音权重`，Unity MouthDriver 驱动
  あ/い/う/え/お blendshape；不依赖 uLipSync。

### 语音
- **TTS**：MiniMax 云 `speech-2.8-turbo`，voice `zhuangfy001`，**PCM 流式**，本地缓存；
  情绪由表情/神态映射到 MiniMax emotion + 语速/音量；首块约 **0.3–0.5s**，首句入队约 **1.27–1.54s**。
- **ASR**：本地 **sherpa-onnx + SenseVoice int8**（中/英/日/韩/粤，CPU 实时）。
- **VAD**：Silero VAD（ONNX）；**AEC**：pyaec（参考对齐 + 回声弱时自动旁路）。
- **全双工 sidecar**（Python，127.0.0.1:8767；TTS 127.0.0.1:8766）：
  麦克风 48k 采集 → 降采样 16k → AEC → VAD →（静音时）ASR final /（AI 播放时）用户开口 → barge_in
  → 停播 + 继续收录音 → ASR；支持插话打断、`/say_queue`、`/say_finish`、健康检查、AEC 校准。
- **流式回复**：`StreamMode=full` → LLM SSE + 增量 JSON + 分句 TTS，首块 0.48–1.14s。

### 视觉
- **两路视觉源**：EyeCamera（角色眼睛位置，FOV 75，排除角色自身 Layer 8）+ 电脑摄像头 WebCamTexture。
- **模型**：Qwen 视觉（DashScope OpenAI 兼容接口），主模型 **qwen3.5-omni-flash**，回退链
  `qwen-vl-ocr → deepseek-v4.1-flash-1017 → qwen3.8-omni-flash`；主模型实测 Ask/Describe ≈0.47–0.73s。
- **两种工作模式**：
  1) 后台观察（变化驱动 + 退避，画面不变不发请求、不烧额度；描述去重、8s TTL）；
  2) 实时问答（用户问“我手里拿的是什么/我比划的是几”时立即抓帧，带原问题问 Qwen）。
- **视频记忆**：滚动帧缓冲（30s / ≤128 帧 / ≤16MB），Qwen 产出结构化 `events/facts/current`，
  存 `VisualMemoryStore`（时间线、事件、事实），供对话引用；仅本局内存保留。
- **四方向观察**：任务里的 `game.observe()` 抓四方向截图 + Qwen 语义分析，回执里带 observation。
- **额度/限流处理**：403 FreeTierOnly / 429 → 多模型回退，全失败才暂停并提示 5 分钟退避。

### LLM / 语言
- **DeepSeek `deepseek-flash`**（OpenAI 兼容 `https://api.deepseek.com/chat/completions`）：
  - 一个角色是 **行动规划器**（任务合同 + Lua 代码生成）；
  - 另一个角色是 **Narrator 对话车道**（独立 `DeepSeekClient` 实例、独立取消令牌、独立上下文）；
  - 关闭 thinking（`thinking.type=disabled`），避免慢和已知 reasoning_content 清空 bug；
  - 支持 SSE 流式、JSON 修复、超时/重试、key 文件/环境变量读取（不打印 key）。
- **混合 NLU**：先用确定性正则/关键词解析高频意图（相机转向、看方向、视觉问题、任务词、确认/否定），
  能本地解决就不调 LLM；复杂语义再交给模型 → 降低首字延迟、降低不确定性。
- **Prompt 工程**：`PromptBuilder`（人设/风格/场景/动作目录/历史/记忆预算）、`ResponseParser`
  （容错 JSON、流式 JSON 解析、纯文本兜底）、`Director`（把模型输出落到 Unity：表情/动作/体态/相机/移动）。
- **记忆**：多轮对话历史（预算 32,000 token 上限，滑窗 6 轮 + 记忆统计）；人格/技能存
  `persistentDataPath/character_skills.json`；场景物体缺失记录写 `missing_requests.jsonl`。

### 世界 / 地图
- **Blender 程序化城市**（`Maps/SovietBlueHour`）：`tools/plan.json` + 模块化建筑生成器
  （住宅/公共/工业/地标/纪念雕塑等 30+ 模块类型）+ `kit.py/sitekit.py/look.py` 灯光/材质/道具；
  48 个街区、38k 对象、1.42M 面、构建 1,247s；导出 GLB + scene manifest + build stats。
- **集成数据生成**（`Maps/SovietBlueHour/integration`）：
  - `make_unity_data.py`：从 Blender 场景抽 AABB 碰撞（91）、导航图（3,486 节点 / 8,551 边）、
    46 个中文语义锚点（出生点 + 地标/商店/广场/公交站等），并做碰撞/可达性过滤；
  - `make_ground_height.py`：从 SITE 网格烘焙 1m 地面高度图（401×305，广场≈0.155m、草地≈0.02m）。
- **Unity 导入**：UniGLTF 导入 GLB（ReverseZ、URP 材质），生成 51 网格地图 prefab；
  场景构建器从旧场景复制角色/UI/任务/语音/视觉，换环境、接相机/灯光/后处理，写入 Build Settings。
- **WorldData 大地图模式**：碰撞 AABB 障碍（含 AgentRadius 膨胀）+ 导航图 A*（节点/边碰撞过滤、
  最近 10 节点接入、逐段校验、拉直）+ 46 锚点 + 区域查询；替代旧的小场景 Renderer 包围盒方案。
- **坐标契约**：Blender Z-up `(bx,by,bz)` → Unity `(bx, bz, by)`（glTF ReverseZ）；三处实测交叉验证，
  并避开 nav 数据里 `(x,z,-y)` 的错误约定。
- **第三人称相机**：CameraDirector `ThirdPerson` 模式（默认面对面 3/4，距离 3.9m、俯仰 6.5°、
  看向高度 1.12m、FOV 42；右键环绕、滚轮缩放、SphereCast 相机避障、说话推近）。
- **QA**：batchmode Play 截图（默认/四个机位/过肩/挥手/走路/眼视/UI/1080p 展示图）+ 静态校验 JSON
  （88 障碍/3,486 节点/46 锚点/相机/灯光/UI 组件/0 问题）。

### UI / 交互
- **统一控制台 `UnifiedHud`**（IMGUI）：左侧 6 标签页（地图/任务/视觉/相机/角色/设置）+ 右上快捷条
  （面板/地图/任务状态/走路/视角/语音/眼睛）+ 小地图；F4 收起，F5~F10 切页。
- **小地图**：启动时把碰撞 AABB / 导航边 / 锚点烘成 560×426 纹理；玩家箭头随朝向旋转；
  “附近可去地点”按距离排序，点击 = `CharacterLocomotion.MoveToAnchor` 走过去。
- **ChatUI**（UI Toolkit，纯 C# 构建）：字幕/内心话/输入框/流式字幕/设置（回复长度、记忆预算、流式、
  语音打断、思考模式、人格/Skills）、状态与提示。
- **YixuanControlPanel**（UI Toolkit）：表情/动作/姿势/播放/微调/灯光/预设；嵌入 HUD「角色」页。
- **TaskMissionPanel**（IMGUI）：任务输入、规划并执行、停止、合同/回执/Lua/证据、输入路由（自动/行动/对话）。
- **VisionControlUI/EyeViewPreview**（uGUI Canvas）：摄像头预览、眼睛视角画中画、视锥线。

### 工程化 / AI 辅助开发
- **Unity MCP**（`com.coplaydev.unity-mcp`）：AI 代理可直接在编辑器里查询场景/组件、进 Play、截图、
  读 Console、执行 C# 编辑器代码，用于实机迭代与 QA。
- **批处理链路**：`Unity.exe -batchmode -executeMethod ...`（构建场景 / 导入 / QA / 校验 / 探针），
  文件报告 + watchdog 防卡死；QA 出图后用图像检查（颜色/占比/是否有洋红 error shader）。
- **验收体系**：14 个 C# 验收套件（任务/失败/代码/打断/注意力/日志/并发/生命周期/配对/真实/路由/
  脚本语义等），输出 JSON/MD 证据（`Captures/TaskMissions`, `D:/AI/GameScaling/docs/evidence`）。
- **文档驱动**：AGENTS.md（接手必读）、PROJECT_NOTES.md（1,237 行踩坑记录）、
  Docs/Architecture 下 20+ 篇交接/评审/验收文档；每次重大改动有代码证据和截图证据。
- **工作流**：Agent Teams + 独立只读审计（Parity Audit）+ 并行子代理（地图分析/导入工程师）+
  Git checkpoint；本轮已完成一次 382 文件的 checkpoint 提交（`3480d3d`）。

---

## 3. 整体架构（分层）

```
┌──────────────────────────── 输入层 ────────────────────────────┐
│ 文本输入框 / 麦克风(48k) → sidecar AEC/VAD/ASR(16k) / 开关按钮   │
└───────────────┬────────────────────────────────────────────────┘
                ▼
┌──────────────────────── 会话与路由层 ──────────────────────────┐
│ ChatSession：混合 NLU（本地正则意图）→ InputMode(Auto/Action/   │
│ Dialogue) → 本地相机/注视/视觉问题处理 → 需要时调 LLM          │
│   ├─ Narrator 对话车道（独立 LLM 请求 / 取消令牌 / 上下文）      │
│   └─ Action 行动车道（TaskMissionRunner）                        │
└───────┬───────────────────────────────────────┬────────────────┘
        ▼                                       ▼
┌─────────────────── 行动车道 ──────────────────┐  ┌──────────── 对话车道 ───────────┐
│ 1. LLM 生成 Intent Contract（目标/次数/顺序/  │  │ 1. 路由分类：chat/status/vision/ │
│    禁止/权限/成功条件/途中时机）               │  │    action/interrupt              │
│ 2. LLM 生成真实 Lua 程序（MoonSharp）          │  │ 2. 只能 speak/express，不能动身体│
│ 3. ProgramLint + ToolRegistry + Policy Guard  │  │ 3. 事实来自 ActionFacts/回执，   │
│ 4. Lua VM 执行 → game.* 工具 → Unity 技能     │  │    不宣称未确认的“完成”          │
│    (navigate/follow_route/gesture/observe/…)  │  │ 4. 流式 TTS / 文字回复           │
│ 5. 真实回执 → Lua resume → 完成校验 → 证据    │  └─────────────────────────────────┘
└───────────────┬───────────────────────────────────────────────┘
                ▼
┌──────────────────────── Unity 真实世界层 ──────────────────────┐
│ CharacterLocomotion（A* / 可续行路径，Layer 8）                 │
│ Character（VRM/MMD 骨骼/表情/口型/动画所有权）                  │
│ CameraDirector + EyeGimbalController（第三人称/眼睛视角/云台）  │
│ VisionObserver + QwenVisionClient（两路视觉/视频记忆）           │
│ WorldData + MapWorldProfile（碰撞/导航图/锚点/地面高度）         │
│ GroundSnap / SpringBone / ADB / Blink / MouthDriver             │
└───────────────┬───────────────────────────────────────────────┘
                ▼
┌──────────────────────── 表现层 ────────────────────────────────┐
│ URP：lilToon 角色 + 地图 URP/Lit + SkyGradient + Bloom/Tonemap │
│ UI：UnifiedHud / ChatUI / YixuanControlPanel / 小地图 / PiP    │
│ 语音：MiniMax 流式 TTS → sidecar 播放 → UDP 口型                │
└───────────────────────────────────────────────────────────────┘
```

---

## 4. 模型与服务清单（谁负责什么）

| 角色 | 供应商/模型 | 端点/进程 | 用途 | 关键设计 |
|---|---|---|---|---|
| 行动规划 LLM | DeepSeek `deepseek-flash` | `api.deepseek.com` | 生成 Intent Contract + Lua 程序 | thinking 关闭；两次调用（合同→代码）；预算 96 次调用/600s/约 200 万 VM 指令 |
| 对话/Narrator LLM | 同一模型、独立客户端 | 同上（独立请求） | 聊天、进度、事实播报、路由分类 | `_narratorConfig = Instantiate(Config)`，独立取消令牌；不能调 navigate/interact；无回执不宣称完成 |
| 视觉模型 | Qwen `qwen3.5-omni-flash`（回退：qwen-vl-ocr → deepseek-v4.1-flash-1017 → qwen3.8-omni-flash） | DashScope OpenAI 兼容 | 看图/视频记忆/四方向观察/实时问答 | 两路视觉源；变化驱动退避；403 额度→自动回退；JPEG 384×288/640×480 细节档 |
| TTS | MiniMax `speech-2.8-turbo` | `api.minimax.cn` + 本地 sidecar 8766/8767 | 流式语音、情绪/语速映射、缓存 | PCM 流式 + 分句队列；首块 0.3–0.5s；AEC 参考信号复用 |
| ASR | sherpa-onnx + SenseVoice int8（本地） | sidecar 8767 | 中文/多语种实时识别 | CPU 实时；Silero VAD 分段；AEC 去回声；barge-in |
| 口型 | 规则驱动（UDP 8770） | sidecar → Unity | 5 元音权重驱动 blendshape | 20ms 包；与 TTS 播放同源，保证音画同步 |
| 编辑器代理 | Unity MCP（Coplay 插件） | 本地 HTTP/WebSocket | 场景查询/Play/截图/Console/执行 C# | 用于 AI 实机迭代、QA 截图、验收 |
| 视觉兜底/评测 | 外部 `vision.js`（Codex 工具） | 本地脚本 | 图片语义问答（开发侧） | 与游戏内视觉链路分开，不混算 |

**关键原则**：只有 Action Lane 能提交副作用；Narrator 只能说；视觉只提供观察；所有“完成”必须有 Unity 组件回执。
多模型不是“多个大脑各解释一句话”，而是 **角色分工 + 同一份契约 + 事件/回执总线**。

---

## 5. 双车道任务运行时（项目最核心的设计）

### 5.1 输入路由

`ChatSession.Submit` 顺序：
1. `TryRouteMission`：本地确定性处理（静音/恢复、停止注视、显式“停下”、任务进行中的插入识别、
   “继续走…聊”特例、Action 模式强制提交任务）；
2. `RouteWithNarrator`：任务在跑/暂停 **或角色正在走向锚点** → Narrator 对话车道；
3. 普通聊天轮次（空闲时，保留流式 TTS 首声速度）。

### 5.2 行动车道（Action）

```
自然语言 → Intent Contract（机器可校验的“用户到底要什么”）
   obligations[]（动作/目标/次数/顺序/禁止/条件/途中时机）
   forbid_move / forbid_speech / allowed_targets / success[]
→ LLM 生成 Lua（真函数/变量/循环/条件/表）
→ ProgramLint（静态检查）→ ToolRegistry（工具/参数/资源/幂等/超时）
→ MoonSharp VM 执行；每个 game.* 调用被“暂停”为 C# 请求
→ Unity 技能执行 → 真实回执（位置/朝向/动画/画面/语音）
→ Lua resume → 完成校验（到没到/面向没/must 条件）
→ evidence.json / code_trace.jsonl / trajectory.jsonl / 录像帧
```

工具（16 个，注册表驱动）：`navigate / return_home / follow_route / move_relative / turn_body /
look_at / look_direction / gesture / interact / observe / check / speak / express / wait / report / parallel`。

已实现的关键能力：
- **可续行 A***：`follow_route(target, {to_fraction=0.5})` 保存真实路径，按累计弧长走到一半；
  `{distance=0.4}` 在剩余路径上续走；返回 route_id/progress/done，可继续剩余路径；
- **暂停/插入/继续**：保留父 Lua VM、变量、循环位置、回执、运动快照；插入完成后继续，不重写剩余脚本；
  插入限静止动作（gesture/express/speak/wait），队列最多 3；“停下”=取消，“改去别处”=替换；
- **真实回执校验**：脚本 `return {ok=true}` 不算完成；导航看位置/朝向/路径进度，动作看动画完成回调，
  观察看 Qwen 回执，交互看注册工具真实完成；
- **失败语义**：不可达/歧义/未知目标/超时/被拒绝都如实报错，不用最近点或瞬移冒充成功；
- **预算与取消**：96 次工具调用、600 秒、约 200 万 VM 指令、每步超时、幂等 key、取消令牌；
- **资源仲裁**：body/camera/voice/expression 资源位与并行前置检查（`parallel` 工具）。

### 5.3 对话车道（Narrator）

- 独立 LLM 请求（克隆配置、独立 cancel token、独立 `_taskDialogueHistory`）；
- Prompt 规定：只能输出 `route (action/interrupt/chat/status/vision) + reply`；不能移动/转相机/做动作；
- 事实来源：`ActionFacts()`（任务进度/回执）与 `WalkingFacts()`（正在走向哪）；
- `status/vision` 路由由应用侧模板/视觉模型接管，避免模型编造；
- Narrator Guard：没有回执不宣称“完成”；失败要纠正而不是圆场；
- TTS 走 `TaskSpeechQueue`（完整句合成）。

### 5.4 “边走边说”的两种路径（历史坑，已统一）

- 任务在跑时聊天 → Narrator 车道，与行动并行（GPT 双车道设计，2026-10-25 起）；
- 非任务的“聊天让她走过去”也走 `Director.walk_to → CharacterLocomotion`；
  10-25 的“首声优化”把 Narrator 收窄成仅任务时启用，导致这条路径掉回普通轮次 **停走**；
  11-02 修复：`Locomotion.IsMoving` 也进 Narrator；普通轮次保留 StopWalking/think 守卫作兜底；
  显式“停下/站住”在路由最前处理。

---

## 6. 对话/语音数据流（一次语音问答）

```
麦克风 48k → sidecar(8767)：降采样 16k → AEC(pyaec) → Silero VAD
   ├─ 静音段：SenseVoice ASR → asr.final → Unity VoiceInputController
   └─ AI 播放中检测到人声：立即停播 + barge_in → Unity ChatSession.CancelDialogueTurn
Unity ChatSession：
   本地意图/视觉问题优先 → 需要时 DeepSeekChatStream(SSE)
   → 增量 JSON 解析（StreamingPlanParser）
   → Director.Apply(表情/动作/体态/相机/移动)
   → TTS: /say_queue 按句入队（MiniMax 流式 PCM）
   → sidecar 播放 48k + 每 20ms UDP 口型 → Unity MouthDriver
   → 字幕/内心话/状态更新
```

要点：LLM 流式 + 分句 TTS 降低首字延迟；filler（“我看看”）遮 Qwen 往返；barge-in 只取消对话车道，
不误杀行动车道；语音输出可单独开关。

## 7. 视觉数据流（一次“我手里拿的是什么”）

```
用户问题 → VisualQueryIntent/本地规则判断是否需要实时视觉
→ VisionObserver.AskNow：抓当前 EyeCamera/摄像头帧（384×288 或 640×480 细节档）
→ QwenVisionClient（qwen3.5-omni-flash，失败按回退链换模型）
→ 带原问题问 Qwen → 返回针对性中文答案
→ ChatSession 作为本轮 reply（不写历史/不污染长期记忆）
```

后台视觉：每 0.5s 采样帧（30s/128 帧缓冲）→ Qwen 产出 `events/facts/current` → VisualMemoryStore；
对话可引用时间线事实（“刚才你举了什么”）；画面无变化自动退避不烧额度。

## 8. 地图管线（Blender → Unity）

```
plan.json（街区规划）
 → 模块化 Blender 生成器（30+ block type / 48 blocks / 38k objects / 1.42M tris，构建 1247s）
 → GLB 导出 + scene manifest + build_stats（Blender 5.2.3 LTS）
 → Unity UniGLTF 导入（ReverseZ、URP 材质）→ 51 网格 prefab（571 材质槽）
 → 集成数据（Python）：
     collision.json 91 AABB / nav_compact.json 3486 节点 8551 边 /
     anchors.json 46 中文锚点 / ground_height.json 401×305 1m 高度图
 → 场景构建器：复制旧场景角色/UI/任务/语音/视觉 → 换环境 → 接相机/灯光/天空/后处理 → Build Settings
 → QA：batchmode Play 截图 + 静态校验（0 问题）+ 小地图烘焙
```

坐标契约：Blender `(bx,by,bz)` → Unity `(bx, bz, by)`；已用 3 处建筑包围盒交叉验证；
导航数据里错误的 `(x,z,-y)` 字段被重新生成时纠正。

## 9. 可靠性设计

1. **契约 > 提示词**：用户意图先固化成 obligations/permissions/success，再让模型写代码；
   用户说“不要动/不要说话”由运行时拒绝，不依赖 prompt 自觉。
2. **回执 > 自述**：任务完成必须由 Unity 组件回执证明（位置/朝向/动画/画面/交互），
   脚本返回值不算数；可复现证据目录（Lua 源码/回执/轨迹/事件）。
3. **工具注册表**：工具名/参数/资源/幂等/超时/副作用统一描述，prompt 由注册表生成，避免漂移。
4. **混合 NLU**：相机/注视/视觉/简单任务走本地规则，LLM 只处理复杂语义；延迟与成本都降。
5. **双车道**：Action 独占副作用，Narrator 只说话；事件/回执驱动事实播报，避免“说到了其实没到”。
6. **可恢复**：Lua VM 暂停/继续 + 可续行 A* 路径 + 运动快照；插入不丢父任务进度。
7. **坐标与视觉调试**：Blender→Unity 坐标契约、相机/灯光分层、洋红 shader 缓存预热、
   GroundSnap 1m 高度图、动态贴地收敛修复。
8. **额度/限流工程**：403 FreeTierOnly/429 分类 → 多模型回退 → 全失败才退避；key 文件不打印。
9. **回归防呆**：14 套验收 + QA 截图 + Parity 审计 + 文档记录；这次还发现并修复了
   “narrator 路由收窄导致非任务走路停走”“think 动画顶掉 walk 动画导致滑步”等回归。

## 10. 已实现能力 vs 未交付（诚实边界）

**已交付**
- 文本/语音实时对话（流式、打断、口型、表情/神态、人格/技能/记忆）；
- 任务编排：自然语言 → 契约 → Lua → Unity 工具 → 回执 → 汇报；
- 导航/相对移动/身体转向/注视/看方向/动作/表情/说话/视觉观察与检查/交互；
- 可续行路径、半路动作时机校验、暂停-插入-继续、取消/替换语义；
- 两路视觉（游戏眼睛 + 摄像头）、后台观察/实时问答、视频记忆、多模型回退；
- 第三人称相机/眼睛视角 PiP/云台/避障；统一 HUD + 小地图 + 点击导航；
- 大地图迁移（导航/碰撞/锚点/地面高度/光照/后处理/QA）；
- 语音 sidecar（AEC/VAD/ASR/TTS/口型）与全双工打断。

**未交付/限制**
- 没有上下半身动画分层：走路中不能做全身挥舞动作（只能说话/口型/表情）；
- 任意工具/移动子任务的通用抢占与任意嵌套恢复未完成；
- 部分复杂“循环 + 条件 + 多约束”会被保守拒绝；
- 旧场景的端到端验收脚本写死旧坐标，不能直接用于新地图；新地图做了 QA/校验/行走/视觉/接地验证，
  但未在新地图重跑全部端到端任务验收；
- Qwen 免费额度曾耗尽，已用同网关多模型回退；真人 ASR 在噪声/抢话下未重新验收；
- 地图二进制资产（112MB GLB 等）按约定未进普通 git（用 LFS/本地分发）。
