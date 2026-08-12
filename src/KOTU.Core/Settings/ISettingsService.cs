namespace KOTU.Core.Settings;

/// <summary>키-값 설정 저장소. 모듈은 자신의 Id를 키 접두어로 사용한다. 예: "video.volume".</summary>
public interface ISettingsService
{
    /// <summary>
    /// 설정이 저장되는 실제 파일 경로 (A36, v0.109.0). 설정 화면이 경로를 그대로 보여주고
    /// "Open settings.json"으로 이 파일을 열기 때문에 하드코딩 대신 여기서 가져간다.
    /// </summary>
    string FilePath { get; }

    T Get<T>(string key, T defaultValue);
    void Set<T>(string key, T value);
    void Save();
}
