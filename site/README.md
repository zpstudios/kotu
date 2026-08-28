# KOTU 웹사이트

앱 개발과는 **별개**로 관리하는 홍보용 랜딩 페이지. `docs/REQUIREMENTS.md`의 A번호 체계와 무관하다.

```
site/
├── index.html      # 랜딩 페이지 (HTML + CSS + JS 한 파일, 빌드 단계 없음)
├── guide.html      # 사용 설명서 게시본 — 정본은 docs/USER-GUIDE.md
├── assets/         # 이미지 (전부 자체 제작 SVG)
└── README.md
```

두 페이지는 **디자인 토큰·상단 네비·푸터가 동일**하다(guide.html 머리 주석 참조) — 한쪽을 고치면
다른 쪽도 같이 고친다. 내용의 정본은 index.html = `README.md`, guide.html = `docs/USER-GUIDE.md`.

## 미리보기

`index.html`을 브라우저에서 그냥 열면 된다. 빌드·서버 불필요.

## ⚠️ 아트 상태 — 마스코트 미포함

시안에 있던 **캐릭터(고지라풍 마스코트)는 저작권 문제로 이 빌드에 들어 있지 않다.**
현재 들어 있는 그래픽은 전부 자체 제작한 도형이다.

| 파일 | 쓰이는 곳 | 성격 |
|---|---|---|
| `skyline.svg` | 히어로 우측 배경 (달·구름·야경 스카이라인) | 자체 제작, 캐릭터 없음 |
| `logo-mark.svg` | 상단 네비 · 푸터 로고 (왕관 마크) | 자체 제작 |
| `favicon.svg` | 파비콘 | 자체 제작 |

### 캐릭터 리소스가 나오면

`index.html`에서 **`TODO(art)`** 로 검색하면 교체 지점이 전부 나온다. 총 7곳:

1. **히어로** — `.hero-art` 안의 `<img class="skyline">`를 캐릭터 이미지로 교체.
   좌·하단 페이드는 `.hero-art > *`의 `mask-image`가 처리하므로 **이미지를 따로 자를 필요 없다.**
   불투명·투명 어느 쪽이든 된다. 권장 비율 900×522 근처.
2. **모듈 카드 4곳** (`mascot-photo` / `video` / `archive` / `docs`) — 주석 처리된
   `<img class="card-mascot">` 한 줄을 살리고 파일만 넣으면 된다.
   카드 우하단에 높이 126px로 붙고, 마지막 두 줄 텍스트는 자동으로 비켜난다
   (`.card:has(.card-mascot) li:nth-last-child(-n+2)`).
3. **로고** — `logo-mark.svg`를 교체하거나 캐릭터 아이콘으로 대체.
4. **og:image** — `<head>`에 주석으로 남겨 둔 1200×630 공유 이미지.

## 스크린샷 섹션

지금은 자리표시자다. 실제 캡처가 나오면 `assets/shot-*.png`로 넣고
`.shot-body` 블록을 `<img src="assets/shot-image.png" alt="…">` 로 바꾸면 프레임 스타일은 그대로 유지된다.

## 갱신할 때 챙길 것

- 버전 문자열이 **3곳**에 있다 — index.html의 히어로 `.verline`과 다운로드 섹션 `.sec-sub`,
  guide.html 머리의 `.asof` 배지. `Directory.Build.props`의 `<Version>`과 맞출 것.
- 기능 서술이 바뀌면 index.html(모듈 카드·엔진 목록·FAQ)과 guide.html을 각각의 정본
  (`README.md` · `docs/USER-GUIDE.md`)에 맞춰 함께 갱신한다.
- 다운로드 버튼은 `releases/latest`를 가리킨다. 파일명이 바뀌면 다운로드 카드의 `<code>`도 함께 수정.
- 폰트는 Google Fonts(Archivo · Barlow) CDN. 오프라인 환경에서는 Arial 계열로 폴백된다.

## 배포

정적 파일뿐이라 어디든 올라간다. GitHub Pages를 쓸 경우 `site/`를 소스 디렉터리로 지정하면 된다
(별도 워크플로는 아직 만들지 않았다).
