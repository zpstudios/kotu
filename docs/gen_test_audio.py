"""스피커 테스트 음악 합성 (32초, 44.1kHz 스테레오 WAV).

구성 (각 8초):
  1) 왼쪽 채널만  — 펜타토닉 아르페지오 (좌 스피커 확인)
  2) 오른쪽 채널만 — 응답 프레이즈 (우 스피커 확인)
  3) 양쪽        — 베이스 + 코드 + 멜로디 풀 믹스 (스테레오)
  4) 저음 스윕(35→90Hz, 우퍼) → 고음 톤(8~12kHz, 트위터) → 마무리 코드 페이드
"""
import wave

import numpy as np

SR = 44100
DUR = 32.0
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


def pluck(freq, dur, amp=0.5, decay=4.0):
    """일렉 피아노풍 감쇠음: 기음 + 배음 2개."""
    t = np.arange(int(SR * dur)) / SR
    env = np.exp(-decay * t) * np.minimum(1, t * 200)  # 클릭 방지 어택
    w = (np.sin(2 * np.pi * freq * t)
         + 0.35 * np.sin(2 * np.pi * 2 * freq * t)
         + 0.15 * np.sin(2 * np.pi * 3 * freq * t))
    return amp * env * w


def bass(freq, dur, amp=0.5):
    t = np.arange(int(SR * dur)) / SR
    env = np.exp(-1.2 * t) * np.minimum(1, t * 100)
    w = np.sin(2 * np.pi * freq * t) + 0.25 * np.sin(2 * np.pi * 2 * freq * t)
    return amp * env * w


N = {  # 음이름 → 주파수
    "C2": 65.41, "F1": 43.65, "G1": 49.00, "A1": 55.00,
    "F3": 174.61, "G3": 196.00, "A3": 220.00, "B3": 246.94,
    "C4": 261.63, "D4": 293.66, "E4": 329.63, "G4": 392.00,
    "C5": 523.25, "D5": 587.33, "E5": 659.26, "G5": 783.99, "A5": 880.00,
    "C6": 1046.50, "E6": 1318.51,
}

EIGHTH = 0.5  # 120BPM 8분음표

# 1) 0–8s 왼쪽만
phrase_l = ["C5", "E5", "G5", "C6", "A5", "G5", "E5", "D5",
            "C5", "E5", "G5", "A5", "G5", "E5", "D5", "C5"]
for k, n in enumerate(phrase_l):
    add(pluck(N[n], 1.2, 0.5), k * EIGHTH, ch=0)

# 2) 8–16s 오른쪽만 (프레이즈를 3도 위 느낌으로)
phrase_r = ["E5", "G5", "A5", "E6", "C6", "A5", "G5", "E5",
            "E5", "G5", "C6", "A5", "G5", "E5", "D5", "C5"]
for k, n in enumerate(phrase_r):
    add(pluck(N[n], 1.2, 0.5), 8 + k * EIGHTH, ch=1)

# 3) 16–24s 풀 믹스
for k, n in enumerate(["C2", "C2", "A1", "A1", "F1", "F1", "G1", "G1"]):
    add(bass(N[n], 1.0, 0.55), 16 + k, ch=2)
chords = [["C4", "E4", "G4"], ["A3", "C4", "E4"], ["F3", "A3", "C4"], ["G3", "B3", "D4"]]
for k in range(8):  # 1초마다 스탭, 코드는 2초마다 진행
    for n in chords[k // 2]:
        add(pluck(N[n], 0.9, 0.16, decay=5.0), 16 + k, ch=2)
for k, n in enumerate(phrase_l):
    add(pluck(N[n], 1.2, 0.30), 16 + k * EIGHTH, ch=2)

# 4a) 24–27s 저음 스윕 35→90Hz (우퍼)
t = np.arange(int(SR * 3.0)) / SR
freq = 35 + (90 - 35) * t / 3.0
phase = 2 * np.pi * np.cumsum(freq) / SR
env = np.minimum(1, t * 8) * np.minimum(1, (3.0 - t) * 2)
add(0.55 * env * np.sin(phase), 24, ch=2)

# 4b) 27–29s 고음 톤 (트위터, L→R→양쪽)
for k, (f, ch) in enumerate([(8000, 0), (10000, 1), (12000, 2)]):
    tt = np.arange(int(SR * 0.4)) / SR
    e = np.minimum(1, tt * 300) * np.exp(-6 * tt)
    add(0.18 * e * np.sin(2 * np.pi * f * tt), 27 + k * 0.6, ch=ch)

# 4c) 28.5–32s 마무리 C 메이저 롱 코드 페이드
t = np.arange(int(SR * 3.5)) / SR
env = np.minimum(1, t * 4) * np.exp(-1.1 * t)
w = sum(np.sin(2 * np.pi * N[n] * t) for n in ["C2", "G3", "C4", "E4", "G4", "C5"])
add(0.10 * env * w, 28.5, ch=2)

# 정규화 → 16bit WAV
buf *= 0.92 / max(1e-9, np.abs(buf).max())
pcm = (buf * 32767).astype("<i2")
with wave.open("/tmp/test-audio.wav", "wb") as f:
    f.setnchannels(2)
    f.setsampwidth(2)
    f.setframerate(SR)
    f.writeframes(pcm.tobytes())

# 섹션별 채널 RMS 검증 출력
for name, a, b in [("1 L만", 0, 8), ("2 R만", 8, 16), ("3 양쪽", 16, 24), ("4 마무리", 24, 32)]:
    seg = buf[int(a * SR):int(b * SR)]
    print(f"{name}: L rms={np.sqrt((seg[:,0]**2).mean()):.3f}  R rms={np.sqrt((seg[:,1]**2).mean()):.3f}")
print("wrote /tmp/test-audio.wav")
