#!/usr/bin/env python3
"""브랜드 에셋 단계형 적용 — 생성 스크립트 공용부 (A79, v0.119.0)

앱 런타임 쪽 단일 매핑은 src/KOTU.App/BrandAssets.cs다. 이 파일은 **빌드 산출물을
만드는 스크립트 쪽의 같은 표**다(아이콘·스플래시는 저장소에 커밋되는 파일이라
런타임 레벨 값이 닿지 않는다). 두 표는 반드시 같은 값이어야 한다 —
한쪽을 고치면 다른 쪽도 고칠 것.

레벨 구획(사용자 확정 2026-08-13):
  0 현행 무적용(기본값) / 1 아이콘 포인트 / 2 +워드마크·스피너 / 3 +마스코트·랜딩

발바닥 도형(PAW)도 여기 한 벌만 둔다. C# 판은 src/KOTU.App/BrandPaw.cs의 Shape 표이고
값이 같아야 한다 — 16px에서 읽혀야 해서 래스터를 줄이지 않고 양쪽 다 벡터로 그린다.
"""
import sys

MIN_LEVEL = 0
MAX_LEVEL = 3

# 적용 지점 → 켜지는 최소 레벨 (BrandAssets.MinimumLevel과 같은 표)
POINT_MIN_LEVEL = {
    "neutral_paw": 1,      # ① 앱·트레이 중립 아이콘의 발바닥
    "module_paw_mark": 1,  # ② 모듈·파일 아이콘 우하단 작은 발바닥
    "wordmark": 2,         # ③ 시작 메뉴·설정 상단 워드마크
    "paw_spinner": 2,      # ⑤ 로딩 인디케이터
    "mascot": 3,           # ④ 스플래시·웰컴 마스코트
    "site_logo": 3,        # ⑥ 랜딩 페이지 로고
}


def is_enabled(point, level):
    """지점이 이 레벨에서 켜지는가. 지점별로 레벨 숫자를 직접 비교하지 말고 이것만 쓸 것."""
    return level >= POINT_MIN_LEVEL[point]


def level_from_argv(argv=None):
    """레벨 인자(기본 0)를 읽어 0~3으로 클램프한다. 인자 형식은 아래 파서 참고."""
    argv = list(sys.argv[1:] if argv is None else argv)
    value = 0
    for i, token in enumerate(argv):
        if token.startswith("level="):
            value = token.split("=", 1)[1]
            break
        if token.lstrip("-") == "level" and i + 1 < len(argv):
            value = argv[i + 1]
            break
    try:
        value = int(value)
    except (TypeError, ValueError):
        value = 0
    return max(MIN_LEVEL, min(MAX_LEVEL, value))


# 발바닥 도형: (중심 x, 중심 y, 폭, 높이) — 0~1 정규화. 큰 패드 1개 + 발가락 4개.
# 조각끼리 붙으면 실루엣이 뭉개져 발바닥으로 안 읽히므로 사이를 반드시 띄운다
# (발가락 사이 0.045~0.06, 발가락 아래와 패드 사이 0.06).
PAW = [
    (0.500, 0.775, 0.660, 0.450),  # 패드
    (0.095, 0.335, 0.190, 0.290),  # 바깥 왼쪽 발가락
    (0.345, 0.185, 0.215, 0.320),  # 안쪽 왼쪽 발가락
    (0.655, 0.185, 0.215, 0.320),  # 안쪽 오른쪽 발가락
    (0.905, 0.335, 0.190, 0.290),  # 바깥 오른쪽 발가락
]


def draw_paw(draw, box, fill):
    """정규화 도형을 사각형 box=(x0, y0, x1, y1) 안에 채워 그린다(Pillow ImageDraw)."""
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    for cx, cy, pw, ph in PAW:
        draw.ellipse(
            [x0 + (cx - pw / 2) * w, y0 + (cy - ph / 2) * h,
             x0 + (cx + pw / 2) * w, y0 + (cy + ph / 2) * h],
            fill=fill)
