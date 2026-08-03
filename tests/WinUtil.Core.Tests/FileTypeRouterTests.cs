using WinUtil.Core.Contracts;
using WinUtil.Core.Routing;
using Xunit;

namespace WinUtil.Core.Tests;

public class FileTypeRouterTests
{
    private sealed class FakeModule(string id, params string[] exts) : IModule
    {
        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<string> SupportedExtensions => exts;
        public object CreateView(OpenContext context) => new();
    }

    [Fact]
    public void Resolve_확장자로_모듈을_찾는다()
    {
        var router = new FileTypeRouter();
        var image = new FakeModule("image", ".jpg", ".png");
        router.Register(image);

        Assert.Same(image, router.Resolve(@"C:\pics\cat.JPG")); // 대소문자 무시
        Assert.Null(router.Resolve(@"C:\docs\a.hwp"));
        Assert.Null(router.Resolve(@"C:\docs\README")); // 확장자 없음
    }

    [Fact]
    public void Resolve_등록순서가_우선순위다()
    {
        var router = new FileTypeRouter();
        var image = new FakeModule("image", ".gif");
        var video = new FakeModule("video", ".gif");
        router.Register(image);
        router.Register(video);

        Assert.Same(image, router.Resolve("a.gif"));
    }

    [Fact]
    public void Resolve_사용자_재정의가_등록순서보다_우선한다()
    {
        var router = new FileTypeRouter();
        router.Register(new FakeModule("image", ".gif"));
        var video = new FakeModule("video", ".gif");
        router.Register(video);

        router.SetOverride("gif", "video"); // 점 없이 넣어도 정규화

        Assert.Same(video, router.Resolve("a.gif"));
    }

    [Fact]
    public void Register_중복_ID는_거부한다()
    {
        var router = new FileTypeRouter();
        router.Register(new FakeModule("image", ".jpg"));

        Assert.Throws<InvalidOperationException>(
            () => router.Register(new FakeModule("image", ".png")));
    }
}
