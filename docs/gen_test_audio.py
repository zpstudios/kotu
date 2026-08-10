#!/usr/bin/env python3
"""스피커 테스트 음악 합성 v5 — 소프트 레트로(슈퍼마리오 월드풍) 120 BPM (32초, 44.1kHz 스테레오 WAV).

사용자 요구(v0.56.0): v4보다 더 소프트하고 덜 거슬리게 — 슈퍼마리오 월드 음악 참고.
  - 리드: 펄스파 → 마림바풍 타악 멜로디 톤(사인 + 짧은 고배음, 퍼커시브 감쇠) — SMW 특유의 통통 튀는 음색
  - 조성: A 마이너 펜타토닉 → C 메이저 펜타토닉(밝고 순한 느낌)
  - 스윙: 16분음표 뒤박을 1/3만큼 밀어 가벼운 셔플 그루브(SMW 리듬감)
  - 아르페지오 → 은은한 벨(사인 감쇠), 햇/스네어 추가 축소 + 로우패스
  - 베이스: NES 삼각파 유지하되 음량 축소 — 낮고 둥근 저음
  - 트위터 핑(8/10/12kHz)은 스피커 점검 기능이라 유지, 음량만 소폭 축소

구성(섹션 경계는 v2와 동일 — 영상과 동기):
  1) 0–8s    왼쪽 채널만  — 전부 하드 L (좌 스피커 확인)
  2) 8–16s   오른쪽 채널만 — 응답 리프 전부 하드 R (우 스피커 확인)
  3) 16–26s  풀 믹스     — 리듬 센터, 리드는 반마디마다 L↔R 핑퐁 + 벨 아르페지오
  4) 26–29.6s 저음 스윕(35→90Hz, 우퍼) → 고음 핑(8/10/12kHz, L→R→양쪽, 트위터)
  5) 29.6–32s 마무리 킥 + C 메이저 마림바 롤 페이드

재생성:
  python3 docs/gen_test_audio.py           # /tmp/kotu-test-audio.wav 생성
  ffmpeg -y -i src/WinUtil.Module.Video/Assets/test-clip.mp4 -i /tmp/kotu-test-audio.wav \
         -map 0:v -map 1:a -c:v copy -c:a aac -b:a 160k -movflags +faststart /tmp/test-clip-new.mp4
  mv /tmp/test-clip-new.mp4 src/WinUtil.Module.Video/Assets/test-clip.mp4
"""
import os
import wave

import numpy as np

OUT = os.path.join(os.environ.get("TMPDIR", "/tmp"), f"kotu-test-audio-{os.getpid()}.wav")
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


def lowpass(sig, cutoff, taps=101):
    """윈도우드 싱크 FIR 로우패스 — 노이즈의 쏘는 고역을 깎는다."""
    m = np.arange(taps) - (taps - 1) / 2
    h = np.sinc(2 * cutoff / SR * m) * np.hanning(taps)
    h /= h.sum()
    return np.convolve(sig, h, mode="same")


# ---------- 음원 (v5: 마림바/벨 중심 — SMW풍) ----------

def marimba(freq, dur, amp=0.3):
    """마림바풍 타악 멜로디: 기음 + 빨리 죽는 4·10배음 — 통통 튀지만 부드럽다 (SMW 리드)."""
    n = int(SR * dur)
    t = np.arange(n) / SR
    env = np.minimum(1, t / 0.002) * np.exp(-t / 0.18)
    w = (np.sin(2 * np.pi * freq * t)
         + 0.35 * np.sin(2 * np.pi * freq * 4 * t) * np.exp(-t / 0.045)
         + 0.12 * np.sin(2 * np.pi * freq * 10.08 * t) * np.exp(-t / 0.02))
    return amp * env * w


def bell(freq, dur, amp=0.05):
    """은은한 벨(아르페지오 배경): 순수 사인 감쇠 — v4 펄스 아르페지오 대체."""
    n = int(SR * dur)
    t = np.arange(n) / SR
    return amp * np.minimum(1, t / 0.002) * np.exp(-t / 0.10) * np.sin(2 * np.pi * freq * t)


def nes_triangle(freq, n):
    """NES 삼각파 채널: 16단계 계단 양자화 삼각파 — 둥글고 낮은 베이스."""
    ph = (freq * np.arange(n) / SR) % 1.0
    w = 4 * np.abs(ph - 0.5) - 1
    return np.round(w * 7.5) / 7.5


def kick(amp=0.7):
    """칩 킥: 85→38Hz 피치 드롭 사인 — v5: 음량 축소."""
    n = int(SR * 0.26)
    t = np.arange(n) / SR
    freq = 38 + (85 - 38) * np.exp(-t * 30)
    phase = 2 * np.pi * np.cumsum(freq) / SR
    return amp * env_ad(n, 0.001, 0.10) * np.sin(phase)


RNG = np.random.default_rng(20260806)  # 재현 가능한 노이즈


def hat(amp=0.045, dur=0.035):
    """햇: 고역 노이즈 틱 — v5: 추가 축소 + 7kHz 로우패스."""
    n = int(SR * dur)
    noise = np.diff(RNG.standard_normal(n + 1))
    return lowpass(amp * env_ad(n, 0.001, 0.013) * noise, 7000)


def snare(amp=0.11):
    """스네어: 짧은 노이즈 브러시 — v5: 추가 축소 + 3.5kHz 로우패스."""
    n = int(SR * 0.12)
    noise = np.diff(RNG.standard_normal(n + 1))
    return lowpass(amp * env_ad(n, 0.001, 0.04) * noise, 3500)


def bassnote(freq, dur, amp=0.45):
    n = int(SR * dur)
    return amp * env_ad(n, 0.003, 0.13) * nes_triangle(freq, n)


N = {  # 음이름 → 주파수
    "C2": 65.41, "D2": 73.42, "E2": 82.41, "F2": 87.31, "G2": 98.00, "A2": 110.00,
    "C3": 130.81, "D3": 146.83, "E3": 164.81, "F3": 174.61, "G3": 196.00, "A3": 220.00, "B3": 246.94,
    "C4": 261.63, "D4": 293.66, "E4": 329.63, "G4": 392.00, "A4": 440.00,
}

sixteenth = BEAT / 4
SWING = sixteenth / 3   # 16분 뒤박을 1/3 밀기 — 가벼운 셔플 (SMW 그루브)


def swung(k):
    """k번째 16분음표의 스윙 적용 시각 오프셋(박 시작 기준)."""
    return k * sixteenth + (SWING if k % 2 == 1 else 0.0)


def groove(t0, dur, ch, riff, bass_line, lead_amp=0.26):
    """킥(4분)+햇(스윙 8분 오프비트)+스네어(백비트)+삼각파 베이스(8분)+마림바 리프(스윙 16분)."""
    beats = int(round(dur / BEAT))
    for b in range(beats):
        add(kick(), t0 + b * BEAT, ch)
        add(hat(), t0 + (b + 0.5) * BEAT + SWING / 2, ch)  # 오프비트 햇도 살짝 뒤로
        if b % 4 in (1, 3):  # 백비트 스네어
            add(snare(), t0 + b * BEAT, ch)
    eighth = BEAT / 2
    for k in range(beats * 2):
        add(bassnote(N[bass_line[(k // 4) % len(bass_line)]], eighth * 0.9), t0 + k * eighth, ch)
    for k in range(beats * 4):
        note = riff[k % len(riff)]
        if note:
            add(marimba(N[note], sixteenth * 2.2, lead_amp), t0 + swung(k), ch)


# C 메이저 펜타토닉 리프 (v5: 밝고 순하게, 쉼표 많이 — 통통 튀는 SMW 프레이즈). ""는 쉼표
RIFF_L = ["C3", "", "E3", "G3", "", "E3", "D3", "", "C3", "", "D3", "E3", "G3", "", "E3", ""]
RIFF_R = ["E3", "", "G3", "A3", "", "G3", "E3", "", "D3", "", "E3", "G3", "A3", "", "G3", ""]
BASS = ["C2", "C2", "F2", "G2"]

# 1) 0–8s 왼쪽만 / 2) 8–16s 오른쪽만
groove(0.0, 8.0, 0, RIFF_L, BASS)
groove(8.0, 8.0, 1, RIFF_R, BASS)

# 3) 16–26s 풀 믹스: 리듬 섹션은 센터, 리드는 반마디(2박)마다 L↔R 핑퐁
groove(16.0, 10.0, 2, [""] * 16, BASS)  # 킥+햇+스네어+베이스만 (리프는 핑퐁으로 따로)
half_bar = BEAT * 2
k = 0
t = 16.0
while t < 25.8:
    side = 0 if k % 2 == 0 else 1
    riff = RIFF_L if side == 0 else RIFF_R
    for j in range(8):  # 반마디 = 16분음표 8개
        note = riff[j % len(riff)]
        if note:
            add(marimba(N[note], sixteenth * 2.2, 0.3), t + swung(j), side)
    k += 1
    t += half_bar

# 배경 벨 아르페지오(16분) — 마디(2s)마다 코드 전환: C → Am → F → G → C
CHORDS = [["C3", "E3", "G3"], ["A2", "C3", "E3"], ["C3", "F3", "A3"], ["D3", "G3", "B3"], ["C3", "E3", "G3"]]
ARP_PATTERN = [0, 1, 2, 1]
for bar, chord in enumerate(CHORDS):
    bar_t = 16.0 + bar * BEAT * 4
    for k in range(16):  # 1마디 = 16분음표 16개
        tt = bar_t + swung(k)
        if tt >= 25.9:
            break
        add(bell(N[chord[ARP_PATTERN[k % 4]]], sixteenth * 1.6), tt, ch=2)

# 4a) 26–28.2s 저음 스윕 35→90Hz (우퍼)
n = int(SR * 2.2)
t = np.arange(n) / SR
freq = 35 + (90 - 35) * t / 2.2
phase = 2 * np.pi * np.cumsum(freq) / SR
add(0.5 * np.minimum(1, t * 8) * np.minimum(1, (2.2 - t) * 2) * np.sin(phase), 26.0, ch=2)

# 4b) 28.2–29.6s 고음 핑 (트위터, L→R→양쪽) — v5: 음량 소폭 축소
for k, (f, ch) in enumerate([(8000, 0), (10000, 1), (12000, 2)]):
    tt = np.arange(int(SR * 0.35)) / SR
    e = np.minimum(1, tt * 300) * np.exp(-7 * tt)
    add(0.09 * e * np.sin(2 * np.pi * f * tt), 28.2 + k * 0.45, ch=ch)

# 5) 29.6–32s 마무리: 소프트 킥 + C 메이저 마림바 롤(C3-E3-G3-C4) + 저음 페이드
add(kick(0.75), 29.6, 2)
for i, x in enumerate(["C3", "E3", "G3", "C4"]):
    add(marimba(N[x], 1.2, 0.22), 29.65 + i * 0.11, ch=2)
n = int(SR * 2.3)
t = np.arange(n) / SR
fade = np.minimum(1, t * 6) * np.exp(-1.4 * t)
add(0.2 * fade * nes_triangle(N["C2"], n), 29.65, ch=2)

# 소프트 클립 → 정규화 → 16bit WAV
buf = np.tanh(buf)
buf *= 0.9 / max(1e-9, np.abs(buf).max())
pcm = (buf * 32767).astype("<i2")
with wave.open(OUT, "wb") as f:
    f.setnchannels(2)
    f.setsampwidth(2)
    f.setframerate(SR)
    f.writeframes(pcm.tobytes())

# 섹션별 채널 RMS 검증 출력 (좌/우 분리 확인용)
for name, a, b in [("1 L만", 0, 8), ("2 R만", 8, 16), ("3 풀믹스", 16, 26),
                   ("4 테스트톤", 26, 29.6), ("5 아웃트로", 29.6, 32)]:
    seg = buf[int(a * SR):int(b * SR)]
    print(f"{name}: L rms={np.sqrt((seg[:, 0] ** 2).mean()):.3f}  R rms={np.sqrt((seg[:, 1] ** 2).mean()):.3f}")
print("wrote " + OUT)
