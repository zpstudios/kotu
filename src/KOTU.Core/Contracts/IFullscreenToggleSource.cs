namespace KOTU.Core.Contracts;

/// <summary>중첩 전달 중 원래 콘텐츠나 부착이 만료되면 함께 무효가 되는 전체화면 토글 요청.</summary>
public sealed class FullscreenToggleRequest(Func<bool> isCurrent)
{
    public bool IsCurrent => isCurrent();
    public FullscreenToggleRequest WithLifetime(Func<bool> ownerIsCurrent) =>
        new(() => ownerIsCurrent() && IsCurrent);
}

/// <summary>뷰 고유 입력의 전체화면 토글을 창 기하를 소유한 셸에 요청한다.</summary>
public interface IFullscreenToggleSource
{
    event Action<FullscreenToggleRequest>? FullscreenToggleRequested;
}
