#!/usr/bin/env python3
"""ZP 앱 아이콘 생성기 (v0.26.0) → src/WinUtil.App/Assets/app*.ico

기존 W 플레이스홀더를 브랜드 이름 "ZP" 형상으로 교체하고,
현재 사용 중 모듈을 아이콘 색으로 구분한다(사용자 요청 — 타이틀바·작업표시줄·트레이 공통).
색은 Branding.ModuleAccent와 동일하게 유지할 것.

  app.ico          중립(브랜드 #15072E) — 빈 셸·설정
  app-archive.ico  amber #C77E1F (ZP-zip)
  app-image.ico    green #2E9E5B
  app-video.ico    red   #D6494F
  app-hardware.ico blue  #3874D8

실행: python3 packaging/gen_app_icon.py
"""
import os

from PIL import Image, ImageDraw, ImageFont

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(REPO, "src", "WinUtil.App", "Assets")
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"

VARIANTS = {
    "app.ico": (0x15, 0x07, 0x2E),
    "app-archive.ico": (0xC7, 0x7E, 0x1F),
    "app-image.ico": (0x2E, 0x9E, 0x5B),
    "app-video.ico": (0xD6, 0x49, 0x4F),
    "app-hardware.ico": (0x38, 0x74, 0xD8),
}
SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]


def make(color):
    """256px 마스터: 라운드 사각 배경 + 흰 ZP + 어두운 배경에서도 보이게 옅은 외곽선."""
    s = 256
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([8, 8, s - 8, s - 8], radius=56, fill=color + (255,),
                        outline=(255, 255, 255, 70), width=4)
    # ZP: 16px 축소에서도 읽히도록 크고 굵게, 광학 중앙(살짝 위)으로
    font = ImageFont.truetype(FONT, 132)
    d.text((s / 2, s / 2 - 6), "ZP", font=font, fill=(255, 255, 255, 255), anchor="mm")
    return img


def main():
    for name, color in VARIANTS.items():
        path = os.path.join(OUT_DIR, name)
        make(color).save(path, format="ICO", sizes=SIZES)
        print("생성:", path, os.path.getsize(path), "bytes")


if __name__ == "__main__":
    main()
