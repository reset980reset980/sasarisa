"""Exercise hold-to-talk boundaries with a synthetic WAV; report accuracy separately.

Usage: python runtime/check_hold.py artifacts/hold-speech.wav
This does not open a microphone or execute a recognized command.
"""
import contextlib
import io
import json
from pathlib import Path
import sys
import numpy as np
import soundfile as sf
from speech_worker import Recognizer, SR, CHUNK

x, sr = sf.read(sys.argv[1], dtype="float32", always_2d=True)
x = x.mean(axis=1)
x = np.interp(np.arange(0, len(x), sr / SR), np.arange(len(x)), x).astype(np.float32)
with contextlib.redirect_stdout(io.StringIO()) as captured:
    recognizer = Recognizer({"Activation": "hold", "Kws": False})
    for offset in range(0, len(x), CHUNK):
        recognizer.feed(x[offset:offset + CHUNK])
    before = [json.loads(line) for line in captured.getvalue().splitlines() if line.startswith("{")]
    assert not any(row["type"] == "final" for row in before), "Command emitted before release"
    recognizer.finish_held()
rows = [json.loads(line) for line in captured.getvalue().splitlines() if line.startswith("{")]
finals = [row for row in rows if row["type"] == "final"]
assert len(finals) == 1 and finals[0]["command"], "Expected one nonempty result after release"
assert rows[-1]["type"] == "flushed", "Missing completion signal"
report = {
    "test": "hold-to-talk decoder boundaries", "pass": True,
    "earlyFinals": 0, "finalsAfterRelease": 1,
    "expectedSyntheticPhrase": "메모장 열어", "recognized": finals[0]["command"],
    "syntheticPhraseMatched": finals[0]["command"].strip(" .!?") == "메모장 열어",
    "note": "Boundary success is not evidence of recognition accuracy."
}
target = Path(__file__).resolve().parents[1] / "artifacts" / "hold-check.json"
target.parent.mkdir(exist_ok=True)
target.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(report, ensure_ascii=False))
