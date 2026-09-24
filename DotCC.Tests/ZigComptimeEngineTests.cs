#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for the comptime engine segment (the maintainer's choice: extend the IR interpreter). E1: a pointer
/// to a comptime AGGREGATE is the aggregate itself, so a comptime call can hand a struct to a
/// <c>self: *@This()</c> method, or an array to a <c>*[N]T</c> parameter, and see the mutation. Also the runtime
/// fix that fell out: <c>buf[i]</c> through a <c>*[N]T</c> indexes the array's elements. End-to-end in the
/// <c>comptime_pointer_to_aggregate</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigComptimeEngineTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigce-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_comptime_call_mutates_a_struct_and_an_array_through_pointers()
    {
        var cs = EmitZig("""
            const Acc = struct {
                n: u32,
                fn add(self: *Acc, v: u32) void { self.n += v; }
            };
            fn fill(buf: *[2]u32, v: u32) void { buf[1] = v; }
            fn total() u32 {
                var a = Acc{ .n = 0 };
                a.add(40);
                var buf = [2]u32{ 0, 0 };
                fill(&buf, 2);
                return a.n + buf[1];
            }
            pub fn main() u8 {
                const t = comptime total();
                return @intCast(t);
            }
            """);
        cs.ShouldContain("uint t = 42u;");
        // The runtime body indexes the pointed-at array's elements.
        cs.ShouldContain("buf[1] = v;");
    }
}
