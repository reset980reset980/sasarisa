"""Read an existing Korean wake-command WAV into real models, without audio devices."""
import argparse
import json
from pathlib import Path

import numpy as np
import soundfile as sf
import speech_worker as worker


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('wav', type=Path)
    parser.add_argument('report', type=Path)
    args = parser.parse_args()
    audio, sr = sf.read(args.wav, dtype='float32', always_2d=True)
    audio = audio.mean(axis=1)
    if sr != worker.SR:
        audio = np.interp(np.arange(0, len(audio), sr / worker.SR), np.arange(len(audio)), audio).astype(np.float32)
    samples = np.concatenate([audio, np.zeros(worker.SR * 3, dtype=np.float32)])
    rows, events = [], []
    worker.emit = lambda kind, **data: events.append(dict(type=kind, **data))
    for name, cfg, expected in [
        ('Custom list recognizes an explicit second phrase with real Korean ASR and KWS', dict(Name='코코', WakeWords=['도와줘', '자비스']), True),
        ('Replacing the custom list excludes the former name from real Korean ASR and KWS', dict(Name='자비스', WakeWords=['안녕 도우미', '이리 와봐']), False),
        ('Clearing overrides restores the normal name with real Korean ASR and KWS', dict(Name='자비스', WakeWords=[]), True),
    ]:
        events.clear()
        decoder = worker.Recognizer(dict(cfg, Kws=True, Activation='voice', Threads=2, WhisperMode='off'))
        for pos in range(0, len(samples), worker.CHUNK): decoder.feed(samples[pos:pos + worker.CHUNK])
        decoder.finish()
        finals = [event for event in events if event['type'] == 'final']
        wake = any(event['type'] == 'wake' for event in events)
        passed = bool(finals) and decoder.kws is not None and wake == expected and any(event['has_wake'] for event in finals) == expected
        if expected:
            passed = passed and any('몇' in event['command'] and '자비스' not in event['command'] for event in finals)
        rows.append(dict(name=name, passed=passed, keywords=decoder.keyword_names, finals=finals, wakes=[event for event in events if event['type'] == 'wake']))
    result = dict(total=len(rows), failed=sum(not row['passed'] for row in rows), worker=str(Path(worker.__file__).resolve()), microphone=False, speaker=False, wav=str(args.wav.resolve()), checks=rows)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({key: result[key] for key in ('total', 'failed', 'worker')}, ensure_ascii=False))
    return bool(result['failed'])


if __name__ == '__main__': raise SystemExit(main())
