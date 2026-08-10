#!/usr/bin/env python3
"""내장 테스트 클립(영상) 생성기 v3 → src/WinUtil.Module.Video/Assets/test-clip.mp4

방송 테스트 패턴 스타일, 오디오(gen_test_audio.py) 섹션과 동기화된 32초 1080p.
v3(v0.22.0): LEFT/RIGHT 텍스트를 스피커 아이콘으로 교체 (사용자 요청 — 언어 없이 직관적으로).
  1) 0–8s    SMPTE 컬러바 + 좌측 스피커 아이콘   (좌 스피커 구간)
  2) 8–16s   RGB/그레이 램프 + 우측 스피커 아이콘 (우 스피커 구간)
  3) 16–26s  해상력 차트 — 존 플레이트 + 수평/수직 라인 웨지 + 미세 체커보드
  4) 26–29.6s 그레이스케일 11스텝 + 균일도 (저음 스윕 구간)
  5) 29.6–32s KOTU 브랜드 아웃트로 (#15072E)

실행: python3 docs/gen_test_clip.py   (오디오 wav가 없으면 gen_test_audio.py를 먼저 돌린다)
"""
import os
import subprocess
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

W, H = 1920, 1080
TMP = "/tmp/kotu-testclip-v3"
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, "src", "WinUtil.Module.Video", "Assets", "test-clip.mp4")
FONT_B = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"


def font(size):
    return ImageFont.truetype(FONT_B, size)


def label(img, text, anchor_xy, size=110, fill=(255, 255, 255)):
    d = ImageDraw.Draw(img)
    x, y = anchor_xy
    # 가독성용 검정 띠
    bbox = d.textbbox((x, y), text, font=font(size), anchor="mm")
    pad = 24
    d.rectangle([bbox[0] - pad, bbox[1] - pad, bbox[2] + pad, bbox[3] + pad], fill=(0, 0, 0))
    d.text((x, y), text, font=font(size), fill=fill, anchor="mm")


def speaker_icon(height=320, color=(255, 255, 255)):
    """스피커 아이콘(드라이버+콘+음파 아크 3개) RGBA. 기본은 소리가 오른쪽으로 퍼지는 방향."""
    u = height / 320.0
    w = int(380 * u)
    im = Image.new("RGBA", (w, height), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rectangle([40 * u, 115 * u, 116 * u, 205 * u], fill=color)          # 드라이버 몸통
    d.polygon([(116 * u, 115 * u), (205 * u, 45 * u),
               (205 * u, 275 * u), (116 * u, 205 * u)], fill=color)       # 콘
    for r in (65, 110, 155):                                              # 음파 아크
        bbox = [(205 - r) * u, (160 - r) * u, (205 + r) * u, (160 + r) * u]
        d.arc(bbox, -52, 52, fill=color, width=int(14 * u))
    return im


def stamp_speaker(img, cx, cy, mirror=False):
    """검정 배경 패널 위에 스피커 아이콘을 찍는다. mirror=True면 소리가 왼쪽으로 퍼지는 방향."""
    icon = speaker_icon()
    if mirror:
        icon = icon.transpose(Image.FLIP_LEFT_RIGHT)
    pad = 40
    x0, y0 = int(cx - icon.width / 2), int(cy - icon.height / 2)
    ImageDraw.Draw(img).rectangle(
        [x0 - pad, y0 - pad, x0 + icon.width + pad, y0 + icon.height + pad], fill=(0, 0, 0))
    img.paste(icon, (x0, y0), icon)


def smpte_bars():
    """1) SMPTE풍 컬러바 + 좌측 스피커 아이콘."""
    img = Image.new("RGB", (W, H))
    d = ImageDraw.Draw(img)
    top = [(192, 192, 192), (192, 192, 0), (0, 192, 192), (0, 192, 0),
           (192, 0, 192), (192, 0, 0), (0, 0, 192)]
    bw = W / 7
    for i, c in enumerate(top):
        d.rectangle([i * bw, 0, (i + 1) * bw, H * 0.66], fill=c)
    mid = [(0, 0, 192), (19, 19, 19), (192, 0, 192), (19, 19, 19),
           (0, 192, 192), (19, 19, 19), (192, 192, 192)]
    for i, c in enumerate(mid):
        d.rectangle([i * bw, H * 0.66, (i + 1) * bw, H * 0.78], fill=c)
    bottom = [(0, 33, 76), (255, 255, 255), (50, 0, 106), (19, 19, 19),
              (9, 9, 9), (19, 19, 19), (29, 29, 29)]
    for i, c in enumerate(bottom):
        d.rectangle([i * bw, H * 0.78, (i + 1) * bw, H], fill=c)
    stamp_speaker(img, W * 0.25, H * 0.5, mirror=True)  # 좌 스피커: 소리가 왼쪽으로
    return img


def ramps():
    """2) R/G/B/그레이 램프 + 우측 스피커 아이콘."""
    img = Image.new("RGB", (W, H))
    arr = np.zeros((H, W, 3), dtype=np.uint8)
    x = np.linspace(0, 255, W).astype(np.uint8)
    qh = H // 4
    arr[0:qh] = np.stack([x, np.zeros_like(x), np.zeros_like(x)], 1)[None, :, :]
    arr[qh:2 * qh] = np.stack([np.zeros_like(x), x, np.zeros_like(x)], 1)[None, :, :]
    arr[2 * qh:3 * qh] = np.stack([np.zeros_like(x), np.zeros_like(x), x], 1)[None, :, :]
    arr[3 * qh:] = np.stack([x, x, x], 1)[None, :, :]
    img = Image.fromarray(arr)
    stamp_speaker(img, W * 0.75, H * 0.5, mirror=False)  # 우 스피커: 소리가 오른쪽으로
    return img


def resolution_chart():
    """3) 해상력 차트: 중앙 존 플레이트 + 라인 웨지 + 미세 체커보드."""
    arr = np.full((H, W), 128, dtype=np.uint8)
    yy, xx = np.mgrid[0:H, 0:W]

    # 중앙 존 플레이트 (동심원 주파수 증가 — 해상력·모아레 확인의 고전 패턴)
    cx, cy, radius = W // 2, H // 2, 420
    r2 = (xx - cx) ** 2 + (yy - cy) ** 2
    zone = (0.5 + 0.5 * np.cos(np.pi * r2 / 760.0)) * 255
    mask = r2 <= radius ** 2
    arr[mask] = zone[mask].astype(np.uint8)

    def vwedge(x0, x1, y0, y1, start_period, end_period):
        """수직선 웨지: 왼→오로 갈수록 선 간격이 좁아진다."""
        wseg = np.zeros((y1 - y0, x1 - x0), dtype=np.uint8)
        n = x1 - x0
        period = np.linspace(start_period, end_period, n)
        phase = np.cumsum(1.0 / period)
        wseg[:, :] = ((np.sin(2 * np.pi * phase) > 0) * 255).astype(np.uint8)[None, :]
        arr[y0:y1, x0:x1] = wseg

    def hwedge(x0, x1, y0, y1, start_period, end_period):
        """수평선 웨지: 위→아래로 갈수록 선 간격이 좁아진다."""
        n = y1 - y0
        period = np.linspace(start_period, end_period, n)
        phase = np.cumsum(1.0 / period)
        col = ((np.sin(2 * np.pi * phase) > 0) * 255).astype(np.uint8)
        arr[y0:y1, x0:x1] = col[:, None]

    # 좌우: 수직선 웨지 (16px → 2px 주기), 상하: 수평선 웨지
    vwedge(80, 560, 120, 400, 16, 2)
    vwedge(W - 560, W - 80, 120, 400, 2, 16)
    hwedge(80, 560, H - 400, H - 120, 16, 2)
    hwedge(W - 560, W - 80, H - 400, H - 120, 2, 16)

    # 모서리 미세 체커보드 1px/2px/3px (픽셀 매핑 확인)
    for k, size in enumerate((1, 2, 3)):
        block = ((xx // size + yy // size) % 2 * 255).astype(np.uint8)
        x0 = 700 + k * 180
        arr[40:150, x0:x0 + 150] = block[40:150, x0:x0 + 150]

    img = Image.fromarray(np.stack([arr] * 3, axis=-1))
    label(img, "RESOLUTION", (W // 2, H - 60), size=64)
    return img


def grayscale_steps():
    """4) 11스텝 그레이스케일 + 상단 흑→백 램프 (계조·균일도)."""
    img = Image.new("RGB", (W, H), (0, 0, 0))
    d = ImageDraw.Draw(img)
    x = np.linspace(0, 255, W).astype(np.uint8)
    arr = np.zeros((H // 3, W, 3), dtype=np.uint8)
    arr[:, :, :] = np.stack([x, x, x], 1)[None, :, :]
    img.paste(Image.fromarray(arr), (0, 0))
    steps = 11
    bw = W / steps
    for i in range(steps):
        v = round(i * 255 / (steps - 1))
        d.rectangle([i * bw, H / 3, (i + 1) * bw, H], fill=(v, v, v))
        d.text((i * bw + bw / 2, H * 0.9), f"{round(i * 10)}", font=font(36),
               fill=(255 - v, 255 - v, 255 - v), anchor="mm")
    return img


def outro():
    """5) KOTU 브랜드 아웃트로 — 타이틀바와 같은 #15072E."""
    img = Image.new("RGB", (W, H), (0x15, 0x07, 0x2E))
    d = ImageDraw.Draw(img)
    d.text((W / 2, H / 2 - 40), "KOTU", font=font(280), fill=(255, 255, 255), anchor="mm")
    d.text((W / 2, H / 2 + 180), "display & speaker test", font=font(48),
           fill=(205, 198, 224), anchor="mm")
    return img


def main():
    os.makedirs(TMP, exist_ok=True)

    wav = "/tmp/kotu-test-audio.wav"
    if not os.path.exists(wav):
        subprocess.run([sys.executable, os.path.join(REPO, "docs", "gen_test_audio.py")], check=True)

    sections = [  # (이미지, 길이초) — 오디오 섹션과 동기
        (smpte_bars(), 8.0),
        (ramps(), 8.0),
        (resolution_chart(), 10.0),
        (grayscale_steps(), 3.6),
        (outro(), 2.4),
    ]
    concat_lines = []
    for i, (img, dur) in enumerate(sections):
        p = f"{TMP}/s{i}.png"
        img.save(p)
        concat_lines.append(f"file '{p}'\nduration {dur}")
    concat_lines.append(f"file '{TMP}/s{len(sections) - 1}.png'")  # concat 규약: 마지막 프레임 반복 명시
    concat_path = f"{TMP}/concat.txt"
    open(concat_path, "w").write("\n".join(concat_lines))

    subprocess.run([
        "ffmpeg", "-y", "-f", "concat", "-safe", "0", "-i", concat_path, "-i", wav,
        "-map", "0:v", "-map", "1:a",
        "-r", "30", "-pix_fmt", "yuv420p",
        "-c:v", "libx264", "-crf", "20", "-preset", "slow", "-tune", "stillimage",
        "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart",
        "-t", "32", OUT,
    ], check=True)
    print("생성:", OUT, os.path.getsize(OUT), "bytes")


if __name__ == "__main__":
    main()
