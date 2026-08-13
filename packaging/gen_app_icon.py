#!/usr/bin/env python3
"""KOTU 앱 아이콘 생성기 (v0.26.0 → A3/v0.59.0 반전 → A46/v0.86.0 리브랜딩 → A79/v0.119.0 발바닥)
→ src/KOTU.App/Assets/app*.ico

A3(사용자 지정): 메인 표식 = 모듈 식별 글씨(ZIP/IMG/VID/HW/DOC), 우측 하단 작은 표식 = kotu.
중립 app.ico(빈 셸·설정)는 브랜드를 메인으로 유지하되, A46에서 이름이 4글자가 되면서
한 줄로는 16px에서 뭉개져 **"KO"/"TU" 2줄**로 확정했다(사용자 선택, 2026-08-10).
색은 Branding.ModuleAccent와 동일하게 유지할 것.

A79(v0.119.0): 브랜드 레벨 1부터 ① 중립 표식이 발바닥으로, ② 우하단 kotu 표식이 작은 발바닥으로
바뀐다. 레벨은 레벨 인자로 준다(예: 뒤에 "level=1"을 붙여 실행). 기본값 0 =
지금까지의 모습이고, 저장소에 커밋되는 .ico는 항상 기본값으로 만든 것이어야 한다.
발바닥은 16px 가독성 때문에 시트 래스터가 아니라 벡터로 그린다(packaging/brand.py의 PAW).
런타임(창·트레이 아이콘)은 이 파일을 다시 만들지 않고 src/KOTU.App/BrandIcons.cs가 같은 도형을
GDI+로 덧그려 레벨을 반영한다 — 두 곳의 좌표가 같아야 한다.

  app.ico          중립(브랜드 #15072E) — "KO"/"TU" 2줄
  app-archive.ico  amber  #C77E1F — "ZIP" + kotu
  app-image.ico    green  #2E9E5B — "IMG" + kotu
  app-video.ico    red    #D6494F — "VID" + kotu
  app-audio.ico    teal   #1FA8A0 — "AUD" + kotu (A10)
  app-hardware.ico blue   #3874D8 — "HW" + kotu
  app-document.ico purple #7A5AC8 — "DOC" + kotu
  app-allreadable.ico magenta #C2499A — "ALL" + kotu (A59)

실행: python3 packaging/gen_app_icon.py
"""
import os

from PIL import Image, ImageDraw, ImageFont

import brand

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(REPO, "src", "KOTU.App", "Assets")
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"

SUBMARK = "kotu"  # 브랜드 연결 표식 (구: "zp")

# 이름 → (색, 메인 글씨 줄 목록, 서브 표식 표시 여부)
VARIANTS = {
    "app.ico": ((0x15, 0x07, 0x2E), ["KO", "TU"], False),
    "app-archive.ico": ((0xC7, 0x7E, 0x1F), ["ZIP"], True),
    "app-image.ico": ((0x2E, 0x9E, 0x5B), ["IMG"], True),
    "app-video.ico": ((0xD6, 0x49, 0x4F), ["VID"], True),
    "app-audio.ico": ((0x1F, 0xA8, 0xA0), ["AUD"], True),
    "app-hardware.ico": ((0x38, 0x74, 0xD8), ["HW"], True),
    "app-document.ico": ((0x7A, 0x5A, 0xC8), ["DOC"], True),
    "app-allreadable.ico": ((0xC2, 0x49, 0x9A), ["ALL"], True),
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


def make(color, lines, with_submark, level):
    """256px 마스터: 라운드 사각 배경 + 흰 메인 표식 (+우하단 작은 연결 표식)."""
    s = 256
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([8, 8, s - 8, s - 8], radius=56, fill=color + (255,),
                        outline=(255, 255, 255, 70), width=4)

    # 중립 아이콘(서브 표식이 없는 쪽)의 메인 표식만 ①의 대상이다.
    neutral = not with_submark
    if neutral and brand.is_enabled("neutral_paw", level):
        # ① 발바닥. 좌표는 BrandIcons.DrawNeutralPaw와 같아야 한다.
        side = 0.66 * s
        x0, y0 = (s - side) / 2, 0.48 * s - side / 2
        brand.draw_paw(d, (x0, y0, x0 + side, y0 + side), (255, 255, 255, 255))
    # 메인 글씨: 16px 축소에서도 읽히도록 크게, 광학 중앙(살짝 위)으로.
    # 2줄이면 가장 긴 줄에 폭을 맞추고 줄 간격은 폰트 크기의 0.95배.
    elif len(lines) == 1:
        font = fit_font(d, lines[0], max_width=s - 56)
        d.text((s / 2, (s / 2 - 10) if with_submark else (s / 2 - 6)), lines[0],
               font=font, fill=(255, 255, 255, 255), anchor="mm")
    else:
        font = fit_font(d, max(lines, key=len), max_width=s - 56, start=110)
        for i, line in enumerate(lines):
            y = s / 2 - 6 + (i - (len(lines) - 1) / 2) * (font.size * 0.95)
            d.text((s / 2, y), line, font=font, fill=(255, 255, 255, 255), anchor="mm")

    if with_submark:
        if brand.is_enabled("module_paw_mark", level):
            # ② 작은 발바닥. 좌표는 BrandIcons.DrawModulePaw와 같아야 한다.
            brand.draw_paw(d, (0.645 * s, 0.66 * s, 0.885 * s, 0.90 * s),
                           (255, 255, 255, 235))
        else:
            # 우측 하단 작은 kotu — "KOTU 앱에 연결됨" 표식 (A3). 4글자라 폰트를 줄인다.
            sub = ImageFont.truetype(FONT, 30)
            d.text((s - 26, s - 20), SUBMARK, font=sub,
                   fill=(255, 255, 255, 210), anchor="rb")
    return img


def main():
    level = brand.level_from_argv()
    for name, (color, lines, with_submark) in VARIANTS.items():
        path = os.path.join(OUT_DIR, name)
        make(color, lines, with_submark, level).save(path, format="ICO", sizes=SIZES)
        print("생성:", path, os.path.getsize(path), "bytes")


if __name__ == "__main__":
    main()
