"""Import the native speech packages using the distribution's isolated Python."""
import importlib
import json
import platform
import struct
import sys

modules = ["numpy", "sherpa_onnx", "sounddevice", "soundfile", "sentencepiece", "cffi"]
for name in modules:
    importlib.import_module(name)
assert sys.version_info[:2] == (3, 13), sys.version
assert struct.calcsize("P") == 8
assert sys.flags.isolated, "Embedded runtime must ignore external Python environments"
print(json.dumps({"python": platform.python_version(), "bits": 64, "isolated": True, "modules": modules}))
