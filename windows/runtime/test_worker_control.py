"""Exercise the real worker loop with deterministic microphone/decoder substitutes."""
import io
import json
import queue
import sys
import threading
import time
import types
import unittest
from unittest.mock import patch

import numpy as np
import speech_worker as worker


class ControlChecks(unittest.TestCase):
    def test_capture_fences_and_barge_preroll(self):
        controls = queue.Queue()
        events, feeds, replay = [], [], []
        stage = 'starting'
        levels = 0
        deadline = time.monotonic() + 8

        class Input:
            def readline(self): return json.dumps(dict(InitialCapture=False, Device='')) + '\n'
            def __iter__(self): return self
            def __next__(self):
                line = controls.get()
                if line is None: raise StopIteration
                return json.dumps(line) + '\n'

        class Decoder:
            hint_count = 0
            def __init__(self, cfg): self.sequence = 1
            def reset(self): self.sequence += 1
            def feed(self, samples):
                feeds.append((self.sequence, float(samples[0])))
                if stage == 'replaying': replay.append(float(samples[0]))
            def finish_held(self): self.reset()

        class Stream:
            def __init__(self, **options): self.callback = options['callback']; self.stop = threading.Event()
            def __enter__(self):
                def produce():
                    number = 0
                    while not self.stop.wait(.1):
                        number += 1
                        self.callback(np.full((1600, 1), number / 10000, dtype=np.float32), 1600, None, None)
                        if time.monotonic() > deadline:
                            controls.put(dict(type='shutdown')); break
                self.thread = threading.Thread(target=produce, daemon=True); self.thread.start(); return self
            def __exit__(self, *args): self.stop.set(); self.thread.join(timeout=1)

        def emit(kind, **data):
            nonlocal stage, levels
            events.append(dict(type=kind, **data))
            if kind == 'ready': controls.put(dict(type='capture', enabled=True, request=31))
            elif kind == 'capture' and data['request'] == 31: stage = 'capturing'
            elif kind == 'level' and stage == 'capturing':
                levels += 1
                if levels == 8:
                    controls.put(dict(type='barge')); stage = 'requested'
            elif kind == 'barge' and data['accepted']:
                self.assertEqual(stage, 'requested'); stage = 'replaying'
            elif kind == 'level' and stage == 'replaying':
                stage = 'paused'; controls.put(dict(type='capture', enabled=False, request=32))
            elif kind == 'capture' and data['request'] == 32:
                controls.put(dict(type='barge'))
            elif kind == 'barge' and not data['accepted']:
                stage = 'done'; controls.put(dict(type='shutdown'))

        with patch.object(worker, 'Recognizer', Decoder), patch.object(worker, 'emit', emit), \
                patch.object(sys, 'stdin', Input()), patch.object(sys, 'argv', ['speech_worker.py']), \
                patch.dict(sys.modules, {'sounddevice': types.SimpleNamespace(InputStream=Stream)}):
            try: worker.main()
            finally: controls.put(None)
        self.assertEqual(stage, 'done', 'Control loop did not complete')
        acks = [e for e in events if e['type'] == 'capture']
        self.assertEqual([(e['request'], e['enabled']) for e in acks], [(31, True), (32, False)])
        self.assertGreater(acks[1]['utterance'], acks[0]['utterance'])
        current_request = 0
        for event in events:
            if event['type'] == 'capture': current_request = event['request']
            elif event['type'] == 'level': self.assertEqual(event['capture_request'], current_request, 'Level state belongs to a different control request')
        self.assertEqual(len(replay), 6, 'First six recent chunks were not replayed after the acknowledgement')
        initial = [value for seq, value in feeds if seq == acks[0]['utterance']]
        self.assertEqual(replay, initial[-6:], 'Barge reset lost or reordered the beginning of speech')
        self.assertFalse(any(seq >= acks[1]['utterance'] for seq, value in feeds), 'Disabled capture replayed audio')


if __name__ == '__main__':
    unittest.main()
