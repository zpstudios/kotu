#!/usr/bin/env python3
"""ZP 앱 아이콘 생성기 (v0.26.0 → A3/v0.59.0 반전) → src/WinUtil.App/Assets/app*.ico

A3(사용자 지정): 메인 표식 = 모듈 식별 글씨(ZIP/IMG/VID/HW/DOC), 우측 하단 작은 표식 = zp.
중립 app.ico(빈 셸·설정)만 브랜드 "ZP"를 메인으로 유지한다.
색은 Branding.ModuleAccent와 동일하게 유지할 것.

  app.ico          중립(브랜드 #15072E) — "ZP" 메인
  app-archive.ico  amber  #C77E1F — "ZIP" + zp
  app-image.ico    green  #2E9E5B — "IMG" + zp
  app-video.ico    red    #D6494F — "VID" + zp
  app-audio.ico    teal   #1FA8A0 — "AUD" + zp (A10)
  app-hardware.ico blue   #3874D8 — "HW" + zp
  app-document.ico purple #7A5AC8 — "DOC" + zp

실행: python3 packaging/gen_app_icon.py
"""
import os

from PIL import Image, ImageDraw, ImageFont

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(REPO, "src", "WinUtil.App", "Assets")
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"

# 이름 → (색, 메인 글씨, zp 서브 표시 여부)
VARIANTS = {
    "app.ico": ((0x15, 0x07, 0x2E), "ZP", False),
    "app-archive.ico": ((0xC7, 0x7E, 0x1F), "ZIP", True),
    "app-image.ico": ((0x2E, 0x9E, 0x5B), "IMG", True),
    "app-video.ico": ((0xD6, 0x49, 0x4F), "VID", True),
    "app-audio.ico": ((0x1F, 0xA8, 0xA0), "AUD", True),
    "app-hardware.ico": ((0x38, 0x74, 0xD8), "HW", True),
    "app-document.ico": ((0x7A, 0x5A, 0xC8), "DOC", True),
}
SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]


def fit_font(draw, text, max_width, start=140, minimum=40):
    """글자 수가 달라도 폭에 맞게 폰트 크기를 줄여가며 고른다."""
    size = start
    while size > minimum:
        font = ImageFont.truetype(FONT, size)
        left, _, right, _ = draw.textbbox((0, 0), text, font=font)
        if right - left <= max_width:
            return font
        size -= 4
    return ImageFont.truetype(FONT, minimum)


def make(color, label, with_zp):
    """256px 마스터: 라운드 사각 배경 + 흰 메인 글씨 (+우하단 작은 zp)."""
    s = 256
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([8, 8, s - 8, s - 8], radius=56, fill=color + (255,),
                        outline=(255, 255, 255, 70), width=4)

    # 메인 글씨: 16px 축소에서도 읽히도록 크게, 광학 중앙(살짝 위)으로
    font = fit_font(d, label, max_width=s - 56)
    d.text((s / 2, (s / 2 - 10) if with_zp else (s / 2 - 6)), label,
           font=font, fill=(255, 255, 255, 255), anchor="mm")

    if with_zp:
        # 우측 하단 작은 zp — "ZP 앱에 연결됨" 표식 (A3)
        sub = ImageFont.truetype(FONT, 44)
        d.text((s - 26, s - 20), "zp", font=sub,
               fill=(255, 255, 255, 210), anchor="rb")
    return img


def main():
    for name, (color, label, with_zp) in VARIANTS.items():
        path = os.path.join(OUT_DIR, name)
        make(color, label, with_zp).save(path, format="ICO", sizes=SIZES)
        print("생성:", path, os.path.getsize(path), "bytes")


if __name__ == "__main__":
    main()
