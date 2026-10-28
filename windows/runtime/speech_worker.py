"""Local speech worker. JSON lines over stdio; never executes recognized text.

The desktop app owns wake/session/action policy. Audio stays in memory; a WAV is
included only when the user enables managed Whisper or a local Whisper server.
"""
import argparse
import base64
import io
import json
import os
from pathlib import Path
import queue
import re
import sys
import tempfile
import threading
import time
import unicodedata
import wave

import numpy as np
import sherpa_onnx
from acoustics import periodicity, PreRoll
from input_devices import device_options, bind_input
from multilingual import MultilingualRecognizer, validate_language

WINDOWS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WINDOWS / "poc"))
from wake_poc import sound_alike, hangul_reading, model_files, encode_keyword

SR, CHUNK = 16000, 1600


def emit(kind, **data):
    print(json.dumps(dict(type=kind, **data), ensure_ascii=False), flush=True)


def vocabulary_tokens(words):
    """Validate complete BPE phrases; unsupported words cannot create partial hints."""
    if not isinstance(words, list) or len(words) > 150:
        raise ValueError("자주 쓰는 단어 목록을 확인하세요.")
    if not words:
        return [], 0
    import sentencepiece as spm
    model = Path(model_files(True)["tokens"]).parent
    sp = spm.SentencePieceProcessor(model_file=str(model / "bpe.model"))
    vocabulary = {line.rsplit(" ", 1)[0] for line in (model / "tokens.txt").read_text(encoding="utf-8").splitlines()}
    phrases, skipped = [], 0
    for word in dict.fromkeys(words):
        if not isinstance(word, str) or not word.strip() or len(word) > 60 or any(ord(c) < 32 for c in word):
            raise ValueError("자주 쓰는 단어는 한 줄에 1~60자로 입력하세요.")
        pieces = sp.encode(word.strip(), out_type=str)
        if not pieces or any(piece == "<unk>" or piece not in vocabulary for piece in pieces) or any(part.startswith(":") for part in word.split()):
            skipped += 1
            continue
        phrases.append(word.strip())
    return phrases, skipped


def make_asr(cfg):
    options = dict(**model_files(True), num_threads=int(cfg.get("Threads", 2)), sample_rate=SR,
        decoding_method="greedy_search", enable_endpoint_detection=True,
        rule1_min_trailing_silence=2.4, rule2_min_trailing_silence=float(cfg.get("SilenceSeconds", 1.2)),
        rule3_min_utterance_length=25)
    keyword_path = None
    vocab_path = None
    count = 0
    try:
        phrases, skipped = vocabulary_tokens(cfg.get("Text", {}).get("CustomWords", []))
        count = len(phrases)
        if cfg.get("WakeHints", False) and cfg.get("Activation", "voice") == "voice":
            names, _ = vocabulary_tokens(wake_words(cfg))
            phrases = list(dict.fromkeys(phrases + names))
        if skipped:
            emit("warning", message=f"모델이 지원하지 않는 단어 {skipped}개는 인식 힌트에서 제외했어요.")
        if phrases:
            import sentencepiece as spm
            sp = spm.SentencePieceProcessor(model_file=str(Path(options["tokens"]).parent / "bpe.model"))
            with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".vocab", delete=False) as f:
                vocab_path = f.name
                for index in range(sp.get_piece_size()):
                    f.write(f"{sp.id_to_piece(index)}\t{sp.get_score(index)}\n")
            with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".txt", delete=False) as f:
                keyword_path = f.name
                f.write("\n".join(phrases) + "\n")
            options.update(decoding_method="modified_beam_search", hotwords_file=keyword_path,
                hotwords_score=1.5, max_active_paths=4, modeling_unit="bpe", bpe_vocab=vocab_path)
        return sherpa_onnx.OnlineRecognizer.from_transducer(**options), count
    except Exception as ex:
        if keyword_path is None:
            raise
        emit("warning", message="단어 힌트를 적용하지 못해 기본 인식으로 시작합니다: " + str(ex))
        for key in ("hotwords_file", "hotwords_score", "max_active_paths", "modeling_unit", "bpe_vocab"):
            options.pop(key, None)
        options["decoding_method"] = "greedy_search"
        return sherpa_onnx.OnlineRecognizer.from_transducer(**options), 0
    finally:
        for path in (keyword_path, vocab_path):
            if not path:
                continue
            try:
                os.unlink(path)
            except OSError:
                pass


def explicit_wakes(cfg):
    words = cfg.get("WakeWords", [])
    if not isinstance(words, list) or len(words) > 32:
        raise ValueError("별도 호출어는 32개까지 입력할 수 있어요.")
    for word in words:
        if not isinstance(word, str) or not word.strip() or len(word) > 40 or any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in word) or not any(c.isalnum() for c in word):
            raise ValueError("별도 호출어는 글자나 숫자를 포함해 1~40자로 입력하세요.")
    return list(dict.fromkeys(words))


def wake_words(cfg):
    if words := explicit_wakes(cfg):
        return words
    name = cfg.get("Name", "자비스").strip()
    h = hangul_reading(name) or name
    learned = cfg.get("LearnedWakes", {})
    if not isinstance(learned, dict) or len(learned) > 32:
        raise ValueError("이름 보정 목록을 확인하세요.")
    aliases = learned.get(name, [])
    if not isinstance(aliases, list) or len(aliases) > 32:
        raise ValueError("이름 보정 발음 개수를 확인하세요.")
    for word in aliases:
        if not isinstance(word, str) or len(word) > 40:
            raise ValueError("이름 보정은 2~12글자의 발음이어야 합니다.")
        normalized = unicodedata.normalize('NFC', word)
        letters = ''.join(c for c in normalized if c.isalnum())
        if not 2 <= len(letters.encode('utf-16-le')) // 2 <= 12 or not all((c.isalnum() or c.isspace() or unicodedata.category(c) in ('Mn', 'Mc')) and unicodedata.category(c) != 'Cc' for c in word):
            raise ValueError("이름 보정은 2~12글자의 발음이어야 합니다.")
    return list(dict.fromkeys([name, h] + aliases))


def wake_pattern(cfg):
    words = sorted(wake_words(cfg), key=lambda word: len(''.join(word.split())), reverse=True)
    variants = [r"\s*".join(map(re.escape, ''.join(word.split()))) for word in words]
    if not explicit_wakes(cfg):
        h = hangul_reading(cfg.get("Name", "자비스")) or cfg.get("Name", "자비스")
        if h == "자비스":
            variants += [r"재\s*비\s*스", "jarvis"]
        sound = sound_alike(h)
        if sound:
            variants.append(sound)
    return re.compile(r"(?:헤이\s*|하이\s*|hey\s*)?(?:" + "|".join(variants) + ")", re.I)


def whisper_enabled(cfg):
    return cfg.get("WhisperMode", "external") in ("cpu", "cuda") or (cfg.get("WhisperMode", "external") == "external" and bool(cfg.get("WhisperUrl")))


def missed_name_audio(cfg, audio):
    """Keep a short voiced candidate when ASR produced no text for a Latin name.

    Silence, a single click and non-periodic noise must not start repeated model work.
    The app still owns the name/command decision and cancellation.
    """
    if not whisper_enabled(cfg) or cfg.get("Activation", "voice") != "voice" or not any(re.search(r"[a-z]", word, re.I) for word in wake_words(cfg)):
        return []
    voiced = [i for i, x in enumerate(audio) if len(x) >= 640 and np.isfinite(x).all()
              and float(np.sqrt(np.mean(x * x))) >= .003 and periodicity(x) >= .3]
    if len(voiced) < 2:
        return []
    candidate = audio[max(0, voiced[0] - 1):min(len(audio), voiced[-1] + 3)]
    count = sum(len(x) for x in candidate)
    return candidate if SR * .5 < count < SR * 4.5 else []


class Recognizer:
    def __init__(self, cfg):
        self.cfg = cfg
        self.wake = wake_pattern(cfg)  # Validate all phrases before loading native models.
        self.input_gain = float(cfg.get("InputGain", 1.0))
        if not np.isfinite(self.input_gain) or not .25 <= self.input_gain <= 20:
            raise ValueError("인식 입력 증폭은 0.25~20배로 설정하세요.")
        self.next_gain_warning = 0.
        self.asr, self.hint_count = make_asr(cfg)
        self.kws = None
        self.keyword_names = {}
        if cfg.get("Kws", True):
            keyword_path = None
            try:
                name = cfg.get("Name", "자비스").strip()
                keywords = explicit_wakes(cfg) or [hangul_reading(name) or name]
                lines, skipped = [], 0
                for word in keywords:
                    label = "wake_" + str(len(lines))
                    try:
                        lines.append(encode_keyword(word, str(Path(model_files(True)["tokens"]).parent), label))
                        self.keyword_names[label] = word
                    except ValueError:
                        skipped += 1
                if skipped:
                    emit("warning", message=f"호출어 {skipped}개는 모델의 전용 감지 범위를 벗어나 받아쓰기 결과에서 확인합니다.")
                if not lines:
                    raise ValueError("전용 감지에 사용할 수 있는 호출어가 없어요.")
                with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".txt", delete=False) as f:
                    keyword_path = f.name
                    f.writelines(lines)
                self.kws = sherpa_onnx.KeywordSpotter(**model_files(True), keywords_file=keyword_path,
                    num_threads=int(cfg.get("Threads", 2)), keywords_score=2.0,
                    keywords_threshold=float(cfg.get("KeywordThreshold", .15)))
            except Exception as ex:
                emit("warning", message="이름 전용 감지기를 켜지 못해 받아쓰기 방식으로 감지합니다: " + str(ex))
            finally:
                if keyword_path:
                    try:
                        os.unlink(keyword_path)
                    except OSError:
                        pass
        self.sequence = 0
        self.reset()

    def reset(self):
        self.s = self.asr.create_stream()
        self.ks = self.kws.create_stream() if self.kws else None
        self.text, self.woken, self.audio = "", False, []
        self.sequence += 1
        self.started = time.perf_counter()
        self.discarding = False

    def resync(self, lag=0):
        # A missing beginning cannot be reconstructed from a command's tail.
        self.reset()
        self.discarding = True
        emit("resync", utterance=self.sequence, lag_ms=round(max(0, lag) * 1000))

    def feed(self, x):
        started = time.perf_counter()
        # Keep the capture meter/echo measurements raw. Apply once on entry to
        # recognition, including pre-roll replay and optional Whisper audio.
        if self.input_gain != 1:
            scaled = x * self.input_gain
            if self.input_gain > 1 and np.count_nonzero(np.abs(scaled) > 1) > len(scaled) * .01 and time.monotonic() >= self.next_gain_warning:
                self.next_gain_warning = time.monotonic() + 10
                emit("warning", message="인식 입력 증폭이 커서 소리 일부가 잘리고 있어요. 설정에서 증폭 배수를 낮춰 주세요.")
            x = np.clip(scaled, -1., 1.).astype(np.float32)
        self.audio.append(x)
        self.s.accept_waveform(SR, x)
        while self.asr.is_ready(self.s):
            self.asr.decode_stream(self.s)
        text = self.asr.get_result(self.s).strip()
        if text != self.text:
            self.text = text
            if not self.discarding:
                emit("partial", text=text, utterance=self.sequence,
                     has_wake=self.woken or bool(self.wake.search(text)))
        if not self.discarding and not self.woken and (match := self.wake.search(text)):
            self.woken = True
            emit("wake", name=match.group(), utterance=self.sequence)
        if self.kws and not self.discarding:
            self.ks.accept_waveform(SR, x)
            while self.kws.is_ready(self.ks):
                self.kws.decode_stream(self.ks)
                if result := self.kws.get_result(self.ks):
                    self.kws.reset_stream(self.ks)
                    if not self.woken:
                        self.woken = True
                        emit("wake", name=self.keyword_names.get(result, result), utterance=self.sequence)
        if (self.cfg.get("Activation") != "hold" and self.asr.is_endpoint(self.s)) or len(self.audio) >= 300:
            if self.discarding:
                # The decoder also has a 25-second length endpoint. Keep the
                # quarantine across that boundary; a short endpoint is needed.
                long_segment = sum(len(x) for x in self.audio) >= SR * 25
                self.reset()
                self.discarding = long_segment
            else:
                self.finish()
        return time.perf_counter() - started

    def finish(self):
        if self.discarding:
            self.reset()
            return
        audio = self.audio if self.text else missed_name_audio(self.cfg, self.audio)
        if self.text or audio:
            match = self.wake.search(self.text)
            command = re.sub(r"^[야아](?=\s|[,.!?~]|$)", "", self.text[match.end():]).strip(" ,.!?~") if match else self.text
            data = dict(text=self.text, command=command, has_wake=self.woken,
                        utterance=self.sequence, duration=round(sum(len(x) for x in audio) / SR, 2))
            if whisper_enabled(self.cfg) and audio:
                pcm = np.clip(np.concatenate(audio) * 32767, -32768, 32767).astype("<i2")
                buf = io.BytesIO()
                with wave.open(buf, "wb") as wav:
                    wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(SR)
                    wav.writeframes(pcm.tobytes())
                data["wav"] = base64.b64encode(buf.getvalue()).decode("ascii")
            emit("final", **data)
        self.reset()

    def finish_held(self):
        if self.audio and not self.discarding:
            # Flush the decoder's lookahead so releasing the key does not lose
            # the last syllable. This padding is not included in the audio sent
            # to optional Whisper refinement.
            self.s.accept_waveform(SR, np.zeros(SR, dtype=np.float32))
            self.s.input_finished()
            while self.asr.is_ready(self.s):
                self.asr.decode_stream(self.s)
            self.text = self.asr.get_result(self.s).strip()
        self.finish()
        emit("flushed")


def devices():
    import sounddevice as sd
    emit("devices", devices=device_options(sd))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--devices", action="store_true")
    ap.add_argument("--wav")
    ap.add_argument("--config")
    a = ap.parse_args()
    if a.devices:
        devices(); return
    cfg = json.loads(Path(a.config).read_text(encoding="utf-8-sig")) if a.config else json.loads(sys.stdin.readline())
    language = validate_language(cfg)
    if not a.wav:
        import sounddevice as sd
        input_options = bind_input(sd, cfg.get('Device', ''))
    recognizer = Recognizer(cfg) if language == 'ko' else MultilingualRecognizer(cfg, emit)
    if a.wav:
        import soundfile as sf
        x, sr = sf.read(a.wav, dtype="float32", always_2d=True)
        x = x.mean(axis=1)
        if sr != SR:
            x = np.interp(np.arange(0, len(x), sr / SR), np.arange(len(x)), x).astype(np.float32)
        x = np.concatenate((x, np.zeros(SR * 3, np.float32)))
        emit("ready", mode="wav", hint_count=recognizer.hint_count, process_id=os.getpid())
        for p in range(0, len(x), CHUNK):
            recognizer.feed(x[p:p + CHUNK])
        if cfg.get("Activation") == "hold":
            recognizer.finish_held()
        else:
            recognizer.finish()
        return
    import sounddevice as sd
    controls, audio = queue.Queue(), queue.Queue(maxsize=30)
    pre_roll = PreRoll()
    stopped = threading.Event()

    def control_reader():
        try:
            for line in sys.stdin:
                try:
                    message = json.loads(line)
                    message["received_at"] = time.monotonic()
                    controls.put(message)
                except ValueError:
                    pass
        finally:
            stopped.set()

    def callback(data, frames, timing, status):
        packet = (data[:, 0].copy(), str(status) if status else "", time.monotonic())
        try:
            audio.put_nowait(packet)
        except queue.Full:
            # Retain the newest samples when the decoder falls behind.
            try: audio.get_nowait()
            except queue.Empty: pass
            try: audio.put_nowait(packet)
            except queue.Full: pass

    threading.Thread(target=control_reader, daemon=True).start()
    capture = bool(cfg.get("InitialCapture", cfg.get("Activation", "voice") == "voice"))
    capture_request = 0
    next_level, last_warning = 0.0, 0.0
    with sd.InputStream(samplerate=SR, channels=1, dtype="float32", blocksize=CHUNK, callback=callback, **input_options):
        emit("ready", mode="mic", device=str(input_options['device']), hint_count=recognizer.hint_count, process_id=os.getpid())
        while not stopped.is_set():
            while not controls.empty():
                msg = controls.get_nowait()
                if msg.get("type") == "shutdown":
                    return
                if msg.get("type") == "finish":
                    if capture:
                        # Consume audio queued before release before flushing.
                        # Audio arriving afterwards must not extend this command.
                        while not audio.empty():
                            try:
                                pending, _, captured_at = audio.get_nowait()
                            except queue.Empty:
                                break
                            if captured_at <= msg["received_at"]:
                                recognizer.feed(pending)
                        recognizer.finish_held()
                    else:
                        emit("flushed")
                    capture = False
                if msg.get("type") == "capture":
                    capture = bool(msg.get("enabled"))
                    capture_request = msg.get("request", 0)
                    recognizer.reset()
                    pre_roll.clear()
                    while not audio.empty():
                        try: audio.get_nowait()
                        except queue.Empty: break
                    emit("capture", enabled=capture, request=msg.get("request", 0), utterance=recognizer.sequence)
                if msg.get("type") == "barge":
                    discarding = getattr(recognizer, "discarding", False)
                    recognizer.reset()
                    recognizer.discarding = discarding
                    emit("barge", accepted=capture, utterance=recognizer.sequence)
                    if capture:
                        for pending in pre_roll.recent(time.monotonic()):
                            recognizer.feed(pending)
                    pre_roll.clear()
            try:
                x, status, captured_at = audio.get(timeout=.2)
            except queue.Empty:
                continue
            now = time.monotonic()
            lag = max(0, now - captured_at)
            if audio.qsize() > 15 or lag >= 1.5:
                # Also discard x: it was removed before draining and is older
                # than everything left in the queue.
                while audio.qsize() > 2:
                    audio.get_nowait()
                recognizer.resync(lag)
                pre_roll.clear()
                emit("warning", message="음성 처리가 밀려 일부 소리를 놓쳤어요. 이번 문장은 실행하지 않아요. 잠깐 쉬었다가 이름을 다시 부르거나 ‘지금 말하기’를 누른 뒤 말씀해 주세요.")
                try: x, status, captured_at = audio.get_nowait()
                except queue.Empty: continue
                if now - captured_at >= 1.5: continue
            if status and now - last_warning > 5:
                last_warning = now
                emit("warning", message="마이크 입력 상태: " + status)
            if now >= next_level:
                rms = float(np.sqrt(np.mean(x * x)))
                emit("level", value=min(1., rms * 12), rms=rms,
                     peak=float(np.max(np.abs(x))), recognizing=capture, capture_request=capture_request,
                     pitch=periodicity(x) if rms > .02 else 0.0)
                next_level = now + .09
            pre_roll.append(x, captured_at)
            if capture:
                recognizer.feed(x)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
    except Exception as ex:
        emit("error", message=str(ex))
        sys.exit(1)
