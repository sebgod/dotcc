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
    public unsafe void Empty_default_appends_and_grows_across_the_doubling_boundary()
    {
        var list = default(ZigList<int>);   // zig `.empty`
        list.Len.ShouldBe(0UL);
        list.Cap.ShouldBe(0UL);
        (list.Ptr == null).ShouldBeTrue();

        var a = ZigAlloc.CHeap();
        for (var i = 0; i < 10; i++)        // 10 > the initial capacity of 8 → one regrow
        {
            list.Append(a, i * 2, Oom).IsErr.ShouldBeFalse();
        }
        list.Len.ShouldBe(10UL);
        list.Cap.ShouldBe(16UL);
        list.Items[0].ShouldBe(0);
        list.Items[9].ShouldBe(18);
        list.Items.Len.ShouldBe(10UL);

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
        byte* buf = stackalloc byte[64];
        var fba = FixedBufferAllocator.Init(buf, 64);
        var a = ZigAlloc.FbaAllocator(&fba);

        var list = default(ZigList<long>);
        // 8 longs = 64 bytes — the first grow (cap 8) consumes the whole buffer.
        for (var i = 0; i < 8; i++)
        {
            list.Append(a, i, Oom).IsErr.ShouldBeFalse();
        }
        // The 9th append needs a regrow to 16 longs = 128 bytes — deterministic OOM.
        var r = list.Append(a, 8, Oom);
        r.IsErr.ShouldBeTrue();
        r.Code.ShouldBe(Oom);
        list.Len.ShouldBe(8UL);            // the failed append left the list intact
    }
}
