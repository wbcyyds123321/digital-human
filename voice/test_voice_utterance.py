import unittest
import numpy as np
from voice_utterance import PCMHistory, UtteranceBuffer

class AudioTests(unittest.TestCase):
    def test_preroll_keeps_onset(self):
        h=PCMHistory(100,40,0.35); x=np.arange(200,dtype=np.float32); h.append(x)
        padded, n=h.segment(80,x[80:120]); self.assertEqual(n,35); np.testing.assert_array_equal(padded,x[45:120])
    def test_no_duplicate_words_across_segments(self):
        h=PCMHistory(100,40,0.35); x=np.arange(200,dtype=np.float32); h.append(x)
        h.segment(80,x[80:120]); padded,n=h.segment(130,x[130:170]); self.assertEqual(n,10);np.testing.assert_array_equal(padded,x[120:170])
    def test_initial_segment_clamps_to_available_audio(self):
        h=PCMHistory(100,40,0.35);h.append(np.arange(100,dtype=np.float32));p,n=h.segment(10,np.arange(10,50,dtype=np.float32));self.assertEqual(n,10);self.assertEqual(p[0],0)
    def test_bounded_retention(self):
        h=PCMHistory(100,1,0.35)
        for i in range(30):h.append(np.ones(20,dtype=np.float32)*i)
        self.assertLessEqual(sum(len(x) for _,x in h.blocks),120)
    def test_fallback_when_segment_is_not_retained(self):
        h=PCMHistory(100,1,0.35);p,n=h.segment(20,np.ones(10,dtype=np.float32));self.assertEqual(len(p),10);self.assertEqual(n,0)
    def test_async_pending_blocks_flush(self):
        b=UtteranceBuffer();b.job_start();b.job_done('回原点',0);b.job_start();self.assertFalse(b.ready(5));b.job_done('挥手',5);self.assertFalse(b.ready(5.7));self.assertTrue(b.ready(6))
    def test_resumed_speech_holds_first_half(self):
        b=UtteranceBuffer();b.speech_start(0);b.speech_end(1);b.job_start();b.job_done('先回原点然后',1.2);b.speech_start(1.6);self.assertFalse(b.ready(4));b.speech_end(5);b.job_start();b.job_done('挥舞双手',5.1);self.assertTrue(b.ready(6));self.assertEqual(b.take(),'先回原点然后，挥舞双手')
    def test_empty_asr_never_emits(self):
        b=UtteranceBuffer();b.job_start();b.job_done(' ',1);self.assertFalse(b.ready(4))
    def test_take_only_once(self):
        b=UtteranceBuffer();b.job_start();b.job_done('回原点',0);self.assertTrue(b.ready(1));self.assertEqual(b.take(),'回原点');self.assertFalse(b.ready(2))
    def test_new_speech_after_end_does_not_flush_old_timer(self):
        b=UtteranceBuffer();b.job_start();b.job_done('第一句',0);b.speech_start(.7);self.assertFalse(b.ready(.9));b.speech_end(2);self.assertFalse(b.ready(2.5));self.assertTrue(b.ready(3))
if __name__=='__main__':unittest.main(verbosity=2)
