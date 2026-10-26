"""Vocabulary integration checks against the installed Korean model."""
import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np
from speech_worker import Recognizer, make_asr, vocabulary_tokens, SR


class VocabularyChecks(unittest.TestCase):
    def test_supported_phrases_duplicates_and_unrecognized_symbols(self):
        words, skipped = vocabulary_tokens(["메모장", "메모장", "인천상아초등학교", "🦄", ":not-a-score"])
        self.assertEqual(words, ["메모장", "인천상아초등학교"])
        self.assertEqual(skipped, 2)

    def test_invalid_limits_are_rejected_before_native_decoder(self):
        for words in (["bad\nword"], ["a"] * 151, "not-a-list"):
            with self.assertRaises(ValueError):
                vocabulary_tokens(words)

    def test_real_decoder_loads_hints_and_temp_files_are_removed(self):
        with tempfile.TemporaryDirectory() as folder:
            with patch.object(tempfile, "tempdir", folder):
                asr, count = make_asr({"Text": {"CustomWords": ["메모장", "인천상아초등학교"]}})
                self.assertEqual(count, 2)
                self.assertEqual(list(Path(folder).iterdir()), [])
                stream = asr.create_stream()
                stream.accept_waveform(SR, np.zeros(SR, np.float32))
                stream.input_finished()
                while asr.is_ready(stream):
                    asr.decode_stream(stream)
                self.assertEqual(asr.get_result(stream).strip(), "")

    def test_default_decoder_and_held_hint_stream_flush(self):
        capture = io.StringIO()
        with contextlib.redirect_stdout(capture):
            default = Recognizer({"Kws": False})
            self.assertEqual(default.hint_count, 0)
            hinted = Recognizer({"Kws": False, "Activation": "hold", "Text": {"CustomWords": ["메모장"]}})
            for _ in range(12):
                hinted.feed(np.zeros(1600, np.float32))
            hinted.finish_held()
            self.assertEqual(hinted.hint_count, 1)
        events = [json.loads(line) for line in capture.getvalue().splitlines()]
        self.assertEqual(sum(e["type"] == "flushed" for e in events), 1)
        self.assertFalse(any(e["type"] == "final" for e in events))

    def test_keyword_spotter_uses_selected_model_not_development_directory(self):
        capture = io.StringIO()
        with tempfile.TemporaryDirectory() as folder, patch("speech_worker.WINDOWS", Path(folder)), contextlib.redirect_stdout(capture):
            recognizer = Recognizer({"Kws": True})
            self.assertIsNotNone(recognizer.kws)
            recognizer.feed(np.zeros(1600, np.float32))
        events = [json.loads(line) for line in capture.getvalue().splitlines()]
        self.assertFalse(any(e["type"] == "warning" for e in events))

    def test_spaced_wake_name_loads_real_keyword_spotter_without_native_abort(self):
        capture = io.StringIO()
        with tempfile.TemporaryDirectory() as folder, patch.object(tempfile, "tempdir", folder), contextlib.redirect_stdout(capture):
            recognizer = Recognizer({"Kws": True, "Name": "세션 시험"})
            self.assertIsNotNone(recognizer.kws)
            recognizer.feed(np.zeros(1600, np.float32))
            self.assertEqual(list(Path(folder).iterdir()), [])
        self.assertFalse(any(json.loads(line)["type"] == "warning" for line in capture.getvalue().splitlines()))

    def test_unsupported_wake_name_preserves_asr_without_calling_native_keyword_loader(self):
        for name in ("🦄", "자비스\n#0.0", "자비스 @other"):
            capture = io.StringIO()
            with patch("speech_worker.sherpa_onnx.KeywordSpotter") as native, contextlib.redirect_stdout(capture):
                recognizer = Recognizer({"Kws": True, "Name": name})
                native.assert_not_called()
                self.assertIsNone(recognizer.kws)
                recognizer.feed(np.zeros(1600, np.float32))
            self.assertTrue(any(json.loads(line)["type"] == "warning" for line in capture.getvalue().splitlines()))


if __name__ == "__main__":
    unittest.main()
