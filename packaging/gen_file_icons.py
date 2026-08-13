#!/usr/bin/env python3
"""KOTU 확장자별 파일 아이콘 생성기 (A23, v0.60.0 → A46/v0.86.0 리브랜딩)
→ src/KOTU.App/Assets/fileicons/kotu-*.ico

탐색기에서 KOTU에 연결된 파일이 쓰는 아이콘: 모듈 고유색 배경에
확장자 텍스트를 크게, 우측 하단에 작은 kotu(연결 표식).
파일명 접두사는 ExplorerIntegration.FileIconPath와 반드시 일치해야 한다.
A79(v0.119.0): 브랜드 레벨 1부터 우하단 kotu 표식이 작은 발바닥으로 바뀐다(레벨 인자로 지정).
기본값 0 = 지금까지의 모습이고, 저장소에 커밋되는 .ico는 항상 기본값으로 만든 것이어야 한다.
※ 탐색기가 읽는 파일이라 런타임 레벨 값이 닿지 않는다 — 이 아이콘들은 이 스크립트로 다시
  만들어 커밋할 때만 바뀐다(A52의 우클릭 라벨과 같은 성격의 한계).

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

import brand

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(REPO, "src", "KOTU.App", "Assets", "fileicons")
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
PREFIX = "kotu"   # 파일명 접두사 (구: "zp") — ExplorerIntegration.FileIconPath와 일치
SUBMARK = "kotu"  # 우하단 연결 표식 (구: "zp")

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


def make(color, ext, level):
    """256px 마스터: 모듈 색 라운드 사각 + 확장자 대문자 + 우하단 작은 연결 표식."""
    s = 256
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([8, 8, s - 8, s - 8], radius=56, fill=color + (255,),
                        outline=(255, 255, 255, 70), width=4)

    font = fit_font(d, ext.upper(), max_width=s - 52)
    d.text((s / 2, s / 2 - 10), ext.upper(),
           font=font, fill=(255, 255, 255, 255), anchor="mm")

    if brand.is_enabled("module_paw_mark", level):
        # A79 ② 작은 발바닥. 좌표는 gen_app_icon.py·BrandIcons.DrawModulePaw와 같아야 한다.
        brand.draw_paw(d, (0.645 * s, 0.66 * s, 0.885 * s, 0.90 * s), (255, 255, 255, 235))
    else:
        sub = ImageFont.truetype(FONT, 30)  # 4글자라 구 "zp"(44)보다 작게
        d.text((s - 26, s - 20), SUBMARK, font=sub, fill=(255, 255, 255, 210), anchor="rb")
    return img


def main():
    level = brand.level_from_argv()
    os.makedirs(OUT_DIR, exist_ok=True)
    count = 0
    for _module, (color, exts) in MODULES.items():
        for ext in exts:
            path = os.path.join(OUT_DIR, f"{PREFIX}-{ext}.ico")
            make(color, ext, level).save(path, format="ICO", sizes=SIZES)
            count += 1
    print(f"생성: {count}개 → {OUT_DIR}")


if __name__ == "__main__":
    main()
