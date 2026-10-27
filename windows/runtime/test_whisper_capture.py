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
    def finish(self, settings):
        decoder = object.__new__(worker.Recognizer)
        decoder.cfg = settings
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


if __name__ == '__main__':
    unittest.main()
