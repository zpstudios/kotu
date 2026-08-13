#!/usr/bin/env python3
"""KOTU 브랜드 에셋 조각 생성기 (A79, v0.119.0)
docs/assets/kotu-brand-sheet.png → 개별 PNG 조각

원본 시트(1536×1024, 2.7MB)는 사용자가 만든 AI 생성 브랜드 시트다. 재배포물에
통째로 넣지 않고 **필요한 조각만** 잘라서 동봉한다. 시트는 이미 알파 채널을
갖고 있으므로(배경 alpha=0) 자르기만 하면 투명 배경이 유지된다 — 다만 조각
주변에 이웃 그림의 연기·글로우가 옅은 알파로 번져 있어 마스크로 걷어낸다.

산출물(이 스크립트로만 갱신할 것 — 손으로 고치지 말 것):
  src/KOTU.App/Assets/Brand/wordmark.png  ③ 시작 메뉴·설정 상단 워드마크(가로 배너)
  src/KOTU.App/Assets/Brand/mascot.png    ④ 웰컴 다이얼로그·설치 스플래시 마스코트(원형 엠블럼)
  src/KOTU.App/Assets/Brand/spinner.png   ⑤ 발바닥 스피너(통째로 회전시킨다)
  site/assets/logo-mark-brand.png         ⑥ 랜딩 페이지 로고(기본 미사용 — HTML에서 교체)

①(중립 발바닥)·②(모듈 아이콘 작은 발바닥)는 여기서 자르지 않는다.
16px에서 읽혀야 해서 래스터를 줄이면 뭉개지기 때문 — packaging/gen_app_icon.py의
draw_paw()(그리고 같은 도형의 C# 판인 src/KOTU.App/BrandPaw.cs)가 벡터로 그린다.

실행: python3 packaging/gen_brand_assets.py
"""
import os
from collections import deque

from PIL import Image, ImageFilter

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHEET = os.path.join(REPO, "docs", "assets", "kotu-brand-sheet.png")
APP_DIR = os.path.join(REPO, "src", "KOTU.App", "Assets", "Brand")
SITE_DIR = os.path.join(REPO, "site", "assets")

# 이름 → (시트에서의 넉넉한 사각형, 마스크 방식, 알파 임계값, 출력 폴더)
#   "cc"     = 임계값 이상 픽셀의 가장 큰 연결 성분만 남긴다(한 덩어리 그림용).
#   "thresh" = 임계값 이상 픽셀을 전부 남긴다(점 여러 개로 나뉜 그림용).
PIECES = {
    "wordmark": ((870, 264, 1146, 370), "cc", 150, APP_DIR),
    "mascot": ((505, 8, 830, 342), "cc", 150, APP_DIR),
    "spinner": ((248, 860, 328, 940), "thresh", 90, APP_DIR),
    "logo-mark-brand": ((1322, 24, 1421, 122), "cc", 150, SITE_DIR),
}


def largest_component(alpha, size, threshold):
    """임계값 이상 픽셀의 가장 큰 4연결 성분 마스크. 이웃 그림의 연기·글로우를 걷어낸다."""
    w, h = size
    seen = bytearray(w * h)
    best = []
    for sy in range(h):
        for sx in range(w):
            if seen[sy * w + sx] or alpha[sx, sy] < threshold:
                continue
            queue = deque([(sx, sy)])
            seen[sy * w + sx] = 1
            component = []
            while queue:
                x, y = queue.popleft()
                component.append((x, y))
                for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
                    if 0 <= nx < w and 0 <= ny < h and not seen[ny * w + nx] \
                            and alpha[nx, ny] >= threshold:
                        seen[ny * w + nx] = 1
                        queue.append((nx, ny))
            if len(component) > len(best):
                best = component
    mask = Image.new("L", size, 0)
    pixels = mask.load()
    for x, y in best:
        pixels[x, y] = 255
    return mask


def cut(sheet, box, method, threshold):
    piece = sheet.crop(box)
    alpha = piece.getchannel("A")
    if method == "cc":
        mask = largest_component(alpha.load(), piece.size, threshold)
    else:
        mask = alpha.point(lambda v: 255 if v >= threshold else 0)
    # 2px 팽창: 마스크 경계에서 원본의 안티에일리어싱 가장자리를 잘라먹지 않게
    for _ in range(2):
        mask = mask.filter(ImageFilter.MaxFilter(3))

    kept = Image.new("L", piece.size, 0)
    source, allow, target = alpha.load(), mask.load(), kept.load()
    for y in range(piece.height):
        for x in range(piece.width):
            target[x, y] = source[x, y] if allow[x, y] else 0
    piece.putalpha(kept)
    return piece.crop(kept.point(lambda v: 255 if v else 0).getbbox())


def main():
    sheet = Image.open(SHEET).convert("RGBA")
    os.makedirs(APP_DIR, exist_ok=True)
    os.makedirs(SITE_DIR, exist_ok=True)
    for name, (box, method, threshold, out_dir) in PIECES.items():
        piece = cut(sheet, box, method, threshold)
        path = os.path.join(out_dir, f"{name}.png")
        piece.save(path, format="PNG", optimize=True)
        print(f"생성: {path} {piece.size[0]}x{piece.size[1]} {os.path.getsize(path)} bytes")


if __name__ == "__main__":
    main()
