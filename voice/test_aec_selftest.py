#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
AEC 自测（无声、合成回声）—— 不需要扬声器，可随时跑。

方法：
  参考信号 ref（类语音调制信号）
  回声 echo = ref 延迟 60ms ×0.3 + 低通
  近端用户 user = 1.5~2.5s 的 500Hz 单音
  mic = echo + user + 少量噪声
  用 pyaec（AEC 库）对齐参考后做回声消除，计算 ERLE。

运行：
  python Tools/Voice/test_aec_selftest.py
结果写入 Tools/_verify/p5_aec_selftest.txt
"""
import pathlib
import sys

import numpy as np

try:
    import pyaec
except Exception as e:  # noqa: BLE001
    sys.exit(f"pyaec 未安装: {e}")

SR = 16000
FRAME = 320
TAPS = 1024
OUT = pathlib.Path(__file__).resolve().parents[2] / "Tools/_verify/p5_aec_selftest.txt"


def to_i16(x):
    return np.clip(x * 32767, -32768, 32767).astype(np.int16)


def run(mic, ref):
    aec = pyaec.Aec(FRAME, TAPS, SR, False)   # 预处理关闭：只消回声，避免把用户语音当噪声
    out = np.zeros(len(mic), dtype=np.float32)
    for i in range(0, len(mic) - FRAME, FRAME):
        r = to_i16(ref[i:i + FRAME])
        m = to_i16(mic[i:i + FRAME])
        y = aec.cancel_echo(m.tolist(), r.tolist())
        out[i:i + FRAME] = np.array(y, dtype=np.float32) / 32768.0
    return out


def main():
    rng = np.random.default_rng(0)
    t = np.arange(SR * 4)
    ref = (0.2 * np.sin(2 * np.pi * 220 * t / SR) * (0.6 + 0.4 * np.sin(2 * np.pi * 3 * t / SR))
           + 0.05 * rng.standard_normal(len(t))).astype(np.float32)
    delay = 960  # 60ms
    echo = np.zeros_like(ref)
    echo[delay:] = ref[:-delay] * 0.3
    echo = np.convolve(echo, [0.3, 0.5, 0.2], mode="same").astype(np.float32)
    # 语音状用户信号（带限调制噪声，比纯音更接近人声）
    u = rng.standard_normal(len(t)).astype(np.float32)
    u = np.convolve(u, np.ones(16) / 16, mode="same")
    u *= (0.5 + 0.5 * np.sin(2 * np.pi * 4 * t / SR))
    user = np.zeros_like(ref)
    user[SR * 2:SR * 3] = 0.25 * u[SR * 2:SR * 3] / max(np.max(np.abs(u[SR * 2:SR * 3])), 1e-9)
    mic = (echo + user + 0.002 * rng.standard_normal(len(t))).astype(np.float32)

    aligned_ref = np.zeros_like(ref)
    aligned_ref[delay:] = ref[:-delay]

    err_raw = run(mic, ref)
    err = run(mic, aligned_ref)
    eslice = slice(SR // 2, SR * 2)          # 回声段（用户未说话）
    uslice = slice(SR * 2 + 2000, SR * 3)    # 用户段
    erle_raw = 10 * np.log10(np.mean(mic[eslice] ** 2) / max(np.mean(err_raw[eslice] ** 2), 1e-12))
    erle = 10 * np.log10(np.mean(mic[eslice] ** 2) / max(np.mean(err[eslice] ** 2), 1e-12))
    keep = np.mean(err[uslice] ** 2) / max(np.mean(mic[uslice] ** 2), 1e-12)

    lines = [
        "AEC 自测（pyaec, 合成回声, 无声, 预处理关闭）",
        f"sample_rate={SR} frame={FRAME} taps={TAPS} delay=60ms echo_gain=0.3",
        f"ERLE(未对齐参考) = {erle_raw:.2f} dB",
        f"ERLE(对齐参考)   = {erle:.2f} dB",
        f"用户段能量保留率 = {keep:.3f}（1.0=完全保留；>0.2 即可被 VAD 检出）",
        "判定：对齐后 ERLE ≥ 10dB 即通过；用户段保留 >0.2 说明没把用户语音当回声消掉。",
        "备注：本机实测扬声器→麦克风回声很弱（~-30dB，被麦克风阵列/系统处理过），",
        "      运行时 calibrate() 会自动旁路 AEC（echo_gain < 0.015 时），避免误伤用户语音。",
    ]
    text = "\n".join(lines)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(text, encoding="utf-8")
    print(text)
    print("saved", OUT)


if __name__ == "__main__":
    main()
