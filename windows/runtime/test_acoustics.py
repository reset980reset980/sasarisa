import unittest
import numpy as np
from acoustics import periodicity, PreRoll


class AcousticChecks(unittest.TestCase):
    def test_voice_pitch_and_noise_separation(self):
        t = np.arange(1600) / 16000
        for frequency in (80, 180, 350):
            voice = (.2 * np.sin(2 * np.pi * frequency * t)).astype(np.float32)
            self.assertGreater(periodicity(voice), .42)
        noise = np.random.default_rng(123).normal(0, .2, 1600).astype(np.float32)
        self.assertLess(periodicity(noise), .42)

    def test_silence_short_and_invalid_samples(self):
        for data in (np.zeros(1600), np.ones(100), np.full(1600, np.nan)):
            self.assertEqual(periodicity(data), 0)

    def test_pre_roll_is_bounded_fresh_and_resettable(self):
        roll = PreRoll()
        for i in range(10):
            roll.append(np.array([i], dtype=np.float32), i / 10)
        self.assertEqual([int(x[0]) for x in roll.recent(1.0)], list(range(4, 10)))
        self.assertEqual(roll.recent(2.0), [])
        roll.clear()
        self.assertEqual(roll.recent(1.0), [])


if __name__ == "__main__":
    unittest.main()
