# A385 중앙 썸네일 축소 품질

## 근거와 수정

사용자가 첨부한 브랜드 시트 PNG 축소 화면에서 날카롭고 깨져 보이는 표시를 제보했다.
첨부 원본과 같은 파일을 앱에서 재현하거나 전후 화면을 비교한 것은 아니다.
코드 조사에서 기존 경로는 워커가 셸에 768px 미리보기를 요청한 뒤, 압축 바이트를
UI `BitmapImage.SetSourceAsync`에 전달하고 `Image.Stretch=Uniform`으로 표시했다.
표시 크기에 맞춘 명시적 사전 필터가 없어 큰 축소를 렌더러에 맡기는 구조였으며,
이것만으로 제보의 모든 원인이 확정되지는 않는다. 셸이 만든 원본 미리보기의 품질 한계는 남는다.

셸 요청 버킷 768과 `ReturnOnlyIfCached` 계약을 보존했다. `ThumbnailRaster.Decode`가
같은 ThumbPool 작업 안에서 메모리 스트림만 WIC로 디코드하고, Fant 면적 보간으로 실제
미리보기 영역(좌우·상하 여백 각각 4 DIP 제외)에 맞춰 축소한다. UI에서 캡처한
`RasterizationScale`을 곱한 물리 픽셀 크기로 계산한다. 원본보다 확대하지 않고 비율을 유지한다.
BGRA8·premultiplied alpha·sRGB 결과를 반환하여 투명 영역의 색 번짐을 막는다.
이미 셸이 표시 방향을 결정한 미리보기에 EXIF 방향을 다시 적용하지 않는다.

UI는 완성 픽셀을 `WriteableBitmap.PixelBuffer`에 복사하고 `Invalidate` 및 Image 배치만 한다.
원본 파일 직접 디코드, UI WinRT/COM 동기 대기, UI CPU 이미지 변환은 추가하지 않았다.

## 수명과 상한

- 기존 폴더 seq, 배치 cancellation, `PreviewInFlight`, 게이트 및 컨테이너 재조회 계약을 유지한다.
- 워커 반환 후 현재 호스트의 물리 픽셀 목표가 달라졌으면 적용하지 않고 최신 크기로 재요청한다.
- 호스트 `SizeChanged`는 한 UI 틱 뒤 요청한다. 당시의 컨테이너/뷰모델과 LivePreviewHost를
  재조회하므로 재활용·분리된 호스트가 옛 항목을 바꾸지 않는다. 초기 영역이 0이면 작업을 미루고
  실제 영역이 생기는 SizeChanged에서 시작한다. XamlRoot 배율 변경도 보이는 컨테이너만 갱신한다.
- XamlRoot 이벤트는 Loaded/Unloaded 수명에 묶이고, 해제 후 seq·취소 검사가 늦은 요청을 막는다.
- 출력 한 변 최대 768, 완성 픽셀 최대 2,359,296 bytes/요청. 압축 셸 스트림은 16 MiB,
  디코더 입력 메타데이터 한 변은 8192 상한으로 방어한다. WIC 내부 임시 할당 크기는 별도
  측정하지 않았다. 성공 비트맵/픽셀을 뷰모델에 캐시하지 않으며 재활용 시 화면 비트맵을 해제한다.
- 파일 종류 Icon과 실패는 확장자 타일, placeholder는 캐시 조회만 유지한다.
- A387 파일명 14 DIP와 셀 높이 `size + 4`를 보존했다.

## API와 검증

기존 WIC 선례는 `ImageClipboardSnapshot`의 BitmapDecoder/GetPixelDataAsync 및
`ImageQuickInfo`의 워커 WinRT 대기다. Fant·premultiplied output과 UI WriteableBitmap 복사 조합은
이번 신규 사용이다. 추가 패키지는 없다. 설치된 SDK와 App 빌드에서 API 실재를 확인했다.

2026-10-06 로컬 .NET 8 SDK:

- App 전체 Release/x64, `TreatWarningsAsErrors=true`: 경고 0, 오류 0.
- ThumbnailRasterTests 5/5 통과: 64px 교대 선을 8px로 축소 시 회색 평균(앨리어싱 방어),
  투명 파란 픽셀+불투명 빨간 픽셀 축소 시 fringe 방어, 작은 원본 비확대와 알파 보존,
  비율/여백/DPI/상한, 취소·손상 데이터 실패.
- WIC 테스트는 실제 Windows 디코더와 Fant 변환을 실행한다. UI WriteableBitmap 화면과
  가상화·배율 이동의 실제 화면 테스트는 수행하지 않았다.

실기기 확인: 제보 브랜드 시트와 투명 PNG의 100/150/200% 배율 축소 표시, 창 크기 변경·도크
전환·빠른 스크롤·모니터 이동 중 오래된 크기/다른 파일 그림 잔존 여부, 온라인 전용 파일의
원본 다운로드가 발생하지 않는지. UI 비트맵 경로에 런타임 문제가 확인되면 A385 변경만 되돌리고
A387의 파일명·셀 높이는 유지한다.
