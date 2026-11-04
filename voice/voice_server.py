#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
P5 全双工语音服务（外放 + AEC + 打断）—— M1 的 tts_server 之上加"耳朵和嘴"。

职责：
  POST /say         {text, voice_id?, emotion?, demeanor?, intensity?} → 合成并播放（TTS 播放搬到这里，为了 AEC 参考信号）
  POST /cancel      停止当前播放
  POST /calibrate   播放 1.2s 噪声估算"扬声器→麦克风"延迟（给 AEC 对齐）
  POST /inject_user {path}  测试用：把一段 WAV 当成"用户近端语音"注入（不走扬声器，用于验证打断链路）
  GET  /events?cursor=N&timeout=1.0  长轮询事件：vad.start / vad.end / asr.partial(暂不) / asr.final / tts.start / tts.end / barge_in / error
  GET  /health      状态
  GET  /devices     音频设备列表

链路：
  mic(48k, device 1) → 降采样 16k → AEC(pyaec, 参考=已播放 PCM) → Silero VAD →
    - 静默时：VAD 段落 → SenseVoice ASR → asr.final
    - AI 播放中：VAD 检测到用户开口 → 立刻停播 + barge_in 事件 → 继续收段落 → asr.final
  TTS：MiniMax（复用 tts_server.minimax_synth 与缓存）→ mp3 解码成 48k 播放 + 16k 参考

只用标准库 + sounddevice + numpy + sherpa-onnx + miniaudio + pyaec。
"""

import argparse
import array
import collections
import json
import os
import queue
import struct
import sys
import threading
import time
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor

import numpy as np
import sounddevice as sd
import miniaudio
import sherpa_onnx as so
import pyaec
from voice_utterance import PCMHistory, UtteranceBuffer

HERE = Path(__file__).resolve().parent
MODELS = HERE / "models"
SENSEVOICE_DIR = MODELS / "sense-voice/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09"
SILERO = MODELS / "silero_vad.onnx"

# 音频参数：48k 采集/播放（实测 16k 会被系统处理掉回声），16k 处理
CAP_SR = 48000
PROC_SR = 16000
# MiniMax 并发流上限（Claude 建议 2）：句数多时避免被限流导致后半段 0 音频
TTS_STREAM_SEM = threading.Semaphore(int(os.environ.get("TTS_STREAM_CONCURRENCY", "2")))
BLOCK48 = 960      # 20ms @48k
BLOCK16 = 320      # 20ms @16k
FRAME = 320        # pyaec 帧
TAPS = 1024        # 64ms 回声路径
MIC_DEVICE = int(os.environ.get("VOICE_INPUT_DEVICE", "1"))
SPK_DEVICE = int(os.environ.get("VOICE_OUTPUT_DEVICE", "3"))
VOICE_ID = os.environ.get("MINIMAX_VOICE_ID", "zhuangfy001")


def log(msg):
    print(time.strftime("[%H:%M:%S] ") + msg, flush=True)


def dec3(x48):
    """48k → 16k：低通 + 3:1 抽取。"""
    h = np.sinc(np.linspace(-6, 6, 49)) * np.hamming(49)
    h = (h / h.sum()).astype(np.float32)
    y = np.convolve(x48.astype(np.float32), h, mode="same")
    return y[::3].astype(np.float32)


def load_wav_16k(path):
    d = miniaudio.decode_file(str(path), output_format=miniaudio.SampleFormat.FLOAT32,
                              nchannels=1, sample_rate=PROC_SR)
    return np.array(d.samples, dtype=np.float32)


class MouthTracker:
    """播放时把"张口度 + 五元音权重"通过 UDP 推给 Unity（口型同步）。

    packet: <I f f f f f f  => seq, rms, wA, wI, wU, wE, wO  (28 bytes)
    UDP 目标：127.0.0.1:8770（VOICE_MOUTH_PORT 可覆盖）
    说明：元音用 F1/F2 峰值 + 原型距离估计；识别不稳时自然退化为"张口度为主"。
    """

    PROTOS = [
        ("a", 800.0, 1200.0),
        ("i", 300.0, 2300.0),
        ("u", 350.0, 1500.0),
        ("e", 550.0, 1900.0),
        ("o", 500.0, 900.0),
    ]

    def __init__(self):
        import socket
        self.enabled = os.environ.get("VOICE_LIPSYNC", "1") != "0"
        self.addr = ("127.0.0.1", int(os.environ.get("VOICE_MOUTH_PORT", "8770")))
        self.sock = None
        self.seq = 0
        self.sent = 0
        self._smooth = np.zeros(5, dtype=np.float32)
        if self.enabled:
            try:
                self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            except Exception as e:  # noqa: BLE001
                log(f"[MOUTH] UDP socket 创建失败：{e}")

    @staticmethod
    def _formants(freqs, mag):
        mask = (freqs >= 200.0) & (freqs <= 3500.0)
        f = freqs[mask]
        s = mag[mask]
        if len(s) < 8:
            return 0.0, 0.0
        s = np.convolve(s, np.ones(3) / 3.0, mode="same")
        peaks = []
        for i in range(2, len(s) - 2):
            if s[i] > s[i - 1] and s[i] >= s[i + 1] and s[i] > s[i - 2] and s[i] >= s[i + 2]:
                peaks.append((float(f[i]), float(s[i])))
        if not peaks:
            return 0.0, 0.0
        mx = max(p[1] for p in peaks)
        peaks = [p for p in peaks if p[1] >= 0.15 * mx]
        peaks.sort(key=lambda p: p[0])
        if len(peaks) >= 2:
            return peaks[0][0], peaks[1][0]
        return peaks[0][0], 0.0

    def feed(self, block48):
        if not self.enabled or self.sock is None or len(block48) < 64:
            return
        try:
            x = block48.astype(np.float32)
            rms = float(np.sqrt(np.mean(x * x)))
            weights = np.zeros(5, dtype=np.float32)

            if rms >= 0.006:
                win = np.hanning(len(x)).astype(np.float32)
                spec = np.abs(np.fft.rfft(x * win))
                freqs = np.fft.rfftfreq(len(x), 1.0 / CAP_SR)
                f1, f2 = self._formants(freqs, spec)

                if f1 > 0 and f2 > 0:
                    ds = []
                    for _name, p1, p2 in self.PROTOS:
                        ds.append(((f1 - p1) / 400.0) ** 2 + ((f2 - p2) / 600.0) ** 2)
                    ds = np.array(ds, dtype=np.float32)
                    w = np.exp(-ds)
                    if float(w.sum()) > 1e-6:
                        weights = (w / w.sum()).astype(np.float32)
                else:
                    # 兜底：按频谱质心粗分宽/窄口型
                    centroid = float(np.sum(freqs * spec) / (np.sum(spec) + 1e-9))
                    if centroid >= 1700:
                        weights = np.array([0.1, 0.45, 0.0, 0.35, 0.1], dtype=np.float32)
                    elif centroid >= 900:
                        weights = np.array([0.1, 0.0, 0.15, 0.55, 0.2], dtype=np.float32)
                    else:
                        weights = np.array([0.45, 0.0, 0.3, 0.0, 0.25], dtype=np.float32)

            openness = float(np.clip((rms - 0.004) / 0.055, 0.0, 1.0))
            target = weights * openness
            self._smooth = 0.55 * self._smooth + 0.45 * target
            packet = struct.pack("<Iffffff", self.seq, rms,
                                 float(self._smooth[0]), float(self._smooth[1]),
                                 float(self._smooth[2]), float(self._smooth[3]),
                                 float(self._smooth[4]))
            self.sock.sendto(packet, self.addr)
            self.seq += 1
            self.sent += 1
        except Exception:
            pass

    def silence(self):
        if not self.enabled or self.sock is None:
            return
        try:
            self._smooth = np.zeros(5, dtype=np.float32)
            packet = struct.pack("<Iffffff", self.seq, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0)
            self.sock.sendto(packet, self.addr)
            self.seq += 1
            self.sent += 1
        except Exception:
            pass


class StreamSentence:
    """一句话的 MiniMax 流式 PCM 生产器。

    创建即开始请求（SSE 逐块 PCM 32k），把块重采样到 48k 放进 audio_q；
    播放线程边取边播。失败时回退整句合成，保证不会因为流断而完全没声音。
    """

    def __init__(self, server, text, voice_id, emotion, demeanor, intensity,
                 mm_emotion, speed, vol, sentence_id, is_last):
        self.server = server
        self.text = text
        self.voice_id = voice_id
        self.emotion = emotion
        self.demeanor = demeanor
        self.intensity = intensity
        self.mm_emotion = mm_emotion
        self.speed = speed
        self.vol = vol
        self.sentence_id = sentence_id
        self.is_last = is_last
        self.audio_q = queue.Queue()
        self.done = False
        self.error = None
        self.stop = threading.Event()
        self.first_ms = 0
        self.t0 = time.time()
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self):
        t0 = self.t0
        try:
            def on_audio(b):
                if self.stop.is_set():
                    return
                pcm32 = np.frombuffer(b, dtype="<i2").astype(np.float32) / 32768.0
                if len(pcm32) == 0:
                    return
                if self.first_ms == 0:
                    self.first_ms = int((time.time() - t0) * 1000)
                n_out = max(1, int(len(pcm32) * CAP_SR / 32000))
                pcm48 = np.interp(np.linspace(0, len(pcm32) - 1, n_out),
                                  np.arange(len(pcm32)), pcm32).astype(np.float32)
                self.audio_q.put(pcm48)

            with TTS_STREAM_SEM:
                chunks, total = self.server.tts_mod.minimax_stream(
                    self.text, self.voice_id, self.mm_emotion, self.speed, self.vol,
                    on_audio, timeout=10)
            if chunks == 0 or total == 0:
                raise RuntimeError("流式返回 0 音频（可能被限流）")
            log(f"[TTS-STREAM] sentence#{self.sentence_id} {len(self.text)}字 chunks={chunks} "
                f"bytes={total} first={self.first_ms}ms total={int((time.time()-t0)*1000)}ms")
        except Exception as e:  # noqa: BLE001
            self.error = e
            # 已经播出去一部分后再回退整句 = 复读；只在"一个字都没出"时回退
            if self.first_ms == 0:
                log(f"[TTS-STREAM] sentence#{self.sentence_id} 流失败（无音频），回退整句：{e}")
                if not self.stop.is_set():
                    try:
                        mp3 = self.server.synth(self.text, self.voice_id, self.emotion,
                                               self.demeanor, self.intensity)
                        pcm48, _ = self.server._decode_mp3(mp3)
                        self.audio_q.put(pcm48)
                    except Exception as e2:  # noqa: BLE001
                        self.error = e2
                        log(f"[TTS-STREAM] sentence#{self.sentence_id} 回退也失败：{e2}")
            else:
                log(f"[TTS-STREAM] sentence#{self.sentence_id} 流中途失败，保留已播部分（不回退）：{e}")
        finally:
            self.done = True


class VoiceServer:
    def __init__(self, autoreload_tts=True):
        log("加载模型...")
        t0 = time.time()
        self.recognizer = so.OfflineRecognizer.from_sense_voice(
            model=str(SENSEVOICE_DIR / "model.int8.onnx"),
            tokens=str(SENSEVOICE_DIR / "tokens.txt"),
            use_itn=True, language="zh", num_threads=4)
        cfg = so.VadModelConfig()
        cfg.silero_vad.model = str(SILERO)
        cfg.silero_vad.threshold = 0.5
        # VAD only cuts acoustic segments; utterance assembly waits across breath pauses.
        cfg.silero_vad.min_silence_duration = 0.45
        cfg.silero_vad.min_speech_duration = 0.18
        cfg.sample_rate = PROC_SR
        self.vad = so.VoiceActivityDetector(cfg, buffer_size_in_seconds=30)
        self.aec = pyaec.Aec(FRAME, TAPS, PROC_SR, False)   # 关掉预处理，保留用户语音
        self.aec_enabled = os.environ.get("VOICE_AEC", "auto") != "0"
        self.aec_auto = os.environ.get("VOICE_AEC", "auto") == "auto"
        self.echo_gain = None
        self.echo_corr = None
        log(f"模型就绪 {time.time()-t0:.2f}s")

        self.lock = threading.Lock()
        self.events = []                  # list of dict
        self.events_cv = threading.Condition(self.lock)
        self.event_seq = 0

        # 音频
        self.mic_q = queue.Queue(maxsize=200)
        self.play_q = queue.Queue()
        self.ref_ring = collections.OrderedDict()   # ref block index -> np.float32[320]
        self.ref_index = 0
        self.mic_index = 0
        self.play_start_mic = None                  # 播放开始时 mic_index
        self.delay_blocks = 0                       # 扬声器→麦克风延迟（16k blocks）
        self.playing = False
        self.play_token = 0
        self.stop_play = threading.Event()
        self.play_thread = None
        self.say_queue = queue.Queue()          # (pcm48, ref16, sentence_id, is_last)
        self.queue_end = False
        self.queue_thread = None
        self.play_ref_start = 0
        self.pending_items = {}                 # sentence_id -> (pcm48, ref16, is_last)
        self.next_sentence_id = 1
        self.current_turn_id = None
        self.tts_last_ms = 0
        self.tts_fail = 0
        self.tts_slow = 0
        self.tts_synth_count = 0
        self.tts_cache_hits = 0
        self.capture_thread = None
        self.proc_thread = None
        self.running = False
        # 麦克风按需开关：Unity 停止轮询后自动释放
        self.mic_open = False
        self.last_client_poll = 0.0
        self.mic_idle_timeout = float(os.environ.get("VOICE_MIC_IDLE", "30.0"))
        self.inject_q = queue.Queue()
        self.noise_floor = 0.002
        self.barge_this_play = False
        self.barge_cooldown_until = 0.0
        self.barge_streak = 0
        self.barge_enabled = True
        self.mouth = MouthTracker()
        self.asr_buf = ""  # legacy diagnostic field
        self.utterance = UtteranceBuffer(float(os.environ.get("VOICE_MERGE_SECONDS", "0.8")))
        self.audio_history = PCMHistory(PROC_SR, preroll_seconds=float(os.environ.get("VOICE_PREROLL", "0.35")))
        self.audio_stats = {"segments": 0, "preroll_samples": 0, "queue_drops": 0}
        self.asr_timer = None
        self.mic_ready = False

        self.asr_pool = ThreadPoolExecutor(max_workers=1, thread_name_prefix="asr")
        self.voice_id = VOICE_ID
        self._load_tts()

    # ---------------- TTS 合成（复用 tts_server） ----------------
    def _load_tts(self):
        self.tts_mod = None
        try:
            sys.path.insert(0, str(HERE))
            import tts_server
            self.tts_mod = tts_server
            log("已复用 tts_server 的 MiniMax 合成")
        except Exception as e:  # noqa: BLE001
            log(f"[!] 载入 tts_server 失败：{e}（/say 将不可用）")

    def synth(self, text, voice_id=None, emotion="", demeanor="", intensity=0.6, engine="minimax"):
        if self.tts_mod is None:
            raise RuntimeError("tts_server 未加载")
        mm_emotion, speed, vol = self.tts_mod.resolve_voice_params(emotion, demeanor, intensity)
        vid = voice_id or self.voice_id
        cp = self.tts_mod.cache_path(text, vid, mm_emotion, speed, vol)
        t0 = time.time()
        if self.tts_mod.USE_CACHE and cp.exists():
            mp3 = cp.read_bytes()
            self.tts_cache_hits += 1
            self.tts_last_ms = int((time.time() - t0) * 1000)
            log(f"[TTS] cache {len(text)}字 → {mm_emotion} ({self.tts_last_ms}ms)")
            return mp3
        # 实时语音：超时收紧到 6s（原来 30s，遇到 MiniMax 连接重置会卡 19s+）
        timeout = float(os.environ.get("MINIMAX_TIMEOUT_SEC", "6"))
        try:
            mp3, dur, _ = self.tts_mod.minimax_synth(text, vid, mm_emotion, speed, vol, timeout=timeout)
        except Exception:
            self.tts_fail += 1
            self.tts_last_ms = int((time.time() - t0) * 1000)
            raise
        self.tts_synth_count += 1
        self.tts_last_ms = int((time.time() - t0) * 1000)
        if self.tts_last_ms >= 5000:
            self.tts_slow += 1
            log(f"[TTS][SLOW] {len(text)}字 synth={self.tts_last_ms}ms（MiniMax 延迟偏高）")
        try:
            cp.parent.mkdir(parents=True, exist_ok=True)
            cp.write_bytes(mp3)
        except Exception:
            pass
        log(f"[TTS] synth {len(text)}字 → {mm_emotion} ({len(mp3)}B, {self.tts_last_ms}ms)")
        return mp3

    # ---------------- 事件 ----------------
    def emit(self, kind, **payload):
        with self.events_cv:
            # Allocate sequence under the same lock as append; audio/ASR/TTS emit concurrently.
            ev = {"seq": self.event_seq, "type": kind, "t": round(time.time(), 3)}
            ev.update(payload)
            self.event_seq += 1
            self.events.append(ev)
            if len(self.events) > 500:
                self.events = self.events[-400:]
            self.events_cv.notify_all()

    def events_since(self, cursor, timeout):
        """cursor = "下一条要取的序号"（从 0 开始）。返回 (events, next_cursor)。"""
        deadline = time.time() + timeout
        with self.events_cv:
            while True:
                out = [e for e in self.events if e["seq"] >= cursor]
                if out:
                    return out, out[-1]["seq"] + 1
                if time.time() >= deadline:
                    return [], self.event_seq
                self.events_cv.wait(max(0.02, deadline - time.time()))

    # ---------------- 采集线程 ----------------
    def capture_loop(self):
        def cb(indata, nframes, t, status):
            try:
                self.mic_q.put_nowait(indata[:, 0].copy())
                self.mic_ready = True
            except queue.Full:
                self.audio_stats["queue_drops"] += 1

        # 没有 Unity 客户端轮询 /events 时不占用麦克风；
        # 超过 mic_idle_timeout 秒没有轮询就关闭 InputStream，下一次 Play 再自动打开。
        while self.running:
            if time.time() - self.last_client_poll > self.mic_idle_timeout:
                if self.mic_open:
                    self.mic_open = False
                    log("采集已释放（客户端空闲）")
                time.sleep(0.2)
                continue

            try:
                with sd.InputStream(device=MIC_DEVICE, samplerate=CAP_SR, channels=1,
                                    blocksize=BLOCK48, dtype="float32", callback=cb):
                    self.mic_open = True
                    log(f"采集已启动 device={MIC_DEVICE} {CAP_SR}Hz")
                    while self.running and (time.time() - self.last_client_poll <= self.mic_idle_timeout):
                        time.sleep(0.1)
            except Exception as e:  # noqa: BLE001
                log(f"[!] 麦克风打开失败，1 秒后重试：{e}")
                time.sleep(1.0)
            finally:
                self.mic_open = False
                self.mic_ready = False
                # 清掉残留音频，避免下一次打开时处理旧数据
                while True:
                    try:
                        self.mic_q.get_nowait()
                    except queue.Empty:
                        break

    # ---------------- 播放线程 ----------------
    def _play_worker(self, pcm48, ref16, token):
        self.stop_play.clear()
        baseline = self.mic_index
        with self.lock:
            self.play_start_mic = baseline
            self.play_ref_start = self.ref_index
            self.playing = True
            self.barge_this_play = False
            self.barge_cooldown_until = 0.0
        self.emit("tts.start", token=token)
        log(f"[SAY] 开始播放 {len(pcm48)/CAP_SR:.2f}s")

        try:
            with sd.OutputStream(device=SPK_DEVICE, samplerate=CAP_SR, channels=1,
                                 blocksize=BLOCK48, dtype="float32") as ost:
                for i in range(0, len(pcm48) - BLOCK48, BLOCK48):
                    if self.stop_play.is_set() or token != self.play_token:
                        log("[SAY] 被打断/取消，停止播放")
                        break
                    ost.write(pcm48[i:i + BLOCK48].reshape(-1, 1))
                    self.mouth.feed(pcm48[i:i + BLOCK48])
                    # 参考块（与播放同步推进）
                    b = i // BLOCK48
                    ref_block = ref16[b * BLOCK16:(b + 1) * BLOCK16]
                    with self.lock:
                        self.ref_ring[self.ref_index] = ref_block
                        self.ref_index += 1
                        while len(self.ref_ring) > 3000:
                            self.ref_ring.popitem(last=False)
        except Exception as e:  # noqa: BLE001
            log(f"[!] 播放失败：{e}")
            self.emit("error", msg=f"播放失败: {e}")
        finally:
            with self.lock:
                if token == self.play_token:
                    self.playing = False
            if self.stop_play.is_set():
                self.emit("tts.cancelled", token=token)
            else:
                self.emit("tts.end", token=token)
            self.mouth.silence()
            log("[SAY] 播放结束")

    def say(self, text, voice_id=None, emotion="", demeanor="", intensity=0.6):
        """合成 → 解码 → 开始播放（异步）。返回音频时长（秒）。"""
        mp3 = self.synth(text, voice_id, emotion, demeanor, intensity)
        return self.play_mp3(mp3)

    def play_mp3(self, mp3: bytes):
        import tempfile

        with tempfile.NamedTemporaryFile(suffix=".mp3", delete=False) as f:
            f.write(mp3)
            tmp = f.name
        try:
            d48 = miniaudio.decode_file(tmp, output_format=miniaudio.SampleFormat.FLOAT32,
                                        nchannels=1, sample_rate=CAP_SR)
            pcm48 = np.array(d48.samples, dtype=np.float32)
            d16 = miniaudio.decode_file(tmp, output_format=miniaudio.SampleFormat.FLOAT32,
                                        nchannels=1, sample_rate=PROC_SR)
            ref16 = np.array(d16.samples, dtype=np.float32)
        finally:
            try:
                os.unlink(tmp)
            except OSError:
                pass

        with self.lock:
            self.play_token += 1
            token = self.play_token
        self.stop_play.set()   # 让旧播放线程停下
        if self.play_thread is not None and self.play_thread.is_alive():
            self.play_thread.join(timeout=1.0)
        self.stop_play.clear()
        self.play_thread = threading.Thread(target=self._play_worker,
                                            args=(pcm48, ref16, token), daemon=True)
        self.play_thread.start()
        return len(pcm48) / CAP_SR

    def cancel(self):
        with self.lock:
            self.play_token += 1
            self.queue_end = False
        self.stop_play.set()
        self.mouth.silence()
        # 清空分句队列（流式播放时）
        while True:
            try:
                self.say_queue.get_nowait()
            except queue.Empty:
                break
        log("[SAY] /cancel")
        return True

    # ---------------- 流式 TTS 队列（分句边合成边播） ----------------
    def _decode_mp3(self, mp3: bytes):
        import tempfile
        with tempfile.NamedTemporaryFile(suffix=".mp3", delete=False) as f:
            f.write(mp3)
            tmp = f.name
        try:
            d48 = miniaudio.decode_file(tmp, output_format=miniaudio.SampleFormat.FLOAT32,
                                        nchannels=1, sample_rate=CAP_SR)
            pcm48 = np.array(d48.samples, dtype=np.float32)
            d16 = miniaudio.decode_file(tmp, output_format=miniaudio.SampleFormat.FLOAT32,
                                        nchannels=1, sample_rate=PROC_SR)
            ref16 = np.array(d16.samples, dtype=np.float32)
        finally:
            try:
                os.unlink(tmp)
            except OSError:
                pass
        return pcm48, ref16

    def enqueue_sentence(self, text, voice_id=None, emotion="", demeanor="", intensity=0.6,
                         sentence_id=0, is_last=False, turn_id=0):
        """把一句加入播放队列：优先缓存；否则 MiniMax 流式 PCM（TTS_STREAM=1），失败回退整句。"""
        t0 = time.time()
        if self.tts_mod is None:
            raise RuntimeError("tts_server 未加载")

        item = None
        duration = 0.0
        vid = voice_id or self.voice_id
        mm_emotion, speed, vol = self.tts_mod.resolve_voice_params(emotion, demeanor, intensity)
        cp = self.tts_mod.cache_path(text, vid, mm_emotion, speed, vol)
        use_stream = os.environ.get("TTS_STREAM", "1") != "0"

        if self.tts_mod.USE_CACHE and cp.exists():
            mp3 = cp.read_bytes()
            pcm48, ref16 = self._decode_mp3(mp3)
            item = ("static", pcm48, ref16, sentence_id, is_last)
            duration = len(pcm48) / CAP_SR
            log(f"[TTS] cache {len(text)}字 → {mm_emotion}")
        elif use_stream:
            item = StreamSentence(self, text, vid, emotion, demeanor, intensity,
                                  mm_emotion, speed, vol, sentence_id, is_last)
            log(f"[TTS-STREAM] sentence#{sentence_id} 开始流式（{len(text)}字）")
        else:
            mp3 = None
            last_err = None
            for attempt in range(2):
                t_attempt = time.time()
                try:
                    mp3 = self.synth(text, voice_id, emotion, demeanor, intensity)
                    break
                except Exception as e:  # noqa: BLE001
                    last_err = e
                    log(f"[STREAM] sentence#{sentence_id} synth 失败（尝试 {attempt+1}，"
                        f"{(time.time()-t_attempt)*1000:.0f}ms）：{e}")
                    time.sleep(0.35)
            if mp3 is None:
                raise last_err if last_err is not None else RuntimeError("synth failed")
            synth_ms = int((time.time() - t0) * 1000)
            pcm48, ref16 = self._decode_mp3(mp3)
            item = ("static", pcm48, ref16, sentence_id, is_last)
            duration = len(pcm48) / CAP_SR
            log(f"[STREAM] sentence#{sentence_id} {len(text)}字 synth={synth_ms}ms "
                f"dur={duration:.2f}s last={is_last}")

        with self.lock:
            worker_alive = self.queue_thread is not None and self.queue_thread.is_alive()
            new_turn = (turn_id != self.current_turn_id)
            if new_turn:
                self.current_turn_id = turn_id
                self.play_token += 1
                self.stop_play.set()
                self.queue_end = False
                self.pending_items = {}
                self.next_sentence_id = 1
                while True:
                    try:
                        self.say_queue.get_nowait()
                    except queue.Empty:
                        break
                self.queue_thread = threading.Thread(target=self._queue_worker,
                                                     args=(self.play_token,), daemon=True)
                self.queue_thread.start()
            elif not worker_alive:
                self.play_token += 1
                self.queue_end = False
                self.stop_play.clear()
                self.queue_thread = threading.Thread(target=self._queue_worker,
                                                     args=(self.play_token,), daemon=True)
                self.queue_thread.start()

            self.pending_items[sentence_id] = item
            while self.next_sentence_id in self.pending_items:
                it = self.pending_items.pop(self.next_sentence_id)
                self.say_queue.put(it)
                is_last_item = it.is_last if isinstance(it, StreamSentence) else it[4]
                if is_last_item:
                    self.queue_end = True
                self.next_sentence_id += 1

        return duration

    def finish_queue(self):
        """标记本轮分句已全部入队；队列播完（且没有更多句子）时发 tts.end。"""
        with self.lock:
            self.queue_end = True
            worker_alive = self.queue_thread is not None and self.queue_thread.is_alive()
            empty = self.say_queue.empty()
            # 兜底：如果有乱序/缺号的缓冲，按 id 顺序直接冲掉，避免永久丢失
            if self.pending_items:
                for sid in sorted(self.pending_items.keys()):
                    self.say_queue.put(self.pending_items.pop(sid))
                empty = False
        if not worker_alive and empty:
            self.emit("tts.end", token=self.play_token)
        log("[SAY] /say_finish（队列收尾）")
        return True

    def _write_block(self, ost, block, token):
        """写一个 20ms 播放块 + mouth + AEC 参考；返回 False 表示被取消。"""
        if self.stop_play.is_set() or token != self.play_token:
            return False
        ost.write(block.reshape(-1, 1))
        self.mouth.feed(block)
        ref_block = block[::3]           # 48k → 16k（960 → 320）
        with self.lock:
            self.ref_ring[self.ref_index] = ref_block
            self.ref_index += 1
            while len(self.ref_ring) > 3000:
                self.ref_ring.popitem(last=False)
        return True

    def _play_stream_sentence(self, ost, item, token):
        """边收 MiniMax 流式 PCM 边播；队列空但未完成时短暂等待。"""
        with self.lock:
            self.play_start_mic = self.mic_index
            self.play_ref_start = self.ref_index
        self.emit("tts.sentence_start", sentence_id=item.sentence_id)
        buf = np.zeros(0, dtype=np.float32)
        try:
            while True:
                if self.stop_play.is_set() or token != self.play_token:
                    item.stop.set()
                    return False
                try:
                    chunk = item.audio_q.get(timeout=0.05)
                except queue.Empty:
                    if item.done:
                        break
                    continue
                if len(chunk) > 0:
                    buf = np.concatenate([buf, chunk])
                while len(buf) >= BLOCK48:
                    block = buf[:BLOCK48]
                    buf = buf[BLOCK48:]
                    if not self._write_block(ost, block, token):
                        item.stop.set()
                        return False
            if len(buf) > 0:
                block = np.zeros(BLOCK48, dtype=np.float32)
                block[:len(buf)] = buf[:BLOCK48]
                if not self._write_block(ost, block, token):
                    item.stop.set()
                    return False
            self.emit("tts.sentence_end", sentence_id=item.sentence_id)
            if item.error:
                log(f"[TTS-STREAM] sentence#{item.sentence_id} 播放结束（有错误：{item.error}）")
            return True
        finally:
            item.stop.set()

    def _queue_worker(self, token):
        """顺序播放队列里的句子；每句都写入 AEC 参考；队列空且已 finish 时发 tts.end。"""
        cancelled = False
        self.stop_play.clear()
        with self.lock:
            self.playing = True
            self.barge_this_play = False
            self.barge_cooldown_until = 0.0
        self.emit("tts.start", token=token)
        log("[SAY] 队列播放开始")
        try:
            with sd.OutputStream(device=SPK_DEVICE, samplerate=CAP_SR, channels=1,
                                 blocksize=BLOCK48, dtype="float32") as ost:
                while True:
                    if self.stop_play.is_set() or token != self.play_token:
                        cancelled = True
                        break

                    try:
                        item = self.say_queue.get(timeout=0.05)
                    except queue.Empty:
                        with self.lock:
                            end = self.queue_end
                        if end:
                            break
                        continue

                    if isinstance(item, StreamSentence):
                        if not self._play_stream_sentence(ost, item, token):
                            cancelled = True
                            break
                        continue

                    _, pcm48, ref16, sid, _ = item
                    with self.lock:
                        self.play_start_mic = self.mic_index
                        self.play_ref_start = self.ref_index
                    self.emit("tts.sentence_start", sentence_id=sid)
                    ok = True
                    for i in range(0, len(pcm48) - BLOCK48, BLOCK48):
                        if self.stop_play.is_set() or token != self.play_token:
                            ok = False
                            cancelled = True
                            break
                        ost.write(pcm48[i:i + BLOCK48].reshape(-1, 1))
                        self.mouth.feed(pcm48[i:i + BLOCK48])
                        b = i // BLOCK48
                        ref_block = ref16[b * BLOCK16:(b + 1) * BLOCK16]
                        with self.lock:
                            self.ref_ring[self.ref_index] = ref_block
                            self.ref_index += 1
                            while len(self.ref_ring) > 3000:
                                self.ref_ring.popitem(last=False)
                    if not ok:
                        break
                    self.emit("tts.sentence_end", sentence_id=sid)
        except Exception as e:  # noqa: BLE001
            log(f"[!] 队列播放失败：{e}")
            self.emit("error", msg=f"队列播放失败: {e}")
        finally:
            with self.lock:
                if token == self.play_token:
                    self.playing = False
                self.queue_end = False
            while True:
                try:
                    self.say_queue.get_nowait()
                except queue.Empty:
                    break
            if cancelled:
                self.emit("tts.cancelled", token=token)
            else:
                self.emit("tts.end", token=token)
            self.mouth.silence()
            log("[SAY] 队列播放结束")

    # ---------------- 处理线程 ----------------
    def _echo_ref_for(self, mic_idx):
        with self.lock:
            if self.play_start_mic is None:
                return None
            ref_idx = self.play_ref_start + (mic_idx - self.play_start_mic - self.delay_blocks)
            return self.ref_ring.get(ref_idx)

    def _process_loop(self):
        vad = self.vad
        speech_active = False
        seg_audio = []
        barge = False
        last_emit = 0.0
        inject = np.zeros(0, dtype=np.float32)

        while self.running:
            try:
                block48 = self.mic_q.get(timeout=0.2)
            except queue.Empty:
                continue
            mic16 = dec3(block48)
            # 注入测试语音（模拟近端用户）
            while True:
                try:
                    if len(inject) == 0:
                        path = self.inject_q.get_nowait()
                        inject = load_wav_16k(path)
                        log(f"[TEST] inject {len(inject)/PROC_SR:.2f}s user audio")
                    break
                except queue.Empty:
                    break
            if len(inject) > 0:
                n = min(len(inject), len(mic16))
                mic16 = mic16.copy()
                mic16[:n] += inject[:n]
                inject = inject[n:]

            with self.lock:
                self.mic_index += 1
                mi = self.mic_index - 1
            ref = self._echo_ref_for(mi)
            if self.aec_enabled and ref is not None and len(ref) == BLOCK16:
                rec_i = np.clip(mic16 * 32767, -32768, 32767).astype(np.int16).tolist()
                echo_i = np.clip(ref * 32767, -32768, 32767).astype(np.int16).tolist()
                try:
                    out = self.aec.cancel_echo(rec_i, echo_i)
                    proc = np.array(out, dtype=np.float32) / 32768.0
                except Exception:
                    proc = mic16
            else:
                proc = mic16

            # 噪声底跟踪（注入测试音频时不更新，避免被"用户语音"抬高）
            rms = float(np.sqrt(np.mean(proc ** 2)) + 1e-12)
            self.audio_history.append(proc)
            if not speech_active and not self.playing and len(inject) == 0:
                self.noise_floor = 0.995 * self.noise_floor + 0.005 * min(rms, 0.02)
            self.noise_floor = min(self.noise_floor, 0.01)

            # 能量门限：太弱的声音不当作"用户说话"（防环境噪声触发/幻觉）
            gate = max(float(os.environ.get("VOICE_MIN_RMS", "0.0035")), self.noise_floor * 3.5)
            vad.accept_waveform(proc)
            voiced = vad.is_speech_detected() and rms >= gate
            if voiced:
                # 打断确认：连续 3 帧（60ms）且能量更高，避免 TTS 回声/瞬时噪声误停。
                # AEC 旁路时门限更高；用户正常开口通常 rms 0.03+，60ms 内就能确认。
                if self.playing and self.barge_enabled and not self.barge_this_play:
                    barge_gate = max(gate, 0.008 if self.aec_enabled else 0.022)
                    if rms >= barge_gate:
                        self.barge_streak += 1
                    else:
                        self.barge_streak = 0
                    if self.barge_streak >= 3:
                        now = time.time()
                        with self.lock:
                            can_barge = (self.playing and self.barge_enabled and not self.barge_this_play
                                         and now >= self.barge_cooldown_until)
                            if can_barge:
                                self.barge_this_play = True
                                self.barge_cooldown_until = now + 2.0
                        if can_barge:
                            self.stop_play.set()
                            self.emit("barge_in", reason="user_speech", rms=round(rms, 5))
                            log(f"[BARGE] 用户开口 → 停播 (rms={rms:.4f}, 确认={self.barge_streak}帧)")
                        self.barge_streak = 0
                else:
                    self.barge_streak = 0

                if not speech_active:
                    speech_active = True
                    seg_audio = []
                    with self.lock:
                        self.utterance.speech_start(time.monotonic())
                    self.emit("vad.start", rms=round(rms, 5), playing=bool(self.playing))
                seg_audio.append(proc.copy())
            else:
                self.barge_streak = 0
                if speech_active:
                    seg_audio.append(proc.copy())

            while not vad.empty():
                seg = vad.front
                # sherpa front is a borrowed reference, invalidated by pop(). Copy BOTH first.
                segment_start = int(seg.start)
                samples = np.array(seg.samples, dtype=np.float32, copy=True) if hasattr(seg, "samples") else np.zeros(0)
                vad.pop()
                if samples.size == 0 and seg_audio:
                    samples = np.concatenate(seg_audio) if seg_audio else np.zeros(0)
                seg_rms = float(np.sqrt(np.mean(samples ** 2)) + 1e-12) if samples.size else 0.0
                with self.lock:
                    self.utterance.speech_end(time.monotonic())
                if samples.size > 0 and seg_rms >= gate:
                    padded, prefix = self.audio_history.segment(segment_start, samples)
                    self.audio_stats["segments"] += 1
                    self.audio_stats["preroll_samples"] += prefix
                    self.emit("asr.segment", start_sample=segment_start, samples=len(padded), preroll_samples=prefix)
                    self._do_asr(padded)
                elif samples.size > 0:
                    log(f"[VAD] 丢弃弱段落 rms={seg_rms:.5f} < gate={gate:.5f}")
                seg_audio = []
                speech_active = False
                self.emit("vad.end", barge=self.barge_this_play)
                barge = False

    def _schedule_asr_flush_locked(self, delay=0.2):
        if self.asr_timer is not None:
            self.asr_timer.cancel()
        self.asr_timer = threading.Timer(delay, self._flush_asr)
        self.asr_timer.daemon = True
        self.asr_timer.start()

    def _do_asr(self, samples16):
        with self.lock:
            self.utterance.job_start()
        def work():
            t0 = time.time()
            text = ""
            try:
                s = self.recognizer.create_stream()
                s.accept_waveform(PROC_SR, samples16)
                self.recognizer.decode_stream(s)
                text = (s.result.text or "").strip()
                if text:
                    log(f"[ASR] {text}  ({time.time()-t0:.2f}s) → 连续话语缓冲")
            except Exception as e:
                self.emit("error", msg=f"ASR 失败: {e}")
            finally:
                with self.lock:
                    self.utterance.job_done(text, time.monotonic())
                    self.asr_buf = '，'.join(self.utterance.parts)
                    self._schedule_asr_flush_locked()
        self.asr_pool.submit(work)

    def _flush_asr(self):
        with self.lock:
            self.asr_timer = None
            if not self.utterance.ready(time.monotonic()):
                if self.utterance.parts:
                    self._schedule_asr_flush_locked()
                return
            text = self.utterance.take()
            self.asr_buf = ""
        if text:
            self.emit("asr.final", text=text, ms=800)
            log(f"[ASR] final → {text}")

    # ---------------- 校准 ----------------
    def calibrate(self, dur=1.5):
        log("[CAL] 播放噪声测回声延迟...")
        rng = np.random.default_rng(1)
        n = int(CAP_SR * dur)
        noise48 = (rng.standard_normal(n) * 0.25).astype(np.float32)
        # 先记录 0.3s 静音，再播放
        rec = []
        done = threading.Event()

        def cb(indata, nframes, t, status):
            rec.append(indata[:, 0].copy())

        with sd.InputStream(device=MIC_DEVICE, samplerate=CAP_SR, channels=1,
                            blocksize=BLOCK48, dtype="float32", callback=cb) as ist, \
             sd.OutputStream(device=SPK_DEVICE, samplerate=CAP_SR, channels=1,
                             blocksize=BLOCK48, dtype="float32") as ost:
            ist.start()
            time.sleep(0.3)
            for i in range(0, n - BLOCK48, BLOCK48):
                ost.write(noise48[i:i + BLOCK48].reshape(-1, 1))
            time.sleep(0.5)

        mic48 = np.concatenate(rec).astype(np.float32)
        mic16 = dec3(mic48)
        # 参考：播放开始时间 ≈ 0.3s（mic 记录里）
        ref16 = dec3(noise48)
        lead_blocks = int(0.3 * PROC_SR / BLOCK16)
        best = (0, -1.0)
        for dly in range(0, 2400):  # ≤150ms
            start = lead_blocks + dly
            seg = np.abs(mic16[start:start + len(ref16)])
            if len(seg) < len(ref16) * 0.7:
                break
            r = np.abs(ref16[:len(seg)])
            c = float(np.corrcoef(seg, r)[0, 1])
            if c > best[1]:
                best = (dly, c)
        self.delay_blocks = best[0]
        gain = float(np.sqrt(np.mean(mic16[max(0,lead_blocks):] ** 2)) /
                     (np.sqrt(np.mean(ref16 ** 2)) + 1e-12))
        # 回声太弱/相关度太低（扬声器静音、耳机、麦克风 DSP 抑制等）→ delay 不可信，归零
        if best[1] < 0.05 or gain < 0.002:
            log(f"[CAL] 回声太弱（corr={best[1]:.3f} gain={gain:.4f}），delay 归零")
            self.delay_blocks = 0
        self.delay_blocks = max(0, min(int(self.delay_blocks), 100))   # 最多 2s，防异常值
        self.echo_gain = gain
        self.echo_corr = best[1]
        # 自动决定是否启用 AEC：回声太弱（本机实测 ~-30dB）就旁路，避免把用户语音一起压掉
        if self.aec_auto:
            self.aec_enabled = bool(gain >= 0.015 and best[1] >= 0.15)
        log(f"[CAL] delay={self.delay_blocks} blocks ({self.delay_blocks*20}ms) corr={best[1]:.3f} "
            f"echo_gain={gain:.3f} → AEC={'ON' if self.aec_enabled else 'BYPASS（回声弱）'}")
        return {"delay_blocks": self.delay_blocks, "delay_ms": self.delay_blocks * 20,
                "corr": round(best[1], 3), "echo_gain": round(gain, 3),
                "aec_enabled": self.aec_enabled}

    # ---------------- 启动/停止 ----------------
    def start(self):
        self.running = True
        self.capture_thread = threading.Thread(target=self.capture_loop, daemon=True)
        self.proc_thread = threading.Thread(target=self._process_loop, daemon=True)
        self.capture_thread.start()
        self.proc_thread.start()
        log("VoiceServer 已启动")

    def stop(self):
        self.running = False
        self.stop_play.set()
        time.sleep(0.3)


# ================= HTTP =================
class Handler(BaseHTTPRequestHandler):
    server_version = "YixuanVoice/1.0"

    def log_message(self, fmt, *args):
        pass

    def _json(self, code, obj):
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

    def _body(self):
        n = int(self.headers.get("Content-Length", "0") or 0)
        raw = self.rfile.read(n) if n else b""
        try:
            return json.loads(raw.decode("utf-8") or "{}")
        except Exception:
            return {}

    def do_GET(self):
        srv: VoiceServer = self.server.vs
        if self.path.startswith("/health"):
            self._json(200, {"ok": True, "playing": srv.playing, "voice_id": srv.voice_id,
                             "delay_blocks": srv.delay_blocks, "mic": MIC_DEVICE,
                             "mic_open": bool(srv.mic_open), "mic_ready": bool(srv.mic_ready),
                             "audio_stats": srv.audio_stats, "asr_pending": srv.utterance.pending,
                             "preroll_ms": srv.audio_history.preroll * 1000 // PROC_SR,
                             "mic_idle_timeout": srv.mic_idle_timeout,
                             "speaker": SPK_DEVICE, "noise_floor": round(srv.noise_floor, 5),
                             "aec_enabled": srv.aec_enabled, "echo_gain": srv.echo_gain,
                             "mouth_sent": srv.mouth.sent,
                             "tts_last_ms": srv.tts_last_ms, "tts_fail": srv.tts_fail,
                             "tts_slow": srv.tts_slow, "tts_synth": srv.tts_synth_count,
                             "tts_cache_hits": srv.tts_cache_hits})
            return
        if self.path.startswith("/devices"):
            out = []
            for i, d in enumerate(sd.query_devices()):
                out.append({"index": i, "name": d["name"], "in": d["max_input_channels"],
                            "out": d["max_output_channels"], "sr": d["default_samplerate"]})
            self._json(200, {"devices": out, "default": list(sd.default.device)})
            return
        if self.path.startswith("/events"):
            from urllib.parse import urlparse, parse_qs
            q = parse_qs(urlparse(self.path).query)
            cursor = int(q.get("cursor", ["-1"])[0])
            timeout = float(q.get("timeout", ["1.0"])[0])
            srv.last_client_poll = time.time()
            if cursor < 0:
                # Atomic live cursor: do not discard finals that arrive during a slow bootstrap response.
                with srv.lock:
                    evs, next_cursor = [], srv.event_seq
            else:
                evs, next_cursor = srv.events_since(cursor, timeout)
            self._json(200, {"events": evs, "cursor": next_cursor, "mic_ready": bool(srv.mic_ready)})
            return
        self._json(404, {"ok": False, "error": "not found"})

    def do_POST(self):
        srv: VoiceServer = self.server.vs
        body = self._body()
        try:
            if self.path.startswith("/say_queue"):
                text = (body.get("text") or "").strip()
                if not text:
                    self._json(400, {"ok": False, "error": "text 为空"})
                    return
                srv.barge_enabled = bool(body.get("barge_enabled", True))
                dur = srv.enqueue_sentence(
                    text, body.get("voice_id"), body.get("emotion", ""),
                    body.get("demeanor", ""), float(body.get("intensity", 0.6)),
                    sentence_id=int(body.get("sentence_id", 0)),
                    is_last=bool(body.get("is_last", False)),
                    turn_id=int(body.get("turn_id", 0)))
                self._json(200, {"ok": True, "duration": round(dur, 2)})
                return
            if self.path.startswith("/say_finish"):
                srv.finish_queue()
                self._json(200, {"ok": True})
                return
            if self.path.startswith("/say"):
                text = (body.get("text") or "").strip()
                if not text:
                    self._json(400, {"ok": False, "error": "text 为空"})
                    return
                srv.barge_enabled = bool(body.get("barge_enabled", True))
                dur = srv.say(text, body.get("voice_id"), body.get("emotion", ""),
                              body.get("demeanor", ""), float(body.get("intensity", 0.6)))
                self._json(200, {"ok": True, "duration": round(dur, 2)})
                return
            if self.path.startswith("/cancel"):
                srv.cancel()
                self._json(200, {"ok": True})
                return
            if self.path.startswith("/calibrate"):
                self._json(200, {"ok": True, **srv.calibrate()})
                return
            if self.path.startswith("/inject_user"):
                path = body.get("path")
                if not path or not Path(path).exists():
                    self._json(400, {"ok": False, "error": "path 不存在"})
                    return
                srv.inject_q.put(path)
                self._json(200, {"ok": True})
                return
            self._json(404, {"ok": False, "error": "not found"})
        except Exception as e:  # noqa: BLE001
            log(f"[HTTP] 500 {e}")
            self._json(500, {"ok": False, "error": str(e)})


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=int(os.environ.get("VOICE_PORT", "8767")))
    ap.add_argument("--no-calibrate", action="store_true")
    args = ap.parse_args()

    vs = VoiceServer()
    vs.start()
    if not args.no_calibrate:
        try:
            vs.calibrate()
        except Exception as e:  # noqa: BLE001
            log(f"[CAL] 失败：{e}（AEC 将对齐到 0）")

    httpd = ThreadingHTTPServer((args.host, args.port), Handler)
    httpd.vs = vs
    log(f"HTTP http://{args.host}:{args.port}  (POST /say /cancel /calibrate /inject_user, GET /events /health /devices)")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        log("bye")
    finally:
        vs.stop()


if __name__ == "__main__":
    main()
