namespace KOTU.App.Integration;

/// <summary>
/// 확장자 연결 작업(등록·해제·기본 앱 지정)의 진행률 보고 단위 (A77, v0.106.0).
/// 확장자 하나를 처리할 때마다 한 번 보고된다 — <see cref="Done"/>/<see cref="Total"/>은
/// 확장자 개수 기준이고, <see cref="Phase"/>는 화면에 그대로 찍히는 단계 이름이다.
///
/// A42의 <c>WorkContext.Progress</c>는 0..1 double 하나뿐이라 단계 이름과 n/m을 같이 실어
/// 나를 수 없다. 그래서 워커 실행은 <c>ModuleWorker</c> 계약을 그대로 쓰되, 진행률만
/// 이 전용 형식의 <see cref="IProgress{T}"/>로 따로 넘긴다.
/// </summary>
public readonly record struct AssociationProgress(int Done, int Total, string Phase)
{
    /// <summary>파일 연결(ProgID·OpenWithProgids) 등록 단계.</summary>
    public const string Registering = "Registering";

    /// <summary>파일 연결 해제 단계.</summary>
    public const string Unregistering = "Unregistering";

    /// <summary>A38 UserChoice 쓰기 단계 — 확장자마다 최대 3회 재시도라 눈에 띄게 걸린다.</summary>
    public const string SettingDefault = "Setting as default";
}
