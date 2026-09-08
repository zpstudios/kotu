namespace KOTU.Core.Contracts;

/// <summary>현재 콘텐츠가 관찰 중인 작업. 전역의 다른 창 작업과 구분한다.</summary>
public interface IBackgroundJobOwner
{
    Guid? ActiveJobId { get; }
}
