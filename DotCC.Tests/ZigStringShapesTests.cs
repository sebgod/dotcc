#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for what std's string helpers needed (road-to-zig-std, tasks #52 to #55): a struct field typed by a comptime
/// `switch` (std.mem's SplitIterator `delimiter: switch (delimiter_type) {…}`), an enum-literal type argument
/// (`Split(.scalar)`), a local struct chosen by an if-capture over a comptime optional (std.mem.indexOfScalarPos's
/// `const Scan = if (suggestVectorLength(T)) |n| struct {…} else struct {…}`), an `and` whose comptime-false left
/// operand settles an `if` in an unrolled `inline for`, and a string literal in a tuple whose element type drops the
/// NUL, so `@field(args, "0")` at a `[]const u8` sink is the literal's length (std.fmt's `{s}`). Also a value
/// `if (switch …) |i|` (SplitIterator.next), `@bitCast(s[0..4].*)` (std.mem.eqlBytes), and ArrayList.appendSlice of
/// `&amp;.{…}` with std.mem.sliceAsBytes. End-to-end in the <c>string_shapes</c> and <c>list_slice_as_bytes</c>
/// zig-oracle programs and the real-std string pipeline differential.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigStringShapesTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigstr-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string Program = """
        fn contains(s: []const u8, c: u8) bool {
            for (s) |d| {
                if (d == c) return true;
            }
            return false;
        }

        const Kind = enum { any, scalar };

        fn Split(comptime k: Kind) type {
            return struct {
                buffer: []const u8,
                index: usize,
                delimiter: switch (k) {
                    .any => []const u8,
                    .scalar => u8,
                },

                pub fn count(self: *@This()) usize {
                    var n: usize = 0;
                    while (self.index < self.buffer.len) : (self.index += 1) {
                        const c = self.buffer[self.index];
                        const hit = switch (k) {
                            .any => contains(self.delimiter, c),
                            .scalar => c == self.delimiter,
                        };
                        if (hit) n += 1;
                    }
                    return n;
                }
            };
        }

        fn chunkSize(comptime vec: ?usize) usize {
            const Scan = if (vec) |v|
                struct {
                    pub const size = v;
                }
            else
                struct {
                    pub const size = 1;
                };
            return Scan.size;
        }

        fn widest(a: []const u8) usize {
            inline for (1..6) |s| {
                const n = 4 << s;
                if (n <= 32 and a.len <= n) {
                    return n;
                }
            }
            return 7;
        }

        fn firstLen(args: anytype) usize {
            const s: []const u8 = @field(args, "0");
            return s.len;
        }

        fn find(s: []const u8, c: u8) ?usize {
            for (s, 0..) |d, i| {
                if (d == c) return i;
            }
            return null;
        }

        fn firstHit(comptime k: Kind, s: []const u8) usize {
            return if (switch (k) {
                .any => find(s, ';'),
                .scalar => find(s, ','),
            }) |i| i else 99;
        }

        fn word(s: []const u8) u32 {
            const w: u32 = @bitCast(s[0..4].*);
            return w % 7;
        }

        fn helper() usize {
            return 5;
        }

        pub fn main() u8 {
            var sp = Split(.scalar){ .buffer = "a,b,,c", .index = 0, .delimiter = ',' };
            var sa = Split(.any){ .buffer = "a;b,c", .index = 0, .delimiter = ",;" };
            const buf = "abcdefghijklmnopqrst";
            const total = sp.count() + sa.count() + chunkSize(16) + chunkSize(null) + widest(buf) + firstLen(.{ "abc", 1 }) + helper() + firstHit(.any, "ab;c") + firstHit(.scalar, "abc") + word("abcd");
            return @intCast(total);
        }
        """;

    private const string ListProgram = """
        const std = @import("std");

        pub fn main() !u8 {
            const alloc = std.heap.page_allocator;
            var list: std.ArrayList(u16) = .empty;
            defer list.deinit(alloc);
            try list.appendSlice(alloc, &.{ 3, 4, 5 });
            const bytes = std.mem.sliceAsBytes(list.items);
            var sum: usize = 0;
            for (bytes) |b| sum += b;
            return @intCast(bytes.len + sum);
        }
        """;

    [Fact]
    public void A_switch_typed_field_takes_the_type_of_the_selected_prong()
    {
        var cs = EmitZig(Program);
        cs.ShouldMatch(@"struct Split__1\s*\{[^}]*public byte delimiter;");
        cs.ShouldMatch(@"struct Split__0\s*\{[^}]*public ConstSlice<byte> delimiter;");
        cs.ShouldContain("CBool hit = contains(self->delimiter, c);");
    }

    [Fact]
    public void An_if_capture_over_a_comptime_optional_selects_one_local_struct()
    {
        var cs = EmitZig(Program);
        cs.ShouldMatch(@"ulong chunkSize__opt16\(\)\s*\{\s*return 16UL;");
        cs.ShouldMatch(@"ulong chunkSize__optnull\(\)\s*\{\s*return 1;");
    }

    [Fact]
    public void A_comptime_false_left_operand_of_and_drops_an_unrolled_if()
    {
        var cs = EmitZig(Program);
        // n = 8, 16, 32 keep their `if`; n = 64 and 128 fail `n <= 32`, so their bodies are gone.
        cs.ShouldContain("if (Cond.B(((CBool)(Cond.B(((CBool)(n__2 <= 32))) && Cond.B(((CBool)(a.Len <= (ulong)(n__2))))))))");
        cs.ShouldNotContain("n__3 <= 32");
        cs.ShouldNotContain("n__4 <= 32");
    }

    [Fact]
    public void A_string_literal_tuple_element_slices_without_its_nul()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("new System.ValueTuple<nint, int>((nint)(Libc.L(\"abc\\0\"u8)), 1)");
        cs.ShouldContain("ConstSlice<byte> s = new ConstSlice<byte>(((byte*)(args.Item1)), 3UL);");
    }

    [Fact]
    public void A_value_if_capture_over_a_switch_condition_selects_the_prong_per_instance()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("ulong? __ifcapc0 = find(s, 59);");   // `.any` looks for ';'
        cs.ShouldContain("ulong? __ifcapc2 = find(s, 44);");   // `.scalar` looks for ','
    }

    [Fact]
    public void A_bit_cast_of_a_slice_deref_reads_through_its_data_pointer()
    {
        var cs = EmitZig(Program);
        cs.ShouldContain("uint w = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(new ConstSlice<byte>(s.Ptr + 0, unchecked((ulong)(4 - 0))).Ptr);");
    }

    [Fact]
    public void Append_slice_of_an_anonymous_list_literal_and_slice_as_bytes_lower()
    {
        var cs = EmitZig(ListProgram);
        cs.ShouldContain("ushort* __cl0 = stackalloc ushort[]{ 3, 4, 5 };");
        cs.ShouldContain("list.AppendSlice(ZigAlloc.CHeap(), new Slice<ushort>(__cl0, 3UL), 1)");
        cs.ShouldContain("Slice<byte> bytes = new Slice<byte>((byte*)list.Items.Ptr, list.Items.Len * ((ulong)(sizeof(ushort))));");
    }
}
