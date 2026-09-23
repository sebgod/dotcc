#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for zig's <c>*[N]T</c> → <c>[*]T</c> coercion (hash_map's FieldIterator builds
/// <c>.{ .metadata = &amp;self.metadata, … }</c>): the address of an array at a many-pointer sink is its first
/// element's, which is what the array itself renders as in C# (C's decay). It used to render <c>&amp;arr</c>,
/// a pointer to that pointer (CS0266). End-to-end in the <c>array_address_to_many_pointer</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigArrayDecayTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigad-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_address_of_a_local_or_field_array_at_a_many_pointer_sink_is_its_element_pointer()
    {
        var cs = EmitZig("""
            const Mark = struct { used: bool };
            const Holder = struct {
                vals: [3]u8,
                marks: [2]Mark,
                pub fn first(self: *const Holder) u8 {
                    const p: [*]const u8 = &self.vals;
                    const m: [*]const Mark = &self.marks;
                    return if (m[1].used) p[0] else 0;
                }
            };
            pub fn main() u8 {
                var h: Holder = undefined;
                h.vals[0] = 41;
                h.marks[1] = .{ .used = true };
                const arr = [3]u8{ 1, 2, 3 };
                const q: [*]const u8 = &arr;
                return h.first() + q[0];
            }
            """);
        cs.ShouldContain("byte* q = arr;");
        cs.ShouldContain("byte* p = self->vals;");
        cs.ShouldNotContain("= &arr;");
        cs.ShouldNotContain("= &self->marks;");
    }
}
