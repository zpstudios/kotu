namespace KOTU.Core.Contracts;

/// <summary>네이티브 키 경로를 소유하지 않는 편집기의 단일 Escape 요청.</summary>
public interface IEditorEscapeSource
{
    event Action? EditorEscapeRequested;
}
