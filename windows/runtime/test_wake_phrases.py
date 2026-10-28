"""Wake overrides use the same choices in ASR, KWS and optional hints; no audio devices."""
from pathlib import Path
import unittest
from unittest.mock import Mock, patch

import numpy as np
import speech_worker as worker


class WakePhraseChecks(unittest.TestCase):
    def test_explicit_list_replaces_name_aliases_and_default_fuzzy_patterns(self):
        cfg = dict(Name='자비스', LearnedWakes={'자비스': ['자빗스']}, WakeWords=['코코', '코코 도와줘', '음', 'A+B'])
        pattern = worker.wake_pattern(cfg)
        for text in ['자비스', '자빗스', '재비스', 'jarvis', 'AAAB']:
            self.assertIsNone(pattern.search(text), text)
        for text, expected in [('코 코 도와줘 몇 시야', '코 코 도와줘'), ('음 몇 시야', '음'), ('A+B 몇 시야', 'A+B')]:
            self.assertEqual(pattern.search(text).group(), expected)
        cfg['WakeWords'] = []
        self.assertTrue(worker.wake_pattern(cfg).search('자비스'))
        self.assertTrue(worker.wake_pattern(cfg).search('자빗스'))

    def test_bad_lists_are_rejected_before_loading_any_native_model(self):
        for words in [None, '코코', [None], [''], [' '], ['!'], ['줄\n바꿈'], ['가' * 41], ['코코'] * 33]:
            with patch.object(worker, 'make_asr') as load:
                with self.assertRaises(ValueError): worker.Recognizer({'WakeWords': words})
                load.assert_not_called()

    def test_wake_hints_use_active_phrases_and_do_not_change_custom_word_count(self):
        captured = []
        def load(**options):
            captured.append(Path(options['hotwords_file']).read_text(encoding='utf-8').splitlines())
            return object()
        with patch.object(worker.sherpa_onnx.OnlineRecognizer, 'from_transducer', side_effect=load):
            _, count = worker.make_asr(dict(Name='자비스', WakeWords=['코코', '도와줘'], WakeHints=True, Text={'CustomWords': ['메모장']}))
        self.assertEqual(captured, [['메모장', '코코', '도와줘']])
        self.assertEqual(count, 1)

    def test_kws_loads_multiple_safe_ids_and_keeps_asr_fallback_per_unsupported_phrase(self):
        lines = []
        def encode(word, model, label):
            if word == 'A+B': raise ValueError('unsupported fixture')
            return 'token @' + label + '\n'
        def spotter(**options):
            lines.extend(Path(options['keywords_file']).read_text(encoding='utf-8').splitlines())
            return Mock()
        with patch.object(worker, 'make_asr', return_value=(Mock(), 0)), patch.object(worker, 'encode_keyword', side_effect=encode) as encoded, patch.object(worker.sherpa_onnx, 'KeywordSpotter', side_effect=spotter), patch.object(worker, 'emit'):
            decoder = worker.Recognizer(dict(Name='자비스', WakeWords=['코코', 'A+B', '도와줘']))
        self.assertEqual([call.args[0] for call in encoded.call_args_list], ['코코', 'A+B', '도와줘'])
        self.assertEqual(lines, ['token @wake_0', 'token @wake_1'])
        self.assertEqual(decoder.keyword_names, {'wake_0': '코코', 'wake_1': '도와줘'})
        self.assertIsNotNone(decoder.wake.search('A+B 몇 시야'))
        self.assertIsNone(decoder.wake.search('자비스 몇 시야'))

    def test_entirely_unsupported_kws_list_never_reenables_the_default_name(self):
        with patch.object(worker, 'make_asr', return_value=(Mock(), 0)), patch.object(worker, 'encode_keyword', side_effect=ValueError('unsupported')), patch.object(worker.sherpa_onnx, 'KeywordSpotter') as native, patch.object(worker, 'emit'):
            decoder = worker.Recognizer(dict(Name='자비스', WakeWords=['A+B']))
        native.assert_not_called()
        self.assertIsNone(decoder.kws)
        self.assertIsNone(decoder.wake.search('자비스'))
        self.assertTrue(decoder.wake.search('A+B'))

    def test_empty_whisper_audio_gate_uses_only_active_latin_phrases(self):
        t = np.arange(worker.CHUNK, dtype=np.float32) / worker.SR
        chunks = [(np.sin(t * 2 * np.pi * 200) * .05).astype(np.float32)] * 10
        common = dict(Name='Sasarisa', Activation='voice', WhisperMode='cpu')
        self.assertEqual(worker.missed_name_audio(dict(common, WakeWords=['코코']), chunks), [])
        self.assertTrue(worker.missed_name_audio(dict(common, WakeWords=['hello']), chunks))


if __name__ == '__main__': unittest.main()
