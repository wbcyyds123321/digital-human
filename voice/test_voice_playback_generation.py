"""Focused production playback races with mocked audio/cloud; no service or model startup."""
import collections
import json
import os
from pathlib import Path
import queue
import sys
import threading
import time
import types
import unittest
from unittest import mock

import numpy as np

# voice_server imports audio drivers/models eagerly; these tests never instantiate them.
for name in ("sounddevice", "miniaudio", "sherpa_onnx", "pyaec"):
    sys.modules[name] = types.ModuleType(name)
import voice_server as product
from voice_utterance import UtteranceBuffer


class Sink:
    blocks = []
    events_at_write = []
    entered = None
    release = None
    gate_token = None
    server = None

    def __init__(self, **kwargs):
        self.token = Sink.server.play_token

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def write(self, block):
        Sink.events_at_write.append((self.token, list(Sink.server.events)))
        Sink.blocks.append((self.token, block.copy()))
        if self.token == Sink.gate_token and Sink.entered is not None:
            Sink.entered.set()
            Sink.release.wait(2)


def fake_server():
    s = product.VoiceServer.__new__(product.VoiceServer)
    s.lock = threading.Lock()
    s.events_cv = threading.Condition(s.lock)
    s.events, s.event_seq, s.service_id = [], 0, "mock-service"
    s.play_token, s.latest_turn_id, s.current_turn_id = 0, 0, None
    s.playback_turn, s.queue_thread = None, None
    s.stop_play, s.say_queue = threading.Event(), queue.Queue()
    s.pending_items, s.queue_end, s.playing = {}, False, False
    s.barge_enabled = True
    s.mic_index, s.ref_index = 0, 0
    s.ref_ring = collections.OrderedDict()
    s.mouth = types.SimpleNamespace(feed=lambda x: None, silence=lambda: None)
    s.voice_id = "mock"
    s.tts_mod = types.SimpleNamespace(
        USE_CACHE=False, resolve_voice_params=lambda *x: ("", 1, 1),
        cache_path=lambda *x: Path("__no_audio_cache__"))
    s.synth = mock.Mock(side_effect=lambda text, *args: text.encode())
    s._decode_mp3 = lambda raw: (np.ones(product.BLOCK48 * 2 + 17, np.float32),
                                 np.ones(product.BLOCK16 * 2 + 6, np.float32))
    Sink.blocks, Sink.entered, Sink.release, Sink.gate_token, Sink.server = [], None, None, None, s
    Sink.events_at_write = []
    return s


def join_turn(ctx):
    if ctx.thread is not None:
        ctx.thread.join(3)
        if ctx.thread.is_alive():
            raise AssertionError("mock playback did not terminate")


class PlaybackGenerationTests(unittest.TestCase):
    def setUp(self):
        self.patches = [mock.patch.dict(os.environ, {"TTS_STREAM": "0"}),
                        mock.patch.object(product.sd, "OutputStream", Sink, create=True)]
        for patch in self.patches:
            patch.start()
        self.s = fake_server()

    def tearDown(self):
        self.s.cancel()
        if self.s.playback_turn:
            join_turn(self.s.playback_turn)
        for patch in reversed(self.patches):
            patch.stop()

    def test_old_synthesis_cannot_reclaim_or_cancel_new_turn(self):
        entered, release = threading.Event(), threading.Event()
        def synth(text, *args):
            if text == "old":
                entered.set()
                release.wait(2)
            return text.encode()
        self.s.synth.side_effect = synth
        result = []
        old = threading.Thread(target=lambda: result.append(self.s.enqueue_sentence(
            "old", turn_id=101, sentence_id=1)))
        old.start()
        self.assertTrue(entered.wait(1))
        self.assertTrue(self.s.enqueue_sentence("new", turn_id=102, sentence_id=1)["accepted"])
        new = self.s.playback_turn
        self.assertFalse(self.s.enqueue_sentence("stale", turn_id=101, sentence_id=2,
                                                 barge_enabled=False)["accepted"])
        self.assertTrue(self.s.barge_enabled)
        self.assertFalse(self.s.finish_queue(101, 1)["accepted"])
        self.assertFalse(self.s.cancel(101)["accepted"])
        release.set()
        old.join(2)
        self.assertFalse(result[0]["accepted"])
        self.assertIs(self.s.playback_turn, new)
        self.s.finish_queue(102, 1)
        join_turn(new)

    def test_old_worker_teardown_keeps_new_audio_and_tail(self):
        Sink.entered, Sink.release, Sink.gate_token = threading.Event(), threading.Event(), 1
        self.s.enqueue_sentence("old", turn_id=201, sentence_id=1)
        old = self.s.playback_turn
        self.assertTrue(Sink.entered.wait(1))
        self.s.enqueue_sentence("new", turn_id=202, sentence_id=1)
        new = self.s.playback_turn
        self.s.finish_queue(202, 1)
        join_turn(new)
        Sink.release.set()
        join_turn(old)
        # Includes the 17-sample final block, which old range(..., len-BLOCK) dropped.
        self.assertEqual(3, sum(token == new.token for token, _ in Sink.blocks))
        first_write_events = next(evs for token, evs in Sink.events_at_write if token == new.token)
        self.assertFalse(any(e["type"] == "tts.audio_start" and e["turn_id"] == 202 for e in first_write_events))
        self.assertEqual(1, sum(e["type"] == "tts.audio_start" and e["turn_id"] == 202 for e in self.s.events))
        ends = [e for e in self.s.events if e["type"] == "tts.end" and e["turn_id"] == 202]
        self.assertEqual(1, len(ends))
        self.assertFalse(ends[0]["failed"])

    def test_finish_before_queue_and_out_of_order_arrival(self):
        self.assertTrue(self.s.finish_queue(301, 2)["accepted"])
        self.s.enqueue_sentence("second", turn_id=301, sentence_id=2)
        self.s.enqueue_sentence("first", turn_id=301, sentence_id=1)
        join_turn(self.s.playback_turn)
        ids = [e["sentence_id"] for e in self.s.events if e["type"] == "tts.sentence_start"]
        self.assertEqual([1, 2], ids)

    def test_cancel_fences_duplicate_and_late_requests_without_resynthesis(self):
        self.s.enqueue_sentence("one", turn_id=401, sentence_id=1)
        self.assertFalse(self.s.enqueue_sentence("duplicate", turn_id=401, sentence_id=1)["accepted"])
        self.s.cancel(401)
        self.assertFalse(self.s.enqueue_sentence("late", turn_id=401, sentence_id=2)["accepted"])
        self.assertEqual(1, self.s.synth.call_count)
        self.assertTrue(self.s.enqueue_sentence("next", turn_id=402, sentence_id=1)["accepted"])
        self.s.finish_queue(402, 1)
        join_turn(self.s.playback_turn)

    def test_legacy_requests_remain_usable(self):
        self.assertTrue(self.s.enqueue_sentence("first", sentence_id=0)["accepted"])
        self.assertTrue(self.s.finish_queue()["accepted"])
        join_turn(self.s.playback_turn)
        self.assertTrue(self.s.say("second")["accepted"])
        join_turn(self.s.playback_turn)

    def test_cancel_overtaking_first_queue_request_leaves_tombstone(self):
        self.assertTrue(self.s.cancel(501)["accepted"])
        self.assertFalse(self.s.enqueue_sentence("late", turn_id=501, sentence_id=1)["accepted"])
        self.assertEqual(0, self.s.synth.call_count)
        self.assertTrue(self.s.enqueue_sentence("next", turn_id=502, sentence_id=1)["accepted"])
        self.s.finish_queue(502, 1)
        join_turn(self.s.playback_turn)

    def test_zero_millisecond_first_chunk_does_not_trigger_full_sentence_replay(self):
        def broken_stream(*args, **kwargs):
            args[5](np.ones(320, dtype="<i2").tobytes())
            raise RuntimeError("mock stream disconnected after first PCM")
        self.s.tts_mod.minimax_stream = broken_stream
        with mock.patch.object(product.time, "time", return_value=100.0):
            item = product.StreamSentence(self.s, "hello", "mock", "", "", .6,
                                          "", 1, 1, 1, True)
            item.thread.join(2)
        self.assertTrue(item.audio_received)
        self.assertEqual(0, item.first_ms)
        self.assertEqual(0, self.s.synth.call_count)
        self.assertIsNotNone(item.error)
        self.assertEqual(1, item.audio_q.qsize())

    def test_asr_final_keeps_origin_and_capture_window(self):
        self.s.asr_timer = None
        self.s.utterance = UtteranceBuffer(0)
        self.s.utterance.job_done("hello", 0)
        self.s.utterance_id = 7
        self.s.utterance_started_at, self.s.utterance_ended_at = 100.0, 102.0
        self.s.utterance_source = "mic"
        self.s._flush_asr()
        event = self.s.events[-1]
        self.assertEqual(("asr.final", "mock-service", "mic", "7", 100., 102.),
                         tuple(event[k] for k in ("type", "service_id", "source", "utterance_id",
                                                "vad_started_at", "vad_ended_at")))


if __name__ == "__main__":
    unittest.main(verbosity=2)
