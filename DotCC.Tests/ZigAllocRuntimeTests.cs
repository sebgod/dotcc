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
    public unsafe void Fixed_buffer_allocator_aligns_each_allocation_pointer()
    {
        // A bump allocator over a byte buffer must still hand back pointers aligned to the
        // element's alignment (real zig aligns the pointer up; dotcc caps alignment at 16).
        byte* buf = stackalloc byte[64];
        var fba = FixedBufferAllocator.Init(buf, 64);
        var a = ZigAlloc.FbaAllocator(&fba);

        var r1 = a.Alloc<byte>(1, Oom);   // 1-byte alloc can land at any offset
        r1.IsErr.ShouldBeFalse();

        var r4 = a.Alloc<int>(1, Oom);    // 4-byte element -> a 4-aligned pointer
        r4.IsErr.ShouldBeFalse();
        ((nuint)r4.Value.Ptr % 4).ShouldBe((nuint)0);

        var r8 = a.Alloc<long>(1, Oom);   // 8-byte element -> an 8-aligned pointer
        r8.IsErr.ShouldBeFalse();
        ((nuint)r8.Value.Ptr % 8).ShouldBe((nuint)0);
    }

    [Fact]
    public unsafe void Fixed_buffer_allocator_reclaims_the_last_allocation_on_free()
    {
        byte* buf = stackalloc byte[64];
        var fba = FixedBufferAllocator.Init(buf, 64);
        var a = ZigAlloc.FbaAllocator(&fba);

        var r1 = a.Alloc<byte>(8, Oom);
        fba.EndIndex.ShouldBe(8UL);
        var r2 = a.Alloc<byte>(8, Oom);
        fba.EndIndex.ShouldBe(16UL);

        // Freeing the LAST allocation rewinds the bump cursor (real zig's isLastAllocation)...
        a.Free(r2.Value);
        fba.EndIndex.ShouldBe(8UL);

        // ...so the next allocation reuses exactly that reclaimed space.
        var r3 = a.Alloc<byte>(4, Oom);
        r3.IsErr.ShouldBeFalse();
        fba.EndIndex.ShouldBe(12UL);
        ((nuint)r3.Value.Ptr).ShouldBe((nuint)r2.Value.Ptr);
    }

    [Fact]
    public unsafe void Fixed_buffer_allocator_free_of_a_non_last_allocation_is_a_no_op()
    {
        byte* buf = stackalloc byte[64];
        var fba = FixedBufferAllocator.Init(buf, 64);
        var a = ZigAlloc.FbaAllocator(&fba);

        var r1 = a.Alloc<byte>(8, Oom);
        _ = a.Alloc<byte>(8, Oom);
        fba.EndIndex.ShouldBe(16UL);

        // Freeing r1 (not the top of the bump stack) can't be reclaimed -> no-op, no corruption.
        a.Free(r1.Value);
        fba.EndIndex.ShouldBe(16UL);
    }

    [Fact]
    public unsafe void Fixed_buffer_allocator_devirt_free_reclaims_the_last_allocation()
    {
        // The FBA-site-devirtualized path (AllocFba/FreeFba) must reclaim the last allocation too.
        byte* buf = stackalloc byte[64];
        var fba = FixedBufferAllocator.Init(buf, 64);

        var r1 = ZigAlloc.AllocFba<byte>(&fba, 8, Oom);
        var r2 = ZigAlloc.AllocFba<byte>(&fba, 8, Oom);
        r1.IsErr.ShouldBeFalse();
        r2.IsErr.ShouldBeFalse();
        fba.EndIndex.ShouldBe(16UL);

        ZigAlloc.FreeFba(&fba, r2.Value);
        fba.EndIndex.ShouldBe(8UL);

        var r3 = ZigAlloc.AllocFba<byte>(&fba, 2, Oom);
        ((nuint)r3.Value.Ptr).ShouldBe((nuint)r2.Value.Ptr);
    }

    [Fact]
    public unsafe void Fba_resize_grows_and_shrinks_the_last_allocation_in_place()
    {
        byte* buf = stackalloc byte[64];
        var fba = FixedBufferAllocator.Init(buf, 64);

        var r = ZigAlloc.AllocFba<byte>(&fba, 4, Oom);
        fba.EndIndex.ShouldBe(4UL);

        // Grow the last allocation in place: succeeds, cursor advances, pointer is unchanged.
        (ZigAlloc.ResizeFba(&fba, r.Value, 8) != 0).ShouldBeTrue();
        fba.EndIndex.ShouldBe(8UL);

        // Shrink the last allocation in place: succeeds, cursor rewinds.
        (ZigAlloc.ResizeFba(&fba, new Slice<byte>(r.Value.Ptr, 8), 3) != 0).ShouldBeTrue();
        fba.EndIndex.ShouldBe(3UL);
    }

    [Fact]
    public unsafe void Fba_resize_of_a_non_last_block_only_shrinks()
    {
        byte* buf = stackalloc byte[64];
        var fba = FixedBufferAllocator.Init(buf, 64);

        var first = ZigAlloc.AllocFba<byte>(&fba, 4, Oom);
        _ = ZigAlloc.AllocFba<byte>(&fba, 4, Oom);   // `first` is no longer the top of the bump stack
        fba.EndIndex.ShouldBe(8UL);

        // A non-last block can't grow (would collide with the block above it) — no cursor change.
        (ZigAlloc.ResizeFba(&fba, first.Value, 6) != 0).ShouldBeFalse();
        fba.EndIndex.ShouldBe(8UL);

        // A non-last shrink is a no-op success (the freed tail can't be reclaimed mid-stack).
        (ZigAlloc.ResizeFba(&fba, first.Value, 2) != 0).ShouldBeTrue();
        fba.EndIndex.ShouldBe(8UL);
    }

    [Fact]
    public unsafe void Fba_resize_grow_past_capacity_fails_without_advancing()
    {
        byte* buf = stackalloc byte[8];
        var fba = FixedBufferAllocator.Init(buf, 8);

        var r = ZigAlloc.AllocFba<byte>(&fba, 4, Oom);
        (ZigAlloc.ResizeFba(&fba, r.Value, 100) != 0).ShouldBeFalse();   // 100 > 8 capacity
        fba.EndIndex.ShouldBe(4UL);
    }

    [Fact]
    public unsafe void Fba_remap_returns_the_same_pointer_on_success()
    {
        byte* buf = stackalloc byte[64];
        var fba = FixedBufferAllocator.Init(buf, 64);

        var r = ZigAlloc.AllocFba<byte>(&fba, 4, Oom);
        r.Value[0] = 9;

        // An FBA never moves, so a successful remap returns the SAME pointer with the new length.
        var m = ZigAlloc.RemapFba(&fba, r.Value, 6);
        m.HasValue.ShouldBeTrue();
        ((nuint)m!.Value.Ptr).ShouldBe((nuint)r.Value.Ptr);
        m.Value.Len.ShouldBe(6UL);
        ((int)m.Value[0]).ShouldBe(9);   // the preserved prefix
        fba.EndIndex.ShouldBe(6UL);
    }

    [Fact]
    public unsafe void Fba_remap_returns_null_when_it_cannot_grow()
    {
        byte* buf = stackalloc byte[8];
        var fba = FixedBufferAllocator.Init(buf, 8);

        var r = ZigAlloc.AllocFba<byte>(&fba, 4, Oom);
        var m = ZigAlloc.RemapFba(&fba, r.Value, 100);   // past the 8-byte buffer
        m.HasValue.ShouldBeFalse();
        fba.EndIndex.ShouldBe(4UL);                      // a failed remap must not advance the cursor
    }

    [Fact]
    public unsafe void Opaque_resize_through_an_fba_backed_vtable_grows_and_shrinks()
    {
        // The indirect path an OPAQUE `std.mem.Allocator` takes: dispatch through the vtable's
        // `resize` fn. Backed by an FBA, the answer is the deterministic last-allocation logic.
        byte* buf = stackalloc byte[64];
        var fba = FixedBufferAllocator.Init(buf, 64);
        var a = ZigAlloc.FbaAllocator(&fba);   // an opaque Allocator over the FBA

        var r = a.Alloc<byte>(4, Oom);
        r.IsErr.ShouldBeFalse();
        fba.EndIndex.ShouldBe(4UL);

        (a.Resize(r.Value, 8) != 0).ShouldBeTrue();                        // grow the last block in place
        fba.EndIndex.ShouldBe(8UL);
        (a.Resize(new Slice<byte>(r.Value.Ptr, 8), 3) != 0).ShouldBeTrue(); // shrink it back
        fba.EndIndex.ShouldBe(3UL);
    }

    [Fact]
    public unsafe void Opaque_remap_through_an_fba_backed_vtable_returns_the_new_slice()
    {
        byte* buf = stackalloc byte[64];
        var fba = FixedBufferAllocator.Init(buf, 64);
        var a = ZigAlloc.FbaAllocator(&fba);

        var r = a.Alloc<byte>(4, Oom);
        r.Value[0] = 9;

        var m = a.Remap(r.Value, 6);   // grow via the vtable's remap
        m.HasValue.ShouldBeTrue();
        ((nuint)m!.Value.Ptr).ShouldBe((nuint)r.Value.Ptr);   // an FBA never moves
        m.Value.Len.ShouldBe(6UL);
        ((int)m.Value[0]).ShouldBe(9);                        // preserved prefix
        fba.EndIndex.ShouldBe(6UL);
    }

    [Fact]
    public unsafe void Opaque_resize_and_remap_on_the_c_heap_vtable_report_no_in_place()
    {
        // dotcc's C-heap allocator can't grow/shrink in place, so its vtable's resize is always
        // false and remap always null — a valid Allocator whose caller falls back to a copying
        // realloc. (This is why the C-heap DEVIRT site stays a loud cut: it can't honor in place,
        // but real zig's page/c allocator sometimes can, so a devirt guess would diverge.)
        var a = ZigAlloc.CHeap();
        var r = a.Alloc<byte>(4, Oom);
        r.IsErr.ShouldBeFalse();

        (a.Resize(r.Value, 8) != 0).ShouldBeFalse();
        a.Remap(r.Value, 8).HasValue.ShouldBeFalse();

        a.Free(r.Value);
    }

    [Fact]
    public unsafe void Indirect_dispatch_through_the_materialized_c_heap_vtable()
    {
        // The materialized default allocator: dispatch goes through the vtable's alloc /
        // free (the indirect path), which still reach Libc.malloc / Libc.free.
        var a = ZigAlloc.CHeap();
        var r = a.Alloc<byte>(2, Oom);
        r.IsErr.ShouldBeFalse();
        r.Value.Len.ShouldBe(2UL);

        r.Value[0] = 21; r.Value[1] = 21;
        ((int)r.Value[0] + r.Value[1]).ShouldBe(42);

        a.Free(r.Value);
    }
}
