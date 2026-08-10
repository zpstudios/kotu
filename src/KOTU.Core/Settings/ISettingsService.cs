namespace KOTU.Core.Settings;

/// <summary>키-값 설정 저장소. 모듈은 자신의 Id를 키 접두어로 사용한다. 예: "video.volume".</summary>
public interface ISettingsService
{
    T Get<T>(string key, T defaultValue);
    void Set<T>(string key, T value);
    void Save();
}
