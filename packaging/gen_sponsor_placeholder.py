#!/usr/bin/env python3
"""스폰서(광고) 자리 플레이스홀더 로고 생성기 → src/WinUtil.App/Assets/sponsor-msi.png

실제 스폰서 로고가 정해지면 같은 파일명으로 교체만 하면 된다(코드 수정 불필요).
지금은 MSI 워드마크 느낌의 플레이스홀더(빨간 카드 + 흰 msi 텍스트).
"""
from PIL import Image, ImageDraw, ImageFont

W, H = 360, 110  # 표시 크기의 @2x — UI에서 Height 44로 축소 표시
img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle([0, 0, W - 1, H - 1], radius=18, fill=(0xE4, 0x00, 0x0F, 255))
font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-BoldOblique.ttf", 64)
d.text((W / 2, H / 2 - 4), "msi", font=font, fill=(255, 255, 255, 255), anchor="mm")
out = __file__.rsplit("/", 2)[0] + "/src/WinUtil.App/Assets/sponsor-msi.png"
img.save(out)
print("생성:", out)
