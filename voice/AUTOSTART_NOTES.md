# 语音服务自启说明（2026-10-28 修改）

## 改之前的问题

开机登录后，计划任务 `Yixuan Voice Server Watchdog` 会自动启动 `tts_server.py`(8766) 和 `voice_server.py`(8767)。
`voice_server.py` 启动时会调用 `calibrate()` 播放约 1.5 秒校准噪声（用于测量扬声器→麦克风延迟，AEC 对齐），
所以每次开机都会突然响一下。

## 改之后的行为

| 场景 | 行为 |
|---|---|
| 开机 / 登录 | **不再启动**（计划任务已去掉登录触发器） |
| 打开本 Unity 项目 | 自动启动语音服务 |
| 关闭 Unity 编辑器 | 自动停止（仅当本次是打开项目时由编辑器启动的；手动启动的不受影响） |
| 手动启动 | `schtasks /Run /TN "Yixuan Voice Server Watchdog"` 或 Unity 菜单 |
| 手动停止 | `powershell -NoProfile -ExecutionPolicy Bypass -File "voice\stop_voice.ps1"` |

> 注意：服务启动时仍会有一次约 1.5 秒的校准噪声（AEC 校准需要，未改动）。

## 涉及文件

| 文件 | 作用 |
|---|---|
| `Assets/Editor/VoiceServicesAutoStart.cs` | Unity 编辑器脚本：项目打开自动启动、编辑器退出自动停止、菜单入口 |
| `voice/stop_voice.ps1` | 停止脚本：只杀本目录相关的看门狗 / tts_server / voice_server 及其子进程 |
| `voice/voice_watchdog.ps1` | 原看门狗（未改动） |
| `voice/_task_backup.xml` | 计划任务原始 XML 备份（用于回滚） |
| 计划任务 `Yixuan Voice Server Watchdog` | 保留，但已无任何触发器，仅供手动 / Unity 调用 |

## Unity 菜单

`Tools → Yixuan → Voice Services`

- `Start`：手动启动
- `Stop`：手动停止
- `Health Check`：检查 8766 / 8767 健康状态（Console 输出）
- `Auto-Start On Project Open`：勾选开关（默认开），关掉后打开项目不再自动启动

## 回滚

1. 恢复开机自启：
   ```
   schtasks /Create /TN "Yixuan Voice Server Watchdog" /XML "<project>/voice/_task_backup.xml" /F
   ```
2. 取消 Unity 侧自动启停：删除或移走 `Assets/Editor/VoiceServicesAutoStart.cs`（以及 `.meta`）。

## 备注

- 另一个开机自启项（与本项目无关）：`HKCU\...\Run\DSHVoiceBridge` → 本机另一个语音桥接服务（与本项目无关）。
