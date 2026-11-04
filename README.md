# 虚拟数字人 / AI Character Interaction System

一个跑在 Unity 3D 场景里的 AI 数字人原型：她能听懂自然语言，在场景里走过去、做动作、看用户和游戏世界，再开口回话；项目同时包含一套程序化城市地图生成工具链。

> 毕业设计（进行中）。当前已跑通**单数字人闭环**；下一步是同一张大地图里并行多个数字人、玩家可控角色进入，最终让玩家和数字人一起「造世界」。

- 演示视频：https://space.bilibili.com/503297296
- 项目仓库：https://github.com/wbcyyds123321/digital-human

---

## 项目状态：阶段性源码开源（不是成品游戏）

**本项目目前只是阶段性的源码开源，不是已经封装好的可安装应用或游戏。**

- 没有针对普通用户体验做任何优化；
- 后续还会继续改动，所以目前没有封装成可以直接安装、即点即用的应用包/游戏发行版；
- 现阶段主要用于代码与架构展示、审稿；
- 要运行仍需自行准备 Unity 工程、第三方依赖、角色/地图素材和 API 配置。

等后续版本稳定后，再考虑打包与发行。

## 她现在能做什么

- **听懂并做事**：自然语言指令 → 任务契约 → Lua 任务程序 → Unity 技能执行 → 真实回执校验。
- **边走边说**：行动/对话双车道，支持打断、暂停-插入-继续；做不下去会给出明确原因（不可达 / 歧义 / 超时）。
- **看得见**：VLM 两路视觉——笔记本摄像头看用户，Unity 相机看游戏世界；当前用于对话/观察，也为后续「自己看地图、判断改造」打底。
- **说得出**：全双工语音链路（AEC / VAD / ASR + 流式 TTS + 口型同步），支持边说边听、被打断会停。
- **演得像**：15 种表情、10 种神态、16 个角色技能，VRM / MMD 骨骼 + 表情/口型/头发物理。
- **能改世界**：程序化地图工具链（2D 规划 → GPT-Image 参考图 → Blender 模块化生成 → GLB + 导航/碰撞/锚点/地面高度 + 中文名/坐标映射）。

## 核心架构

### 1. 行动车道 / 对话车道

- **行动车道**：`ChatSession` → 任务契约（目标、次数、顺序、禁止项、成功条件）→ `TaskLuaRuntime` 执行 Lua 程序 → `TaskToolRegistry` 里的 `game.*` 技能 → Unity 真实回执。
- **对话车道**：独立的一次 LLM 请求，只负责聊天、进度和看图，不直接控制身体。
- 两条车道共享对话状态，所以可以边走边说；模型只负责出方案，执行与完成判定交给 Unity。

关键代码：
- `unity/Assets/Scripts/AI/ChatSession.cs`
- `unity/Assets/Scripts/AI/Tasks/TaskMissionRunner.cs`
- `unity/Assets/Scripts/AI/Tasks/TaskLuaRuntime.cs`
- `unity/Assets/Scripts/AI/Tasks/TaskToolRegistry.cs`

### 2. 视觉与语音

- `QwenVisionClient` 走 OpenAI 兼容接口，支持普通问答、视频帧、取消/超时与回退模型；摄像头和 Unity 相机两个来源共用同一套视觉链路。
- `voice/voice_server.py` 是独立语音 sidecar：AEC / VAD / 本地 ASR / 流式 TTS / 打断控制；Unity 侧通过 UDP 接收口型数据。

关键代码：
- `unity/Assets/Scripts/Vision/QwenVisionClient.cs`
- `voice/voice_server.py`

### 3. 导航与地图

- 大地图模式不依赖场景里的 `Ground`：障碍来自 AABB，寻路走导航图，语义地点来自中文锚点；角色用地面高度图动态贴地。
- 地图工具链输出 GLB/FBX、碰撞 AABB、导航图、中文锚点、1m 地面高度图，以及「中文名 + 坐标」映射表，让模型/Agent 能查表说「去面包房」，而不是猜坐标。

关键代码：
- `unity/Assets/Scripts/World/WorldData.cs`
- `unity/Assets/Scripts/Motion/GroundSnap.cs`
- `map/tools/make_plan.py`
- `map/tools/build.py`
- `map/integration/make_unity_data.py`

## 仓库结构

```text
unity/
  Assets/Scripts/        # 运行时 C#：AI / 任务 / 导航 / 视觉 / 语音 / UI / 角色
  Assets/Editor/         # 编辑器工具：地图迁移、任务场景构建、回归检查
  ProjectSettings/       # 仅保留 ProjectVersion
voice/                   # Python 语音 sidecar（AEC / VAD / ASR / TTS / 打断）
map/
  tools/                 # Blender + Python 程序化地图生成
  integration/           # 导出 Unity 运行时数据（导航 / 碰撞 / 锚点 / 地面高度）
docs/
  architecture.md        # 项目技术总览（架构 / 数据流 / 模型 / 指标）
  roadmap.md             # 下一版方向：VLM 改地图 / 双全工语音 / Agent 注入
```

## 建议先看这些

1. `docs/architecture.md`：全局架构、双车道、任务契约、数据流和指标。
2. `unity/Assets/Scripts/AI/ChatSession.cs` 和 `AI/Tasks/TaskMissionRunner.cs`：一句话如何变成可执行、可验证的任务。
3. `unity/Assets/Scripts/AI/Tasks/TaskToolRegistry.cs`：角色技能如何注册并被 Lua 调用。
4. `voice/voice_server.py`：语音 sidecar 的 AEC / VAD / ASR / 流式 TTS 组织方式。
5. `map/tools/build.py` + `map/integration/make_unity_data.py`：地图从 2D 规划到 Unity 数据的完整链路。

## 技术栈

- Unity 2022.3.62f3c1 + URP；C#；VRM / UniGLTF / lilToon / MMD 骨骼 + 变形桥接。
- Python 3.11+ 语音 sidecar；本地 ASR、流式 TTS、AEC/VAD。
- LLM / VLM 通过可配置 endpoint 接入（规划、对话、视觉可独立换模型）。
- Blender + Python 程序化地图生成；GPT-Image 参考图；GLB/FBX 导出。

## 关于本仓库的说明

这是一个**源代码 + 架构文档**仓库，不是可直接运行的完整 Unity 工程：

- **不包含**角色 VRM / MMD 模型、美术贴图、动画资产、地图 GLB/BLEND/渲染图、模型权重、TTS 参考音频；
- **不包含**内部 agent 记录、`Logs/`、`Captures/`、本地缓存与任何 API Key；
- 第三方 Unity 包（VRM / UniGLTF / lilToon / ADB / Unity MCP 等）不在此仓库中，README 只说明依赖关系；
- 代码通过环境变量或本地配置文件读取密钥，仓库内没有任何可用的 API key。

如果你需要完整工程或美术资源，请通过 GitHub / Bilibili 联系作者。

## Roadmap

- **A. 地图 VLM → 外部 Agent → 地图工具链**：数字人自己看地图、提改造方案，外挂 Agent 细化后调用工具链生成并热插拔。
- **B. 双全工语音**：全双工 + 全工具，像打电话一样边说边调工具。
- **C. Agent 生成地图 / 脚本 / 插件**：从数据级生成走到代码级注入。

详见 `docs/roadmap.md`。

## License

源代码采用 [MIT License](LICENSE)。角色模型、贴图、音频、地图资产与第三方包不在本仓库内，不适用该许可证；如需使用请联系作者确认授权。
