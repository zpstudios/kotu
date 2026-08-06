#!/usr/bin/env python3
"""스피커 테스트 음악 합성 v2 — 신나는 128 BPM (32초, 44.1kHz 스테레오 WAV).

사용자 요구(v0.19.0): 좌/우가 명확히 구분되고 + 더 신나게.

구성:
  1) 0–8s    왼쪽 채널만  — 킥+하이햇+베이스+리프 전부 하드 L (좌 스피커 확인)
  2) 8–16s   오른쪽 채널만 — 같은 그루브, 응답 리프 전부 하드 R (우 스피커 확인)
  3) 16–26s  풀 믹스     — 킥·베이스·클랩 센터, 리드는 반마디마다 L↔R 핑퐁
  4) 26–29s  저음 스윕(35→90Hz, 우퍼) → 고음 핑(8/10/12kHz, L→R→양쪽, 트위터)
  5) 29–32s  마무리 스탭 + 페이드

재생성:
  python3 docs/gen_test_audio.py           # /tmp/test-audio.wav 생성
  ffmpeg -y -i src/WinUtil.Module.Video/Assets/test-clip.mp4 -i /tmp/test-audio.wav \
         -map 0:v -map 1:a -c:v copy -c:a aac -b:a 160k -movflags +faststart /tmp/test-clip-new.mp4
  mv /tmp/test-clip-new.mp4 src/WinUtil.Module.Video/Assets/test-clip.mp4
"""
import wave

import numpy as np

SR = 44100
DUR = 32.0
BPM = 128.0
BEAT = 60.0 / BPM          # 0.46875s
buf = np.zeros((int(SR * DUR), 2))  # [샘플, 좌우]


def add(sig, t0, ch):
    """ch: 0=L, 1=R, 2=양쪽. t0초 위치에 신호를 더한다."""
    i = int(t0 * SR)
    seg = buf[i:i + len(sig)]
    s = sig[:len(seg)]
    if ch in (0, 2):
        seg[:, 0] += s
    if ch in (1, 2):
        seg[:, 1] += s


def env_ad(n, attack, decay):
    t = np.arange(n) / SR
    return np.minimum(1, t / max(attack, 1e-4)) * np.exp(-t / decay)


def kick(amp=0.85):
    """댄스 킥: 120→45Hz 피치 드롭 사인."""
    n = int(SR * 0.28)
    t = np.arange(n) / SR
    freq = 45 + (120 - 45) * np.exp(-t * 28)
    phase = 2 * np.pi * np.cumsum(freq) / SR
    return amp * env_ad(n, 0.001, 0.09) * np.sin(phase)


RNG = np.random.default_rng(20260806)  # 재현 가능한 노이즈


def hat(amp=0.16, dur=0.05):
    """하이햇: 고역 노이즈 (1차 차분으로 저역 제거)."""
    n = int(SR * dur)
    noise = np.diff(RNG.standard_normal(n + 1))
    return amp * env_ad(n, 0.001, 0.02) * noise


def clap(amp=0.30):
    """클랩/스네어: 밴드 노이즈, 짧은 이중 어택."""
    n = int(SR * 0.18)
    noise = np.diff(RNG.standard_normal(n + 1))
    e = env_ad(n, 0.002, 0.05)
    e[int(0.012 * SR):] += 0.6 * env_ad(n - int(0.012 * SR), 0.001, 0.06)
    return amp * e * noise


def saw(freq, n, harmonics=8):
    """유사 톱니파: 배음 합(1/k). 펀치 있는 신스 소리."""
    t = np.arange(n) / SR
    w = np.zeros(n)
    for k in range(1, harmonics + 1):
        w += np.sin(2 * np.pi * freq * k * t) / k
    return w


def bassnote(freq, dur, amp=0.5):
    n = int(SR * dur)
    return amp * env_ad(n, 0.004, 0.14) * (
        0.7 * saw(freq, n, 5) + 0.5 * np.sin(2 * np.pi * freq * np.arange(n) / SR))


def lead(freq, dur, amp=0.4):
    """리드: 살짝 디튠한 톱니 2개 — 두껍고 신나는 소리."""
    n = int(SR * dur)
    return amp * env_ad(n, 0.004, 0.16) * (saw(freq, n, 8) + saw(freq * 1.006, n, 8)) * 0.5


def stab(freqs, dur, amp=0.18):
    n = int(SR * dur)
    w = sum(saw(f, n, 6) for f in freqs)
    return amp * env_ad(n, 0.004, 0.12) * w / len(freqs)


N = {  # 음이름 → 주파수
    "A1": 55.00, "C2": 65.41, "D2": 73.42, "E2": 82.41, "G2": 98.00, "A2": 110.00,
    "C3": 130.81, "D3": 146.83, "E3": 164.81, "G3": 196.00, "A3": 220.00,
    "C4": 261.63, "D4": 293.66, "E4": 329.63, "G4": 392.00, "A4": 440.00,
    "C5": 523.25, "D5": 587.33, "E5": 659.26, "G5": 783.99, "A5": 880.00,
    "C6": 1046.50, "D6": 1174.66, "E6": 1318.51,
}


def groove(t0, dur, ch, riff, bass_line):
    """킥(4분)+햇(8분 오프비트)+클랩(백비트)+베이스(8분)+리프(16분)를 ch 채널에 깐다."""
    beats = int(round(dur / BEAT))
    for b in range(beats):
        add(kick(), t0 + b * BEAT, ch)
        add(hat(), t0 + (b + 0.5) * BEAT, ch)
        if b % 4 in (1, 3):  # 백비트 클랩
            add(clap(0.22), t0 + b * BEAT, ch)
    eighth = BEAT / 2
    for k in range(beats * 2):
        add(bassnote(N[bass_line[(k // 4) % len(bass_line)]], eighth * 0.9, 0.42), t0 + k * eighth, ch)
    sixteenth = BEAT / 4
    for k in range(beats * 4):
        note = riff[k % len(riff)]
        if note:
            add(lead(N[note], sixteenth * 1.6, 0.30), t0 + k * sixteenth, ch)


# A마이너 펜타토닉 리프 (신나는 레이브풍) — ""는 쉼표
RIFF_L = ["A4", "", "C5", "A4", "E5", "", "D5", "C5", "A4", "", "C5", "D5", "E5", "D5", "C5", "A4"]
RIFF_R = ["C5", "", "E5", "C5", "G5", "", "E5", "D5", "C5", "", "D5", "E5", "G5", "E5", "D5", "C5"]
BASS = ["A1", "A1", "C2", "D2"]

# 1) 0–8s 왼쪽만 / 2) 8–16s 오른쪽만
groove(0.0, 8.0, 0, RIFF_L, BASS)
groove(8.0, 8.0, 1, RIFF_R, BASS)

# 3) 16–26s 풀 믹스: 리듬 섹션은 센터, 리드는 반마디(2박)마다 L↔R 핑퐁
groove(16.0, 10.0, 2, [""] * 16, BASS)  # 킥+햇+클랩+베이스만 (리프는 핑퐁으로 따로)
half_bar = BEAT * 2
sixteenth = BEAT / 4
k = 0
t = 16.0
while t < 25.8:
    side = 0 if k % 2 == 0 else 1
    riff = RIFF_L if side == 0 else RIFF_R
    for j in range(8):  # 반마디 = 16분음표 8개
        note = riff[j % len(riff)]
        if note:
            add(lead(N[note], sixteenth * 1.6, 0.34), t + j * sixteenth, side)
    k += 1
    t += half_bar
for b, chord in enumerate([["A3", "C4", "E4"], ["C4", "E4", "G4"], ["D4", "G4", "A4"], ["A3", "C4", "E4"]]):
    add(stab([N[n] for n in chord], BEAT * 1.5, 0.14), 16 + b * BEAT * 4 + BEAT * 2, 2)

# 4a) 26–28.2s 저음 스윕 35→90Hz (우퍼)
n = int(SR * 2.2)
t = np.arange(n) / SR
freq = 35 + (90 - 35) * t / 2.2
phase = 2 * np.pi * np.cumsum(freq) / SR
add(0.55 * np.minimum(1, t * 8) * np.minimum(1, (2.2 - t) * 2) * np.sin(phase), 26.0, ch=2)

# 4b) 28.2–29.6s 고음 핑 (트위터, L→R→양쪽)
for k, (f, ch) in enumerate([(8000, 0), (10000, 1), (12000, 2)]):
    tt = np.arange(int(SR * 0.35)) / SR
    e = np.minimum(1, tt * 300) * np.exp(-7 * tt)
    add(0.16 * e * np.sin(2 * np.pi * f * tt), 28.2 + k * 0.45, ch=ch)

# 5) 29.6–32s 마무리: 킥 + A 마이너 롱 스탭 페이드
add(kick(0.9), 29.6, 2)
n = int(SR * 2.3)
t = np.arange(n) / SR
w = sum(saw(N[x], n, 6) for x in ["A1", "A2", "C4", "E4", "A4"])
add(0.09 * np.minimum(1, t * 6) * np.exp(-1.4 * t) * w, 29.65, ch=2)

# 소프트 클립(펀치 유지) → 정규화 → 16bit WAV
buf = np.tanh(buf * 1.15)
buf *= 0.95 / max(1e-9, np.abs(buf).max())
pcm = (buf * 32767).astype("<i2")
with wave.open("/tmp/test-audio.wav", "wb") as f:
    f.setnchannels(2)
    f.setsampwidth(2)
    f.setframerate(SR)
    f.writeframes(pcm.tobytes())

# 섹션별 채널 RMS 검증 출력 (좌/우 분리 확인용)
for name, a, b in [("1 L만", 0, 8), ("2 R만", 8, 16), ("3 풀믹스", 16, 26),
                   ("4 테스트톤", 26, 29.6), ("5 아웃트로", 29.6, 32)]:
    seg = buf[int(a * SR):int(b * SR)]
    print(f"{name}: L rms={np.sqrt((seg[:, 0] ** 2).mean()):.3f}  R rms={np.sqrt((seg[:, 1] ** 2).mean()):.3f}")
print("wrote /tmp/test-audio.wav")
