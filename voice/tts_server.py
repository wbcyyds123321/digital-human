#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
本地 TTS 小服务（M1）—— 纯标准库，无第三方依赖。

职责：
  POST /tts    {"text":"...", "emotion":"smile", "demeanor":"shy", "intensity":0.6,
                "voice_id":"zhuangfy001", "engine":"minimax"}
              → 返回 audio/mpeg（MiniMax speech-2.8-turbo），响应头带 X-Duration-Ms。
  GET  /health  → {"ok":true,"engine":"minimax","voice_id":"...","key_loaded":true}

情绪映射：BehaviorPlan.emotion 是 ExpressionLibrary 的 15 个表情 id，demeanor 是 10 个神态；
  MiniMax 只支持 neutral/happy/sad/angry/fearful/disgusted/surprised，
  所以在这里做「表情/神态 → 语音情绪 + 语速/音量微调」的映射（数据在下方表里，不写死在 Unity）。

API Key 读取：环境变量 MINIMAX_API_KEY，或
  %USERPROFILE%\\AppData\\LocalLow\\DefaultCompany\\Become human\\minimax_key.txt
Key 不会打印、不会写日志。

启动：双击 Tools/Voice/start_tts.bat，或
  python Tools/Voice/tts_server.py [--port 8766]
"""

import argparse
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

BASE = "https://api.minimax.cn"
DEFAULT_VOICE = os.environ.get("MINIMAX_VOICE_ID", "zhuangfy001")
DEFAULT_MODEL = "speech-2.8-turbo"
KEY_FILE = Path(os.path.expanduser("~")) / "AppData/LocalLow/DefaultCompany/Become human/minimax_key.txt"
CACHE_DIR = Path(__file__).resolve().parent / "cache"
USE_CACHE = os.environ.get("TTS_NO_CACHE", "") != "1"

# ExpressionLibrary 15 个表情 id → (MiniMax 情绪, 语速倍率, 音量倍率)
EXPRESSION_MAP = {
    "smile":     ("happy",     1.02, 1.00),
    "laugh":     ("happy",     1.05, 1.03),
    "grin":      ("happy",     1.03, 1.02),
    "smug":      ("happy",     1.00, 1.00),
    "wink":      ("happy",     1.02, 1.00),
    "winkR":     ("happy",     1.02, 1.00),
    "angry":     ("angry",     1.05, 1.05),
    "rage":      ("angry",     1.08, 1.08),
    "sad":       ("sad",       0.92, 0.92),
    "troubled":  ("sad",       0.94, 0.94),
    "surprised": ("surprised", 1.03, 1.02),
    "shock":     ("surprised", 1.06, 1.05),
    "serious":   ("neutral",   0.95, 0.97),
    "shy":       ("neutral",   0.92, 0.85),   # MiniMax 无 shy：压低语速/音量表达"害羞"
    "neutral":   ("neutral",   1.00, 1.00),
}

# DemeanorCatalog 10 个神态 → 同结构；expression=neutral 时才看神态
DEMEANOR_MAP = {
    "attentive": ("neutral",   0.98, 1.00),
    "relaxed":   ("neutral",   1.00, 1.00),
    "wary":      ("neutral",   1.02, 1.00),
    "happy":     ("happy",     1.03, 1.02),
    "confident": ("neutral",   1.03, 1.02),   # 沉稳自信，不硬套 happy
    "shy":       ("neutral",   0.92, 0.85),
    "thinking":  ("neutral",   0.90, 0.92),   # 慢一点、轻一点，像在斟酌
    "down":      ("sad",       0.92, 0.92),
    "tired":     ("sad",       0.90, 0.88),
    "surprised": ("surprised", 1.03, 1.02),
}

VALID_EMOTIONS = ("neutral", "happy", "sad", "angry", "fearful", "disgusted", "surprised")


def load_key():
    key = os.environ.get("MINIMAX_API_KEY", "").strip()
    if key:
        return key
    try:
        if KEY_FILE.exists():
            return KEY_FILE.read_text(encoding="utf-8-sig").strip()
    except Exception as e:  # noqa: BLE001
        log(f"[!] 读 key 文件失败: {e}")
    return ""


def log(msg):
    print(time.strftime("[%H:%M:%S] ") + msg, flush=True)


def resolve_voice_params(emotion, demeanor, intensity):
    """返回 (minimax_emotion, speed, vol)。emotion 非 neutral 优先，否则看 demeanor。"""
    emo = (emotion or "").strip()
    dem = (demeanor or "").strip()

    base = None
    if emo and emo.lower() != "neutral" and emo in EXPRESSION_MAP:
        base = EXPRESSION_MAP[emo]
    elif dem and dem in DEMEANOR_MAP:
        base = DEMEANOR_MAP[dem]
    elif emo in EXPRESSION_MAP:
        base = EXPRESSION_MAP[emo]
    else:
        base = ("neutral", 1.00, 1.00)

    mm_emotion, speed_mul, vol_mul = base
    if mm_emotion not in VALID_EMOTIONS:
        mm_emotion = "neutral"

    try:
        k = float(intensity)
    except (TypeError, ValueError):
        k = 0.6
    k = max(0.0, min(1.0, k))
    speed = max(0.85, min(1.15, speed_mul * (1.0 + 0.08 * (k - 0.5))))
    vol = max(0.7, min(1.0, vol_mul * (0.95 + 0.10 * k)))
    return mm_emotion, round(speed, 2), round(vol, 2)


def minimax_stream(text, voice_id, emotion, speed, vol, on_audio, timeout=25):
    """MiniMax 流式 TTS：SSE 逐块返回 PCM(32k/16bit/mono)，每块回调 on_audio(bytes)。

    注意：音频格式用 pcm（不是 mp3），这样块边界就是原始采样，播放端可以直接边收边播。
    返回 (chunks, bytes) 用于日志。
    """
    key = load_key()
    if not key:
        raise RuntimeError("没有 API Key")
    payload = {
        "model": DEFAULT_MODEL,
        "text": text,
        "stream": True,
        "voice_setting": {
            "voice_id": voice_id,
            "speed": speed,
            "vol": vol,
            "pitch": 0,
            "emotion": emotion,
        },
        "audio_setting": {"sample_rate": 32000, "bitrate": 128000, "format": "pcm", "channel": 1},
        "language_boost": "Chinese",
    }
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        BASE + "/v1/t2a_v2", data=data, method="POST",
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json",
                 "Accept": "text/event-stream"},
    )
    chunks = 0
    total = 0
    acc = bytearray()                 # 已输出的音频（用于识别累计/重复块）
    skipped = 0
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            for raw in r:
                line = raw.decode("utf-8", "replace").strip()
                if not line.startswith("data:"):
                    continue
                payload_s = line[5:].strip()
                if payload_s in ("[DONE]", ""):
                    continue
                try:
                    obj = json.loads(payload_s)
                except Exception:
                    continue
                br = obj.get("base_resp") or {}
                code = br.get("status_code", 0)
                if code:
                    raise RuntimeError(f"MiniMax status_code={code} {br.get('status_msg', '')}")
                audio = (obj.get("data") or {}).get("audio")
                if not audio:
                    continue
                try:
                    b = bytes.fromhex(audio)
                except ValueError:
                    continue
                if not b:
                    continue

                # ★ MiniMax 流式协议：
                #   - 正常块：与已输出内容不重叠 → 直接播；
                #   - 累计块（包含前面全部内容）：只播新增后缀；
                #   - 结尾常会再发一份"整段完整音频"（与已收到内容完全相同）→ 跳过，避免复读。
                if acc:
                    prev = bytes(acc)
                    if b == prev:
                        skipped += 1
                        continue
                    if len(b) > len(prev) and b.startswith(prev):
                        b = b[len(prev):]
                    elif len(b) < len(prev) and prev.endswith(b):
                        skipped += 1
                        continue

                on_audio(b)
                acc.extend(b)
                chunks += 1
                total += len(b)
    except Exception as e:  # noqa: BLE001
        raise RuntimeError(f"流式网络错误: {e}") from e
    if skipped:
        log(f"[TTS-STREAM] 跳过重复/累计块 {skipped} 个（避免复读）")
    return chunks, total


def minimax_synth(text, voice_id, emotion, speed, vol, timeout=8):
    key = load_key()
    if not key:
        raise RuntimeError("没有 API Key（环境变量 MINIMAX_API_KEY 或 minimax_key.txt）")

    payload = {
        "model": DEFAULT_MODEL,
        "text": text,
        "stream": False,
        "voice_setting": {
            "voice_id": voice_id,
            "speed": speed,
            "vol": vol,
            "pitch": 0,
            "emotion": emotion,
        },
        "audio_setting": {"sample_rate": 32000, "bitrate": 128000, "format": "mp3", "channel": 1},
        "language_boost": "Chinese",
    }
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        BASE + "/v1/t2a_v2", data=data, method="POST",
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            obj = json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")[:300]
        raise RuntimeError(f"HTTP {e.code}: {body}") from e
    except Exception as e:  # noqa: BLE001
        raise RuntimeError(f"网络错误: {e}") from e

    br = obj.get("base_resp") or {}
    code = br.get("status_code", 0)
    if code:
        raise RuntimeError(f"MiniMax status_code={code} {br.get('status_msg', '')}")
    audio_hex = (obj.get("data") or {}).get("audio")
    if not audio_hex:
        raise RuntimeError("MiniMax 没返回 audio")
    info = obj.get("extra_info") or {}
    return bytes.fromhex(audio_hex), int(info.get("audio_length", 0)), int(info.get("audio_size", 0))


def cache_path(text, voice_id, emotion, speed, vol):
    raw = f"{text}|{voice_id}|{emotion}|{speed}|{vol}|{DEFAULT_MODEL}".encode("utf-8")
    return CACHE_DIR / (hashlib.sha1(raw).hexdigest() + ".mp3")


class Handler(BaseHTTPRequestHandler):
    server_version = "YixuanTTS/1.0"

    def log_message(self, fmt, *args):  # 关掉默认访问日志，改成自己的简洁日志
        pass

    def _send_json(self, code, obj):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(body)

    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.end_headers()

    def do_GET(self):
        if self.path.startswith("/health"):
            self._send_json(200, {
                "ok": True,
                "service": "yixuan-tts",
                "engine": "minimax",
                "voice_id": DEFAULT_VOICE,
                "model": DEFAULT_MODEL,
                "key_loaded": bool(load_key()),
                "cache": USE_CACHE,
            })
            return
        self._send_json(404, {"ok": False, "error": "not found"})

    def do_POST(self):
        if not self.path.startswith("/tts"):
            self._send_json(404, {"ok": False, "error": "not found"})
            return

        t0 = time.time()
        try:
            n = int(self.headers.get("Content-Length", "0"))
            body = json.loads(self.rfile.read(n).decode("utf-8") or "{}")
        except Exception as e:  # noqa: BLE001
            self._send_json(400, {"ok": False, "error": f"bad json: {e}"})
            return

        text = (body.get("text") or "").strip()
        engine = (body.get("engine") or "minimax").strip().lower()
        voice_id = (body.get("voice_id") or DEFAULT_VOICE).strip()
        expression = (body.get("emotion") or "").strip()
        demeanor = (body.get("demeanor") or "").strip()
        try:
            intensity = float(body.get("intensity", 0.6))
        except (TypeError, ValueError):
            intensity = 0.6

        if not text:
            self._send_json(400, {"ok": False, "error": "text 为空"})
            return
        if len(text) > 1000:
            text = text[:1000]

        mm_emotion, speed, vol = resolve_voice_params(expression, demeanor, intensity)

        if engine != "minimax":
            self._send_json(503, {"ok": False, "error": f"引擎 {engine} 未接入（当前仅 minimax）"})
            return

        try:
            cp = cache_path(text, voice_id, mm_emotion, speed, vol)
            if USE_CACHE and cp.exists():
                audio = cp.read_bytes()
                dur = 0
                log(f"[TTS] cache hit  {len(text):3d}字 {expression or '-'}→{mm_emotion}  {len(audio)}B")
            else:
                audio, dur, _size = minimax_synth(text, voice_id, mm_emotion, speed, vol)
                if USE_CACHE:
                    try:
                        CACHE_DIR.mkdir(parents=True, exist_ok=True)
                        cp.write_bytes(audio)
                    except Exception as e:  # noqa: BLE001
                        log(f"[!] 写缓存失败: {e}")
                log(f"[TTS] ok        {len(text):3d}字 {expression or '-'}→{mm_emotion} "
                    f"speed={speed} vol={vol}  {len(audio)}B  {time.time()-t0:.2f}s")
        except Exception as e:  # noqa: BLE001
            log(f"[TTS] FAIL     {e}")
            self._send_json(503, {"ok": False, "error": str(e)})
            return

        self.send_response(200)
        self.send_header("Content-Type", "audio/mpeg")
        self.send_header("Content-Length", str(len(audio)))
        self.send_header("X-Duration-Ms", str(dur))
        self.send_header("X-Engine", "minimax")
        self.send_header("X-Emotion", mm_emotion)
        self.send_header("X-Voice-Id", voice_id)
        self.send_header("X-Speed", str(speed))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(audio)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=int(os.environ.get("TTS_PORT", "8766")))
    args = ap.parse_args()

    log("Yixuan TTS sidecar")
    log(f"  key: {'已加载' if load_key() else '未找到（/tts 会返回 503）'}")
    log(f"  voice_id: {DEFAULT_VOICE}  model: {DEFAULT_MODEL}  cache: {USE_CACHE}")
    log(f"  http://{args.host}:{args.port}   (POST /tts, GET /health)")
    srv = ThreadingHTTPServer((args.host, args.port), Handler)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        log("bye")
        srv.shutdown()


if __name__ == "__main__":
    main()
