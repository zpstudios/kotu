using System.ComponentModel;
using KOTU.Module.Image;
using Xunit;

namespace KOTU.Module.Image.Tests;

public sealed class ImageClipboardWriterTests
{
    private static ImageClipboardPayload Payload => new([1, 2], [3, 4], [5, 6]);

    [Fact]
    public async Task AllFormatsArePreparedBeforeOpeningAndTransferredOnlyOnce()
    {
        var native = new FakeNative();
        var prepared = ImageClipboardWriter.Prepare(Payload, native);
        Assert.Equal(3, native.Allocated.Count);
        Assert.Equal(0, native.OpenCalls);
        Assert.True(await prepared.PublishAsync(123, 7, () => true));
        prepared.Dispose();
        prepared.Dispose();
        Assert.Equal((nint)123, native.Owner);
        Assert.Equal(new uint[] { 0xc000, 17, 8 }, native.Transferred.Keys);
        Assert.Equal(new byte[] { 1, 2 }, native.Transferred[0xc000]);
        Assert.Empty(native.Freed);
        Assert.Equal(1, native.EmptyCalls);
        Assert.Equal(1, native.CloseCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.PublishAsync(123, 7, () => true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AllocationFailureFreesEveryEarlierAllocationWithoutOpening(int failure)
    {
        var native = new FakeNative { FailAllocation = failure };
        Assert.Throws<OutOfMemoryException>(() => ImageClipboardWriter.Prepare(Payload, native));
        Assert.Equal(failure - 1, native.Freed.Count);
        Assert.Equal(native.Allocated.Keys, native.Freed);
        Assert.Equal(0, native.OpenCalls);
        Assert.Equal(0, native.EmptyCalls);
    }

    [Fact]
    public void EmptyFormatIsRejectedBeforeAnyAllocation()
    {
        var native = new FakeNative();
        Assert.Throws<ArgumentException>(() => ImageClipboardWriter.Prepare(new([], [1], [2]), native));
        Assert.Empty(native.Allocated);
        Assert.Equal(0, native.OpenCalls);
    }

    [Fact]
    public async Task BusyClipboardRetriesWithYieldAndKeepsPreparedHandles()
    {
        var native = new FakeNative { BusyAttempts = 2 };
        using var prepared = ImageClipboardWriter.Prepare(Payload, native);
        var delays = 0;
        Assert.True(await prepared.PublishAsync(123, 7, () => true, () =>
        {
            delays++;
            Assert.False(native.IsOpen);
            Assert.Equal(0, native.EmptyCalls);
            return Task.CompletedTask;
        }));
        Assert.Equal(2, delays);
        Assert.Equal(3, native.OpenCalls);
        Assert.Equal(3, native.Allocated.Count);
    }

    [Fact]
    public async Task BusyClipboardHasFiniteAttemptsAndNeverClearsIt()
    {
        var native = new FakeNative { BusyAttempts = int.MaxValue };
        var prepared = ImageClipboardWriter.Prepare(Payload, native);
        var delays = 0;
        await Assert.ThrowsAsync<Win32Exception>(() => prepared.PublishAsync(123, 7, () => true,
            () => { delays++; return Task.CompletedTask; }));
        prepared.Dispose();
        Assert.Equal(ImageClipboardWriter.MaximumAttempts, native.OpenCalls);
        Assert.Equal(ImageClipboardWriter.MaximumAttempts - 1, delays);
        Assert.Equal(0, native.EmptyCalls);
        Assert.Equal(3, native.Freed.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewClipboardContentWinsBeforeOrDuringOpen(bool duringOpen)
    {
        var native = new FakeNative();
        if (duringOpen) native.OnOpen = () => native.Sequence++;
        else native.Sequence++;
        using var prepared = ImageClipboardWriter.Prepare(Payload, native);
        Assert.False(await prepared.PublishAsync(123, 7, () => true));
        Assert.Equal(0, native.EmptyCalls);
        Assert.Equal(duringOpen ? 1 : 0, native.CloseCalls);
    }

    [Fact]
    public async Task SourceInvalidationWhileWaitingDiscardsWithoutClearing()
    {
        var native = new FakeNative { BusyAttempts = 1 };
        var current = true;
        using var prepared = ImageClipboardWriter.Prepare(Payload, native);
        Assert.False(await prepared.PublishAsync(123, 7, () => current,
            () => { current = false; return Task.CompletedTask; }));
        Assert.Equal(1, native.OpenCalls);
        Assert.Equal(0, native.EmptyCalls);
    }

    [Fact]
    public async Task SourceIsRecheckedUnderTheClipboardLock()
    {
        var native = new FakeNative();
        var current = true;
        native.OnOpen = () => current = false;
        using var prepared = ImageClipboardWriter.Prepare(Payload, native);
        Assert.False(await prepared.PublishAsync(123, 7, () => current));
        Assert.Equal(1, native.CloseCalls);
        Assert.Equal(0, native.EmptyCalls);
    }

    [Fact]
    public async Task DestroyedOwnerAndNullOwnerCannotClearTheClipboard()
    {
        var native = new FakeNative { WindowExists = false };
        using (var prepared = ImageClipboardWriter.Prepare(Payload, native))
            Assert.False(await prepared.PublishAsync(123, 7, () => true));
        using (var prepared = ImageClipboardWriter.Prepare(Payload, native))
            await Assert.ThrowsAsync<ArgumentException>(() => prepared.PublishAsync(0, 7, () => true));
        Assert.Equal(0, native.OpenCalls);
        Assert.Equal(6, native.Freed.Count);
    }

    [Fact]
    public async Task EmptyFailureClosesAndFreesAllUnpublishedHandles()
    {
        var native = new FakeNative { EmptySucceeds = false };
        var prepared = ImageClipboardWriter.Prepare(Payload, native);
        await Assert.ThrowsAsync<Win32Exception>(() => prepared.PublishAsync(123, 7, () => true));
        prepared.Dispose();
        Assert.Equal(1, native.CloseCalls);
        Assert.Empty(native.Transferred);
        Assert.Equal(3, native.Freed.Count);
    }

    [Theory]
    [InlineData(0xc000u)]
    [InlineData(17u)]
    [InlineData(8u)]
    public async Task PartialPublicationKeepsTransferredFormatsAndFreesOnlyFailedHandle(uint failure)
    {
        var native = new FakeNative { FailedFormat = failure };
        var prepared = ImageClipboardWriter.Prepare(Payload, native);
        await Assert.ThrowsAsync<Win32Exception>(() => prepared.PublishAsync(123, 7, () => true));
        prepared.Dispose();
        prepared.Dispose();
        Assert.Equal(1, native.EmptyCalls);
        Assert.Equal(1, native.CloseCalls);
        Assert.Equal(2, native.Transferred.Count);
        Assert.Single(native.Freed);
        Assert.DoesNotContain(failure, native.Transferred.Keys);
    }

    private sealed class FakeNative : IImageClipboardNative
    {
        public Dictionary<nint, byte[]> Allocated { get; } = [];
        public Dictionary<uint, byte[]> Transferred { get; } = [];
        public List<nint> Freed { get; } = [];
        public uint Sequence { get; set; } = 7;
        public int FailAllocation { get; init; }
        public int BusyAttempts { get; init; }
        public uint FailedFormat { get; init; }
        public bool WindowExists { get; init; } = true;
        public bool EmptySucceeds { get; init; } = true;
        public Action? OnOpen { get; set; }
        public int OpenCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int EmptyCalls { get; private set; }
        public nint Owner { get; private set; }
        public bool IsOpen { get; private set; }

        public uint RegisterPngFormat() => 0xc000;
        public nint Allocate(byte[] bytes)
        {
            if (Allocated.Count + 1 == FailAllocation) throw new OutOfMemoryException();
            var handle = (nint)(Allocated.Count + 1);
            Allocated.Add(handle, bytes);
            return handle;
        }
        public void Free(nint handle)
        {
            Assert.DoesNotContain(handle, Freed);
            Assert.DoesNotContain(Allocated[handle], Transferred.Values);
            Freed.Add(handle);
        }
        public bool IsWindow(nint window) => WindowExists;
        public bool Open(nint owner)
        {
            Assert.Equal(3, Allocated.Count);
            OpenCalls++;
            if (OpenCalls <= BusyAttempts) return false;
            Owner = owner;
            IsOpen = true;
            OnOpen?.Invoke();
            return true;
        }
        public void Close() { Assert.True(IsOpen); IsOpen = false; CloseCalls++; }
        public bool Empty() { Assert.True(IsOpen); EmptyCalls++; return EmptySucceeds; }
        public bool SetData(uint format, nint handle)
        {
            Assert.True(IsOpen);
            Assert.NotEqual((nint)0, handle);
            if (format == FailedFormat) return false;
            Transferred.Add(format, Allocated[handle]);
            return true;
        }
    }
}
