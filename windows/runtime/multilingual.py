"""Language-independent speech segmentation; recognition is owned by the app.

The pinned Silero model only detects speech. No audio, transcript or network
request is persisted here. This module never opens an audio device.
"""
import base64
import hashlib
import io
from pathlib import Path
import time
import wave

import numpy as np
import sherpa_onnx

SR = 16000
LANGUAGES = ('ko', 'en', 'ja', 'zh', 'es', 'fr', 'de', 'pt', 'it', 'vi', 'th', 'id')
VAD_NAME = 'silero_vad.onnx'
VAD_SHA256 = '9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6'
VAD_LENGTH = 643854
VAD_URL = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx'


def model_path():
    root = Path(__file__).resolve().parents[1]
    packaged = root / 'runtime' / 'assets' / VAD_NAME
    path = packaged if packaged.exists() else root / 'artifacts' / 'package-cache' / VAD_NAME
    if not path.is_file() or path.stat().st_size != VAD_LENGTH or hashlib.sha256(path.read_bytes()).hexdigest() != VAD_SHA256:
        raise ValueError('다국어 말 감지 파일이 없거나 손상됐어요. Windows 설치 프로그램으로 복구해 주세요.')
    return path


def validate_language(cfg):
    language = cfg.get('Language', 'ko')
    if language not in LANGUAGES:
        raise ValueError('지원 언어 목록에서 대화 언어를 선택하세요.')
    return language


class MultilingualRecognizer:
    def __init__(self, cfg, emit):
        validate_language(cfg)
        mode = cfg.get('WhisperMode', 'external')
        if not (mode in ('cpu', 'cuda') or mode == 'external' and cfg.get('WhisperUrl')):
            raise ValueError('다국어 음성 입력에는 로컬 Whisper 엔진이 필요해요. 설정에서 엔진을 준비하거나 서버 주소를 입력하세요.')
        self.cfg, self.emit = cfg, emit
        self.gain = float(cfg.get('InputGain', 1))
        self.silence = float(cfg.get('SilenceSeconds', 1.2))
        if not np.isfinite(self.gain) or not .25 <= self.gain <= 20:
            raise ValueError('인식 입력 증폭은 0.25~20배로 설정하세요.')
        if not np.isfinite(self.silence) or not .4 <= self.silence <= 3:
            raise ValueError('말 끝 대기는 0.4~3초입니다.')
        config = sherpa_onnx.VadModelConfig()
        config.silero_vad.model = str(model_path())
        config.silero_vad.min_silence_duration = self.silence
        config.silero_vad.min_speech_duration = .25
        config.silero_vad.max_speech_duration = 25
        config.sample_rate, config.num_threads = SR, 1
        self.vad = sherpa_onnx.VoiceActivityDetector(config, buffer_size_in_seconds=32)
        self.hint_count, self.sequence, self.next_gain_warning = 0, 0, 0
        self.reset()

    def reset(self):
        self.vad.reset()
        self.buffer = np.empty(0, np.float32)
        self.held, self.samples, self.quiet = [], 0, 0
        self.voiced, self.discarding, self.held_damaged = False, False, False
        self.sequence += 1

    def resync(self, lag=0):
        self.reset()
        self.discarding = True
        self.held_damaged = self.cfg.get('Activation') == 'hold'
        self.emit('resync', utterance=self.sequence, lag_ms=round(max(0, lag) * 1000))

    def _send(self, samples):
        if len(samples) < SR * .25:
            return
        pcm = np.clip(samples * 32767, -32768, 32767).astype('<i2')
        data = io.BytesIO()
        with wave.open(data, 'wb') as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(SR)
            wav.writeframes(pcm.tobytes())
        self.emit('final', text='', command='', has_wake=False, needs_transcription=True,
                  utterance=self.sequence, duration=round(len(samples) / SR, 3),
                  wav=base64.b64encode(data.getvalue()).decode('ascii'))
        self.sequence += 1

    def _drain(self):
        while not self.vad.empty():
            samples = np.asarray(self.vad.front.samples, dtype=np.float32).copy()
            self.vad.pop()
            self.voiced = True
            if self.discarding:
                # A forced length boundary is not proof the damaged utterance ended.
                if len(samples) < SR * 24.5:
                    self.discarding = False
            elif self.cfg.get('Activation') != 'hold':
                self._send(samples)

    def feed(self, samples):
        started = time.perf_counter()
        x = np.asarray(samples, dtype=np.float32)
        if x.ndim != 1 or not np.isfinite(x).all():
            raise ValueError('음성 샘플의 형식을 확인하세요.')
        scaled = x * self.gain
        if self.gain > 1 and np.count_nonzero(np.abs(scaled) > 1) > len(x) * .01 and time.monotonic() >= self.next_gain_warning:
            self.next_gain_warning = time.monotonic() + 10
            self.emit('warning', message='인식 입력 증폭이 커서 소리 일부가 잘리고 있어요. 설정에서 증폭 배수를 낮춰 주세요.')
        x = np.clip(scaled, -1, 1).astype(np.float32)
        if self.cfg.get('Activation') == 'hold':
            available = max(0, SR * 30 - self.samples)
            self.held.append(x[:available].copy())
            self.samples += min(available, len(x))
        self.buffer = np.concatenate((self.buffer, x))
        while len(self.buffer) >= 512:
            self.vad.accept_waveform(self.buffer[:512])
            self.buffer = self.buffer[512:]
            speech = self.vad.is_speech_detected()
            self.voiced |= speech
            self.quiet = 0 if speech else self.quiet + 512
            self._drain()
            if self.discarding and self.quiet >= SR * self.silence:
                self.discarding = False
        if self.cfg.get('Activation') == 'hold' and self.samples >= SR * 30:
            self.finish_held()
        return time.perf_counter() - started

    def finish(self):
        if len(self.buffer):
            self.vad.accept_waveform(np.pad(self.buffer, (0, 512 - len(self.buffer))))
            self.buffer = np.empty(0, np.float32)
        self.vad.flush()
        self._drain()

    def finish_held(self):
        discarding = self.discarding or self.held_damaged
        self.finish()
        if self.cfg.get('Activation') == 'hold' and not discarding and self.voiced and self.held:
            self._send(np.concatenate(self.held))
        self.reset()
        self.emit('flushed')
