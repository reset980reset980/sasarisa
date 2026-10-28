"""Exercise the real segmentation adapter with an in-memory VAD; no device I/O."""
import base64
import io
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import wave

import numpy as np
import multilingual as ml


class Vad:
    def __init__(self, *args, **kwargs): self.items, self.speech = [], False
    def reset(self): self.items.clear(); self.speech = False
    def accept_waveform(self, data): pass
    def is_speech_detected(self): return self.speech
    def empty(self): return not self.items
    @property
    def front(self): return SimpleNamespace(samples=self.items[0])
    def pop(self): self.items.pop(0)
    def flush(self): pass


class MultilingualChecks(unittest.TestCase):
    def create(self, **changes):
        events = []
        cfg = dict(Language='en', WhisperMode='cpu', Activation='voice')
        cfg.update(changes)
        with patch.object(ml, 'model_path', return_value=Path('unused.onnx')), patch.object(ml.sherpa_onnx, 'VoiceActivityDetector', Vad):
            instance = ml.MultilingualRecognizer(cfg, lambda kind, **data: events.append(dict(type=kind, **data)))
        return instance, events

    def test_languages_and_connection_are_checked_before_loading(self):
        for language in ml.LANGUAGES:
            self.assertEqual(ml.validate_language(dict(Language=language)), language)
        for language in (None, '', 'auto', 'EN', 'en\n', []):
            with self.assertRaises(ValueError): ml.validate_language(dict(Language=language))
        with patch.object(ml, 'model_path', side_effect=AssertionError('Loaded prematurely')):
            with self.assertRaises(ValueError): ml.MultilingualRecognizer(dict(Language='en'), lambda *args, **kwargs: None)

    def test_silence_emits_no_audio_and_storage_stays_bounded(self):
        instance, events = self.create()
        for _ in range(700): instance.feed(np.zeros(1600, np.float32))
        instance.finish()
        self.assertEqual(events, [])
        self.assertLess(len(instance.buffer), 512)
        self.assertEqual(instance.held, [])

    def test_final_carries_wav_only_and_monotone_utterance_id(self):
        instance, events = self.create()
        for _ in range(2):
            instance.vad.items.append(np.ones(8000, np.float32) * .1)
            instance.feed(np.zeros(1600, np.float32))
        self.assertEqual([x['utterance'] for x in events], [1, 2])
        self.assertTrue(all(x['needs_transcription'] and not x['has_wake'] and not x['text'] and not x['command'] for x in events))
        with wave.open(io.BytesIO(base64.b64decode(events[0]['wav']))) as wav:
            self.assertEqual((wav.getframerate(), wav.getnchannels(), wav.getsampwidth(), wav.getnframes()), (16000, 1, 2, 8000))

    def test_hold_waits_for_release_and_never_sends_silence(self):
        instance, events = self.create(Activation='hold')
        instance.vad.speech = True
        instance.feed(np.ones(8000, np.float32) * .1)
        instance.vad.items.append(np.ones(8000, np.float32) * .1)
        instance.feed(np.zeros(1600, np.float32))
        self.assertEqual(events, [])
        instance.finish_held()
        self.assertEqual([x['type'] for x in events], ['final', 'flushed'])
        instance.finish_held()
        self.assertEqual([x['type'] for x in events], ['final', 'flushed', 'flushed'])

    def test_hold_gain_and_thirty_second_bound(self):
        instance, events = self.create(Activation='hold', InputGain=2)
        instance.vad.speech = True
        for _ in range(300): instance.feed(np.ones(1600, np.float32) * .1)
        finals = [x for x in events if x['type'] == 'final']
        self.assertEqual(len(finals), 1)
        self.assertEqual(finals[0]['duration'], 30)
        with wave.open(io.BytesIO(base64.b64decode(finals[0]['wav']))) as wav:
            pcm = np.frombuffer(wav.readframes(4), '<i2')
            self.assertTrue(np.all(np.abs(pcm / 32767 - .2) < .001))
        self.assertEqual(instance.held, [])

    def test_resync_discards_truncated_speech_and_keeps_length_quarantine(self):
        instance, events = self.create()
        instance.resync(2)
        instance.vad.speech = True
        instance.vad.items.append(np.ones(25 * ml.SR, np.float32) * .1)
        instance.feed(np.zeros(1600, np.float32))
        self.assertTrue(instance.discarding)
        instance.vad.items.append(np.ones(ml.SR, np.float32) * .1)
        instance.feed(np.zeros(1600, np.float32))
        self.assertFalse(instance.discarding)
        self.assertEqual([x['type'] for x in events], ['resync'])
        instance.vad.items.append(np.ones(ml.SR, np.float32) * .1)
        instance.feed(np.zeros(1600, np.float32))
        self.assertEqual([x['type'] for x in events], ['resync', 'final'])

    def test_hold_resync_cannot_flush_damaged_audio(self):
        instance, events = self.create(Activation='hold')
        instance.resync(2); instance.vad.speech = True
        instance.feed(np.ones(16000, np.float32) * .1)
        instance.vad.items.append(np.ones(16000, np.float32) * .1)
        instance.vad.speech = False
        instance.feed(np.zeros(32000, np.float32))
        self.assertFalse(instance.discarding)
        instance.finish_held()
        self.assertEqual([x['type'] for x in events], ['resync', 'flushed'])

    def test_invalid_gain_silence_and_samples_are_rejected(self):
        for field, values in (('InputGain', [0, 21, float('nan')]), ('SilenceSeconds', [.1, 4, float('nan')])):
            for value in values:
                with self.assertRaises(ValueError): self.create(**{field: value})
        instance, _ = self.create()
        with self.assertRaises(ValueError): instance.feed(np.array([float('nan')]))
        with self.assertRaises(ValueError): instance.feed(np.zeros((2, 2)))


if __name__ == '__main__': unittest.main()
