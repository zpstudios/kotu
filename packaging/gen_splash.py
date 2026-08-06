#!/usr/bin/env python3
"""설치 스플래시(packaging/splash.png) 생성기.

Velopack Setup.exe가 설치 진행 중에 띄우는 이미지 (release.yml --splashImage).
미션 스테이트먼트 문구를 바꾸면 src/WinUtil.App/Branding.cs와 함께 이 파일을 고치고
재실행해서 png를 갱신할 것: python3 packaging/gen_splash.py
"""
from PIL import Image, ImageDraw, ImageFont

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
    "Our only revenue: Patreon and one silent Google ad.",
]
# 들여쓰기(   )로 시작하는 줄은 앞 항목의 이어지는 줄 — 불릿을 찍지 않는다

def main() -> None:
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    title = ImageFont.truetype(DEJAVU_BOLD, 64)
    head = ImageFont.truetype(DEJAVU_BOLD, 20)
    body = ImageFont.truetype(DEJAVU, 16)
    small = ImageFont.truetype(DEJAVU, 14)

    # 워드마크
    d.text((W / 2, 78), "ZP", font=title, fill=WHITE, anchor="mm")
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
