"""Signal-only voice periodicity and bounded pre-roll; no recordings or transcripts."""
from collections import deque
import numpy as np


def periodicity(raw):
    raw = np.asarray(raw, dtype=np.float32)
    if len(raw) < 640 or not np.isfinite(raw).all():
        return 0.0
    x = raw.copy()
    x[1:] -= .95 * raw[:-1]
    best = 0.0
    for offset in range(0, len(x) - 639, 320):
        window = x[offset:offset + 640]
        energy = float(np.dot(window, window))
        if energy > 1e-7:
            correlation = np.correlate(window, window, mode="full")[639:]
            best = max(best, float(np.max(correlation[40:229])) / energy)
    return min(1.0, max(0.0, best))


class PreRoll:
    def __init__(self):
        self.chunks = deque(maxlen=6)

    def clear(self):
        self.chunks.clear()

    def append(self, samples, captured_at):
        self.chunks.append((samples, captured_at))

    def recent(self, now):
        return [samples for samples, at in self.chunks if 0 <= now - at <= .7]
