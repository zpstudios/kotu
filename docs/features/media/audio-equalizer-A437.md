# 오디오 EQ — A437

[선택 근거](../../history/requirements-completed-index.md#a437): 기존 프리셋과 하단 EQ 버튼의 배치를 유지하며 개별 밴드를 조절한다.

## 조작과 값

- EQ 팝업은 `Off`, 기존 프리셋, `Custom`, `Reset`과 주파수·dB 표시가 있는 수평 슬라이더를 제공한다. 밴드 수·주파수·프리셋 이름과 값은 LibVLCSharp의 실제 네이티브 API에서 읽는다.
- 각 밴드는 −20~+20 dB 범위에서 0.1 dB씩 조절한다. 프리셋의 밴드 하나를 바꾸면 나머지 밴드와 그 프리셋의 preamp를 유지한 `Custom`이 된다. preamp의 별도 조절 UI는 없다.
- `Off`는 EQ 적용을 끄고 슬라이더를 비활성화한다. Off·프리셋을 오가도 마지막 Custom은 보존한다. `Reset`은 모든 밴드와 preamp가 0 dB인 Custom을 선택한다.
- Reset의 0 dB는 네이티브 입력값이며 Off와 같은 출력 음량을 뜻하지 않는다. libvlc 3.0.24의 `Flat` 프리셋은 preamp +12 dB이므로 Reset과도 구분한다.
- 팝업 안의 방향키·PageUp/PageDown은 EQ 조작에 사용하며 재생 시간·볼륨·이전/다음 파일 단축키로 처리하지 않는다. 팝업은 현재 창 크기에 맞추고 높이가 부족하면 세로로 스크롤한다.

## 저장과 수명

기존 `audio.equalizer`의 빈 문자열 Off와 프리셋 이름을 유지한다. Custom 선택은 ASCII U+001F 접두어의 `KOTU.Audio.Equalizer.Custom.v1` 식별자, 값은 `audio.equalizer.custom.v1`의 JSON `{Version:1, Preamp, Gains}`로 저장한다. 저장 데이터의 버전·밴드 수·유한값·범위를 확인하며 유효하지 않은 선택은 Off로 복원한다. 메타데이터를 얻지 못하면 밴드나 프리셋을 추측하지 않는다. 프리셋 열거 실패의 자동 재시도는 [후보 34](../../history/requirements-completed-index.md#candidate-34)의 별도 범위다.

각 창의 현재 적용값은 독립적이며 설정은 기존 전역 저장소를 사용한다. Off·프리셋 선택만으로 다른 창이 저장한 Custom 값을 덮지 않는다. Custom을 명시 선택하거나 밴드를 수정·Reset한 창이 해당 값을 저장한다. 설정 저장은 180ms 동안 병합하며 팝업 닫기·설정 캡처·뷰 해체 때 마지막 값을 반영한다. Standalone은 기존 정책대로 메모리에서만 유지한다.

네이티브 조회·적용은 Audio Worker에서 처리한다. 연속 입력은 실행 중 하나와 최신 대기 하나로 병합하며 플레이어 교체·해제 전 세대를 무효화한다. 최초·교체 플레이어는 최신 EQ 적용을 비동기로 기다린 뒤 자동 재생한다. 적용 실패 시 Off 복구를 시도하고, 복구에도 실패하면 현재 소리 설정을 확인할 수 없다는 안내를 표시한다.

실제 관측과 남은 조건은 [검수 정본](../../DEVICE-CHECKS.md#native-media), 공식 제품·설치 상태는 [HANDOVER](../../HANDOVER.md)를 따른다.
