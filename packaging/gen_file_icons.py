#!/usr/bin/env python3
"""ZP 확장자별 파일 아이콘 생성기 (A23, v0.60.0) → src/WinUtil.App/Assets/fileicons/zp-*.ico

탐색기에서 ZP에 연결된 파일이 쓰는 아이콘: 모듈 고유색 배경에
확장자 텍스트를 크게(현 앱 아이콘의 "ZP" 스타일), 우측 하단에 작은 zp(연결 표식).
확장자 목록은 각 모듈 코드와 동일하게 유지할 것:
  archive  → ArchiveModule.Extensions
  image    → ImageFolderNavigator.SupportedExtensions
  video    → VideoModule.Extensions (동영상)
  audio    → AudioModule.Extensions (음악 — A10에서 video로부터 분리)
  document → DocumentModule.Extensions

실행: python3 packaging/gen_file_icons.py
"""
import os

from PIL import Image, ImageDraw, ImageFont

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(REPO, "src", "WinUtil.App", "Assets", "fileicons")
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]

# 모듈 색 = Branding.ModuleAccent와 동일하게 유지
MODULES = {
    "archive": ((0xC7, 0x7E, 0x1F),
                ["zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz"]),
    "image": ((0x2E, 0x9E, 0x5B),
              ["jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff", "ico", "psd"]),
    "video": ((0xD6, 0x49, 0x4F),
              ["mp4", "mkv", "avi", "webm", "mov", "wmv", "m4v", "mpg", "mpeg",
               "ts", "m2ts", "flv", "3gp", "ogv"]),
    "audio": ((0x1F, 0xA8, 0xA0),
              ["mp3", "flac", "wav", "ogg", "opus", "m4a", "aac", "wma"]),
    "document": ((0x7A, 0x5A, 0xC8),
                 ["txt", "md", "markdown", "log"]),
}


def fit_font(draw, text, max_width, start=150, minimum=34):
    """확장자 길이(2~8자)에 맞춰 폭 안에 들어가는 최대 폰트 크기를 고른다."""
    size = start
    while size > minimum:
        font = ImageFont.truetype(FONT, size)
        left, _, right, _ = draw.textbbox((0, 0), text, font=font)
        if right - left <= max_width:
            return font
        size -= 4
    return ImageFont.truetype(FONT, minimum)


def make(color, ext):
    """256px 마스터: 모듈 색 라운드 사각 + 확장자 대문자 + 우하단 작은 zp."""
    s = 256
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([8, 8, s - 8, s - 8], radius=56, fill=color + (255,),
                        outline=(255, 255, 255, 70), width=4)

    font = fit_font(d, ext.upper(), max_width=s - 52)
    d.text((s / 2, s / 2 - 10), ext.upper(),
           font=font, fill=(255, 255, 255, 255), anchor="mm")

    sub = ImageFont.truetype(FONT, 44)
    d.text((s - 26, s - 20), "zp", font=sub, fill=(255, 255, 255, 210), anchor="rb")
    return img


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    count = 0
    for _module, (color, exts) in MODULES.items():
        for ext in exts:
            path = os.path.join(OUT_DIR, f"zp-{ext}.ico")
            make(color, ext).save(path, format="ICO", sizes=SIZES)
            count += 1
    print(f"생성: {count}개 → {OUT_DIR}")


if __name__ == "__main__":
    main()
