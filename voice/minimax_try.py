#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
MiniMax 语音试跑脚本（只用 Python 标准库，不需要 pip install）

流程：
  1) 上传参考音频（purpose=voice_clone）→ file_id
  2) 调用 /v1/voice_clone 克隆音色 → voice_id（首次会消耗 9.9 元/音色）
  3) 调用 /v1/t2a_v2 按 7 种情绪各合成一句 → 保存 mp3 到 Tools/_verify/minimax/

用法（PowerShell）：
  $py = 'python'
  & $py 'Tools\\Voice\\minimax_try.py' --ref 'Tools\\TTS\\voices\\zhuangfy\\ref.mp3'

API Key 读取优先级：
  1) --key-file 指定文件
  2) 环境变量 MINIMAX_API_KEY
  3) %USERPROFILE%\\AppData\\LocalLow\\DefaultCompany\\Become human\\minimax_key.txt

注意：
  - 参考音频要求：mp3/m4a/wav，10 秒 ~ 5 分钟，<= 20MB，建议 15~60 秒干净人声（无 BGM）
  - 克隆前需要在 platform.minimax.cn 完成「个人认证」，否则报 2038
  - 克隆出的音色若 7 天内未正式调用会被删除（本脚本第 3 步会调用，所以没问题）
"""
import argparse
import json
import os
import random
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

BASE = "https://api.minimax.cn"
DEFAULT_KEY = Path(os.environ.get("USERPROFILE", "~")) / "AppData/LocalLow/DefaultCompany/Become human/minimax_key.txt"
ROOT = Path(__file__).resolve().parents[2]          # Tools/Voice/minimax_try.py -> 项目根
OUT_DIR = ROOT / "Tools/_verify/minimax"
EMOTIONS = ["neutral", "happy", "sad", "angry", "fearful", "disgusted", "surprised"]
TEST_TEXT = "好，我站好了。看这样还行吧？你刚才说的那些，我都记着呢。"


def log(msg):
    print(msg, flush=True)


def load_key(args):
    if args.key_file:
        p = Path(args.key_file)
        if not p.exists():
            sys.exit(f"[X] key 文件不存在: {p}")
        return p.read_text(encoding="utf-8-sig").strip()
    env = os.environ.get("MINIMAX_API_KEY", "").strip()
    if env:
        return env
    if DEFAULT_KEY.exists():
        return DEFAULT_KEY.read_text(encoding="utf-8-sig").strip()
    sys.exit(
        "[X] 找不到 API Key。请任选一种：\n"
        f"    - 把 key 写进 {DEFAULT_KEY}\n"
        "    - 设置环境变量 MINIMAX_API_KEY\n"
        "    - 运行参数 --key-file <文件路径>"
    )


def http_json(path, payload, key, timeout=300):
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        BASE + path, data=data, method="POST",
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        sys.exit(f"[X] HTTP {e.code} {path}\n{body}")
    except urllib.error.URLError as e:
        sys.exit(f"[X] 网络错误 {path}: {e}")


def http_upload(path, file_path, purpose, key, timeout=300):
    boundary = "----DSH" + "".join(random.choice("0123456789abcdef") for _ in range(24))
    file_bytes = Path(file_path).read_bytes()
    filename = Path(file_path).name
    ctype = "audio/mpeg" if filename.lower().endswith(".mp3") else "application/octet-stream"
    buf = b""
    for name, value in (("purpose", purpose),):
        buf += f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n".encode()
    buf += (
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n"
        f"Content-Type: {ctype}\r\n\r\n"
    ).encode("utf-8") + file_bytes + b"\r\n"
    buf += f"--{boundary}--\r\n".encode()

    req = urllib.request.Request(
        BASE + path, data=buf, method="POST",
        headers={
            "Authorization": f"Bearer {key}",
            "Content-Type": f"multipart/form-data; boundary={boundary}",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        sys.exit(f"[X] HTTP {e.code} {path}\n{body}")


def find_key(obj, target):
    """在嵌套 JSON 里找第一个 target 字段."""
    if isinstance(obj, dict):
        if target in obj:
            return obj[target]
        for v in obj.values():
            r = find_key(v, target)
            if r is not None:
                return r
    elif isinstance(obj, list):
        for v in obj:
            r = find_key(v, target)
            if r is not None:
                return r
    return None


def check_base_resp(resp, stage):
    br = resp.get("base_resp") or find_key(resp, "base_resp") or {}
    code = br.get("status_code", 0) if isinstance(br, dict) else 0
    msg = br.get("status_msg", "") if isinstance(br, dict) else ""
    if code not in (0, None):
        hints = {
            1004: "鉴权失败：检查 API Key 是否正确/是否过期",
            1002: "触发限流：等几秒再试",
            2013: "输入格式不正常：检查音频格式/时长（10s~5min、mp3/m4a/wav、<=20MB）",
            2038: "无复刻权限：先在 platform.minimax.cn 完成个人/企业认证",
        }
        sys.exit(f"[X] {stage}失败: status_code={code} {msg}\n    {hints.get(code, '详见错误码文档')}")
    return resp


def upload_reference(path, purpose, key):
    log(f"[1] 上传音频: {path} (purpose={purpose})")
    resp = http_upload("/v1/files/upload", path, purpose, key)
    fid = find_key(resp, "file_id")
    if fid is None:
        sys.exit(f"[X] 上传失败，响应里没有 file_id:\n{json.dumps(resp, ensure_ascii=False)[:800]}")
    log(f"    file_id = {fid}")
    return fid


def clone_voice(file_id, voice_id, key, args):
    payload = {
        "file_id": file_id,
        "voice_id": voice_id,
        "model": args.clone_model,
        "text": args.clone_text,
    }
    if args.prompt_audio:
        prompt_id = upload_reference(args.prompt_audio, "prompt_audio", key)
        payload["clone_prompt"] = {"prompt_audio": prompt_id, "prompt_text": args.prompt_text or ""}
    log(f"[2] 克隆音色 voice_id={voice_id}（首次约 9.9 元）")
    resp = http_json("/v1/voice_clone", payload, key)
    check_base_resp(resp, "音色克隆")
    log("    响应: " + json.dumps(resp, ensure_ascii=False)[:1000])
    return resp


def tts(text, voice_id, emotion, key, model):
    payload = {
        "model": model,
        "text": text,
        "stream": False,
        "voice_setting": {
            "voice_id": voice_id,
            "speed": 1.0,
            "vol": 1.0,
            "pitch": 0,
            "emotion": emotion,
        },
        "audio_setting": {"sample_rate": 32000, "bitrate": 128000, "format": "mp3", "channel": 1},
        "language_boost": "Chinese",
    }
    resp = http_json("/v1/t2a_v2", payload, key)
    check_base_resp(resp, f"语音合成({emotion})")
    hex_audio = find_key(resp, "audio")
    if not hex_audio:
        sys.exit(f"[X] 合成({emotion})没拿到 audio: {json.dumps(resp, ensure_ascii=False)[:600]}")
    return bytes.fromhex(hex_audio)


def main():
    p = argparse.ArgumentParser(description="MiniMax 音色克隆 + 情绪语音试跑")
    p.add_argument("--ref", help="参考音频路径（10s~5min 干净人声）")
    p.add_argument("--prompt-audio", help="可选：<8s 示例音频，提升相似度")
    p.add_argument("--prompt-text", default="", help="示例音频对应的文本（需与音频一致）")
    p.add_argument("--voice-id", default="zhuangfy001", help="自定义 voice_id（8~256 字符，字母开头）")
    p.add_argument("--text", default=TEST_TEXT, help="测试合成文本")
    p.add_argument("--clone-model", default="speech-2.8-hd", help="克隆试听所用模型")
    p.add_argument("--model", default="speech-2.8-turbo", help="TTS 模型（turbo 更便宜）")
    p.add_argument("--clone-text", default="你好，我是庄方宜。今天想聊点什么？",
                   help="克隆试听文本（<=1000 字符）")
    p.add_argument("--key-file", help="API Key 文件路径")
    p.add_argument("--skip-clone", action="store_true", help="跳过克隆，直接用已有 voice_id 合成")
    p.add_argument("--emotions", default=",".join(EMOTIONS), help="要生成的情绪，逗号分隔")
    args = p.parse_args()

    key = load_key(args)
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    log(f"API Key: {key[:6]}...{key[-4:]}（只显示前后几字符）")

    voice_id = args.voice_id
    if not args.skip_clone:
        if not args.ref:
            sys.exit("[X] 需要 --ref 参考音频路径（或加 --skip-clone 用已有音色）")
        if not Path(args.ref).exists():
            sys.exit(f"[X] 参考音频不存在: {args.ref}")
        file_id = upload_reference(args.ref, "voice_clone", key)
        clone_voice(file_id, voice_id, key, args)

    emotions = [e.strip() for e in args.emotions.split(",") if e.strip()]
    log(f"[3] 合成 {len(emotions)} 条情绪音频（voice_id={voice_id}, model={args.model}）")
    ok, fail = [], []
    for emo in emotions:
        try:
            audio = tts(args.text, voice_id, emo, key, args.model)
            out = OUT_DIR / f"{voice_id}_{emo}.mp3"
            out.write_bytes(audio)
            ok.append(out)
            log(f"    OK  {emo:9s} -> {out.name}  ({len(audio)} bytes)")
        except SystemExit as e:
            fail.append((emo, str(e)))
            log(f"    FAIL {emo}: {e}")
        time.sleep(0.5)

    log("\n=== 结果 ===")
    for f in ok:
        log(f"  {f}")
    if fail:
        log(f"  失败 {len(fail)} 条（情绪名可能不被支持，可换一个再试）")
    log(f"\n把上面的 mp3 用播放器听一下；满意的话把 voice_id 和 key 放置情况告诉我，我接入 Unity。")


if __name__ == "__main__":
    main()
