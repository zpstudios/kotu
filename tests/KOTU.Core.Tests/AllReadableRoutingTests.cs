using KOTU.Core.Contracts;
using KOTU.Core.Routing;
using Xunit;

namespace KOTU.Core.Tests;

/// <summary>All Readable 통합 모듈(A59)의 순수 계산 — 자식 후보·확장자 합집합·자식 선택.</summary>
public class AllReadableRoutingTests
{
    private sealed class FakeModule(string id, params string[] exts) : IModule
    {
        public string Id => id;
        public string DisplayName => id;
        public string BrandName => "KOTU-" + id;
        public IReadOnlyList<string> SupportedExtensions => exts;
        public object CreateView(OpenContext context) => new();
    }

    private static IModule[] Sample() =>
    [
        new FakeModule("image", ".jpg", ".png"),
        new FakeModule("video", ".mp4", ".mkv"),
        new FakeModule("hardware"),                       // 파일을 다루지 않는 모듈 = 확장자 0개
        new FakeModule("allreadable", ".jpg", ".mp4"),    // 자기 자신(합집합을 이미 들고 있다)
    ];

    [Fact]
    public void ChildModules_확장자없는_모듈과_자기자신을_뺀다()
    {
        var children = AllReadableRouting.ChildModules(Sample(), "allreadable");

        Assert.Equal(["image", "video"], children.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void ChildModules_hostId는_대소문자를_구분하지_않는다()
    {
        var children = AllReadableRouting.ChildModules(Sample(), "AllReadable");

        Assert.DoesNotContain(children, m => m.Id == "allreadable");
    }

    [Fact]
    public void UnionExtensions_합집합을_첫등장순서로_중복없이_모은다()
    {
        IModule[] modules =
        [
            new FakeModule("image", ".jpg", ".PNG", "gif"), // 대문자·점 없는 표기도 정규화
            new FakeModule("video", ".mp4", ".jpg"),        // 겹치는 확장자는 한 번만
        ];

        Assert.Equal([".jpg", ".png", ".gif", ".mp4"],
            AllReadableRouting.UnionExtensions(modules).ToArray());
    }

    [Fact]
    public void UnionExtensions_확장자없는_모듈만_있으면_비어_있다()
    {
        Assert.Empty(AllReadableRouting.UnionExtensions([new FakeModule("hardware")]));
    }

    [Fact]
    public void ResolveChild_확장자로_자식모듈을_찾는다()
    {
        var children = AllReadableRouting.ChildModules(Sample(), "allreadable");

        Assert.Equal("image", AllReadableRouting.ResolveChild(children, @"C:\pics\cat.JPG")?.Id);
        Assert.Equal("video", AllReadableRouting.ResolveChild(children, @"C:\clips\a.mp4")?.Id);
    }

    [Fact]
    public void ResolveChild_담당모듈이_없으면_null이다()
    {
        var children = AllReadableRouting.ChildModules(Sample(), "allreadable");

        Assert.Null(AllReadableRouting.ResolveChild(children, @"C:\a\config.json")); // 라우팅 재정의 전용
        Assert.Null(AllReadableRouting.ResolveChild(children, @"C:\a\README"));      // 확장자 없음
    }

    [Fact]
    public void ResolveChild_자기자신은_후보에_없어_중첩재귀가_생기지_않는다()
    {
        var children = AllReadableRouting.ChildModules(Sample(), "allreadable");

        // .jpg는 자기 자신도 주장하지만 후보에서 빠져 있으므로 항상 전용 모듈이 나온다.
        Assert.Equal("image", AllReadableRouting.ResolveChild(children, "a.jpg")?.Id);
    }

    [Fact]
    public void ResolveChild_목록순서가_우선순위다()
    {
        IModule[] children = [new FakeModule("image", ".gif"), new FakeModule("video", ".gif")];

        Assert.Equal("image", AllReadableRouting.ResolveChild(children, "a.gif")?.Id);
    }
}
