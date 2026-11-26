namespace KOTU.Core.Contracts;

public enum TextSearchCommand { Find, Replace, Next, Previous, ReturnToDocument, EscapeReleased }

/// <summary>Routes document search keys across a detached bottom bar and a composite content host.</summary>
public interface ITextSearchTarget
{
    /// <summary>
    /// focusScope는 현재 통합 호스트 자신에 초점이 있을 때 검색 진입만 허용하는 조상 객체다.
    /// neutralFocus는 셸이 명시적으로 허용한 중립 요소 자신이며, 현재 초점과 일치할 때만 검색 진입에 쓴다.
    /// </summary>
    bool TryTextSearchCommand(TextSearchCommand command, bool fromEnter = false, bool repeat = false,
        object? focusScope = null, object? neutralFocus = null);
}
