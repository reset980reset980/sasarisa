import unittest
from speech_worker import wake_pattern


class NameChecks(unittest.TestCase):
    def test_learned_phrase_and_command_boundary(self):
        cfg = dict(Name='사사리사', LearnedWakes={'사사리사': ['서서', '서서 있어']})
        pattern = wake_pattern(cfg)
        text = '헤이 서 서 있 어, 타이머 3분'
        match = pattern.search(text)
        self.assertIsNotNone(match)
        self.assertEqual(text[match.end():].strip(' ,.!?'), '타이머 3분')

    def test_aliases_do_not_cross_names(self):
        pattern = wake_pattern(dict(Name='코코', LearnedWakes={'사사리사': ['서서 있어']}))
        self.assertIsNone(pattern.search('서서 있어'))
        self.assertIsNotNone(pattern.search('코 코'))

    def test_old_names_and_spacing_are_preserved(self):
        pattern = wake_pattern(dict(Name='자비스'))
        for phrase in ('자비스', '재 비 스', '헤이 jarvis'):
            self.assertIsNotNone(pattern.search(phrase))
        self.assertIsNotNone(wake_pattern(dict(Name='사사 리사')).search('사 사 리 사'))
        self.assertIsNotNone(wake_pattern(dict(Name='sasarisa')).search('사사리사'))
        symbols = wake_pattern(dict(Name='C++'))
        self.assertIsNone(symbols.search('C 드라이브 열어'))
        self.assertIsNotNone(symbols.search('C++ 메모장 열어'))

    def test_invalid_aliases_are_rejected_before_native_decoder(self):
        for value in ('그.*만', '', 'a', '가' * 13, '줄\n바꿈'):
            with self.assertRaises(ValueError): wake_pattern(dict(Name='코코', LearnedWakes={'코코': [value]}))
        with self.assertRaises(ValueError): wake_pattern(dict(Name='코코', LearnedWakes=[]))


if __name__ == '__main__':
    unittest.main()
