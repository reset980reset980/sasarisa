"""Windows 검증용: 이름 부르기(웨이크) + 실시간 받아쓰기가 맥만큼 빠른지 잰다.

맥판(Ear.swift + Parser.wakeRange)과 같은 방식으로 두 길을 동시에 돌린다.
  asr  실시간 받아쓰기(sherpa-onnx 한국어 스트리밍 모델) 글자에서 이름 패턴을 찾는다 — 맥과 같은 방식
  kws  같은 모델로 이름만 따로 듣는 키워드 감지기 — 맥에는 없는, 새로 지은 이름에 강한 방식

  python wake_poc.py mic                     마이크로 실시간 측정
  python wake_poc.py record 10               이름만 10번 녹음 → samples/ (나중에 wav 로 다시 잴 수 있음)
  python wake_poc.py wav samples/*.wav       녹음 파일을 실시간 속도로 흘려 넣어 측정
  python wake_poc.py devices                 마이크 목록

지연 = 감지된 순간 − 목소리가 끝난 순간(소리 크기로 판단). 음수면 말하는 도중에 이미 알아들은 것.
"""
import argparse, csv, glob, os, re, sys, time
from datetime import datetime

import numpy as np
import sherpa_onnx

HERE = os.path.dirname(os.path.abspath(__file__))
MODEL = os.path.join(HERE, "models", "sherpa-onnx-streaming-zipformer-korean-2024-06-16")
SR = 16000
CHUNK = 1600                    # 0.1 s — 맥 쪽 탭(1024 샘플 @48 kHz ≈ 0.02 s)보다 크지만 감지 지연엔 거의 영향 없음


# ── 이름 패턴: Config.soundAlike / hangulReading / wakes 를 그대로 옮김 ──────────────────────────────────

def sound_alike(hangul):
    """사사리사 → [사싸][사싸]… — 받아쓰기가 헷갈리는 자음·모음까지 허용하는 정규식 (받침 없는 3글자 이상만)."""
    leads = [[0, 1, 15], [2, 5, 11], [3, 4, 16], [5, 2, 11, 3], [6, 7], [7, 8, 17, 6], [9, 10], [11], [12, 13, 14], [14, 12],
             [15, 0, 1], [16, 3, 4], [17, 7, 8], [18, 11]]
    lead_index = {0: 0, 1: 0, 2: 1, 3: 2, 4: 2, 5: 3, 6: 4, 7: 5, 8: 5, 9: 6, 10: 6, 11: 7, 12: 8, 13: 8, 14: 9, 15: 10, 16: 11, 17: 12, 18: 13}
    vowels = {0: [0, 2, 4], 2: [2, 0], 4: [4, 0, 5], 5: [5, 1, 4], 1: [1, 5], 8: [8, 13], 13: [13, 8, 18], 18: [18, 20, 13], 20: [20, 19, 18]}
    parts = []
    for ch in hangul:
        v = ord(ch) - 0xAC00
        if not (0 <= v < 11172 and v % 28 == 0):
            return None
        l, m = v // 588, (v % 588) // 28
        ls = leads[lead_index[l]] if l in lead_index else [l]
        ms = vowels.get(m, [m])
        opts = sorted({chr(0xAC00 + (a * 21 + b) * 28) for a in ls for b in ms})
        parts.append("[" + "".join(opts) + "]")
    return r"[\s~,.]*".join(parts) if len(parts) >= 3 else None


def hangul_reading(name):
    """sasarisa → 사사리사 (받침 없는 로마자 이름만)."""
    w = name.lower()
    if not re.fullmatch(r"([bcdfghjklmnprstvz]?[aeiouy])+", w):
        return None
    lead = dict(g=0, n=2, d=3, r=5, l=5, m=6, b=7, v=7, s=9, j=12, z=12, c=15, k=15, t=16, p=17, f=17, h=18)
    vowel = dict(a=0, e=5, i=20, y=20, o=8, u=13)
    out, pending = "", None
    for ch in w:
        if ch in vowel:
            out += chr(0xAC00 + ((11 if pending is None else pending) * 21 + vowel[ch]) * 28)
            pending = None
        else:
            pending = lead.get(ch)
    return out or None


HEARD_AS = {"사사리사": ["서 서 있어", "서서 있어", "사사 있어", "싸싸 있어", "사사 리사", "수술 있어", "사파리 있어", "safari 있어",
                     "헛소리 있어", "싸싸리싸", "쌰쌰리쌰", "사사리싸", "샤샤리샤", "사싸리사"]}
NOT_ADDRESS = r"(?!\s*(?:(?:\.|닷)\s*(?:com|컴|net|넷|kr|co\b)|버프|buff))"


class WakeMatcher:
    def __init__(self, name):
        h = hangul_reading(name) or (name if all(0xAC00 <= ord(c) < 0xD7A4 for c in name) else None)
        self.hangul = h
        p = sound_alike(h) if h else None
        self.sound = re.compile(r"(헤이|하이|hey)?\s*" + p + "[~∼〜]*" + NOT_ADDRESS, re.I) if p else None
        words = [name, "헤이 " + name]
        if h:
            words += [h, "헤이 " + h, h.replace("사", "싸")] + HEARD_AS.get(h, [])
        words += ["자비스", "헤이 자비스", "재비스"]
        words = sorted(set(words), key=len, reverse=True)
        self.words = [re.compile(r"\s*".join(re.escape(c) for c in w.replace(" ", "")) + NOT_ADDRESS, re.I) for w in words]

    def find(self, text):
        if self.sound and (m := self.sound.search(text)):
            return m.group(0)
        for w in self.words:
            if m := w.search(text):
                return m.group(0)
        return None


# ── 모델 ─────────────────────────────────────────────────────────────────────────────────────────────

def model_files(int8):
    q = "int8." if int8 else ""
    f = lambda part: os.path.join(MODEL, f"{part}-epoch-99-avg-1.{q}onnx")
    if not os.path.exists(f("encoder")):
        sys.exit(f"모델이 없습니다: {MODEL}\n먼저 setup.ps1 을 실행하세요.")
    return dict(tokens=os.path.join(MODEL, "tokens.txt"), encoder=f("encoder"), decoder=f("decoder"), joiner=f("joiner"))


def make_asr(a):
    return sherpa_onnx.OnlineRecognizer.from_transducer(
        **model_files(not a.fp32), num_threads=a.threads, sample_rate=SR, decoding_method="greedy_search",
        enable_endpoint_detection=True, rule1_min_trailing_silence=2.4, rule2_min_trailing_silence=1.2, rule3_min_utterance_length=20)


def make_kws(a, hangul):
    import sentencepiece as spm
    sp = spm.SentencePieceProcessor(model_file=os.path.join(MODEL, "bpe.model"))
    names = sorted({hangul, hangul.replace("사", "싸")} | set(a.kw or []))
    path = os.path.join(HERE, "keywords.txt")
    with open(path, "w", encoding="utf-8") as f:
        for n in names:
            f.write(" ".join(sp.encode(n, out_type=str)) + f" @{n}\n")
    return sherpa_onnx.KeywordSpotter(**model_files(not a.fp32), keywords_file=path, num_threads=a.threads,
                                      keywords_score=a.kw_score, keywords_threshold=a.kw_threshold)


# ── 측정 ─────────────────────────────────────────────────────────────────────────────────────────────

class Meter:
    """0.1 s 조각마다 받아쓰기·키워드 감지에 넣고, 이름이 처음 보인 순간과 목소리 끝 시각을 기록한다."""

    def __init__(self, a, matcher):
        self.a, self.m = a, matcher
        self.asr = make_asr(a); self.s = self.asr.create_stream()
        self.kws = make_kws(a, matcher.hangul) if matcher.hangul and not a.no_kws else None
        self.ks = self.kws.create_stream() if self.kws else None
        self.pos = 0                                    # 지금까지 넣은 샘플 수 (오디오 시간)
        self.floor, self.last_loud, self.onset = 0.01, -1e9, None
        self.text, self.hit = "", {}                    # hit: {"asr": 오디오 시각, "kws": …} — 한 발화에 한 번씩
        self.cost = []                                  # 조각 하나 처리에 든 실제 시간
        self.events = []                                # 발화별 결과

    def feed(self, x):
        t0 = time.perf_counter()
        rms = float(np.sqrt(np.mean(x * x))) if len(x) else 0.0
        self.floor = rms if rms < self.floor else self.floor * 0.998 + rms * 0.002
        self.pos += len(x)
        now = self.pos / SR
        if rms > max(self.floor * 3.5, 0.003):
            if self.onset is None:
                self.onset = now - len(x) / SR
            self.last_loud = now
        self.s.accept_waveform(SR, x)
        while self.asr.is_ready(self.s):
            self.asr.decode_stream(self.s)
        t = self.asr.get_result(self.s)
        if t != self.text:
            self.text = t
            if "asr" not in self.hit and (w := self.m.find(t)):
                self.hit["asr"] = now; self.say(f"  ▶ asr 감지 「{w}」  ({self._rel(now)})")
        if self.kws:
            self.ks.accept_waveform(SR, x)
            while self.kws.is_ready(self.ks):
                self.kws.decode_stream(self.ks)
                if r := self.kws.get_result(self.ks):
                    self.kws.reset_stream(self.ks)
                    if "kws" not in self.hit:
                        self.hit["kws"] = now; self.say(f"  ▶ kws 감지 「{r}」  ({self._rel(now)})")
        self.cost.append(time.perf_counter() - t0)
        if self.asr.is_endpoint(self.s) or (self.onset is not None and now - self.last_loud > 2.5 and not self.text):
            self.close()

    def _rel(self, now):
        d = now - self.last_loud
        return "말하는 중" if d < 0.15 else f"목소리 끝난 뒤 +{d:.2f}초"

    def close(self):
        if self.onset is not None:
            end = self.last_loud
            lat = {k: round(v - end, 2) for k, v in self.hit.items()}
            self.events.append(dict(start=round(self.onset, 2), end=round(end, 2), text=self.text, **{f"{k}_delay": v for k, v in lat.items()}))
            best = min(lat.values()) if lat else None
            self.say(f"[{self.onset:6.1f}s] 「{self.text}」  →  " + (f"감지 지연 {best:+.2f}초 ({', '.join(f'{k} {v:+.2f}' for k, v in lat.items())})" if lat else "이름 못 들음"))
        self.asr.reset(self.s)
        self.text, self.hit, self.onset = "", {}, None

    def partial(self):
        return self.text

    @staticmethod
    def say(s):
        print(s, flush=True)

    def summary(self, label):
        c = np.array(self.cost) if self.cost else np.zeros(1)
        rtf = c.mean() / (CHUNK / SR)
        heard = [e for e in self.events if any(k.endswith("_delay") for k in e)]
        print("\n── 요약 " + "─" * 40)
        print(f"처리 속도: 0.1초 소리를 평균 {c.mean()*1000:.1f} ms, 최대 {c.max()*1000:.1f} ms 에 처리  (RTF {rtf:.2f} — 0.3 이하면 여유)")
        print(f"발화 {len(self.events)}개 중 이름 감지 {len(heard)}개")
        for k in ("asr", "kws"):
            d = [e[f"{k}_delay"] for e in self.events if f"{k}_delay" in e]
            if d:
                print(f"  {k}: {len(d)}번 감지, 지연 중앙값 {np.median(d):+.2f}초, 최악 {max(d):+.2f}초")
        os.makedirs(os.path.join(HERE, "results"), exist_ok=True)
        path = os.path.join(HERE, "results", f"{label}-{datetime.now():%Y%m%d-%H%M%S}.csv")
        keys = ["start", "end", "text", "asr_delay", "kws_delay"]
        with open(path, "w", newline="", encoding="utf-8-sig") as f:
            w = csv.DictWriter(f, keys); w.writeheader(); w.writerows(self.events)
            f.write(f"# rtf,{rtf:.3f},chunk_ms_mean,{c.mean()*1000:.1f},chunk_ms_max,{c.max()*1000:.1f},threads,{self.a.threads}\n")
        print(f"결과 저장: {path}")


# ── 실행 모드 ────────────────────────────────────────────────────────────────────────────────────────

def run_mic(a, matcher):
    import queue, sounddevice as sd
    m = Meter(a, matcher)
    q = queue.Queue()
    print(f"듣는 중… 「{matcher.hangul or a.name}」 하고 불러 보세요. 끝내려면 Ctrl+C\n")
    with sd.InputStream(samplerate=SR, channels=1, dtype="float32", blocksize=CHUNK, device=a.device,
                        callback=lambda d, *_: q.put(d[:, 0].copy())):
        try:
            last = ""
            while True:
                x = q.get()
                if q.qsize() > 5:
                    print(f"  ⚠ 처리가 밀립니다 (대기 {q.qsize()}조각) — 이 PC에선 느림", flush=True)
                m.feed(x)
                if m.partial() != last and m.partial():
                    last = m.partial(); print(f"    … {last}", flush=True)
        except KeyboardInterrupt:
            m.close()
    m.summary("mic")


def run_wav(a, matcher):
    import soundfile as sf
    files = [f for p in a.files for f in (glob.glob(p) or [p])]
    m = Meter(a, matcher)
    for f in files:
        x, sr = sf.read(f, dtype="float32", always_2d=True)
        x = x[:, 0]
        if sr != SR:
            x = np.interp(np.arange(0, len(x), sr / SR), np.arange(len(x)), x).astype(np.float32)
        print(f"\n# {os.path.basename(f)}")
        x = np.concatenate([x, np.zeros(SR * 3, np.float32)])      # 끝난 뒤 3초 조용히 — 발화가 닫히도록
        for i in range(0, len(x), CHUNK):
            m.feed(x[i:i + CHUNK])
            if a.realtime:
                time.sleep(CHUNK / SR)
        m.close()
    m.summary("wav")


def run_record(a, matcher):
    import sounddevice as sd, soundfile as sf
    out = os.path.join(HERE, "samples"); os.makedirs(out, exist_ok=True)
    stamp = f"{datetime.now():%Y%m%d-%H%M%S}"
    for i in range(a.count):
        input(f"[{i+1}/{a.count}] Enter 를 누르고 「{matcher.hangul or a.name}」 한 번만 말하세요 (3초 녹음)…")
        x = sd.rec(int(SR * 3), samplerate=SR, channels=1, dtype="float32", device=a.device); sd.wait()
        p = os.path.join(out, f"{stamp}-{a.tag}-{i+1:02d}.wav"); sf.write(p, x, SR); print("  저장:", p)
    print(f"\n다시 재기: python wake_poc.py wav \"samples\\{stamp}-*.wav\"")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--name", default="사사리사", help="비서 이름 (한글 또는 받침 없는 로마자)")
    ap.add_argument("--kw", nargs="*", help="키워드 감지기에 더 넣을 소리 (예: 샤샤리샤)")
    ap.add_argument("--kw-threshold", type=float, default=0.1, help="낮을수록 잘 잡지만 잘못 깨기도 쉬움")
    ap.add_argument("--kw-score", type=float, default=2.0)
    ap.add_argument("--no-kws", action="store_true", help="받아쓰기 방식만 (맥과 똑같은 방식)")
    ap.add_argument("--threads", type=int, default=2)
    ap.add_argument("--fp32", action="store_true", help="int8 대신 원본 모델 (느리지만 조금 더 정확할 수 있음)")
    ap.add_argument("--device", default=None, help="마이크 번호나 이름 (devices 로 확인)")
    sub = ap.add_subparsers(dest="mode", required=True)
    sub.add_parser("mic")
    w = sub.add_parser("wav"); w.add_argument("files", nargs="+"); w.add_argument("--realtime", action="store_true", help="실제 시간만큼 기다리며 넣기")
    r = sub.add_parser("record"); r.add_argument("count", type=int, nargs="?", default=10); r.add_argument("--tag", default="near", help="파일 이름 표시 (near/far/quiet …)")
    sub.add_parser("devices")
    a = ap.parse_args()
    if a.device is not None and a.device.isdigit():
        a.device = int(a.device)
    if a.mode == "devices":
        import sounddevice as sd; print(sd.query_devices()); return
    matcher = WakeMatcher(a.name)
    {"mic": run_mic, "wav": run_wav, "record": run_record}[a.mode](a, matcher)


if __name__ == "__main__":
    main()
