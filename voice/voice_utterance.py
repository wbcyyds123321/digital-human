"""Pure audio/utterance utilities. No device or model/network side effects."""
from collections import deque
import numpy as np

class PCMHistory:
    def __init__(self, sample_rate=16000, max_seconds=40, preroll_seconds=0.35):
        self.sample_rate = sample_rate
        self.capacity = int(max_seconds * sample_rate)
        self.preroll = int(preroll_seconds * sample_rate)
        self.blocks = deque()
        self.total = 0
        self.last_segment_end = 0

    def append(self, samples):
        samples = np.asarray(samples, dtype=np.float32).copy()
        self.blocks.append((self.total, samples))
        self.total += len(samples)
        cutoff = self.total - self.capacity
        while self.blocks and self.blocks[0][0] + len(self.blocks[0][1]) <= cutoff:
            self.blocks.popleft()

    def segment(self, start, samples):
        samples = np.asarray(samples, dtype=np.float32)
        raw_end = int(start) + len(samples)
        available = self.blocks[0][0] if self.blocks else self.total
        begin = max(available, self.last_segment_end, int(start) - self.preroll)
        end = min(raw_end, self.total)
        pieces = [block[max(0, begin-pos):min(len(block), end-pos)]
                  for pos, block in self.blocks if pos < end and pos + len(block) > begin]
        self.last_segment_end = max(self.last_segment_end, raw_end)
        if not pieces:
            return samples.copy(), 0
        return np.concatenate(pieces), max(0, int(start)-begin)

class UtteranceBuffer:
    """Caller serializes operations. Never flush while user talks or ASR is pending."""
    def __init__(self, merge_seconds=0.8):
        self.merge_seconds = merge_seconds
        self.parts = []
        self.speaking = False
        self.pending = 0
        self.last_activity = 0.0
    def speech_start(self, now):
        self.speaking = True
        self.last_activity = now
    def speech_end(self, now):
        self.speaking = False
        self.last_activity = now
    def job_start(self):
        self.pending += 1
    def job_done(self, text, now):
        self.pending = max(0, self.pending-1)
        if text.strip():
            self.parts.append(text.strip())
        self.last_activity = now
    def ready(self, now):
        return bool(self.parts) and not self.speaking and self.pending == 0 and now-self.last_activity >= self.merge_seconds
    def take(self):
        text = '，'.join(self.parts)
        self.parts.clear()
        return text
