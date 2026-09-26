#nullable enable

using DotCC.Libc;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Stage-0 runtime gate for the curated <c>std.ArrayList(T)</c> (wall-plan W0): drives
/// <c>DotCC.Libc/ZigList.cs</c> DIRECTLY (no lowering yet), proving the abstraction is sound
/// before any emit work — <c>default</c> is zig's <c>.empty</c>, append grows across the
/// doubling boundary, <see cref="ZigList{T}.Items"/> views the occupied prefix,
/// <see cref="ZigList{T}.Pop"/> returns <c>?T</c>, and an exhausted
/// <see cref="FixedBufferAllocator"/> surfaces <c>error.OutOfMemory</c> deterministically.
/// Calls the libc heap, so it sits in the "Runtime" collection.
/// </summary>
[Collection("Runtime")]
public sealed class ZigListRuntimeTests
{
    // An arbitrary non-zero OutOfMemory code (the lowering passes ErrorCode("OutOfMemory")).
    private const ushort Oom = 7;

    [Fact]
    public unsafe void Empty_default_appends_and_grows_like_zig()
    {
        var list = default(ZigList<int>);   // zig `.empty`
        list.Len.ShouldBe(0UL);
        list.Cap.ShouldBe(0UL);
        (list.Ptr == null).ShouldBeTrue();

        // zig's growCapacity(n) = n +| (n / 2 + cache_line / @sizeOf(T)), cache_line 128 on x86_64 and aarch64 (task #145):
        // the first append reserves 1 + 32 = 33 ints, the 34th grows to 34 + 17 + 32 = 83.
        var a = ZigAlloc.CHeap();
        for (var i = 0; i < 10; i++)
        {
            list.Append(a, i * 2, Oom).IsErr.ShouldBeFalse();
        }
        list.Len.ShouldBe(10UL);
        list.Cap.ShouldBe(33UL);
        for (var i = 10; i < 34; i++)
        {
            list.Append(a, i * 2, Oom).IsErr.ShouldBeFalse();
        }
        list.Cap.ShouldBe(83UL);
        list.Items[0].ShouldBe(0);
        list.Items[9].ShouldBe(18);
        list.Items[33].ShouldBe(66);
        list.Items.Len.ShouldBe(34UL);

        list.Deinit(a);
        list.Cap.ShouldBe(0UL);
        list.Deinit(a);                     // idempotent — second deinit sees a null pointer
    }

    [Fact]
    public unsafe void Pop_returns_the_last_element_and_null_when_empty()
    {
        var list = default(ZigList<byte>);
        var a = ZigAlloc.CHeap();
        list.Append(a, 41, Oom).IsErr.ShouldBeFalse();
        list.Append(a, 42, Oom).IsErr.ShouldBeFalse();

        list.Pop().ShouldBe((byte)42);
        list.Pop().ShouldBe((byte)41);
        list.Pop().ShouldBeNull();          // zig 0.15+ `pop()` is `?T`
        list.Len.ShouldBe(0UL);

        list.Deinit(a);
    }

    [Fact]
    public unsafe void AppendSlice_copies_and_clear_retains_capacity()
    {
        var list = default(ZigList<int>);
        var a = ZigAlloc.CHeap();
        int* src = stackalloc int[3] { 10, 15, 17 };
        list.AppendSlice(a, new Slice<int>(src, 3), Oom).IsErr.ShouldBeFalse();

        list.Len.ShouldBe(3UL);
        (list.Items[0] + list.Items[1] + list.Items[2]).ShouldBe(42);

        var capBefore = list.Cap;
        list.ClearRetainingCapacity();
        list.Len.ShouldBe(0UL);
        list.Cap.ShouldBe(capBefore);

        list.Deinit(a);
    }

    [Fact]
    public unsafe void Exhausted_fixed_buffer_allocator_surfaces_out_of_memory()
    {
        byte* buf = stackalloc byte[512];
        var fba = FixedBufferAllocator.Init(buf, 512);
        var a = ZigAlloc.FbaAllocator(&fba);

        var list = default(ZigList<long>);
        // zig's growth (task #145): the first append reserves 1 + 128/8 = 17 longs (136 bytes); the 18th grows to
        // 18 + 9 + 16 = 43 longs (344 bytes), which the FBA remaps IN PLACE as its last allocation.
        for (var i = 0; i < 43; i++)
        {
            list.Append(a, i, Oom).IsErr.ShouldBeFalse();
        }
        list.Cap.ShouldBe(43UL);
        // The 44th needs 44 + 22 + 16 = 82 longs (656 bytes): neither a remap nor a fresh block fits — deterministic OOM.
        var r = list.Append(a, 43, Oom);
        r.IsErr.ShouldBeTrue();
        r.Code.ShouldBe(Oom);
        list.Len.ShouldBe(43UL);           // the failed append left the list intact
        list.Items[42].ShouldBe(42L);
    }
}
