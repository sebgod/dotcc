#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for what std.Io.Writer.Allocating needed (road-to-zig-std, task #60): a vtable const whose function fields
/// name the container's own functions (<c>.put = Sink.put</c>, a bare sibling <c>.reset = resetInner</c>),
/// <c>&amp;vtable</c> of that container const in STATIC storage (it used to be a copy on the current frame: a dangling
/// pointer, a silent crash), <c>@fieldParentPtr("inner", s)</c> from the parent pointer the result type names, the
/// curated allocator's <c>rawAlloc</c> / <c>rawFree</c> with <c>@returnAddress()</c> and <c>Alignment.of(T)</c>, and a
/// value <c>if (r) |n| … else |_| …</c> over an error union. End to end in the <c>writer_shapes</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigWriterShapesTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigwriter-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string Program = """
        const std = @import("std");

        const Sink = struct {
            const VTable = struct {
                put: *const fn (s: *Inner, v: u8) void,
                reset: *const fn (s: *Inner) void,
            };
            const Inner = struct {
                vtable: *const VTable,
                last: u8,
            };

            total: u32,
            inner: Inner,

            const vtable: VTable = .{
                .put = Sink.put,
                .reset = resetInner,
            };

            fn put(s: *Inner, v: u8) void {
                const self: *Sink = @fieldParentPtr("inner", s);
                self.total += v;
                s.last = v;
            }

            fn resetInner(s: *Inner) void {
                const self: *Sink = @fieldParentPtr("inner", s);
                self.total = 0;
            }

            fn init() Sink {
                return .{ .total = 0, .inner = .{ .vtable = &vtable, .last = 0 } };
            }
        };

        fn sizeOr(r: anyerror!usize) usize {
            return if (r) |n| n * 2 else |_| 7;
        }

        fn failing() anyerror!usize {
            return error.Nope;
        }

        pub fn main() u8 {
            var s = Sink.init();
            s.inner.vtable.put(&s.inner, 5);
            s.inner.vtable.put(&s.inner, 9);
            const after_puts = s.total + s.inner.last;
            s.inner.vtable.reset(&s.inner);

            const a = std.heap.page_allocator;
            const alignment: std.mem.Alignment = .of(u64);
            const raw = a.rawAlloc(16, alignment, @returnAddress()) orelse return 1;
            raw[0] = 3;
            const first = raw[0];
            a.rawFree(raw[0..16], alignment, @returnAddress());

            const ok: anyerror!usize = 4;
            return @intCast(after_puts + s.total + first + alignment.toByteUnits() + sizeOr(ok) + sizeOr(failing()));
        }
        """;

    [Fact]
    public void Container_functions_named_as_values_fill_a_vtable_const()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("new Sink__VTable { put = &Sink_put, reset = &Sink_resetInner }");
    }

    [Fact]
    public void The_address_of_a_container_const_is_static_storage()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("public static unsafe Sink__VTable Sink__vtable__static = new Sink__VTable { put = &Sink_put, reset = &Sink_resetInner };");
        cs.ShouldContain("vtable = (Sink__VTable*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref Sink__vtable__static)");
        cs.ShouldNotContain("Sink__VTable __cl");
    }

    [Fact]
    public void Field_parent_ptr_subtracts_the_field_offset_from_the_field_pointer()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("Sink* self = (Sink*)((ulong)s - ");
        cs.ShouldContain("(byte*)&__t.inner - (byte*)&__t");
    }

    [Fact]
    public void Raw_allocator_calls_alignment_of_and_return_address()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("Alignment alignment = Alignment.fromByteUnits(8UL);");
        cs.ShouldContain("ZigAlloc.RawAlloc(ZigAlloc.CHeap(), 16, alignment, 0UL)");
        cs.ShouldContain("ZigAlloc.RawFree(ZigAlloc.CHeap(), ");
    }

    [Fact]
    public void A_value_if_over_an_error_union_captures_the_payload()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("ulong n = r.Value;");
        cs.ShouldContain("__ifcap0 = 7;");
    }
}
