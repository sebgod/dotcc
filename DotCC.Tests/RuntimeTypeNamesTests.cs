#nullable enable

using System;
using System.IO;
using System.Linq;
using DotCC.Ir;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Keeps <see cref="RuntimeTypeNames"/> in sync with the runtime it describes. <c>DotCC.Libc</c> is the
/// SAME source that <c>Compiler.LoadRuntimeBlock</c> splices into every emitted program (with its
/// namespace dropped, so its top-level types land in the program's global namespace), so reflecting over
/// the compiled assembly's non-nested types IS the set of names a user type must avoid. A drift in either
/// direction fails here: a new runtime type that could collide with a user's, or a stale reservation.
/// Also pins the two ways a reserved name is kept out of the emitted program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class RuntimeTypeNamesTests
{
    [Fact]
    public void The_reserved_set_matches_the_runtime_s_top_level_types()
    {
        var declared = typeof(DotCC.Libc.CBool).Assembly.GetTypes()
            .Where(t => !t.IsNested && t.Namespace == "DotCC.Libc" && !t.Name.Contains('<'))
            .Select(t => t.Name.Split('`')[0])   // a generic type's reflection name carries its arity
            .ToHashSet(StringComparer.Ordinal);
        declared.OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(RuntimeTypeNames.RuntimeDeclared.OrderBy(n => n, StringComparer.Ordinal));
    }

    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-rtn-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private static string EmitC(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-rtn-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_zig_root_type_named_like_a_runtime_type_is_qualified()
    {
        // `Allocator` / `Slice` are runtime types; the program still spells them plainly.
        var cs = EmitZig("""
            const Allocator = struct { budget: u8 };
            const Slice = enum { head, tail };
            pub fn main() u8 {
                const a: Allocator = .{ .budget = 42 };
                const s: Slice = .tail;
                return if (s == .tail) a.budget else 1;
            }
            """);
        cs.ShouldContain("struct root__Allocator");
        cs.ShouldContain("enum root__Slice");
    }

    [Fact]
    public void A_c_struct_named_like_a_runtime_type_is_rejected()
    {
        // Renaming a C tag would mean rewriting every reference, so it is the author's call — loudly.
        var ex = Should.Throw<Exception>(() => EmitC("""
            struct Unit { int v; };
            int main(void) { struct Unit u = { 42 }; return u.v; }
            """));
        ex.Message.ShouldContain("'Unit' is reserved");
    }
}
