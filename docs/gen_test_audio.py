#!/usr/bin/env python3
"""스피커 테스트 음악 합성 v3 — 칩튠(레트로 게임)풍 120 BPM (32초, 44.1kHz 스테레오 WAV).

사용자 요구(v0.22.0): v2(레이브풍)보다 키를 낮추고 + 게임기(NES) 칩튠 느낌으로.
  - 리드: 펄스파(듀티 25%) + 살짝 비브라토, v2보다 한 옥타브 낮은 A3~E4 대역
  - 베이스: NES 삼각파(16단계 양자화) — 낮고 둥근 저음
  - 드럼: 노이즈 킥/햇/스네어 (패미컴 노이즈 채널 느낌)
  - 풀 믹스 구간: 칩튠 상징인 고속 아르페지오(듀티 12.5%)를 깔았다

구성(섹션 경계는 v2와 동일 — 영상과 동기):
  1) 0–8s    왼쪽 채널만  — 전부 하드 L (좌 스피커 확인)
  2) 8–16s   오른쪽 채널만 — 응답 리프 전부 하드 R (우 스피커 확인)
  3) 16–26s  풀 믹스     — 리듬 센터, 리드는 반마디마다 L↔R 핑퐁 + 아르페지오
  4) 26–29.6s 저음 스윕(35→90Hz, 우퍼) → 고음 핑(8/10/12kHz, L→R→양쪽, 트위터)
  5) 29.6–32s 마무리 킥 + 칩 코드 페이드

재생성:
  python3 docs/gen_test_audio.py           # /tmp/zp-test-audio.wav 생성
  ffmpeg -y -i src/WinUtil.Module.Video/Assets/test-clip.mp4 -i /tmp/zp-test-audio.wav \
         -map 0:v -map 1:a -c:v copy -c:a aac -b:a 160k -movflags +faststart /tmp/test-clip-new.mp4
  mv /tmp/test-clip-new.mp4 src/WinUtil.Module.Video/Assets/test-clip.mp4
"""
import wave

import numpy as np

SR = 44100
DUR = 32.0
BPM = 120.0
BEAT = 60.0 / BPM          # 0.5s — 8초 섹션 = 딱 16박(4마디)
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


# ---------- 칩튠 음원 ----------

def square(freq, n, duty=0.5, vib=0.0):
    """펄스파. duty로 음색(50%=클라리넷, 25%=NES 리드, 12.5%=얇은 아르페지오), vib=비브라토 깊이."""
    t = np.arange(n) / SR
    f = freq * (1 + vib * np.sin(2 * np.pi * 5.5 * t))
    ph = np.cumsum(f) / SR
    return ((ph % 1.0) < duty) * 2.0 - 1.0


def nes_triangle(freq, n):
    """NES 삼각파 채널: 16단계 계단 양자화 삼각파 — 둥글고 낮은 베이스."""
    ph = (freq * np.arange(n) / SR) % 1.0
    w = 4 * np.abs(ph - 0.5) - 1
    return np.round(w * 7.5) / 7.5


def kick(amp=0.85):
    """칩 킥: 90→38Hz 피치 드롭 사인 — v2보다 낮게."""
    n = int(SR * 0.26)
    t = np.arange(n) / SR
    freq = 38 + (90 - 38) * np.exp(-t * 30)
    phase = 2 * np.pi * np.cumsum(freq) / SR
    return amp * env_ad(n, 0.001, 0.10) * np.sin(phase)


RNG = np.random.default_rng(20260806)  # 재현 가능한 노이즈


def hat(amp=0.13, dur=0.04):
    """햇: 고역 노이즈 (1차 차분으로 저역 제거) — 패미컴 노이즈 채널 짧은 틱."""
    n = int(SR * dur)
    noise = np.diff(RNG.standard_normal(n + 1))
    return amp * env_ad(n, 0.001, 0.015) * noise


def snare(amp=0.26):
    """스네어: 노이즈 버스트, 킥보다 밝고 햇보다 길게."""
    n = int(SR * 0.14)
    noise = np.diff(RNG.standard_normal(n + 1))
    return amp * env_ad(n, 0.001, 0.045) * noise


def bassnote(freq, dur, amp=0.55):
    n = int(SR * dur)
    return amp * env_ad(n, 0.003, 0.13) * nes_triangle(freq, n)


def lead(freq, dur, amp=0.4):
    """리드: 듀티 25% 펄스 + 얕은 비브라토 — 전형적인 칩튠 멜로디 음색."""
    n = int(SR * dur)
    return amp * env_ad(n, 0.003, 0.15) * square(freq, n, duty=0.25, vib=0.004)


def arp_note(freq, dur, amp=0.11):
    """아르페지오용: 듀티 12.5% 펄스, 짧은 감쇠."""
    n = int(SR * dur)
    return amp * env_ad(n, 0.002, 0.05) * square(freq, n, duty=0.125)


N = {  # 음이름 → 주파수
    "A1": 55.00, "C2": 65.41, "D2": 73.42, "E2": 82.41, "G2": 98.00, "A2": 110.00,
    "C3": 130.81, "D3": 146.83, "E3": 164.81, "G3": 196.00, "A3": 220.00,
    "C4": 261.63, "D4": 293.66, "E4": 329.63, "G4": 392.00, "A4": 440.00,
    "C5": 523.25, "D5": 587.33, "E5": 659.26, "G5": 783.99, "A5": 880.00,
}


def groove(t0, dur, ch, riff, bass_line):
    """킥(4분)+햇(8분 오프비트)+스네어(백비트)+삼각파 베이스(8분)+리프(16분)를 ch 채널에 깐다."""
    beats = int(round(dur / BEAT))
    for b in range(beats):
        add(kick(), t0 + b * BEAT, ch)
        add(hat(), t0 + (b + 0.5) * BEAT, ch)
        if b % 4 in (1, 3):  # 백비트 스네어
            add(snare(), t0 + b * BEAT, ch)
    eighth = BEAT / 2
    for k in range(beats * 2):
        add(bassnote(N[bass_line[(k // 4) % len(bass_line)]], eighth * 0.9), t0 + k * eighth, ch)
    sixteenth = BEAT / 4
    for k in range(beats * 4):
        note = riff[k % len(riff)]
        if note:
            add(lead(N[note], sixteenth * 1.6, 0.32), t0 + k * sixteenth, ch)


# A마이너 펜타토닉 리프 — v2에서 한 옥타브 내림(A3~E4/G4). ""는 쉼표
RIFF_L = ["A3", "", "C4", "A3", "E4", "", "D4", "C4", "A3", "", "C4", "D4", "E4", "D4", "C4", "A3"]
RIFF_R = ["C4", "", "E4", "C4", "G4", "", "E4", "D4", "C4", "", "D4", "E4", "G4", "E4", "D4", "C4"]
BASS = ["A1", "A1", "C2", "D2"]

# 1) 0–8s 왼쪽만 / 2) 8–16s 오른쪽만
groove(0.0, 8.0, 0, RIFF_L, BASS)
groove(8.0, 8.0, 1, RIFF_R, BASS)

# 3) 16–26s 풀 믹스: 리듬 섹션은 센터, 리드는 반마디(2박)마다 L↔R 핑퐁
groove(16.0, 10.0, 2, [""] * 16, BASS)  # 킥+햇+스네어+베이스만 (리프는 핑퐁으로 따로)
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
            add(lead(N[note], sixteenth * 1.6, 0.36), t + j * sixteenth, side)
    k += 1
    t += half_bar

# 칩튠 상징: 고속 아르페지오(16분) — 마디(2s)마다 코드 전환, 양 채널 은은하게
CHORDS = [["A2", "C3", "E3"], ["C3", "E3", "G3"], ["D3", "G3", "A3"], ["A2", "C3", "E3"], ["A2", "C3", "E3"]]
ARP_PATTERN = [0, 1, 2, 1]
for bar, chord in enumerate(CHORDS):
    bar_t = 16.0 + bar * BEAT * 4
    for k in range(16):  # 1마디 = 16분음표 16개
        tt = bar_t + k * sixteenth
        if tt >= 25.9:
            break
        add(arp_note(N[chord[ARP_PATTERN[k % 4]]] * 2, sixteenth * 1.2), tt, ch=2)

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

# 5) 29.6–32s 마무리: 킥 + A 마이너 칩 코드(삼각파 베이스 + 펄스 화음) 페이드
add(kick(0.9), 29.6, 2)
n = int(SR * 2.3)
t = np.arange(n) / SR
fade = np.minimum(1, t * 6) * np.exp(-1.4 * t)
w = 0.5 * nes_triangle(N["A1"], n)
for x in ["A2", "C4", "E4", "A4"]:
    w += 0.22 * square(N[x], n, duty=0.25)
add(0.16 * fade * w, 29.65, ch=2)

# 소프트 클립(펀치 유지) → 정규화 → 16bit WAV
buf = np.tanh(buf * 1.15)
buf *= 0.95 / max(1e-9, np.abs(buf).max())
pcm = (buf * 32767).astype("<i2")
with wave.open("/tmp/zp-test-audio.wav", "wb") as f:
    f.setnchannels(2)
    f.setsampwidth(2)
    f.setframerate(SR)
    f.writeframes(pcm.tobytes())

# 섹션별 채널 RMS 검증 출력 (좌/우 분리 확인용)
for name, a, b in [("1 L만", 0, 8), ("2 R만", 8, 16), ("3 풀믹스", 16, 26),
                   ("4 테스트톤", 26, 29.6), ("5 아웃트로", 29.6, 32)]:
    seg = buf[int(a * SR):int(b * SR)]
    print(f"{name}: L rms={np.sqrt((seg[:, 0] ** 2).mean()):.3f}  R rms={np.sqrt((seg[:, 1] ** 2).mean()):.3f}")
print("wrote /tmp/zp-test-audio.wav")
