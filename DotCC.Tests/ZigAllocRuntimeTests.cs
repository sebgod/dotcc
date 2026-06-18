#nullable enable

using DotCC.Libc;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Stage-0 runtime gate for the Zig allocator milestone (Milestone F): drives
/// <c>DotCC.Libc/ZigAlloc.cs</c> DIRECTLY (no lowering yet), proving the abstraction is sound
/// before any emit work — that <c>ErrUnion&lt;Slice&lt;byte&gt;&gt;</c> instantiates (the
/// <c>unmanaged</c> question), the devirtualized C-heap path round-trips, the
/// <see cref="FixedBufferAllocator"/> bumps and OOMs deterministically, and an indirect
/// <see cref="Allocator.Alloc{T}"/> dispatch through both vtables works. Calls the libc heap, so
/// it sits in the "Runtime" collection.
/// </summary>
[Collection("Runtime")]
public sealed class ZigAllocRuntimeTests
{
    // An arbitrary non-zero OutOfMemory code (the lowering passes ErrorCode("OutOfMemory")).
    private const ushort Oom = 7;

    [Fact]
    public unsafe void Devirt_c_heap_alloc_writes_reads_and_frees()
    {
        var r = ZigAlloc.AllocCHeap<byte>(4, Oom);
        r.IsErr.ShouldBeFalse();
        var s = r.Value;
        s.Len.ShouldBe(4UL);

        s[0] = 10; s[1] = 15; s[2] = 17; s[3] = 0;
        ((int)s[0] + s[1] + s[2] + s[3]).ShouldBe(42);

        ZigAlloc.FreeCHeap(s);   // must not throw / must accept the slice
    }

    [Fact]
    public unsafe void Devirt_c_heap_alloc_over_a_wider_element_type()
    {
        var r = ZigAlloc.AllocCHeap<int>(3, Oom);
        r.IsErr.ShouldBeFalse();
        var s = r.Value;
        s.Len.ShouldBe(3UL);

        s[0] = 10; s[1] = 15; s[2] = 17;
        (s[0] + s[1] + s[2]).ShouldBe(42);

        ZigAlloc.FreeCHeap(s);
    }

    [Fact]
    public unsafe void Fixed_buffer_allocator_bumps_within_capacity()
    {
        byte* buf = stackalloc byte[64];
        var fba = FixedBufferAllocator.Init(buf, 64);
        var a = ZigAlloc.FbaAllocator(&fba);

        var r1 = a.Alloc<byte>(3, Oom);
        r1.IsErr.ShouldBeFalse();
        r1.Value.Len.ShouldBe(3UL);
        fba.EndIndex.ShouldBe(3UL);

        var r2 = a.Alloc<byte>(5, Oom);
        r2.IsErr.ShouldBeFalse();
        fba.EndIndex.ShouldBe(8UL);

        // The two slices are distinct, contiguous regions of the same buffer.
        r1.Value[0] = 1; r2.Value[0] = 2;
        ((int)r1.Value[0]).ShouldBe(1);
        ((int)r2.Value[0]).ShouldBe(2);
    }

    [Fact]
    public unsafe void Fixed_buffer_allocator_returns_OutOfMemory_on_overflow()
    {
        byte* buf = stackalloc byte[4];
        var fba = FixedBufferAllocator.Init(buf, 4);
        var a = ZigAlloc.FbaAllocator(&fba);

        var r = a.Alloc<byte>(100, Oom);   // far past capacity
        r.IsErr.ShouldBeTrue();
        r.Code.ShouldBe(Oom);
        fba.EndIndex.ShouldBe(0UL);        // a failed alloc must not advance the cursor
    }

    [Fact]
    public unsafe void Indirect_dispatch_through_the_materialized_c_heap_vtable()
    {
        // The materialized default allocator: dispatch goes through the vtable's AllocFn /
        // FreeFn (the indirect path), which still reach Libc.malloc / Libc.free.
        var a = ZigAlloc.CHeap();
        var r = a.Alloc<byte>(2, Oom);
        r.IsErr.ShouldBeFalse();
        r.Value.Len.ShouldBe(2UL);

        r.Value[0] = 21; r.Value[1] = 21;
        ((int)r.Value[0] + r.Value[1]).ShouldBe(42);

        a.Free(r.Value);
    }
}
