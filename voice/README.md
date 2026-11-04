# P5 全双工语音（AEC + 打断）使用说明

> 2026-10-19。配套计划：`内部证据（未包含）`；证据：`Tools/_verify/p5_e2e_evidence.md`。
> 模型文件较大且含版权音频，均已加入 `.gitignore`，不在仓库里。

## 一键启动

双击 **`voice/start_voice.bat`**：
- 启动 `tts_server.py`（8766，Unity 回退用）
- 启动 `voice_server.py`（8767，全双工：采集 + VAD + ASR + AEC + 播放 + 打断）
- 第一次会播放 **1.5 秒校准噪声**（测扬声器→麦克风延迟/回声强度），然后常驻

> 语音服务启动后再进 Unity Play 即可；关掉窗口即停止。

## 结构

```
Unity (ChatSession / ChatUI / VoiceInputController)
   │  GET /events（长轮询）        ▲ 事件：vad.start/end、asr.final、
   │  POST /say、/cancel           │      tts.start/end/cancelled、barge_in
   ▼                              │
voice_server.py (127.0.0.1:8767)
   ├─ TTS：复用 tts_server.minimax_synth（MiniMax speech-2.8-turbo）
   ├─ 播放：sounddevice 48k（参考信号 = 精确播放 PCM）
   ├─ 采集：sounddevice 48k → 16k
   ├─ AEC：pyaec（参考对齐；回声弱时自动旁路）
   ├─ VAD：Silero（sherpa-onnx）
   └─ ASR：SenseVoice int8（本地 CPU，中文）
```

## Unity 配置（AIConfig.asset）

| 字段 | 默认 | 说明 |
|---|---|---|
| `VoiceInputEnabled` | true | 启用语音输入 |
| `VoiceServerEndpoint` | http://127.0.0.1:8767 | voice_server 地址 |
| `VoiceInputMode` | `duplex` | duplex=sidecar 播放+可打断；unity=M1 播放（半双工）；off=关闭 |
| `BargeInEnabled` | true | 允许开口打断 |

## 常用命令

```powershell
# 健康检查（含 AEC 状态 / 回声增益）
Invoke-RestMethod http://127.0.0.1:8767/health

# 手动校准
Invoke-RestMethod -Method Post http://127.0.0.1:8767/calibrate

# 列出音频设备
Invoke-RestMethod http://127.0.0.1:8767/devices

# 无声测试：把一段 16k/48k WAV 当成"用户说话"注入
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:8767/inject_user -ContentType 'application/json' -Body '{"path":"Tools/_verify/p5_user_voice2.wav"}'

# AEC 合成自测（无声）
python voice/test_aec_selftest.py
```

## 硬件/环境说明

- 默认设备：麦克风 `device 1`（麦克风阵列 Intel）、扬声器 `device 3`（Realtek），
  可用环境变量 `VOICE_INPUT_DEVICE` / `VOICE_OUTPUT_DEVICE` 覆盖；采集/播放固定 48kHz。
- 本机实测回声极弱（约 -30dB，且 16k 采集会被系统处理到近乎为 0），所以 `calibrate()` 在
  `echo_gain < 0.015` 时会**自动旁路 AEC**，避免把用户语音一起压掉；外放声音大/回声强的机器会自动启用。
- 想强制 AEC：启动前 `set VOICE_AEC=1`；强制关闭：`set VOICE_AEC=0`。

## 模型（不在 git 里，需要时重新下载）

```powershell
$base = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models'
curl.exe -L -o voice/models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09.tar.bz2 "$base/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09.tar.bz2"
curl.exe -L -o voice/models/silero_vad.onnx "$base/silero_vad.onnx"
# 解压 tar.bz2 → voice/models/sense-voice/
```

## 早晨真机验证（1 分钟）

1. 双击 `start_voice.bat`，等校准噪声结束、看到 `HTTP http://127.0.0.1:8767`；
2. Unity → Play → 等状态"在线" → 直接说话：「你好，今天怎么样？」；
3. 打断：问长问题（"用五句话介绍你自己"），它说的时候开口「等一下，我问个别的」→
   应在 0.3–0.5 秒内停嘴，然后听你新问题；
4. 出问题看 Console 的 `[VoiceIn]/[Chat]` 日志，或 `/health` 的 `aec_enabled/echo_gain`。

## 已知限制

- 云端 TTS 需要网络；首句合成约 1–2 秒（缓存命中则很快）。
- 口型同步暂未接（用户要求先跳过）。
- 多句连续说话：0.7s 内的相邻 ASR 段落会合并；停顿更久会被当成两轮。
- `voice_server` 只监听 127.0.0.1，不对外网开放。

## 流式回复（P6，2026-10-19）

- Unity `AIConfig.StreamMode`：`full`（默认，LLM SSE + 增量 JSON + 分句 TTS）/ `light`（完整 JSON 后分句）/ `off`（旧整段）。
- sidecar 新增：`POST /say_queue`（按 `sentence_id` 重排入队）与 `POST /say_finish`（队列收尾发 `tts.end`）。
- 实测首块 0.48–1.14s、首句入队 1.27–1.54s；细节见 `内部证据（未包含）`。

## 流式 TTS（P6，2026-10-19）

- MiniMax `stream:true` + `format:"pcm"`：sidecar 边收 PCM 边播，首块约 0.3–0.5s（非流式整句约 1.0s）。
- **MiniMax SSE 会在结尾再发一份完整音频**：`minimax_stream` 会识别并跳过重复/累计块，避免"复读机"。
- `TTS_STREAM=0` 可回退整句合成；流失败会自动回退整句，保证有声音。
- 打断改为连续 3 帧（60ms）+ 更高能量门限，避免外放时 TTS 回声误触发。
- 证据：`内部证据（未包含）`。

## 口型同步（P6 LipSync，2026-10-19）

- sidecar 播放时每 20ms 通过 UDP `127.0.0.1:8770` 发送 `seq + rms + 五元音权重`（28 字节），Unity `MouthDriver` 驱动 `あ/い/う/え/お`；不依赖 uLipSync。
- 开关：`AIConfig.LipSyncEnabled`（默认 true）；关闭后嘴巴不动、表情正常。
- 证据与已知限制见 `内部证据（未包含）`。
