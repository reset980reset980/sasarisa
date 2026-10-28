"""Real capture loop with a disposable in-memory input; never opens a device."""
import json
import queue
import re
import sys
import types
import unittest
from unittest.mock import patch

import numpy as np
import speech_worker as worker


class BacklogChecks(unittest.TestCase):
    def run_burst(self, count, age=0, barge=False):
        controls = queue.Queue()
        fed, events, decoders = [], [], []

        class Input:
            def readline(self): return json.dumps({'InitialCapture': True}) + '\n'
            def __iter__(self): return self
            def __next__(self):
                value = controls.get()
                if value is None: raise StopIteration
                return json.dumps(value) + '\n'

        class Decoder:
            hint_count = 0
            sequence = 1
            def __init__(self, cfg): self.discarding = False; decoders.append(self)
            def reset(self): self.sequence += 1; self.discarding = False
            def resync(self, lag=0): self.reset(); self.discarding = True; worker.emit('resync', utterance=self.sequence, lag_ms=round(lag * 1000))
            def feed(self, samples):
                number = round(float(samples[0]) * 1000)
                fed.append(number)
                if len(fed) >= 2 and not barge: controls.put({'type': 'shutdown'})

        class Stream:
            def __init__(self, **options): self.callback = options['callback']
            def __enter__(self):
                captured = worker.time.monotonic() - age
                with patch.object(worker.time, 'monotonic', return_value=captured):
                    for number in range(1, count + 1):
                        self.callback(np.full((1600, 1), number / 1000, dtype=np.float32), 1600, None, None)
                return self
            def __exit__(self, *args): pass

        def emit(kind, **data):
            if kind == 'barge': data['discarding'] = decoders[0].discarding
            events.append((kind, data))
            if age and kind == 'resync': controls.put({'type': 'shutdown'})
            if barge and kind == 'resync': controls.put({'type': 'barge'})
            if barge and kind == 'barge': controls.put({'type': 'shutdown'})
        with patch.object(worker, 'Recognizer', Decoder), patch.object(worker, 'emit', emit), \
                patch.object(sys, 'stdin', Input()), patch.object(sys, 'argv', ['speech_worker.py']), \
                patch.dict(sys.modules, {'sounddevice': types.SimpleNamespace(InputStream=Stream)}):
            try: worker.main()
            finally: controls.put(None)
        return fed, events

    def test_overload_discards_the_already_dequeued_oldest_frame(self):
        fed, events = self.run_burst(22)
        self.assertTrue(fed)
        self.assertEqual(fed[0], 21, 'Discarded oldest audio was still passed to recognition')

    def test_full_callback_queue_preserves_the_latest_audio(self):
        fed, events = self.run_burst(35)
        self.assertTrue(fed)
        self.assertEqual(fed[0], 34, 'Full queue kept old audio instead of the latest input')

    def decoder(self, activation='voice'):
        class Asr:
            text = '자비스 메모장 열어'
            endpoint = False
            def create_stream(self): return types.SimpleNamespace(accept_waveform=lambda *args: None, input_finished=lambda: None)
            def is_ready(self, stream): return False
            def get_result(self, stream): return self.text
            def is_endpoint(self, stream): return self.endpoint
        decoder = worker.Recognizer.__new__(worker.Recognizer)
        decoder.cfg = {'Activation': activation}; decoder.asr = Asr(); decoder.kws = None
        decoder.input_gain = 1; decoder.next_gain_warning = 0; decoder.sequence = 0; decoder.wake = re.compile('자비스')
        decoder.reset()
        return decoder

    def test_incomplete_audio_never_emits_wake_partial_final_or_whisper_audio(self):
        decoder = self.decoder(); decoder.cfg['WhisperMode'] = 'cpu'; events = []
        with patch.object(worker, 'emit', lambda kind, **data: events.append((kind, data))):
            decoder.resync(); decoder.feed(np.ones(worker.CHUNK, dtype=np.float32) * .01); decoder.finish()
        self.assertEqual([kind for kind, data in events], ['resync'])
        self.assertEqual(events[0][1]['utterance'], 2)

    def test_short_endpoint_recovers_and_only_the_next_complete_command_is_emitted(self):
        decoder = self.decoder(); events = []
        with patch.object(worker, 'emit', lambda kind, **data: events.append((kind, data))):
            decoder.resync(); decoder.feed(np.zeros(worker.CHUNK, dtype=np.float32))
            decoder.asr.endpoint = True; decoder.feed(np.zeros(worker.CHUNK, dtype=np.float32))
            self.assertFalse(decoder.discarding)
            decoder.asr.endpoint = False; decoder.asr.text = '자비스 지금 몇 시야'
            decoder.feed(np.ones(worker.CHUNK, dtype=np.float32) * .02); decoder.finish()
        finals = [data for kind, data in events if kind == 'final']
        self.assertEqual(len(finals), 1)
        self.assertEqual((finals[0]['command'], finals[0]['utterance']), ('지금 몇 시야', 3))
        self.assertFalse(any('메모장' in data.get('text', '') for kind, data in events))

    def test_length_endpoint_cannot_unblock_a_truncated_command(self):
        decoder = self.decoder(); events = []; samples = np.zeros(worker.CHUNK, dtype=np.float32)
        with patch.object(worker, 'emit', lambda kind, **data: events.append((kind, data))):
            decoder.resync()
            for _ in range(249): decoder.feed(samples)
            decoder.asr.endpoint = True; decoder.feed(samples)
        self.assertTrue(decoder.discarding)
        self.assertEqual([kind for kind, data in events], ['resync'])

    def test_releasing_hold_discards_the_incomplete_command_but_acknowledges_release(self):
        decoder = self.decoder('hold'); events = []
        with patch.object(worker, 'emit', lambda kind, **data: events.append((kind, data))):
            decoder.resync(); decoder.feed(np.zeros(worker.CHUNK, dtype=np.float32)); decoder.finish_held()
        self.assertEqual([kind for kind, data in events], ['resync', 'flushed'])
        self.assertFalse(decoder.discarding)

    def test_normal_input_is_not_dropped_or_reset(self):
        fed, events = self.run_burst(2)
        self.assertEqual(fed, [1, 2])
        self.assertFalse(any(kind in ('resync', 'warning') for kind, data in events))

    def test_old_audio_is_discarded_even_when_the_queue_is_short(self):
        fed, events = self.run_burst(2, age=3)
        self.assertEqual(fed, [])
        notices = [data for kind, data in events if kind == 'resync']
        self.assertTrue(notices)
        self.assertGreaterEqual(notices[0]['lag_ms'], 3000)

    def test_barge_reset_cannot_reenable_an_incomplete_command(self):
        fed, events = self.run_burst(22, barge=True)
        replies = [data for kind, data in events if kind == 'barge']
        self.assertEqual(len(replies), 1)
        self.assertTrue(replies[0]['discarding'])


if __name__ == '__main__': unittest.main()
