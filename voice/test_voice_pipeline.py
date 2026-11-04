"""Run actual production VAD/ASR processing with generated PCM, no device/speaker."""
import argparse, json, threading, time, wave
from pathlib import Path
import numpy as np
from voice_server import VoiceServer, load_wav_16k, PROC_SR, BLOCK48

def run(root):
    root=Path(root);phrases=json.loads((root/'phrases.json').read_text(encoding='utf-8'))
    server=VoiceServer();server.aec_enabled=False;server.running=True
    worker=threading.Thread(target=server._process_loop,daemon=True);worker.start()
    results=[]
    try:
        for index,phrase in enumerate(phrases):
            src=load_wav_16k(root/f'sample_{index}.mp3')
            with wave.open(str(root/f'sample_{index}.wav'),'wb') as out:
                out.setparams((1,2,16000,0,'NONE','not compressed'));out.writeframes(np.clip(src*32767,-32768,32767).astype('<i2').tobytes())
            for gain in (1.0,0.35):
                baseline=server.event_seq
                # Only 50ms initial silence, exercising early onset rather than a long warmup.
                samples=np.concatenate([np.zeros(800,dtype=np.float32),src*gain,np.zeros(24000,dtype=np.float32)])
                pcm48=np.repeat(samples,3)
                for pos in range(0,len(pcm48),BLOCK48):
                    block=pcm48[pos:pos+BLOCK48]
                    if len(block)<BLOCK48:block=np.pad(block,(0,BLOCK48-len(block)))
                    server.mic_q.put(block,timeout=5)
                end=time.time()+12;text=''
                while time.time()<end:
                    final=[e for e in server.events if e['seq']>=baseline and e['type']=='asr.final']
                    if final:
                        text='，'.join(e['text'] for e in final);break
                    time.sleep(.05)
                norm=lambda x:''.join(c for c in x if '\u4e00'<=c<='\u9fff')
                prefix=norm(phrase)[:4]
                events=[e for e in server.events if e['seq']>=baseline]
                result={'source':phrase,'gain':gain,'transcript':text,'expected_prefix':prefix,'prefix_preserved':norm(text).startswith(prefix),'preroll_samples':[e.get('preroll_samples') for e in events if e['type']=='asr.segment'],'final_count':sum(e['type']=='asr.final' for e in events)}
                results.append(result);print(json.dumps(result,ensure_ascii=False),flush=True)
    finally:
        server.running=False;worker.join(2);server.asr_pool.shutdown(wait=True)
        if server.asr_timer:server.asr_timer.cancel()
    report={'scope':'generated audio through actual production processing; no live human microphone acceptance','passed':sum(x['prefix_preserved'] and x['final_count']==1 for x in results),'total':len(results),'cases':results,'stats':server.audio_stats}
    (root/'pipeline_results.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    return report
if __name__=='__main__':
    ap=argparse.ArgumentParser();ap.add_argument('root');args=ap.parse_args();run(args.root)
