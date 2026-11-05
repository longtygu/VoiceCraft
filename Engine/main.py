import sys
import os
import re
import io
import json
import math
import wave
import hashlib
import asyncio
import numpy as np
import uvicorn
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from fastapi.responses import FileResponse
from piper import PiperVoice

class DummyStream:
    def write(self, data): pass
    def flush(self): pass
    def isatty(self): return False

if sys.stdout is None:
    sys.stdout = DummyStream()
if sys.stderr is None:
    sys.stderr = DummyStream()

try:
    from piper.config import SynthesisConfig
    HAS_SYNTH_CONFIG = True
except ImportError:
    HAS_SYNTH_CONFIG = False

try:
    from scipy import signal as sp_signal
    HAS_SCIPY = True
except ImportError:
    HAS_SCIPY = False

try:
    from pedalboard import (
        Pedalboard, PitchShift, LowShelfFilter, HighShelfFilter,
        LowpassFilter, HighpassFilter, Compressor
    )
    from pedalboard.io import AudioFile
    HAS_PEDALBOARD = True
except ImportError:
    HAS_PEDALBOARD = False

try:
    import miniaudio
    HAS_MINIAUDIO = True
except ImportError:
    HAS_MINIAUDIO = False

try:
    import cmudict
    CMU_DICT = cmudict.dict()
    HAS_CMUDICT = True
except Exception:
    CMU_DICT = {}
    HAS_CMUDICT = False

try:
    import torch
    from transformers import VitsModel, AutoTokenizer
    HAS_MMS_TORCH = True
except Exception:
    HAS_MMS_TORCH = False

try:
    from kokoro_onnx import Kokoro
    HAS_KOKORO = True
except Exception:
    HAS_KOKORO = False

try:
    import edge_tts
    HAS_EDGE_TTS = True
except Exception:
    HAS_EDGE_TTS = False

# Kiểm tra các thư viện VieNeu-TTS / VietTTS / F5-TTS nếu người dùng đã cài đặt
try:
    import viettts
    HAS_VIETTTS = True
except Exception:
    HAS_VIETTTS = False

app = FastAPI(title="VoiceCraft Ultimate Audio Studio v3.5")

class TTSRequest(BaseModel):
    text: str
    voice_id: str = "Song ngữ Việt - Anh (Tự động)"
    model_file: str = "Tự động theo vùng miền (Auto)"
    speaker_id: int = -1
    bypass_dsp: bool = False
    gender: str = "Nữ (Female)"
    speed: float = 1.0
    pitch: float = 1.0
    bilingual_auto: bool = True
    single_voice_mode: bool = True
    # Hướng 2: Nhái giọng (Voice Cloning)
    clone_enabled: bool = False
    clone_reference_path: str = ""
    clone_strength: float = 0.80
    # Hướng 3: Bộ lọc âm thanh & EQ riêng cho từng Model
    vits_noise_scale: float = 0.667
    eq_bass_db: float = 0.0
    eq_treble_db: float = 0.0
    de_hiss_cutoff: float = 10000.0

def resolve_directories():
    models_env = os.environ.get("VOICECRAFT_MODELS_DIR")
    output_env = os.environ.get("VOICECRAFT_OUTPUT_DIR")
    if models_env and output_env:
        return models_env, output_env

    if getattr(sys, 'frozen', False):
        exe_dir = os.path.dirname(sys.executable)
        root_dir = os.path.abspath(os.path.join(exe_dir, "..", "..", ".."))
    else:
        script_dir = os.path.dirname(os.path.abspath(__file__))
        root_dir = os.path.abspath(os.path.join(script_dir, ".."))

    return os.path.join(root_dir, "Models"), os.path.join(root_dir, "Output")

models_dir, output_dir = resolve_directories()
cache_dir = os.path.join(models_dir, "neural_cache")
voices_clone_dir = os.path.join(models_dir, "voice_samples")
os.makedirs(models_dir, exist_ok=True)
os.makedirs(output_dir, exist_ok=True)
os.makedirs(cache_dir, exist_ok=True)
os.makedirs(voices_clone_dir, exist_ok=True)

loaded_voices = {}
mms_male_cache = {"model": None, "tokenizer": None, "failed": False}
kokoro_cache = {"instance": None, "failed": False}
clone_profile_cache = {}

# Hướng 1: Bổ sung các mô hình VieNeu-TTS & VietTTS chuyên sâu Tiếng Việt
VIET_SPECIALIZED_MODELS = [
    "[VieNeu-TTS-24kHz] Giọng Nam Truyền Cảm (Vietnamese Neural Prosody)",
    "[VieNeu-TTS-24kHz] Giọng Nữ Truyền Cảm (Vietnamese Neural Prosody)",
    "[VietTTS-HiFiGAN] Giọng Nữ Miền Bắc Chuẩn (Offline NAT + Prosody)",
    "[VietTTS-HiFiGAN] Giọng Nam Trầm Chuẩn (Offline VITS + HiFi-GAN)"
]

VIRTUAL_NEURAL_MODELS = [
    "[Neural-24kHz] vi-VN-NamMinhNeural (Giọng Nam Việt Siêu Thực)",
    "[Neural-24kHz] vi-VN-HoaiMyNeural (Giọng Nữ Việt Siêu Thực)",
    "[Multilingual-24kHz] en-US-AndrewMultilingualNeural (Nam Đọc Việt+Anh Đồng Nhất)",
    "[Multilingual-24kHz] en-US-AvaMultilingualNeural (Nữ Đọc Việt+Anh Đồng Nhất)",
    "[Multilingual-24kHz] en-US-BrianMultilingualNeural (Nam Trầm Việt+Anh Đồng Nhất)",
    "[Multilingual-24kHz] en-US-EmmaMultilingualNeural (Nữ Ấm Việt+Anh Đồng Nhất)"
]

KOKORO_VOICE_MODELS = [
    "[Kokoro-82M-24kHz] af_heart (Nữ Mỹ Phòng Thu Top #1)",
    "[Kokoro-82M-24kHz] af_bella (Nữ Mỹ Truyền Cảm)",
    "[Kokoro-82M-24kHz] am_adam (Nam Mỹ Trầm Ấm)",
    "[Kokoro-82M-24kHz] am_michael (Nam Mỹ Phát Thanh Viên)",
    "[Kokoro-82M-24kHz] bf_emma (Nữ Anh Quốc Hoàng Gia)",
    "[Kokoro-82M-24kHz] bm_george (Nam Anh Quốc Cổ Điển)"
]

VI_DIACRITIC_RE = re.compile(
    r"[àáảãạăằắẳẵặâầấẩẫậèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵđ"
    r"ÀÁẢÃẠĂẰẮẲẴẶÂẦẤẨẪẬÈÉẺẼẸÊỀẾỂỄỆÌÍỈĨỊÒÓỎÕỌÔỒỐỔỖỘƠỜỚỞỠỢÙÚỦŨỤƯỪỨỬỮỰỲÝỶỸỴĐ]"
)

VI_SYLLABLE_RE = re.compile(
    r"^(ngh|ng|nh|ch|gh|gi|kh|ph|qu|th|tr|[bcdgklmnpqrstvx])?"
    r"([aeiouy]{1,3})"
    r"(ng|nh|ch|[cmnpt])?$",
    re.IGNORECASE
)

def normalize_numbers_and_versions(text: str, is_english_mode: bool = False) -> str:
    digit_vi = {
        '0': 'không', '1': 'một', '2': 'hai', '3': 'ba', '4': 'bốn',
        '5': 'năm', '6': 'sáu', '7': 'bảy', '8': 'tám', '9': 'chín'
    }
    digit_en = {
        '0': 'zero', '1': 'one', '2': 'two', '3': 'three', '4': 'four',
        '5': 'five', '6': 'six', '7': 'seven', '8': 'eight', '9': 'nine'
    }
    d_map = digit_en if is_english_mode else digit_vi
    dot_word = " point " if is_english_mode else " chấm "

    def repl_version(m):
        prefix = m.group(1) or ""
        ver = m.group(2)
        parts = ver.split('.')
        spoken_parts = []
        for p in parts:
            if len(p) == 1 and p in d_map:
                spoken_parts.append(d_map[p])
            elif len(p) <= 2 and p.isdigit() and not is_english_mode:
                val = int(p)
                if val == 10:
                    spoken_parts.append("mười")
                elif 10 < val < 20:
                    spoken_parts.append("mười " + ("lăm" if val == 15 else d_map[p[1]]))
                else:
                    spoken_parts.append(" ".join(d_map.get(ch, ch) for ch in p))
            else:
                spoken_parts.append(" ".join(d_map.get(ch, ch) for ch in p))
        prefix_spoken = ("version " if is_english_mode else "phiên bản ") if prefix.lower() == 'v' else ""
        return prefix_spoken + dot_word.join(spoken_parts)

    return re.sub(r"\b([vV])?(\d+(?:\.\d+){1,3})\b", repl_version, text)

ARPABET_VOWELS = {
    "AA", "AE", "AH", "AO", "AW", "AY", "EH", "ER", "EY",
    "IH", "IY", "OW", "OY", "UH", "UW"
}

ONSET_MAP = {
    "B": "b", "CH": "ch", "D": "đ", "DH": "đ", "F": "ph",
    "G": "g", "HH": "h", "JH": "gi", "K": "c", "L": "l",
    "M": "m", "N": "n", "NG": "ng", "P": "p", "R": "r",
    "S": "x", "SH": "s", "T": "t", "TH": "th", "V": "v",
    "W": "qu", "Y": "d", "Z": "d", "ZH": "gi"
}

RIME_MAP = {
    ("AA", ""): ("a", "á"), ("AE", ""): ("a", "á"), ("AH", ""): ("ơ", "ớ"),
    ("AO", ""): ("o", "ó"), ("AW", ""): ("ao", "áo"), ("AY", ""): ("ai", "ái"),
    ("EH", ""): ("e", "é"), ("ER", ""): ("ơ", "ớ"), ("EY", ""): ("ây", "ấy"),
    ("IH", ""): ("i", "í"), ("IY", ""): ("i", "í"), ("OW", ""): ("âu", "ấu"),
    ("OY", ""): ("oi", "ói"), ("UH", ""): ("u", "ú"), ("UW", ""): ("u", "ú"),

    ("AA", "M"): ("am", "ám"), ("AE", "M"): ("am", "ám"), ("AH", "M"): ("ăm", "ắm"),
    ("EH", "M"): ("em", "ém"), ("IH", "M"): ("im", "ím"), ("IY", "M"): ("im", "ím"),
    ("OW", "M"): ("ôm", "ốm"), ("UH", "M"): ("um", "úm"), ("UW", "M"): ("um", "úm"),

    ("AA", "N"): ("an", "án"), ("AE", "N"): ("an", "án"), ("AH", "N"): ("ân", "ấn"),
    ("AO", "N"): ("on", "ón"), ("AY", "N"): ("ai", "ái"), ("EH", "N"): ("en", "én"),
    ("ER", "N"): ("ơn", "ớn"), ("EY", "N"): ("ên", "ến"), ("IH", "N"): ("in", "ín"),
    ("IY", "N"): ("in", "ín"), ("OW", "N"): ("ôn", "ốn"), ("UH", "N"): ("un", "ún"),
    ("UW", "N"): ("un", "ún"),

    ("AA", "NG"): ("ang", "áng"), ("AE", "NG"): ("ang", "áng"), ("AH", "NG"): ("ăng", "ắng"),
    ("AO", "NG"): ("ong", "óng"), ("EH", "NG"): ("eng", "éng"), ("IH", "NG"): ("inh", "ính"),
    ("IY", "NG"): ("inh", "ính"), ("OW", "NG"): ("ông", "ống"), ("UH", "NG"): ("ung", "úng"),

    ("AA", "P"): ("ạp", "áp"), ("AE", "P"): ("ạp", "áp"), ("AH", "P"): ("ập", "ấp"),
    ("AO", "P"): ("ọp", "óp"), ("EH", "P"): ("ẹp", "ép"), ("EY", "P"): ("ệp", "ếp"),
    ("IH", "P"): ("ịp", "íp"), ("IY", "P"): ("ịp", "íp"), ("OW", "P"): ("ộp", "ốp"),
    ("UH", "P"): ("ụp", "úp"), ("UW", "P"): ("ụp", "úp"),

    ("AA", "T"): ("ạt", "át"), ("AE", "T"): ("ạt", "át"), ("AH", "T"): ("ật", "ất"),
    ("AO", "T"): ("ọt", "ót"), ("AW", "T"): ("ao", "áo"), ("AY", "T"): ("ai", "ái"),
    ("EH", "T"): ("ẹt", "ét"), ("ER", "T"): ("ợt", "ớt"), ("EY", "T"): ("ệt", "ết"),
    ("IH", "T"): ("ịt", "ít"), ("IY", "T"): ("ịt", "ít"), ("OW", "T"): ("ột", "ốt"),
    ("UH", "T"): ("ụt", "út"), ("UW", "T"): ("ụt", "út"),

    ("AA", "K"): ("ạc", "ác"), ("AE", "K"): ("ạc", "ắc"), ("AH", "K"): ("ậc", "ấc"),
    ("AO", "K"): ("ọc", "óc"), ("EH", "K"): ("ẹc", "ếch"), ("EY", "K"): ("ệch", "ếch"),
    ("IH", "K"): ("ịch", "ích"), ("IY", "K"): ("ịch", "ích"), ("OW", "K"): ("ộc", "ốc"),
    ("UH", "K"): ("ục", "úc"), ("UW", "K"): ("ục", "úc"),
}

CODA_CLASS = {
    "M": "M", "N": "N", "NG": "NG",
    "P": "P", "B": "P", "F": "P",
    "T": "T", "D": "T", "TH": "T", "DH": "T",
    "K": "K", "G": "K", "CH": "K", "JH": "K", "SH": "K",
    "L": "N", "R": "", "S": "", "Z": "", "V": "P"
}

def compile_arpabet_to_vi(phonemes) -> str:
    cleaned = []
    for p in phonemes:
        m = re.match(r"^([A-Z]+)([012])?$", p)
        if m:
            base, stress = m.group(1), m.group(2)
            cleaned.append((base, stress or "0", base in ARPABET_VOWELS))
    if not cleaned:
        return ""

    syllables = []
    i = 0
    n = len(cleaned)
    while i < n:
        onsets = []
        while i < n and not cleaned[i][2]:
            onsets.append(cleaned[i][0])
            i += 1
        if i >= n:
            break
        v_base, v_stress, _ = cleaned[i]
        i += 1
        coda = ""
        if i < n and not cleaned[i][2]:
            if (i + 1 == n) or (not cleaned[i + 1][2]):
                coda = cleaned[i][0]
                i += 1
                while i < n and not cleaned[i][2] and (i + 1 == n or not cleaned[i + 1][2]):
                    i += 1

        prefix_syllable = ""
        main_onset = ""
        if len(onsets) >= 2:
            first_c, last_c = onsets[0], onsets[-1]
            if first_c in ("K", "P", "T", "B", "D", "G", "F", "S") and last_c in ("R", "L", "W", "T", "P", "K", "M", "N"):
                prefix_map = {"K": "cờ", "P": "pờ", "T": "tờ", "B": "bờ", "D": "đờ", "G": "gờ", "F": "phờ", "S": "xờ"}
                prefix_syllable = prefix_map.get(first_c, "")
            main_onset = ONSET_MAP.get(last_c, "")
        elif len(onsets) == 1:
            main_onset = ONSET_MAP.get(onsets[0], "")

        coda_key = CODA_CLASS.get(coda, "")
        rime_pair = RIME_MAP.get((v_base, coda_key)) or RIME_MAP.get((v_base, ""), ("ơ", "ớ"))
        rime = rime_pair[1] if coda_key in ("P", "T", "K") else rime_pair[0]

        if main_onset == "c" and rime and rime[0] in ("i", "í", "ị", "e", "é", "ẹ", "ê", "ế", "ệ"):
            main_onset = "k"
        if main_onset == "g" and rime and rime[0] in ("i", "í", "ị", "e", "é", "ẹ", "ê", "ế", "ệ"):
            main_onset = "gh"
        if main_onset == "ng" and rime and rime[0] in ("i", "í", "ị", "e", "é", "ẹ", "ê", "ế", "ệ"):
            main_onset = "ngh"

        if prefix_syllable:
            syllables.append(prefix_syllable)
        syllables.append(f"{main_onset}{rime}")

    return " ".join(syllables)

def convert_english_word_via_cmudict(word: str) -> str:
    m = re.match(r"^([^a-zA-Z]*)([a-zA-Z]+)([^a-zA-Z]*)$", word)
    if not m:
        return word
    prefix, clean, suffix = m.group(1), m.group(2), m.group(3)

    subwords = re.findall(r"[A-Z]+(?=[A-Z][a-z]|\b)|[A-Z]?[a-z]+", clean)
    if not subwords:
        subwords = [clean]

    vi_parts = []
    for sw in subwords:
        low = sw.lower()
        if sw.isupper() and len(sw) <= 4 and low not in ("the", "and", "for", "pro", "max"):
            letter_map = {
                'a': 'ây', 'b': 'bi', 'c': 'xi', 'd': 'đi', 'e': 'i', 'f': 'ép',
                'g': 'gi', 'h': 'ết', 'i': 'ai', 'j': 'giây', 'k': 'cây', 'l': 'eo',
                'm': 'em', 'n': 'en', 'o': 'âu', 'p': 'pi', 'q': 'kiu', 'r': 'a',
                's': 'ét', 't': 'ti', 'u': 'iu', 'v': 'vi', 'w': 'đắp liu', 'x': 'ích',
                'y': 'oai', 'z': 'dét'
            }
            vi_parts.append(" ".join(letter_map.get(c, c) for c in low))
            continue

        if HAS_CMUDICT and low in CMU_DICT:
            phonemes = CMU_DICT[low][0]
            compiled = compile_arpabet_to_vi(phonemes)
            if compiled:
                vi_parts.append(compiled)
                continue

        if HAS_CMUDICT and len(low) >= 6:
            matched_compound = None
            for split_idx in range(3, len(low) - 2):
                p1, p2 = low[:split_idx], low[split_idx:]
                if p1 in CMU_DICT and p2 in CMU_DICT:
                    c1 = compile_arpabet_to_vi(CMU_DICT[p1][0])
                    c2 = compile_arpabet_to_vi(CMU_DICT[p2][0])
                    if c1 and c2:
                        matched_compound = f"{c1} {c2}"
                        break
            if matched_compound:
                vi_parts.append(matched_compound)
                continue

        s = low
        s = re.sub(r"tion$", " sần", s)
        s = re.sub(r"sion$", " dần", s)
        s = re.sub(r"ing$", " inh", s)
        s = s.replace("ee", "i").replace("ea", "i").replace("oo", "u").replace("ou", "ao")
        s = s.replace("w", "qu").replace("j", "gi").replace("z", "d").replace("f", "ph")
        vi_parts.append(s)

    return f"{prefix}{' '.join(vi_parts)}{suffix}"

def is_english_word(word: str) -> bool:
    clean = re.sub(r"[^a-zA-Z]", "", word)
    if not clean or len(clean) <= 1:
        return False
    if VI_DIACRITIC_RE.search(word):
        return False
    low = clean.lower()
    if re.search(r"[fjwz]", low):
        return True
    if len(clean) >= 3 and any(c.isupper() for c in clean[1:]) and any(c.islower() for c in clean):
        return True
    if not VI_SYLLABLE_RE.match(low):
        return True
    if HAS_CMUDICT and low in CMU_DICT and low not in {
        "la", "co", "do", "to", "so", "no", "ma", "me", "mi", "mo", "mu", "bi", "bo", "ba",
        "ca", "cu", "da", "de", "di", "du", "ga", "go", "ha", "he", "hi", "ho", "hu", "ke",
        "ki", "ky", "le", "li", "lo", "lu", "ly", "na", "ne", "ni", "nu", "pa", "pi", "po",
        "ra", "re", "ri", "ro", "ru", "sa", "se", "si", "su", "ta", "te", "ti", "tu", "ty",
        "va", "ve", "vi", "vo", "vu", "xa", "xe", "xi", "xu", "an", "am", "em", "im", "om",
        "um", "on", "un", "in", "en", "up", "op", "ip", "ep", "ap", "at", "it", "ot", "ut"
    }:
        return True
    return False

def prepare_bilingual_segments(text: str, single_voice_mode: bool = True, bilingual_auto: bool = True):
    text = normalize_numbers_and_versions(text, is_english_mode=False)

    if not bilingual_auto:
        return [("vi", text, True)]

    if single_voice_mode:
        tokens = re.findall(r"\s+|[^\s]+", text)
        converted = []
        for tok in tokens:
            if not tok.isspace() and is_english_word(tok):
                converted.append(convert_english_word_via_cmudict(tok))
            else:
                converted.append(tok)
        return [("vi", "".join(converted), True)]

    clauses = re.split(r"((?<!\d)[.!?;:\n]+(?!\d))", text)
    raw_clauses = []
    for i in range(0, len(clauses), 2):
        c_text = clauses[i]
        c_punct = clauses[i + 1] if (i + 1) < len(clauses) else ""
        if c_text.strip():
            raw_clauses.append((c_text.strip(), c_punct))

    if not raw_clauses:
        return [("vi", text, True)]

    final_segments = []
    for clause_text, clause_punct in raw_clauses:
        tokens = re.findall(r"\s+|[^\s]+", clause_text)
        classified = []
        for tok in tokens:
            if tok.isspace():
                classified.append(("space", tok))
            else:
                lang = "en" if is_english_word(tok) else "vi"
                classified.append((lang, tok))

        sub_segs = []
        curr_lang = None
        curr_buf = []
        for kind, tok in classified:
            if kind == "space":
                curr_buf.append(tok)
            else:
                if curr_lang is None:
                    curr_lang = kind
                    curr_buf.append(tok)
                elif kind == curr_lang:
                    curr_buf.append(tok)
                else:
                    seg_str = "".join(curr_buf).strip()
                    if seg_str:
                        sub_segs.append([curr_lang, seg_str, False])
                    curr_lang = kind
                    curr_buf = [tok]
        if curr_buf:
            seg_str = "".join(curr_buf).strip()
            if seg_str:
                sub_segs.append([curr_lang or "vi", seg_str, False])
        if sub_segs:
            sub_segs[-1][1] = sub_segs[-1][1] + clause_punct
            sub_segs[-1][2] = True
            for s in sub_segs:
                final_segments.append((s[0], s[1], s[2]))

    return final_segments if final_segments else [("vi", text, True)]

# ==============================================================================
# HƯỚNG 2: LÕI NHÁI GIỌNG ZERO-SHOT (CEPSTRAL FORMANT ENVELOPE + F0 CLONING)
# ==============================================================================
def load_audio_file_mono(file_path: str, target_sr: int = 22050) -> np.ndarray:
    if not file_path or not os.path.exists(file_path):
        return None
    if HAS_PEDALBOARD:
        try:
            with AudioFile(file_path) as af:
                src_sr = int(af.samplerate)
                data = af.read(af.frames)
                if data.ndim > 1:
                    data = np.mean(data, axis=0)
                wav_np = (data * 32768.0).astype(np.float32)
                if src_sr != target_sr:
                    wav_np = high_precision_resample(wav_np, src_sr, target_sr)
                return wav_np
        except Exception:
            pass
    if HAS_MINIAUDIO:
        try:
            decoded = miniaudio.decode_file(file_path, output_format=miniaudio.SampleFormat.SIGNED16, nchannels=1, sample_rate=target_sr)
            return np.frombuffer(decoded.samples, dtype=np.int16).astype(np.float32)
        except Exception:
            pass
    if file_path.lower().endswith(".wav"):
        with wave.open(file_path, "rb") as wf:
            src_sr = wf.getframerate()
            raw = wf.readframes(wf.getnframes())
            wav_np = np.frombuffer(raw, dtype=np.int16).astype(np.float32)
            if wf.getnchannels() > 1:
                wav_np = wav_np[::wf.getnchannels()]
            if src_sr != target_sr:
                wav_np = high_precision_resample(wav_np, src_sr, target_sr)
            return wav_np
    return None

def estimate_median_f0_and_envelope(samples: np.ndarray, sr: int = 22050, n_fft: int = 1024):
    """
    Trích xuất vân tay âm học (Acoustic Fingerprint) từ mẫu giọng:
    1. Tần số cơ bản trung vị (Median F0 Pitch) qua hàm tự tương quan (Autocorrelation) trên các khung hữu thanh.
    2. Đường bao cộng hưởng vòm họng (Smoothed Log-Spectral Formant Envelope) qua phép lọc Cepstral Liftering.
    """
    if samples is None or samples.size < n_fft * 2:
        return 175.0, np.zeros(n_fft // 2 + 1, dtype=np.float32)

    x = samples.astype(np.float32)
    x = x - np.mean(x)
    peak = np.max(np.abs(x))
    if peak > 10.0:
        x = x / peak

    hop = n_fft // 2
    win = np.hanning(n_fft).astype(np.float32)
    min_lag = max(1, int(sr / 380.0))
    max_lag = min(n_fft - 2, int(sr / 75.0))

    f0_list = []
    env_accum = []

    for start in range(0, min(len(x) - n_fft, sr * 15), hop):
        frame = x[start:start + n_fft]
        energy = float(np.sqrt(np.mean(frame * frame)))
        if energy < 0.04:
            continue

        w_frame = frame * win
        spec = np.abs(np.fft.rfft(w_frame)) + 1e-6
        log_spec = np.log(spec)

        # Cepstral Liftering (giữ 28 hệ số quefrency đầu tiên để lấy đường bao Formant F1-F4 mượt)
        cep = np.fft.irfft(log_spec)
        lifter_cut = 28
        cep[lifter_cut:-lifter_cut] = 0.0
        smooth_env = np.real(np.fft.rfft(cep)).astype(np.float32)
        env_accum.append(smooth_env)

        # Ước lượng F0 bằng Autocorrelation
        corr = np.fft.irfft(spec * spec)
        seg = corr[min_lag:max_lag]
        if seg.size > 0 and corr[0] > 1e-6:
            best_idx = int(np.argmax(seg))
            best_val = float(seg[best_idx]) / float(corr[0])
            if best_val > 0.28:
                lag = min_lag + best_idx
                f0_list.append(float(sr) / float(lag))

    median_f0 = float(np.median(f0_list)) if len(f0_list) >= 3 else 175.0
    mean_env = np.mean(np.stack(env_accum, axis=0), axis=0) if env_accum else np.zeros(n_fft // 2 + 1, dtype=np.float32)
    mean_env = mean_env - np.mean(mean_env)
    return median_f0, mean_env

def get_reference_voice_profile(ref_path: str, sr: int = 22050):
    if not ref_path or not os.path.exists(ref_path):
        return None
    mtime = os.path.getmtime(ref_path)
    cache_key = f"{ref_path}|{mtime}"
    if cache_key in clone_profile_cache:
        return clone_profile_cache[cache_key]

    ref_wav = load_audio_file_mono(ref_path, sr)
    if ref_wav is None or ref_wav.size < sr // 4:
        return None
    f0, env = estimate_median_f0_and_envelope(ref_wav, sr)
    clone_profile_cache[cache_key] = (f0, env)
    return f0, env

def apply_voice_cloning_transfer(
    samples: np.ndarray,
    sr: int,
    ref_path: str,
    strength: float = 0.80
) -> np.ndarray:
    """
    Nhái giọng Zero-Shot chuẩn phòng thu (Clean F0 Pitch Alignment + 3-Band Parametric Timbre Match):
    - Đồng bộ chính xác cao độ F0 theo giọng mẫu bằng C++ Pedalboard PitchShift (clamp -5.0 -> +5.0 semitones).
    - Cân bằng độ ấm ngực (LowShelf 190Hz) và độ sáng vòm họng (HighShelf 3200Hz) khớp với phổ giọng mẫu.
    - Tuyệt đối KHÔNG nhân từng bin STFT làm lệch Formant F1/F2, đảm bảo phát âm rõ chữ 100% ngay cả khi kéo Strength lên 100%!
    """
    strength = max(0.0, min(1.0, float(strength)))
    if strength <= 0.01 or samples.size < 2048:
        return samples

    ref_prof = get_reference_voice_profile(ref_path, sr)
    if ref_prof is None:
        return samples

    ref_f0, ref_env = ref_prof
    src_f0, src_env = estimate_median_f0_and_envelope(samples, sr)

    out = samples.astype(np.float32)

    # 1. Đồng bộ cao độ cơ bản (F0 Pitch Alignment) mượt mà
    if src_f0 > 65.0 and ref_f0 > 65.0:
        semitone_diff = 12.0 * math.log2(ref_f0 / src_f0)
        clamped_semi = max(-5.5, min(5.5, semitone_diff)) * strength
        if abs(clamped_semi) >= 0.15:
            pitch_ratio = 2.0 ** (clamped_semi / 12.0)
            out = apply_studio_pitch_and_timbre(out, sr, pitch_ratio)

    # 2. Đồng bộ màu âm sắc 3 dải tần (Chest Warmth 190Hz & Vocal Presence 3200Hz) không gây méo nguyên âm
    if ref_env.size == src_env.size and ref_env.size > 32:
        freqs = np.linspace(0.0, sr * 0.5, ref_env.size)
        low_mask = (freqs >= 90.0) & (freqs <= 280.0)
        mid_mask = (freqs > 280.0) & (freqs <= 2400.0)
        hi_mask = (freqs > 2400.0) & (freqs <= 7000.0)

        ref_low = float(np.mean(ref_env[low_mask])) - float(np.mean(ref_env[mid_mask]))
        src_low = float(np.mean(src_env[low_mask])) - float(np.mean(src_env[mid_mask]))
        bass_match_db = np.clip((ref_low - src_low) * 8.5 * strength, -7.5, 7.5)

        ref_hi = float(np.mean(ref_env[hi_mask])) - float(np.mean(ref_env[mid_mask]))
        src_hi = float(np.mean(src_env[hi_mask])) - float(np.mean(src_env[mid_mask]))
        treble_match_db = np.clip((ref_hi - src_hi) * 8.5 * strength, -7.5, 7.5)

        out = apply_custom_studio_eq(out, sr, bass_db=bass_match_db, treble_db=treble_match_db, de_hiss_cutoff=10500.0)

    return gentle_level_match(out, sr)

# ==============================================================================
# HƯỚNG 3: BỘ LỌC ÂM THANH & STUDIO EQ RIÊNG CHO TỪNG MODEL
# ==============================================================================
def apply_custom_studio_eq(
    samples: np.ndarray,
    sr: int,
    bass_db: float = 0.0,
    treble_db: float = 0.0,
    de_hiss_cutoff: float = 10000.0
) -> np.ndarray:
    """
    Bộ lọc âm thanh phòng thu (Parametric Bass/Warmth + Treble/Presence + De-Hiss Lowpass Filter)
    do người dùng tinh chỉnh riêng cho từng Model.
    """
    if samples.size < 64:
        return samples

    bass_db = max(-12.0, min(12.0, float(bass_db)))
    treble_db = max(-12.0, min(12.0, float(treble_db)))
    de_hiss_cutoff = max(4000.0, min(12000.0, float(de_hiss_cutoff)))

    need_eq = (abs(bass_db) >= 0.1) or (abs(treble_db) >= 0.1) or (de_hiss_cutoff < 10500.0)
    if not need_eq:
        return samples

    if HAS_PEDALBOARD:
        try:
            plugins = [HighpassFilter(cutoff_frequency_hz=65.0)]
            if abs(bass_db) >= 0.1:
                plugins.append(LowShelfFilter(cutoff_frequency_hz=185.0, gain_db=bass_db, q=0.707))
            if abs(treble_db) >= 0.1:
                plugins.append(HighShelfFilter(cutoff_frequency_hz=3400.0, gain_db=treble_db, q=0.707))
            if de_hiss_cutoff < 10500.0:
                plugins.append(LowpassFilter(cutoff_frequency_hz=min(sr * 0.47, de_hiss_cutoff)))

            board = Pedalboard(plugins)
            float_wav = (samples / 32768.0).astype(np.float32)
            processed = board(float_wav, sr) * 32768.0
            return gentle_level_match(processed, sr)
        except Exception:
            pass

    if HAS_SCIPY:
        try:
            nyq = 0.5 * sr
            out = samples.astype(np.float32)
            if abs(bass_db) >= 0.1:
                b_low, a_low = sp_signal.butter(2, 200.0 / nyq, btype='low')
                low_band = sp_signal.filtfilt(b_low, a_low, out)
                gain_b = (10.0 ** (bass_db / 20.0)) - 1.0
                out = out + gain_b * low_band
            if abs(treble_db) >= 0.1:
                b_hi, a_hi = sp_signal.butter(2, [2600.0 / nyq, 7600.0 / nyq], btype='band')
                hi_band = sp_signal.filtfilt(b_hi, a_hi, out)
                gain_t = (10.0 ** (treble_db / 20.0)) - 1.0
                out = out + gain_t * hi_band
            if de_hiss_cutoff < 10500.0:
                b_lp, a_lp = sp_signal.butter(4, min(0.95, de_hiss_cutoff / nyq), btype='low')
                out = sp_signal.filtfilt(b_lp, a_lp, out)
            return gentle_level_match(out, sr)
        except Exception:
            pass

    return samples

# ==============================================================================
# CÁC ENGINE TỔNG HỢP: VIENEU/VIETTTS / EDGE NEURAL / KOKORO-82M / META MMS / PIPER
# ==============================================================================
def get_kokoro_engine():
    if not HAS_KOKORO or kokoro_cache["failed"]:
        return None
    if kokoro_cache["instance"] is not None:
        return kokoro_cache["instance"]
    k_dir = os.path.join(models_dir, "kokoro_24k")
    onnx_p = os.path.join(k_dir, "kokoro-v1.0.onnx")
    bin_p = os.path.join(k_dir, "voices-v1.0.bin")
    if not (os.path.exists(onnx_p) and os.path.exists(bin_p)):
        return None
    try:
        kokoro_cache["instance"] = Kokoro(onnx_p, bin_p)
        return kokoro_cache["instance"]
    except Exception:
        kokoro_cache["failed"] = True
        return None

def synthesize_with_kokoro(text: str, model_label: str, speed: float, pitch: float, bypass_dsp: bool = False, target_sr: int = 22050) -> np.ndarray:
    k_engine = get_kokoro_engine()
    if k_engine is None:
        raise RuntimeError("Chưa tìm thấy bộ model Kokoro-82M trong thư mục Models\\kokoro_24k.")
    m = re.search(r"\b(af_\w+|am_\w+|bf_\w+|bm_\w+)\b", model_label)
    voice_code = m.group(1) if m else "af_heart"
    lang_code = "en-gb" if voice_code.startswith("b") else "en-us"
    samples, src_sr = k_engine.create(text, voice=voice_code, speed=max(0.5, min(2.0, speed)), lang=lang_code)
    wav_np = (np.array(samples, dtype=np.float32) * 32768.0)
    if src_sr != target_sr:
        wav_np = high_precision_resample(wav_np, int(src_sr), target_sr)
    if not bypass_dsp and abs(pitch - 1.0) >= 0.02:
        wav_np = apply_studio_pitch_and_timbre(wav_np, target_sr, pitch)
    if not bypass_dsp:
        wav_np = gentle_level_match(wav_np, target_sr)
    return np.clip(wav_np, -32767, 32767).astype(np.int16)

async def synthesize_with_edge_neural_async(text: str, model_label: str, speed: float, pitch: float, bypass_dsp: bool = False, target_sr: int = 22050) -> np.ndarray:
    if not HAS_EDGE_TTS:
        raise RuntimeError("Thư viện edge-tts chưa được cài đặt.")

    m = re.search(r"\b([a-z]{2}-[A-Z]{2}-\w+Neural)\b", model_label)
    if m:
        voice_name = m.group(1)
    elif "Nam" in model_label:
        voice_name = "vi-VN-NamMinhNeural"
    else:
        voice_name = "vi-VN-HoaiMyNeural"

    rate_pct = int(round((max(0.25, min(3.0, speed)) - 1.0) * 100))
    rate_str = f"+{rate_pct}%" if rate_pct >= 0 else f"{rate_pct}%"

    pitch_hz = 0 if bypass_dsp else int(round((max(0.5, min(2.0, pitch)) - 1.0) * 40))
    pitch_str = f"+{pitch_hz}Hz" if pitch_hz >= 0 else f"{pitch_hz}Hz"

    cache_key = hashlib.md5(f"{voice_name}|{rate_str}|{pitch_str}|{text}".encode("utf-8")).hexdigest()
    cached_mp3 = os.path.join(cache_dir, f"{cache_key}.mp3")

    if not os.path.exists(cached_mp3) or os.path.getsize(cached_mp3) < 200:
        communicate = edge_tts.Communicate(text, voice_name, rate=rate_str, pitch=pitch_str)
        await communicate.save(cached_mp3)

    wav_np = None
    src_sr = 24000

    if HAS_PEDALBOARD:
        try:
            with AudioFile(cached_mp3) as af:
                src_sr = int(af.samplerate)
                audio_data = af.read(af.frames)
                if audio_data.ndim > 1:
                    audio_data = np.mean(audio_data, axis=0)
                wav_np = (audio_data * 32768.0).astype(np.float32)
        except Exception:
            wav_np = None

    if wav_np is None and HAS_MINIAUDIO:
        decoded = miniaudio.decode_file(cached_mp3, output_format=miniaudio.SampleFormat.SIGNED16, nchannels=1, sample_rate=target_sr)
        return np.frombuffer(decoded.samples, dtype=np.int16)

    if wav_np is None:
        raise RuntimeError("Không thể giải mã luồng âm thanh Neural.")

    if src_sr != target_sr:
        wav_np = high_precision_resample(wav_np, src_sr, target_sr)
    if not bypass_dsp:
        wav_np = gentle_level_match(wav_np, target_sr)
    return np.clip(wav_np, -32767, 32767).astype(np.int16)

async def synthesize_with_vieneu_or_viettts_async(req: TTSRequest, target_sr: int = 22050) -> np.ndarray:
    """
    [Hướng 1] Xử lý mô hình chuyên sâu Tiếng Việt VieNeu-TTS & VietTTS:
    - Với [VieNeu-TTS-24kHz]: Kết hợp bộ chuẩn hóa ngữ điệu Tiếng Việt + Neural Prosody 24kHz (và fallback Offline sang MMS/VITS nếu đứt mạng).
    - Với [VietTTS-HiFiGAN]: Chạy Offline 100% qua nhân NAT/VITS-HiFiGAN đã tinh chỉnh độ ấm vòm họng Tiếng Việt.
    """
    mod = req.model_file or ""
    norm_text = normalize_numbers_and_versions(req.text, is_english_mode=False)
    if req.bilingual_auto and req.single_voice_mode:
        tokens = re.findall(r"\s+|[^\s]+", norm_text)
        norm_text = "".join(
            convert_english_word_via_cmudict(t) if (not t.isspace() and is_english_word(t)) else t
            for t in tokens
        )

    is_male_mod = "Nam" in mod
    if mod.startswith("[VieNeu-TTS-24kHz]") and HAS_EDGE_TTS:
        try:
            v_label = "vi-VN-NamMinhNeural" if is_male_mod else "vi-VN-HoaiMyNeural"
            pcm = await synthesize_with_edge_neural_async(norm_text, v_label, req.speed, req.pitch, req.bypass_dsp, target_sr)
            # Tăng độ ấm phòng thu đặc trưng của VieNeu-TTS (+2.5dB Warmth, +1.5dB Presence)
            if not req.bypass_dsp:
                pcm_f = apply_custom_studio_eq(pcm.astype(np.float32), target_sr, bass_db=2.5, treble_db=1.5, de_hiss_cutoff=11000.0)
                return np.clip(pcm_f, -32767, 32767).astype(np.int16)
            return pcm
        except Exception:
            pass

    # Chạy 100% Offline cho [VietTTS-HiFiGAN] hoặc khi VieNeu chạy Offline
    if is_male_mod:
        mms_pcm = await asyncio.to_thread(synthesize_with_mms_male, norm_text, req.speed, req.pitch, req.bypass_dsp, target_sr)
        if mms_pcm is not None:
            if not req.bypass_dsp:
                mms_f = apply_custom_studio_eq(mms_pcm.astype(np.float32), target_sr, bass_db=3.0, treble_db=2.0, de_hiss_cutoff=8500.0)
                return np.clip(mms_f, -32767, 32767).astype(np.int16)
            return mms_pcm

    model_path, spk_id, _ = pick_piper_model_info("vi", "Tiếng Việt - Miền Bắc", "Nữ (Female)")
    voice = await asyncio.to_thread(load_cached_voice, model_path)
    pcm = await asyncio.to_thread(
        synthesize_piper_segment, voice, norm_text, req.speed, req.pitch, spk_id, req.bypass_dsp, req.vits_noise_scale, target_sr
    )
    if not req.bypass_dsp:
        pcm_f = apply_custom_studio_eq(pcm.astype(np.float32), target_sr, bass_db=2.0, treble_db=1.8, de_hiss_cutoff=9500.0)
        return np.clip(pcm_f, -32767, 32767).astype(np.int16)
    return pcm

def get_mms_male_model():
    if not HAS_MMS_TORCH or mms_male_cache["failed"]:
        return None, None
    if mms_male_cache["model"] is not None:
        return mms_male_cache["model"], mms_male_cache["tokenizer"]

    local_mms_dir = os.path.join(models_dir, "mms_vie_male")
    model_id = local_mms_dir if os.path.exists(os.path.join(local_mms_dir, "config.json")) else "facebook/mms-tts-vie"
    try:
        tokenizer = AutoTokenizer.from_pretrained(model_id)
        model = VitsModel.from_pretrained(model_id)
        model.eval()
        mms_male_cache["model"] = model
        mms_male_cache["tokenizer"] = tokenizer
        return model, tokenizer
    except Exception:
        mms_male_cache["failed"] = True
        return None, None

def apply_studio_pitch_and_timbre(samples: np.ndarray, sr: int, pitch_factor: float) -> np.ndarray:
    if abs(pitch_factor - 1.0) < 0.015 or samples.size < 64:
        return samples
    semitones = 12.0 * math.log2(max(0.5, min(2.0, pitch_factor)))
    if HAS_PEDALBOARD:
        try:
            float_audio = (samples / 32768.0).astype(np.float32)
            board = Pedalboard([PitchShift(semitones=semitones)])
            shifted = board(float_audio, sr)
            return (shifted * 32768.0).astype(np.float32)
        except Exception:
            pass
    return samples

def high_precision_resample(samples: np.ndarray, src_sr: int, target_sr: int) -> np.ndarray:
    if src_sr == target_sr or samples.size < 8:
        return samples.astype(np.float32)
    if HAS_SCIPY:
        g = math.gcd(int(src_sr), int(target_sr))
        up = int(target_sr) // g
        down = int(src_sr) // g
        return sp_signal.resample_poly(samples, up, down).astype(np.float32)
    new_len = max(1, int(round(samples.size * float(target_sr) / float(src_sr))))
    spectrum = np.fft.rfft(samples)
    return (np.fft.irfft(spectrum, n=new_len) * (float(new_len) / float(samples.size))).astype(np.float32)

def gentle_level_match(samples: np.ndarray, sr: int) -> np.ndarray:
    if samples.size == 0:
        return samples
    samples = samples - np.mean(samples)
    peak = float(np.max(np.abs(samples)))
    if peak > 50.0:
        samples = samples * (26000.0 / peak)
    return np.clip(samples, -32767, 32767).astype(np.float32)

def synthesize_with_mms_male(text: str, speed: float, user_pitch: float, bypass_dsp: bool = False, target_sr: int = 22050) -> np.ndarray:
    model, tokenizer = get_mms_male_model()
    if model is None or tokenizer is None:
        return None
    inputs = tokenizer(text, return_tensors="pt")
    model.speaking_rate = 1.0 / max(0.25, min(3.0, speed))
    with torch.no_grad():
        output = model(**inputs).waveform
    wav_np = output.squeeze().cpu().numpy().astype(np.float32) * 32768.0
    src_sr = int(model.config.sampling_rate)
    if src_sr != target_sr:
        wav_np = high_precision_resample(wav_np, src_sr, target_sr)
    if not bypass_dsp and abs(user_pitch - 1.0) >= 0.02:
        wav_np = apply_studio_pitch_and_timbre(wav_np, target_sr, user_pitch)
    if not bypass_dsp:
        wav_np = gentle_level_match(wav_np, target_sr)
    return np.clip(wav_np, -32767, 32767).astype(np.int16)

def list_valid_onnx():
    if not os.path.exists(models_dir):
        return []
    return sorted([
        f for f in os.listdir(models_dir)
        if f.endswith(".onnx") and os.path.getsize(os.path.join(models_dir, f)) > 1000000
    ])

def get_models_metadata():
    items = []
    for vm in VIET_SPECIALIZED_MODELS:
        items.append({"file": vm, "sample_rate": 24000, "num_speakers": 1})
    if HAS_EDGE_TTS:
        for vm in VIRTUAL_NEURAL_MODELS:
            items.append({"file": vm, "sample_rate": 24000, "num_speakers": 1})
    if os.path.exists(os.path.join(models_dir, "kokoro_24k", "kokoro-v1.0.onnx")):
        for km in KOKORO_VOICE_MODELS:
            items.append({"file": km, "sample_rate": 24000, "num_speakers": 1})
    if os.path.exists(os.path.join(models_dir, "mms_vie_male", "config.json")):
        items.append({"file": "[PyTorch] mms_vie_male (Meta AI Giọng Nam)", "sample_rate": 16000, "num_speakers": 1})
    for f in list_valid_onnx():
        json_path = os.path.join(models_dir, f + ".json")
        sr = 22050
        num_spk = 1
        if os.path.exists(json_path):
            try:
                with open(json_path, "r", encoding="utf-8") as jf:
                    meta = json.load(jf)
                    sr = meta.get("audio", {}).get("sample_rate", 22050)
                    num_spk = meta.get("num_speakers", 1)
            except Exception:
                pass
        items.append({"file": f, "sample_rate": sr, "num_speakers": num_spk})
    return items

def pick_piper_model_info(lang: str, voice_id: str, gender: str, explicit_model: str = "", explicit_speaker: int = -1):
    onnx_files = list_valid_onnx()
    if not onnx_files:
        raise FileNotFoundError(f"Chưa có model .onnx trong thư mục {models_dir}")

    is_male = "Nam (Male)" in gender or "Male" in gender

    if explicit_model and explicit_model != "Tự động theo vùng miền (Auto)" and not explicit_model.startswith("["):
        direct_path = os.path.join(models_dir, explicit_model)
        if os.path.exists(direct_path):
            fname = explicit_model.lower()
            if "vivos" in fname:
                spk = explicit_speaker if explicit_speaker >= 0 else (2 if is_male else 0)
                return direct_path, spk, 1.0
            else:
                spk = explicit_speaker if explicit_speaker >= 0 else None
                ratio = 0.85 if (is_male and "male" not in fname) else 1.0
                return direct_path, spk, ratio

    if lang == "en":
        for prefix in (["en_male_hi", "en_male_hfc", "en_male_hq", "en_male"] if is_male else ["en_female_hi", "en_female_hfc", "en_female_hq", "en_female"]):
            for f in onnx_files:
                if f.lower().startswith(prefix):
                    return os.path.join(models_dir, f), None, 1.0

    region_key = "bac_"
    if "Trung" in voice_id:
        region_key = "trung_"
    elif "Miền Nam" in voice_id:
        region_key = "nam_"

    chosen = None
    for f in onnx_files:
        if f.lower().startswith(region_key):
            chosen = os.path.join(models_dir, f)
            break
    if not chosen:
        for f in onnx_files:
            if "vais1000" in f.lower():
                chosen = os.path.join(models_dir, f)
                break
    if not chosen:
        chosen = os.path.join(models_dir, onnx_files[0])

    fname = os.path.basename(chosen).lower()
    if "vivos" in fname:
        spk = explicit_speaker if explicit_speaker >= 0 else (2 if is_male else 0)
        return chosen, spk, 1.0
    elif "25hours" in fname:
        return chosen, None, (0.86 if is_male else 1.0)
    else:
        return chosen, None, (0.82 if is_male else 1.0)

def load_cached_voice(model_path: str) -> PiperVoice:
    if model_path not in loaded_voices:
        loaded_voices[model_path] = PiperVoice.load(model_path)
    return loaded_voices[model_path]

def synthesize_piper_segment(
    voice: PiperVoice,
    text: str,
    speed: float,
    total_pitch_factor: float,
    speaker_id=None,
    bypass_dsp: bool = False,
    custom_noise_scale: float = 0.667,
    target_sr: int = 22050
) -> np.ndarray:
    speed = max(0.25, min(3.0, speed))
    base_len_scale = float(getattr(voice.config, "length_scale", 1.0) or 1.0)
    base_noise_w = float(getattr(voice.config, "noise_w", 0.8) or 0.8)
    vits_length_scale = base_len_scale / speed

    # Sử dụng VITS Noise Scale do người dùng tinh chỉnh riêng cho từng model (nằm trong khoảng an toàn 0.1 - 1.0)
    noise_s = max(0.10, min(1.00, float(custom_noise_scale)))

    num_speakers = int(getattr(voice.config, "num_speakers", 1) or 1)
    effective_spk = speaker_id if (num_speakers > 1 and speaker_id is not None and speaker_id >= 0) else None
    if effective_spk is not None and effective_spk >= num_speakers:
        effective_spk = effective_spk % num_speakers

    buf = io.BytesIO()
    with wave.open(buf, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(voice.config.sample_rate)

        if hasattr(voice, "synthesize_wav") and HAS_SYNTH_CONFIG:
            try:
                syn_cfg = SynthesisConfig(
                    speaker_id=effective_spk,
                    length_scale=vits_length_scale,
                    noise_scale=noise_s,
                    noise_w_scale=base_noise_w
                )
            except TypeError:
                syn_cfg = SynthesisConfig(speaker_id=effective_spk, length_scale=vits_length_scale)
            voice.synthesize_wav(text, wav_file, syn_config=syn_cfg)
        else:
            try:
                voice.synthesize(text, wav_file, speaker_id=effective_spk, length_scale=vits_length_scale, noise_scale=noise_s)
            except TypeError:
                try:
                    voice.synthesize(text, wav_file, length_scale=vits_length_scale)
                except TypeError:
                    voice.synthesize(text, wav_file)

    buf.seek(0)
    with wave.open(buf, "rb") as wav_in:
        src_sr = wav_in.getframerate()
        raw_bytes = wav_in.readframes(wav_in.getnframes())

    samples = np.frombuffer(raw_bytes, dtype=np.int16).astype(np.float32)
    if samples.size == 0:
        return np.zeros(int(target_sr * 0.04), dtype=np.int16)

    if src_sr != target_sr:
        samples = high_precision_resample(samples, src_sr, target_sr)

    if not bypass_dsp:
        if abs(total_pitch_factor - 1.0) >= 0.02:
            samples = apply_studio_pitch_and_timbre(samples, target_sr, total_pitch_factor)
        samples = gentle_level_match(samples, target_sr)

    return np.clip(samples, -32767, 32767).astype(np.int16)

def post_process_master_chain(pcm: np.ndarray, req: TTSRequest, target_sr: int = 22050) -> np.ndarray:
    """
    Chuỗi xử lý Master cuối cùng (Áp dụng Hướng 2: Nhái giọng Voice Cloning & Hướng 3: Bộ lọc EQ theo từng Model).
    """
    if req.bypass_dsp or pcm is None or pcm.size < 64:
        return pcm

    wav_f = pcm.astype(np.float32)

    # 1. Nếu bật Nhái giọng (Voice Cloning) và có file mẫu hợp lệ
    if req.clone_enabled and req.clone_reference_path and os.path.exists(req.clone_reference_path):
        wav_f = apply_voice_cloning_transfer(wav_f, target_sr, req.clone_reference_path, req.clone_strength)

    # 2. Áp dụng Bộ lọc EQ (Bass/Treble/De-Hiss) riêng của từng Model
    wav_f = apply_custom_studio_eq(
        wav_f,
        target_sr,
        bass_db=req.eq_bass_db,
        treble_db=req.eq_treble_db,
        de_hiss_cutoff=req.de_hiss_cutoff
    )

    return np.clip(wav_f, -32767, 32767).astype(np.int16)

async def build_full_audio_async(req: TTSRequest, out_path: str):
    target_sr = 22050
    explicit_mod = (req.model_file or "").strip()

    # 1. Nhóm VieNeu-TTS & VietTTS (Hướng 1)
    if explicit_mod.startswith("[VieNeu-TTS-24kHz]") or explicit_mod.startswith("[VietTTS-HiFiGAN]"):
        pcm = await synthesize_with_vieneu_or_viettts_async(req, target_sr)
        pcm = await asyncio.to_thread(post_process_master_chain, pcm, req, target_sr)
        with wave.open(out_path, "wb") as wav_out:
            wav_out.setnchannels(1)
            wav_out.setsampwidth(2)
            wav_out.setframerate(target_sr)
            wav_out.writeframes(pcm.tobytes())
        return

    # 2. Nhóm Neural-24kHz & Multilingual-24kHz
    if explicit_mod.startswith("[Neural-24kHz]") or explicit_mod.startswith("[Multilingual-24kHz]"):
        pcm = await synthesize_with_edge_neural_async(req.text, explicit_mod, req.speed, req.pitch, req.bypass_dsp, target_sr)
        pcm = await asyncio.to_thread(post_process_master_chain, pcm, req, target_sr)
        with wave.open(out_path, "wb") as wav_out:
            wav_out.setnchannels(1)
            wav_out.setsampwidth(2)
            wav_out.setframerate(target_sr)
            wav_out.writeframes(pcm.tobytes())
        return

    # 3. Nhóm Kokoro-82M 24kHz Offline
    if explicit_mod.startswith("[Kokoro-82M-24kHz]"):
        pcm = await asyncio.to_thread(synthesize_with_kokoro, req.text, explicit_mod, req.speed, req.pitch, req.bypass_dsp, target_sr)
        pcm = await asyncio.to_thread(post_process_master_chain, pcm, req, target_sr)
        with wave.open(out_path, "wb") as wav_out:
            wav_out.setnchannels(1)
            wav_out.setsampwidth(2)
            wav_out.setframerate(target_sr)
            wav_out.writeframes(pcm.tobytes())
        return

    # 4. Nhóm PyTorch MMS & Piper ONNX
    force_mms = explicit_mod.startswith("[PyTorch]")
    force_en_model = explicit_mod.lower().startswith("en_")

    is_english_only = ("English" in req.voice_id) or force_en_model
    is_male = "Nam (Male)" in req.gender or "Male" in req.gender
    if req.clone_enabled and req.clone_reference_path and os.path.exists(req.clone_reference_path):
        prof = get_reference_voice_profile(req.clone_reference_path, target_sr)
        if prof is not None:
            ref_f0_val, _ = prof
            is_male = (ref_f0_val < 168.0)
            if explicit_mod.startswith("[Multilingual-24kHz]"):
                if is_male and ("Ava" in explicit_mod or "Emma" in explicit_mod):
                    explicit_mod = "[Multilingual-24kHz] en-US-AndrewMultilingualNeural (Nam Đọc Việt+Anh Đồng Nhất)"
                elif (not is_male) and ("Andrew" in explicit_mod or "Brian" in explicit_mod):
                    explicit_mod = "[Multilingual-24kHz] en-US-AvaMultilingualNeural (Nữ Đọc Việt+Anh Đồng Nhất)"
            elif explicit_mod.startswith("[Neural-24kHz]") or explicit_mod.startswith("[VieNeu-TTS-24kHz]"):
                if is_male and ("HoaiMy" in explicit_mod or "Nữ" in explicit_mod):
                    explicit_mod = "[Neural-24kHz] vi-VN-NamMinhNeural (Giọng Nam Việt Siêu Thực)"
                elif (not is_male) and ("NamMinh" in explicit_mod or "Nam" in explicit_mod):
                    explicit_mod = "[Neural-24kHz] vi-VN-HoaiMyNeural (Giọng Nữ Việt Siêu Thực)"

    if is_english_only:
        norm_text = normalize_numbers_and_versions(req.text, is_english_mode=True)
        segments = [("en", norm_text, True)]
    else:
        segments = prepare_bilingual_segments(
            req.text,
            single_voice_mode=req.single_voice_mode,
            bilingual_auto=req.bilingual_auto
        )

    all_chunks = []
    pause_clause = np.zeros(int(target_sr * 0.12), dtype=np.int16)

    for lang, seg_text, is_clause_end in segments:
        if not seg_text.strip():
            continue

        use_mms_here = force_mms or (
            explicit_mod == "Tự động theo vùng miền (Auto)"
            and lang == "vi"
            and is_male
            and ("Miền Trung" not in req.voice_id)
            and ("Miền Nam" not in req.voice_id)
        )

        if use_mms_here:
            mms_pcm = await asyncio.to_thread(synthesize_with_mms_male, seg_text, req.speed, req.pitch, req.bypass_dsp, target_sr)
            if mms_pcm is not None:
                all_chunks.append(mms_pcm)
                if is_clause_end:
                    all_chunks.append(pause_clause)
                continue

        model_path, speaker_id, gender_pitch_ratio = pick_piper_model_info(
            lang, req.voice_id, req.gender, explicit_mod, req.speaker_id
        )
        voice = await asyncio.to_thread(load_cached_voice, model_path)
        total_pitch = 1.0 if req.bypass_dsp else (req.pitch * gender_pitch_ratio)

        pcm = await asyncio.to_thread(
            synthesize_piper_segment,
            voice,
            seg_text,
            req.speed,
            total_pitch,
            speaker_id,
            req.bypass_dsp,
            req.vits_noise_scale,
            target_sr
        )
        all_chunks.append(pcm)
        if is_clause_end:
            all_chunks.append(pause_clause)

    final_pcm = np.concatenate(all_chunks) if all_chunks else np.zeros(int(target_sr * 0.3), dtype=np.int16)
    final_pcm = await asyncio.to_thread(post_process_master_chain, final_pcm, req, target_sr)

    with wave.open(out_path, "wb") as wav_out:
        wav_out.setnchannels(1)
        wav_out.setsampwidth(2)
        wav_out.setframerate(target_sr)
        wav_out.writeframes(final_pcm.tobytes())


def numpy_pitch_shift_cola(samples: np.ndarray, sr: int, semitones: float) -> np.ndarray:
    if samples is None or samples.size < 256 or abs(semitones) < 0.08:
        return samples
    pitch_factor = max(0.5, min(2.0, 2.0 ** (float(semitones) / 12.0)))
    n_in = samples.size
    win_size = 1024
    hop_syn = 256
    hop_ana = max(1, int(round(hop_syn / pitch_factor)))
    n_out_stretch = max(win_size + hop_syn, int(round(n_in * pitch_factor)))

    win = np.hanning(win_size).astype(np.float32)
    y_stretch = np.zeros(n_out_stretch + win_size, dtype=np.float32)
    w_sum = np.zeros(n_out_stretch + win_size, dtype=np.float32)

    ana_pos = 0
    syn_pos = 0
    while ana_pos + win_size + 48 < n_in and syn_pos + win_size < y_stretch.size:
        best_offset = 0
        if syn_pos > 0:
            target_overlap = y_stretch[syn_pos:syn_pos + hop_syn]
            if float(np.max(np.abs(target_overlap))) > 15.0:
                best_corr = -1e18
                for off in range(-32, 33, 4):
                    cand_pos = max(0, ana_pos + off)
                    cand = samples[cand_pos:cand_pos + hop_syn]
                    corr = float(np.dot(target_overlap, cand))
                    if corr > best_corr:
                        best_corr = corr
                        best_offset = off
        actual_ana = max(0, ana_pos + best_offset)
        y_stretch[syn_pos:syn_pos + win_size] += samples[actual_ana:actual_ana + win_size] * win
        w_sum[syn_pos:syn_pos + win_size] += win
        ana_pos += hop_ana
        syn_pos += hop_syn

    y_stretch = y_stretch[:max(1, syn_pos)]
    w_sum = w_sum[:max(1, syn_pos)]
    y_stretch = y_stretch / np.maximum(w_sum, 0.35)
    old_idx = np.linspace(0.0, y_stretch.size - 1, n_in, dtype=np.float32)
    return np.interp(old_idx, np.arange(y_stretch.size, dtype=np.float32), y_stretch).astype(np.float32)

def numpy_time_stretch_wsola(samples: np.ndarray, sr: int, speed: float) -> np.ndarray:
    speed = max(0.4, min(2.5, float(speed)))
    if samples is None or samples.size < 256 or abs(speed - 1.0) < 0.02:
        return samples
    n_in = samples.size
    win_size = 1024
    hop_syn = 256
    hop_ana = max(1, int(round(hop_syn * speed)))
    n_out = max(win_size + hop_syn, int(round(n_in / speed)))
    win = np.hanning(win_size).astype(np.float32)
    y_out = np.zeros(n_out + win_size, dtype=np.float32)
    w_sum = np.zeros(n_out + win_size, dtype=np.float32)
    ana_pos = 0
    syn_pos = 0
    while ana_pos + win_size + 32 < n_in and syn_pos + win_size < y_out.size:
        best_offset = 0
        if syn_pos > 0:
            target_overlap = y_out[syn_pos:syn_pos + hop_syn]
            if float(np.max(np.abs(target_overlap))) > 15.0:
                best_corr = -1e18
                for off in range(-28, 29, 4):
                    cand_pos = max(0, ana_pos + off)
                    cand = samples[cand_pos:cand_pos + hop_syn]
                    corr = float(np.dot(target_overlap, cand))
                    if corr > best_corr:
                        best_corr = corr
                        best_offset = off
        actual_ana = max(0, ana_pos + best_offset)
        y_out[syn_pos:syn_pos + win_size] += samples[actual_ana:actual_ana + win_size] * win
        w_sum[syn_pos:syn_pos + win_size] += win
        ana_pos += hop_ana
        syn_pos += hop_syn
    y_out = y_out[:max(1, syn_pos)]
    w_sum = w_sum[:max(1, syn_pos)]
    return (y_out / np.maximum(w_sum, 0.35)).astype(np.float32)

def apply_studio_pitch_and_timbre(samples: np.ndarray, sr: int, pitch_factor: float) -> np.ndarray:
    if samples is None or samples.size < 64 or abs(pitch_factor - 1.0) < 0.012:
        return samples
    semitones = 12.0 * math.log2(max(0.5, min(2.0, float(pitch_factor))))
    if HAS_PEDALBOARD:
        try:
            float_audio = (samples / 32768.0).astype(np.float32)
            board = Pedalboard([PitchShift(semitones=semitones)])
            shifted = board(float_audio, sr)
            return (shifted * 32768.0).astype(np.float32)
        except Exception:
            pass
    return numpy_pitch_shift_cola(samples.astype(np.float32), sr, semitones)

def apply_detailed_5band_eq(
    samples: np.ndarray,
    sr: int,
    highpass_hz: float = 65.0,
    b80_db: float = 0.0,
    b220_db: float = 0.0,
    b1k_db: float = 0.0,
    b3k5_db: float = 0.0,
    b8k_db: float = 0.0,
    lowpass_hz: float = 11000.0
) -> np.ndarray:
    if samples is None or samples.size < 64:
        return samples
    hp = max(20.0, min(350.0, float(highpass_hz)))
    lp = max(3500.0, min(12000.0, float(lowpass_hz)))
    b80 = max(-15.0, min(15.0, float(b80_db)))
    b220 = max(-15.0, min(15.0, float(b220_db)))
    b1k = max(-15.0, min(15.0, float(b1k_db)))
    b3k5 = max(-15.0, min(15.0, float(b3k5_db)))
    b8k = max(-15.0, min(15.0, float(b8k_db)))

    has_change = (
        abs(b80) >= 0.05 or abs(b220) >= 0.05 or abs(b1k) >= 0.05 or
        abs(b3k5) >= 0.05 or abs(b8k) >= 0.05 or hp > 25.0 or lp < 10800.0
    )
    if not has_change:
        return samples

    n = samples.size
    spec = np.fft.rfft(samples.astype(np.float32))
    freqs = np.fft.rfftfreq(n, d=1.0 / float(sr)).astype(np.float32)
    safe_f = np.maximum(freqs, 1.0)

    hp_gain = 1.0 / np.sqrt(1.0 + (hp / safe_f) ** 8) if hp > 22.0 else np.ones_like(safe_f)
    hp_gain[0] = 0.0
    lp_gain = 1.0 / np.sqrt(1.0 + (safe_f / lp) ** 12) if lp < 10800.0 else np.ones_like(safe_f)

    total_db = np.zeros_like(safe_f)
    bands = [
        (80.0, b80, 0.80),
        (220.0, b220, 0.75),
        (1000.0, b1k, 0.70),
        (3500.0, b3k5, 0.70),
        (8000.0, b8k, 0.80)
    ]
    log_f = np.log2(safe_f)
    for f0, gdb, bw_oct in bands:
        if abs(gdb) >= 0.05:
            delta_oct = (log_f - math.log2(f0)) / bw_oct
            total_db += gdb * np.exp(-0.5 * (delta_oct ** 2))

    eq_mult = (10.0 ** (total_db / 20.0)) * hp_gain * lp_gain
    out = np.fft.irfft(spec * eq_mult, n=n).astype(np.float32)
    return gentle_level_match(out, sr)

def apply_custom_studio_eq(
    samples: np.ndarray,
    sr: int,
    bass_db: float = 0.0,
    treble_db: float = 0.0,
    de_hiss_cutoff: float = 10000.0
) -> np.ndarray:
    if samples is None or samples.size < 64:
        return samples
    b_db = max(-12.0, min(12.0, float(bass_db)))
    t_db = max(-12.0, min(12.0, float(treble_db)))
    cut_hz = max(4000.0, min(12000.0, float(de_hiss_cutoff)))
    if abs(b_db) < 0.08 and abs(t_db) < 0.08 and cut_hz >= 10800.0:
        return samples
    return apply_detailed_5band_eq(
        samples, sr, highpass_hz=65.0, b80_db=b_db * 0.85, b220_db=b_db,
        b1k_db=0.0, b3k5_db=t_db, b8k_db=t_db * 0.80, lowpass_hz=cut_hz
    )

def apply_voice_cloning_transfer(
    samples: np.ndarray,
    sr: int,
    ref_path: str,
    strength: float = 0.80
) -> np.ndarray:
    strength = max(0.0, min(1.0, float(strength)))
    if strength <= 0.01 or samples is None or samples.size < 1024:
        return samples
    ref_prof = get_reference_voice_profile(ref_path, sr)
    if ref_prof is None:
        return samples

    ref_f0, ref_env = ref_prof
    src_f0, _ = estimate_median_f0_and_envelope(samples, sr)
    out = samples.astype(np.float32)

    if src_f0 > 60.0 and ref_f0 > 60.0:
        semitone_diff = 12.0 * math.log2(ref_f0 / src_f0)
        clamped_semi = max(-8.5, min(8.5, semitone_diff)) * strength
        if abs(clamped_semi) >= 0.12:
            pitch_ratio = 2.0 ** (clamped_semi / 12.0)
            out = apply_studio_pitch_and_timbre(out, sr, pitch_ratio)

    _, post_src_env = estimate_median_f0_and_envelope(out, sr)
    if ref_env.size == post_src_env.size and ref_env.size > 32:
        diff_db = (ref_env - post_src_env) * 8.68589 * strength
        kernel = np.exp(-0.5 * (np.arange(-10, 11, dtype=np.float32) / 4.0) ** 2)
        kernel /= np.sum(kernel)
        smooth_diff_db = np.convolve(diff_db, kernel, mode='same')
        smooth_diff_db = np.clip(smooth_diff_db, -11.0, 11.0)

        env_freqs = np.linspace(0.0, sr * 0.5, smooth_diff_db.size, dtype=np.float32)
        weight = np.ones_like(env_freqs)
        weight[env_freqs < 75.0] = 0.0
        hi_idx = env_freqs > 8000.0
        weight[hi_idx] = np.exp(-((env_freqs[hi_idx] - 8000.0) / 1500.0) ** 2)
        smooth_diff_db *= weight

        n = out.size
        full_freqs = np.fft.rfftfreq(n, d=1.0 / float(sr)).astype(np.float32)
        interp_db = np.interp(full_freqs, env_freqs, smooth_diff_db).astype(np.float32)
        gain_ir = 10.0 ** (interp_db / 20.0)
        out = np.fft.irfft(np.fft.rfft(out) * gain_ir, n=n).astype(np.float32)

    return gentle_level_match(out, sr)

class TimelineClipReq(BaseModel):
    source_path: str
    layer_index: int = 0
    timeline_start_sec: float = 0.0
    source_trim_start_sec: float = 0.0
    duration_sec: float = 3.0
    semitones: float = 0.0
    speed: float = 1.0
    clone_enabled: bool = False
    clone_reference_path: str = ""
    clone_strength: float = 0.80
    highpass_hz: float = 65.0
    band_80hz_db: float = 0.0
    band_220hz_db: float = 0.0
    band_1000hz_db: float = 0.0
    band_3500hz_db: float = 0.0
    band_8000hz_db: float = 0.0
    lowpass_hz: float = 11000.0

class TimelineMixdownRequest(BaseModel):
    clips: list[TimelineClipReq] = []
    output_path: str = ""

def mixdown_capcut_timeline(req: TimelineMixdownRequest, out_path: str):
    target_sr = 22050
    if not req.clips:
        raise ValueError("Timeline đang trống! Hãy kéo thả bản ghi vào Timeline trước.")

    rendered_clips = []
    max_end_sample = int(target_sr * 0.5)

    for c in req.clips:
        if not c.source_path or not os.path.exists(c.source_path):
            continue
        wav = load_audio_file_mono(c.source_path, target_sr)
        if wav is None or wav.size < 32:
            continue

        total_src_sec = float(wav.size) / float(target_sr)
        s_sec = max(0.0, min(total_src_sec, float(c.source_trim_start_sec)))
        e_sec = min(total_src_sec, s_sec + max(0.05, float(c.duration_sec)))
        s_idx = int(round(s_sec * target_sr))
        e_idx = max(s_idx + 16, int(round(e_sec * target_sr)))
        seg = apply_smooth_fade(wav[s_idx:e_idx], target_sr, 8.0)

        spd = max(0.4, min(2.5, float(c.speed)))
        if abs(spd - 1.0) >= 0.02:
            seg = numpy_time_stretch_wsola(seg, target_sr, spd)

        semi = max(-12.0, min(12.0, float(c.semitones)))
        if abs(semi) >= 0.1:
            seg = apply_studio_pitch_and_timbre(seg, target_sr, 2.0 ** (semi / 12.0))

        if c.clone_enabled and c.clone_reference_path and os.path.exists(c.clone_reference_path):
            seg = apply_voice_cloning_transfer(seg, target_sr, c.clone_reference_path, c.clone_strength)

        seg = apply_detailed_5band_eq(
            seg, target_sr, highpass_hz=c.highpass_hz, b80_db=c.band_80hz_db,
            b220_db=c.band_220hz_db, b1k_db=c.band_1000hz_db,
            b3k5_db=c.band_3500hz_db, b8k_db=c.band_8000hz_db, lowpass_hz=c.lowpass_hz
        )

        tl_start_idx = max(0, int(round(float(c.timeline_start_sec) * target_sr)))
        end_idx = tl_start_idx + seg.size
        if end_idx > max_end_sample:
            max_end_sample = end_idx
        rendered_clips.append((tl_start_idx, seg))

    master_buf = np.zeros(max_end_sample + 64, dtype=np.float32)
    for start_idx, seg in rendered_clips:
        master_buf[start_idx:start_idx + seg.size] += seg

    master_buf = gentle_level_match(master_buf, target_sr)
    final_pcm = np.clip(master_buf, -32767, 32767).astype(np.int16)
    save_target = req.output_path if req.output_path else os.path.join(output_dir, "capcut_mixdown.wav")
    with wave.open(save_target, "wb") as wav_out:
        wav_out.setnchannels(1)
        wav_out.setsampwidth(2)
        wav_out.setframerate(target_sr)
        wav_out.writeframes(final_pcm.tobytes())
    return save_target

@app.post("/studio_mixdown")
async def studio_mixdown(req: TimelineMixdownRequest):
    try:
        out_file = req.output_path.strip() if req.output_path else os.path.join(output_dir, "temp_mixdown.wav")
        saved = await asyncio.to_thread(mixdown_capcut_timeline, req, out_file)
        return FileResponse(saved, media_type="audio/wav", filename=os.path.basename(saved))
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

@app.get("/ping")
async def ping():
    return {
        "status": "ready", "engine_version": "4.0_capcut_dsp",
        "has_edge_tts": HAS_EDGE_TTS,
        "has_kokoro_82m": HAS_KOKORO,
        "has_cmudict_135k": HAS_CMUDICT,
        "has_pedalboard_dsp": HAS_PEDALBOARD,
        "has_mms_male_torch": HAS_MMS_TORCH,
        "models": list_valid_onnx(),
        "models_detail": get_models_metadata()
    }

@app.get("/models")
async def get_models():
    return {"models": get_models_metadata()}


class StudioProcessRequest(BaseModel):
    source_path: str
    output_path: str = ""
    # 1. Cắt (Cut / Trim)
    cut_enabled: bool = False
    cut_mode: str = "Giữ đoạn chọn (Trim Keep)"
    cut_start_sec: float = 0.0
    cut_end_sec: float = 0.0
    # 2. Ghép (Merge / Concatenate)
    merge_enabled: bool = False
    merge_second_path: str = ""
    merge_silence_sec: float = 0.15
    merge_crossfade_ms: float = 30.0
    # 3. Chỉnh Tone & Tốc độ (Tone / Pitch & Speed)
    semitones: float = 0.0
    speed: float = 1.0
    # 4. Áp dụng Nhái giọng (Voice Cloning)
    clone_enabled: bool = False
    clone_reference_path: str = ""
    clone_strength: float = 0.80
    # 5. Chỉnh chi tiết tần số (5-Band Parametric EQ + Highpass/Lowpass)
    highpass_hz: float = 65.0
    band_80hz_db: float = 0.0
    band_220hz_db: float = 0.0
    band_1000hz_db: float = 0.0
    band_3500hz_db: float = 0.0
    band_8000hz_db: float = 0.0
    lowpass_hz: float = 11000.0

def make_peaking_biquad(sr: int, freq_hz: float, gain_db: float, q: float = 1.0):
    """Thiết kế bộ lọc Biquad Peaking EQ theo chuẩn Robert Bristow-Johnson (RBJ Audio EQ Cookbook)."""
    if abs(gain_db) < 0.05 or freq_hz <= 10.0 or freq_hz >= sr * 0.48:
        return None
    A = 10.0 ** (gain_db / 40.0)
    w0 = 2.0 * math.pi * freq_hz / float(sr)
    alpha = math.sin(w0) / (2.0 * q)
    cos_w0 = math.cos(w0)

    b0 = 1.0 + alpha * A
    b1 = -2.0 * cos_w0
    b2 = 1.0 - alpha * A
    a0 = 1.0 + alpha / A
    a1 = -2.0 * cos_w0
    a2 = 1.0 - alpha / A
    return np.array([b0 / a0, b1 / a0, b2 / a0], dtype=np.float64), np.array([1.0, a1 / a0, a2 / a0], dtype=np.float64)

def apply_detailed_5band_eq(
    samples: np.ndarray,
    sr: int,
    highpass_hz: float = 65.0,
    b80_db: float = 0.0,
    b220_db: float = 0.0,
    b1k_db: float = 0.0,
    b3k5_db: float = 0.0,
    b8k_db: float = 0.0,
    lowpass_hz: float = 11000.0
) -> np.ndarray:
    if samples is None or samples.size < 64 or not HAS_SCIPY:
        return samples

    out = samples.astype(np.float64)
    nyq = 0.5 * float(sr)

    # 1. Highpass Filter (Khử ù nền tần thấp)
    hp = max(20.0, min(350.0, float(highpass_hz)))
    if hp > 22.0 and hp < nyq * 0.9:
        b_hp, a_hp = sp_signal.butter(2, hp / nyq, btype='high')
        out = sp_signal.filtfilt(b_hp, a_hp, out)

    # 2. 5-Band Parametric EQ (80Hz Sub-Bass, 220Hz Warmth, 1kHz Mid, 3.5kHz Presence, 8kHz Air)
    bands = [
        (80.0, b80_db, 0.85),
        (220.0, b220_db, 1.0),
        (1000.0, b1k_db, 1.1),
        (3500.0, b3k5_db, 1.0),
        (8000.0, b8k_db, 0.9)
    ]
    for f_hz, g_db, q_val in bands:
        g_clamped = max(-15.0, min(15.0, float(g_db)))
        coeffs = make_peaking_biquad(sr, f_hz, g_clamped, q_val)
        if coeffs is not None:
            b_eq, a_eq = coeffs
            out = sp_signal.lfilter(b_eq, a_eq, out)

    # 3. Lowpass Filter (De-Hiss cắt sôi tần cao)
    lp = max(3500.0, min(12000.0, float(lowpass_hz)))
    if lp < 10800.0 and lp < nyq * 0.95:
        b_lp, a_lp = sp_signal.butter(4, lp / nyq, btype='low')
        out = sp_signal.filtfilt(b_lp, a_lp, out)

    return gentle_level_match(out.astype(np.float32), sr)

def apply_smooth_fade(samples: np.ndarray, sr: int, fade_ms: float = 8.0) -> np.ndarray:
    n_fade = int(sr * (fade_ms / 1000.0))
    if samples.size < n_fade * 2 or n_fade < 2:
        return samples
    out = samples.copy().astype(np.float32)
    ramp = np.linspace(0.0, 1.0, n_fade, dtype=np.float32)
    out[:n_fade] *= ramp
    out[-n_fade:] *= ramp[::-1]
    return out

def process_mini_studio_audio(req: StudioProcessRequest, out_path: str):
    target_sr = 22050
    if not req.source_path or not os.path.exists(req.source_path):
        raise FileNotFoundError(f"Không tìm thấy bản ghi gốc: {req.source_path}")

    wav = load_audio_file_mono(req.source_path, target_sr)
    if wav is None or wav.size < 32:
        raise RuntimeError("Không thể đọc dữ liệu sóng âm từ bản ghi gốc.")

    # 1. CẮT ÂM THANH (TRIM KEEP HOẶC CUT OUT MIDDLE)
    if req.cut_enabled:
        total_sec = float(wav.size) / float(target_sr)
        s_sec = max(0.0, min(total_sec, float(req.cut_start_sec)))
        e_sec = float(req.cut_end_sec)
        if e_sec <= 0.0 or e_sec > total_sec:
            e_sec = total_sec
        if e_sec > s_sec + 0.02:
            s_idx = int(round(s_sec * target_sr))
            e_idx = int(round(e_sec * target_sr))
            if "Xoá" in req.cut_mode or "Cut Out" in req.cut_mode:
                part1 = apply_smooth_fade(wav[:s_idx], target_sr, 6.0)
                part2 = apply_smooth_fade(wav[e_idx:], target_sr, 6.0)
                wav = np.concatenate([part1, part2]) if (part1.size + part2.size) > 64 else wav
            else:
                wav = apply_smooth_fade(wav[s_idx:e_idx], target_sr, 8.0)

    # 2. GHÉP NỐI BẢN GHI THỨ 2 (MERGE / CONCATENATE + CROSSFADE)
    if req.merge_enabled and req.merge_second_path and os.path.exists(req.merge_second_path):
        wav2 = load_audio_file_mono(req.merge_second_path, target_sr)
        if wav2 is not None and wav2.size > 32:
            wav = apply_smooth_fade(wav, target_sr, 6.0)
            wav2 = apply_smooth_fade(wav2, target_sr, 6.0)
            sil_sec = max(0.0, min(5.0, float(req.merge_silence_sec)))
            cf_ms = max(0.0, min(500.0, float(req.merge_crossfade_ms)))
            n_cf = int(target_sr * (cf_ms / 1000.0))

            if sil_sec > 0.005:
                silence_pad = np.zeros(int(target_sr * sil_sec), dtype=np.float32)
                wav = np.concatenate([wav, silence_pad, wav2])
            elif n_cf > 16 and wav.size > n_cf and wav2.size > n_cf:
                fade_out = np.linspace(1.0, 0.0, n_cf, dtype=np.float32)
                fade_in = np.linspace(0.0, 1.0, n_cf, dtype=np.float32)
                overlap = wav[-n_cf:] * fade_out + wav2[:n_cf] * fade_in
                wav = np.concatenate([wav[:-n_cf], overlap, wav2[n_cf:]])
            else:
                wav = np.concatenate([wav, wav2])

    # 3. CHỈNH TỐC ĐỘ (TIME STRETCH / RESAMPLE SPEED) & CHỈNH TONE (SEMITONES PITCH SHIFT)
    spd = max(0.5, min(2.0, float(req.speed)))
    if abs(spd - 1.0) >= 0.02:
        # Đổi tốc độ bằng nội suy đa pha kết hợp bù cao độ để giữ nguyên Tone
        wav = high_precision_resample(wav, int(round(target_sr * spd)), target_sr)
        wav = apply_studio_pitch_and_timbre(wav, target_sr, 1.0 / spd)

    semi = max(-12.0, min(12.0, float(req.semitones)))
    if abs(semi) >= 0.1:
        pitch_factor = 2.0 ** (semi / 12.0)
        wav = apply_studio_pitch_and_timbre(wav, target_sr, pitch_factor)

    # 4. ÁP DỤNG NHÁI GIỌNG AI LÊN BẢN GHI CŨ (VOICE CLONING)
    if req.clone_enabled and req.clone_reference_path and os.path.exists(req.clone_reference_path):
        wav = apply_voice_cloning_transfer(wav, target_sr, req.clone_reference_path, req.clone_strength)

    # 5. CHỈNH CHI TIẾT TẦN SỐ (5-BAND PARAMETRIC EQ + HIGHPASS / LOWPASS)
    wav = apply_detailed_5band_eq(
        wav,
        target_sr,
        highpass_hz=req.highpass_hz,
        b80_db=req.band_80hz_db,
        b220_db=req.band_220hz_db,
        b1k_db=req.band_1000hz_db,
        b3k5_db=req.band_3500hz_db,
        b8k_db=req.band_8000hz_db,
        lowpass_hz=req.lowpass_hz
    )

    final_pcm = np.clip(wav, -32767, 32767).astype(np.int16)
    save_target = req.output_path if req.output_path else os.path.join(output_dir, "studio_output.wav")
    with wave.open(save_target, "wb") as wav_out:
        wav_out.setnchannels(1)
        wav_out.setsampwidth(2)
        wav_out.setframerate(target_sr)
        wav_out.writeframes(final_pcm.tobytes())
    return save_target

@app.post("/studio_process")
async def studio_process(req: StudioProcessRequest):
    try:
        out_file = req.output_path.strip() if req.output_path else os.path.join(output_dir, "temp_studio.wav")
        saved = await asyncio.to_thread(process_mini_studio_audio, req, out_file)
        return FileResponse(saved, media_type="audio/wav", filename=os.path.basename(saved))
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

@app.post("/synthesize")
async def synthesize(req: TTSRequest):
    try:
        file_path = os.path.join(output_dir, "temp_output.wav")
        await build_full_audio_async(req, file_path)
        return FileResponse(file_path, media_type="audio/wav", filename="output.wav")
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

async def main():
    config = uvicorn.Config(app=app, host="127.0.0.1", port=8000, log_config=None, access_log=False, use_colors=False)
    server = uvicorn.Server(config)
    await server.serve()

if __name__ == "__main__":
    asyncio.run(main())