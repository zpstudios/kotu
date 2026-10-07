namespace KOTU.Core.Contracts;

/// <summary>셸이 워커에서 읽은 파일 크기를 아이콘 공급자와 공유한다. null은 미확인/소실이다.</summary>
public interface IFileSizeConsumer
{
    void SetFileSize(string path, long? bytes);
}
