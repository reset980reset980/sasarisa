"""Input gain and optional wake hints; no microphone or speaker is opened."""
import re
import unittest
from unittest.mock import patch

import numpy as np
import speech_worker as worker


class InputTuningChecks(unittest.TestCase):
    def test_invalid_gain_is_rejected_before_native_models_load(self):
        for gain in [float('nan'), float('inf'), -.1, 0, .24, 20.01]:
            with patch.object(worker, 'make_asr') as native:
                with self.assertRaises(ValueError): worker.Recognizer({'InputGain': gain})
                native.assert_not_called()

    def test_gain_clips_without_mutating_or_double_amplifying_capture_preroll(self):
        class Asr:
            def accept_waveform(self, *args): pass
            def is_ready(self, stream): return False
            def get_result(self, stream): return ''
            def is_endpoint(self, stream): return False
        for gain in [.25, 1, 10, 20]:
            decoder = worker.Recognizer.__new__(worker.Recognizer)
            decoder.cfg = {}; decoder.input_gain = gain; decoder.asr = Asr(); decoder.s = decoder.asr
            decoder.audio = []; decoder.text = ''; decoder.woken = False; decoder.kws = None; decoder.wake = re.compile('name'); decoder.discarding = False
            original = np.array([-.8, -.01, 0, .01, .8], dtype=np.float32); untouched = original.copy()
            with patch.object(worker, 'emit') as warning:
                decoder.next_gain_warning = 0
                decoder.feed(original); decoder.feed(original)
                self.assertEqual(warning.call_count, 1 if gain > 1 else 0)
            np.testing.assert_array_equal(original, untouched)
            for recorded in decoder.audio: np.testing.assert_allclose(recorded, np.clip(untouched * gain, -1, 1))

    def test_wake_hint_is_optional_voice_only_and_preserves_custom_word_count(self):
        captured = []
        def load(**options):
            path = options.get('hotwords_file')
            from pathlib import Path
            captured.append(Path(path).read_text(encoding='utf-8').splitlines() if path else [])
            return object()
        with patch.object(worker.sherpa_onnx.OnlineRecognizer, 'from_transducer', side_effect=load):
            for activation in ['voice', 'hotkey', 'hold']:
                _, count = worker.make_asr({'Name': '자비스', 'WakeHints': True, 'Activation': activation, 'Text': {'CustomWords': ['메모장']}})
                self.assertEqual(count, 1)
                self.assertEqual(captured[-1], ['메모장', '자비스'] if activation == 'voice' else ['메모장'])
            worker.make_asr({'Name': '자비스', 'WakeHints': False}); self.assertEqual(captured[-1], [])
            worker.make_asr({'Name': 'sasarisa', 'WakeHints': True}); self.assertIn('사사리사', captured[-1])

    def test_unsupported_name_does_not_drop_custom_hints_or_change_their_count(self):
        with patch.object(worker.sherpa_onnx.OnlineRecognizer, 'from_transducer', return_value=object()):
            _, count = worker.make_asr({'Name': '🦄', 'WakeHints': True, 'Text': {'CustomWords': ['메모장']}})
            self.assertEqual(count, 1)


if __name__ == '__main__': unittest.main()
