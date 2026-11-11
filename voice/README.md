# Yixuan voice sidecar

独立 Python 语音 sidecar：麦克风采集、VAD、ASR、流式 TTS、AEC 和口型数据输出。

## 端口

- `8766`：TTS 合成服务（可选，Unity 侧可回退）
- `8767`：全双工服务（麦克风 + VAD + ASR + AEC + 打断）

## 启动

```bat
cd Tools\Voice
start_tts.bat
start_voice.bat
```

请先准备 Python 3.11+ 依赖，或在本目录创建 `.venv314` / `.venv`。启动脚本会优先使用项目虚拟环境，其次使用系统 Python；不会硬编码本机路径。

## 测试

```bat
python test_voice_pipeline.py
python test_aec_selftest.py
python test_voice_utterance.py
python test_voice_playback_generation.py
```

测试使用本地/模拟音频，不代表真人麦克风、口音、环境噪声或主观听感验收。

## 配置

模型/服务凭据通过环境变量或本机配置文件提供；**不要提交 API Key、录音、模型或缓存**。Unity 侧 visual provider 与 key 配置独立，详见 README 和 `docs/progress-2026-11-11.md`。
