"""Verify optional in-memory audio handoff without opening a microphone."""
import base64
import io
import re
import unittest
import wave
from unittest.mock import patch
import numpy as np
import speech_worker as worker


class WhisperCaptureChecks(unittest.TestCase):
    def silent_take(self, settings, chunks):
        decoder = object.__new__(worker.Recognizer)
        decoder.cfg = settings; decoder.text = ''; decoder.wake = re.compile('사사리사'); decoder.discarding = False
        decoder.woken = False; decoder.sequence = 11; decoder.audio = chunks
        decoder.reset = lambda: None
        events = []
        with patch.object(worker, 'emit', lambda kind, **data: events.append((kind, data))):
            decoder.finish()
        return events

    def finish(self, settings):
        decoder = object.__new__(worker.Recognizer)
        decoder.cfg = settings
        decoder.discarding = False
        decoder.text = '자비스 지금 몇 시야'
        decoder.wake = re.compile('자비스')
        decoder.woken = True
        decoder.sequence = 7
        decoder.audio = [np.array([0.0, 0.5, -0.5], dtype=np.float32)]
        decoder.reset = lambda: None
        events = []
        with patch.object(worker, 'emit', lambda kind, **data: events.append((kind, data))):
            decoder.finish()
        self.assertEqual(events[0][0], 'final')
        return events[0][1]

    def test_managed_and_legacy_servers_receive_pcm_wave(self):
        for settings in ({'WhisperMode': 'cpu'}, {'WhisperMode': 'cuda'}, {'WhisperUrl': 'http://127.0.0.1:1234'}):
            with self.subTest(settings=settings):
                event = self.finish(settings)
                with wave.open(io.BytesIO(base64.b64decode(event['wav'])), 'rb') as audio:
                    self.assertEqual((audio.getnchannels(), audio.getsampwidth(), audio.getframerate()), (1, 2, worker.SR))
                    np.testing.assert_array_equal(np.frombuffer(audio.readframes(3), dtype='<i2'), [0, 16383, -16383])
                self.assertEqual(event['command'], '지금 몇 시야')

    def test_off_empty_and_unknown_modes_do_not_attach_audio(self):
        for settings in ({}, {'WhisperMode': 'off', 'WhisperUrl': 'http://127.0.0.1:1234'}, {'WhisperMode': 'external'}, {'WhisperMode': 'unknown'}):
            with self.subTest(settings=settings):
                self.assertNotIn('wav', self.finish(settings))

    def test_no_text_latin_name_hands_off_only_the_short_voiced_segment(self):
        quiet = np.zeros(worker.CHUNK, dtype=np.float32)
        voice = (.03 * np.sin(2 * np.pi * 180 * np.arange(worker.CHUNK) / worker.SR)).astype(np.float32)
        events = self.silent_take({'Name': 'Sasarisa', 'WhisperMode': 'cuda'}, [quiet] * 15 + [voice] * 6 + [quiet] * 10)
        self.assertEqual(len(events), 1)
        kind, data = events[0]
        self.assertEqual((kind, data['text'], data['command'], data['has_wake'], data['utterance']), ('final', '', '', False, 11))
        self.assertAlmostEqual(data['duration'], .9)
        with wave.open(io.BytesIO(base64.b64decode(data['wav'])), 'rb') as audio:
            self.assertEqual(audio.getnframes(), worker.CHUNK * 9)

    def test_empty_silence_noise_click_long_voice_and_ineligible_modes_emit_nothing(self):
        quiet = np.zeros(worker.CHUNK, dtype=np.float32)
        voice = (.03 * np.sin(2 * np.pi * 180 * np.arange(worker.CHUNK) / worker.SR)).astype(np.float32)
        noise = np.random.default_rng(31).normal(0, .02, worker.CHUNK).astype(np.float32)
        click = quiet.copy(); click[800] = .9
        cfg = {'Name': 'Sasarisa', 'WhisperMode': 'cpu'}
        for chunks in ([quiet] * 20, [noise] * 10, [quiet, click] + [quiet] * 10, [voice] * 46, [voice] * 3, [voice * .01] * 8):
            self.assertEqual(self.silent_take(cfg, chunks), [])
        for changes in ({'WhisperMode': 'off'}, {'WhisperMode': 'external'}, {'Activation': 'hold'}, {'Activation': 'hotkey'}, {'Name': '자비스'}, {'Name': ''}):
            self.assertEqual(self.silent_take(cfg | changes, [voice] * 8), [])


if __name__ == '__main__':
    unittest.main()
