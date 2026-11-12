using HomeDesk.Contracts;
using Xunit;

namespace HomeDesk.Windows.Tests;

public sealed class DwmThumbnailRendererTests
{
    [Fact]
    public void BindingUpdatingAndDisposalKeepThumbnailResourcesBalanced()
    {
        var native = new FakeDwmThumbnailNativeApi();
        var target = Target(Guid.NewGuid(), 1234);
        var renderer = new DwmThumbnailRenderer(42, native);

        Assert.True(renderer.TryBind(target, out var size, out var failure));
        Assert.Null(failure);
        Assert.Equal(new DwmThumbnailSourceSize(1600, 900), size);
        Assert.True(renderer.TryUpdate(target.WindowId, new PixelRect(10, 20, 320, 180),
            sourceRectangle: new PixelRect(100, 50, 1200, 675)));
        Assert.False(renderer.TryUpdate(target.WindowId, new PixelRect(0, 0, 0, 10)));
        renderer.Dispose();
        renderer.Dispose();

        Assert.Equal((nint)42, native.Destination);
        Assert.Equal((nint)1234, native.Source);
        Assert.Equal(new PixelRect(10, 20, 320, 180), native.LastUpdate!.Value.Destination);
        Assert.Equal(new PixelRect(100, 50, 1200, 675), native.LastUpdate.Value.Source);
        Assert.Equal([(nint)1], native.Unregistered);
    }

    [Fact]
    public void RebindingWindowIdReleasesOldIdentityAndRejectsStaleSources()
    {
        var native = new FakeDwmThumbnailNativeApi();
        var id = Guid.NewGuid();
        using var renderer = new DwmThumbnailRenderer(42, native);
        Assert.True(renderer.TryBind(Target(id, 1234), out _, out _));
        Assert.True(renderer.TryBind(Target(id, 1235), out _, out _));

        Assert.Equal([(nint)1], native.Unregistered);
        native.IsSourceValid = false;
        Assert.False(renderer.TryBind(Target(Guid.NewGuid(), 1236), out _, out var failure));
        Assert.Equal("stale_window", failure);
        Assert.Equal([(nint)1], native.Unregistered);
    }

    [Fact]
    public void FailedSourceSizeLookupUnregistersNewThumbnail()
    {
        var native = new FakeDwmThumbnailNativeApi { QuerySucceeds = false };
        using var renderer = new DwmThumbnailRenderer(42, native);

        Assert.False(renderer.TryBind(Target(Guid.NewGuid(), 1234), out _, out var failure));

        Assert.Equal("source_size_unavailable", failure);
        Assert.Equal([new nint(1)], native.Unregistered);
    }

    private static WindowPreviewTarget Target(Guid id, long handle) =>
        new(id, handle, 10, 20);

    private sealed class FakeDwmThumbnailNativeApi : IDwmThumbnailNativeApi
    {
        private nint nextThumbnail = 1;

        public nint Destination { get; private set; }
        public nint Source { get; private set; }
        public List<nint> Unregistered { get; } = [];
        public bool IsSourceValid { get; set; } = true;
        public bool QuerySucceeds { get; set; } = true;
        public (PixelRect Destination, PixelRect? Source)? LastUpdate { get; private set; }

        public bool IsValidSource(WindowPreviewTarget target) => IsSourceValid;

        public bool TryRegister(nint destination, nint source, out nint thumbnail)
        {
            Destination = destination;
            Source = source;
            thumbnail = nextThumbnail++;
            return true;
        }

        public bool TryQuerySourceSize(nint thumbnail, out DwmThumbnailSourceSize size)
        {
            size = new DwmThumbnailSourceSize(1600, 900);
            return QuerySucceeds;
        }

        public bool TryUpdate(nint thumbnail, PixelRect destination, byte opacity, bool visible, PixelRect? source)
        {
            LastUpdate = (destination, source);
            return true;
        }

        public void Unregister(nint thumbnail) => Unregistered.Add(thumbnail);
    }
}