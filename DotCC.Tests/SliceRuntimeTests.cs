#nullable enable

using DotCC.Libc;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Direct unit tests for the runtime slice value types <see cref="Slice{T}"/> /
/// <see cref="ConstSlice{T}"/> (Milestone E) — the blittable <c>{ ptr, len }</c> fat
/// pointer the Zig front-end lowers <c>[]T</c> / <c>[]const T</c> onto. Exercises the
/// pointer/length surface, the element accessor (lvalue), <c>Sub</c>, the
/// <see cref="System.Span{T}"/> bridge, and the <c>[]T</c> → <c>[]const T</c> conversion.
/// Pure (stack-only, no process-global state) → its own collection.
/// </summary>
[Collection("SliceRuntime")]
public sealed class SliceRuntimeTests
{
    [Fact]
    public unsafe void Len_and_ptr_expose_the_backing()
    {
        int* buf = stackalloc int[3];
        buf[0] = 10; buf[1] = 20; buf[2] = 30;

        var s = new Slice<int>(buf, 3);

        s.Len.ShouldBe((ulong)3);
        (s.Ptr == buf).ShouldBeTrue();
        s[0].ShouldBe(10);
        s[2].ShouldBe(30);
    }

    [Fact]
    public unsafe void Indexer_is_an_lvalue()
    {
        int* buf = stackalloc int[2];
        var s = new Slice<int>(buf, 2);

        s[0] = 42;
        s[1] = s[0] + 1;

        buf[0].ShouldBe(42);
        buf[1].ShouldBe(43);
    }

    [Fact]
    public unsafe void Sub_narrows_to_a_window()
    {
        int* buf = stackalloc int[5];
        for (var i = 0; i < 5; i++) { buf[i] = i; }
        var s = new Slice<int>(buf, 5);

        var mid = s.Sub(1, 4); // [1,4)

        mid.Len.ShouldBe((ulong)3);
        mid[0].ShouldBe(1);
        mid[2].ShouldBe(3);
    }

    [Fact]
    public unsafe void AsSpan_bridges_to_a_BCL_span()
    {
        int* buf = stackalloc int[3];
        buf[0] = 7; buf[1] = 8; buf[2] = 9;
        var s = new Slice<int>(buf, 3);

        var span = s.AsSpan();

        span.Length.ShouldBe(3);
        span[1].ShouldBe(8);
    }

    [Fact]
    public unsafe void Mutable_slice_converts_to_const_slice()
    {
        int* buf = stackalloc int[2];
        buf[0] = 100; buf[1] = 200;
        var s = new Slice<int>(buf, 2);

        ConstSlice<int> cs = s; // implicit []T -> []const T

        cs.Len.ShouldBe((ulong)2);
        (cs.Ptr == buf).ShouldBeTrue();
        cs[1].ShouldBe(200);
    }
}
