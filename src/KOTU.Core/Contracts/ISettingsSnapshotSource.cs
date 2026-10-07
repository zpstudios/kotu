namespace KOTU.Core.Contracts;

/// <summary>재시작 전에 UI 소유 설정(보류 줌·음량·재생 위치)을 메모리 저장소로 반영한다.</summary>
public interface ISettingsSnapshotSource
{
    void CaptureSettings();
}
