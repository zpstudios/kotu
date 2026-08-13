#!/usr/bin/env python3
"""설치 스플래시(packaging/splash.png) 생성기.

Velopack Setup.exe가 설치 진행 중에 띄우는 이미지 (release.yml의 스플래시 이미지 인자).
미션 스테이트먼트 문구를 바꾸면 src/KOTU.App/Branding.cs와 함께 이 파일을 고치고
재실행해서 png를 갱신할 것: python3 packaging/gen_splash.py

A79(v0.119.0): 브랜드 레벨 3부터 ④ 마스코트가 워드마크 자리에 들어간다(레벨 인자로 지정,
예: 뒤에 "level=3"을 붙여 실행). splash.png는 빌드 산출물이 아니라 저장소에 커밋되는
파일이라 런타임 레벨 값이 닿지 않는다 — 커밋된 png는 항상 기본값(레벨 0)으로 만든 것이어야 한다.
"""
import os

from PIL import Image, ImageDraw, ImageFont

import brand

W, H = 560, 470
BG = (0x15, 0x07, 0x2E)          # 브랜드 색 — 타이틀바(TitleBarTheming)와 동일
WHITE = (255, 255, 255)
DIM = (205, 198, 224)            # 본문용 연보라 회색

DEJAVU = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
DEJAVU_BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"

MISSION = [
    "No bloat. Ever.",
    "Crucial features only — easy to use.",
    "Easy to install & uninstall — all files in one folder,",
    "   all settings in settings.ini beside the app.",
    "No personal information collected, whatsoever —",
    "   no watch history, no file history.",
    "Free forever, for everyone — personal and",
    "   commercial use alike.",
    "Our only revenue: Patreon and silent in-app ad.",
]
# 들여쓰기(   )로 시작하는 줄은 앞 항목의 이어지는 줄 — 불릿을 찍지 않는다

MASCOT = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                      "..", "src", "KOTU.App", "Assets", "Brand", "mascot.png")


def main() -> None:
    level = brand.level_from_argv()
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    title = ImageFont.truetype(DEJAVU_BOLD, 64)
    head = ImageFont.truetype(DEJAVU_BOLD, 20)
    body = ImageFont.truetype(DEJAVU, 16)
    small = ImageFont.truetype(DEJAVU, 14)

    # 워드마크 — A79 ④에서는 같은 자리에 마스코트 엠블럼이 대신 들어간다.
    mascot = None
    if brand.is_enabled("mascot", level) and os.path.exists(MASCOT):
        mascot = Image.open(MASCOT).convert("RGBA")
        # 96px — 아래 "Installing..." 줄(y=128)과 겹치지 않는 최대 크기
        side = 96
        mascot = mascot.resize((side, round(side * mascot.height / mascot.width)))
        img.paste(mascot, (round(W / 2 - mascot.width / 2), 14), mascot)
    else:
        d.text((W / 2, 78), "KOTU", font=title, fill=WHITE, anchor="mm")
    d.text((W / 2, 128), "Installing...", font=small, fill=DIM, anchor="mm")

    # 구분선
    d.line([(60, 158), (W - 60, 158)], fill=(70, 55, 110), width=1)

    # 미션 스테이트먼트
    d.text((W / 2, 186), "Mission Statement", font=head, fill=WHITE, anchor="mm")
    y = 220
    for line in MISSION:
        cont = line.startswith("   ")
        x = 92 if cont else 74
        if not cont:
            d.ellipse([x - 14, y + 6, x - 8, y + 12], fill=DIM)  # 불릿
        d.text((x, y), line.strip(), font=body, fill=DIM)
        y += 26

    img.save(__file__.rsplit("/", 1)[0] + "/splash.png")
    print("splash.png 생성 완료")

if __name__ == "__main__":
    main()
