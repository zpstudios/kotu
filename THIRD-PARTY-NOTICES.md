# 서드파티 고지 (Third-Party Notices)

KOTU는 아래 외부 구성요소를 사용합니다. 각 구성요소는 자체 라이선스를 따르며, KOTU의 MIT 라이선스는 이들에 적용되지 않습니다.

## 현재 사용 중

| 구성요소 | 용도 | 라이선스 | 비고 |
|---|---|---|---|
| [7-Zip](https://www.7-zip.org/) `7z.dll` | 압축 해제/생성 엔진 | LGPL-2.1 (+ unRAR 제한) | 동적 로드만 사용. 소스 저장소에는 미포함, 배포 zip에는 원본 그대로 동봉. 소스는 7-zip.org에서 제공 |
| [Squid-Box.SevenZipSharp](https://github.com/squid-box/SevenZipSharp) | 7z.dll .NET 래퍼 | LGPL-3.0 | NuGet, 동적 링크 |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | zip CP949 인코딩 경로 | MIT | NuGet |
| [libvlc](https://www.videolan.org/vlc/libvlc.html) (VideoLAN.LibVLC.Windows) | 동영상 재생 엔진 | LGPL-2.1 | 별도 dll 동적 링크, 원본 그대로 동봉. 소스는 videolan.org에서 제공 |
| [LibVLCSharp / LibVLCSharp.WinUI](https://code.videolan.org/videolan/LibVLCSharp) | libvlc .NET 래퍼 | LGPL-2.1 | NuGet, 동적 링크 |
| [NAudio](https://github.com/naudio/NAudio) (NAudio.Wasapi) | 오디오 VU 미터의 WASAPI 루프백 레벨 캡처 (A304) | MIT | NuGet |
| [Magick.NET](https://github.com/dlemstra/Magick.NET) (Magick.NET-Q8-x64) | WIC가 못 읽는 이미지 포맷(psd 등) 디코드 | Apache-2.0 (동봉 네이티브 [ImageMagick](https://imagemagick.org/script/license.php)은 ImageMagick License) | NuGet |
| Microsoft Windows App SDK / WinUI 3 | UI 프레임워크 (HTML 렌더 뷰의 WebView2 컨트롤 포함, A248) | MIT | NuGet |
| System.Drawing.Common | 트레이·창 아이콘 GDI+ 합성(브랜드 표식) | MIT | NuGet |
| Microsoft.Extensions.* (DI, Logging) | 공통 인프라 | MIT | NuGet |
| System.Text.Encoding.CodePages | CP949 인코딩(압축 항목명·자막) | MIT | NuGet |
| System.Management | WMI 조회(하드웨어 정보) | MIT | NuGet |
| [Velopack](https://github.com/velopack/velopack) | 설치본·자동 업데이트 | MIT | NuGet |
| [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (LibreHardwareMonitorLib) | 센서 모니터링(온도·전력·팬 등, A17) | MPL-2.0 | NuGet, 동적 링크. 0.9.5+는 WinRing0 없이 별도 설치형 [PawnIO](https://pawnio.eu/) 드라이버 기반 — 드라이버 본체는 미동봉·미로드, 서명 모듈(.bin)만 내장(A47). 파일 무수정 사용이라 MPL 공개 의무 없음 |
| [DiskInfoToolkit](https://github.com/Blacktempel/DiskInfoToolkit) | LHM 종속(SMART 디스크 정보) | MPL-2.0 | NuGet |
| [RAMSPDToolkit](https://github.com/Blacktempel/RAMSPDToolkit) (RAMSPDToolkit-NDD) | LHM 종속(RAM SPD) | MPL-2.0 | NuGet |
| [HidSharp](https://www.zer7.com/software/hidsharp) | LHM 종속(HID 장치) | Apache-2.0 | NuGet |
| [xunit](https://xunit.net/) | 테스트 | Apache-2.0 | 개발 시에만 |

LGPL 구성요소는 모두 별도 dll로 동적 링크하며 정적 링크하지 않습니다. rar 형식 지원은 7-Zip에 포함된 unRAR 코드의 제한(역공학하여 rar 압축기를 만드는 것 금지)을 따릅니다.
MPL-2.0 구성요소(LibreHardwareMonitor 계열)는 해당 파일을 수정하지 않고 그대로 사용하므로 소스 공개 의무가 발생하지 않습니다.
