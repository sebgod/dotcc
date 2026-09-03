#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for the boundary between the CURATED std model (Milestone F — <c>std.mem</c>/<c>heap</c>/
/// <c>debug</c>/<c>testing</c> lowered to dotcc's own runtime) and REAL-STD SOURCE NAVIGATION
/// (road-to-zig-std S1/S2 — <c>@import("std")</c> walking an actual zig lib tree when one is
/// configured). The two overlap: upstream re-exports its allocators as whole FILES
/// (<c>pub const FixedBufferAllocator = @import("heap/FixedBufferAllocator.zig");</c>), so
/// <c>std.heap.FixedBufferAllocator</c> is simultaneously a curated type and a navigable module.
/// S1's rule is that the curated set is checked FIRST in every position; these tests hold both
/// halves of it — curated paths are never navigated, and non-curated ones still are.
/// <para>Each test builds a tiny SYNTHETIC std tree (no zig install needed, so this runs everywhere)
/// whose <c>FixedBufferAllocator.zig</c> deliberately cannot lower — if navigation ever wins again,
/// the compile fails loudly on that marker type rather than passing quietly.</para>
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigCuratedStdVsNavigationTests
{
    private const string LibDirEnv = "DOTCC_ZIG_LIB_DIR";

    /// <summary>A marker type spelled by the synthetic <c>FixedBufferAllocator.zig</c>'s signature.
    /// It is not a zig primitive and not registered anywhere, so lowering that file's <c>init</c>
    /// throws naming it — the tell that navigation, not the curated model, handled the call.</summary>
    private const string NavigationMarker = "UpstreamFbaMarker";

    /// <summary>Write a minimal std tree shaped like upstream's: a root that re-exports <c>heap</c> and
    /// <c>ascii</c> as sibling files, a <c>heap.zig</c> that re-exports the allocator as its own FILE
    /// (the shape that collides with the curated path), and a leaf <c>ascii.zig</c> that lowers fine.
    /// Returns the LIB dir (the parent of <c>std/</c>), which is what <c>DOTCC_ZIG_LIB_DIR</c> names.</summary>
    private static string WriteSyntheticStdTree(string root)
    {
        var std = Path.Combine(root, "lib", "std");
        Directory.CreateDirectory(Path.Combine(std, "heap"));
        File.WriteAllText(Path.Combine(std, "std.zig"),
            "pub const heap = @import(\"heap.zig\");\n" +
            "pub const ascii = @import(\"ascii.zig\");\n" +
            "pub const mem = @import(\"mem.zig\");\n" +
            "pub const array_list = @import(\"array_list.zig\");\n");
        // `std.mem.Allocator` is a CURATED type and (here, as upstream) also a navigable module member —
        // the type-position twin of the heap/FixedBufferAllocator.zig collision. Its field type is the
        // unlowerable marker, so if type-position navigation ever beat the curated model the compile
        // would fail loudly on it (road-to-zig-std S4d must not regress S1's curated-first rule).
        File.WriteAllText(Path.Combine(std, "mem.zig"),
            $"pub const Allocator = struct {{ marker: {NavigationMarker} }};\n");
        // A type-returning generic reached through the module graph, carrying a method — the shape
        // `std.ArrayList` has (road-to-zig-std G4 × S4d).
        File.WriteAllText(Path.Combine(std, "array_list.zig"),
            "pub fn Aligned(comptime T: type) type {\n" +
            "    return struct {\n" +
            "        first: T,\n" +
            "        len: usize,\n" +
            "        const Self = @This();\n" +
            "        pub fn total(self: Self) usize { return self.len + self.first; }\n" +
            "    };\n" +
            "}\n");
        File.WriteAllText(Path.Combine(std, "heap.zig"),
            "pub const FixedBufferAllocator = @import(\"heap/FixedBufferAllocator.zig\");\n");
        File.WriteAllText(Path.Combine(std, "heap", "FixedBufferAllocator.zig"),
            $"pub fn init(buffer: []u8) {NavigationMarker} {{ return buffer; }}\n");
        File.WriteAllText(Path.Combine(std, "ascii.zig"),
            "pub const Pair = struct { a: u8, b: u8 };\n" +
            "pub fn isDigit(c: u8) bool { return c >= '0' and c <= '9'; }\n");
        return Path.Combine(root, "lib");
    }

    /// <summary>Compile a Zig program with a std source tree configured. The std root reaches the
    /// front-end only through <c>DOTCC_ZIG_LIB_DIR</c> today, so the variable is set for the duration
    /// and restored after — safe because this assembly runs tests SERIALLY
    /// (<c>CollectionBehavior(DisableTestParallelization = true)</c>, see AssemblyInfo.cs), so no other
    /// test can observe the window.</summary>
    private static string EmitWithStdTree(string program)
    {
        var root = Path.Combine(Path.GetTempPath(), $"dotcc-zigstd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var libDir = WriteSyntheticStdTree(root);
        var mainPath = Path.Combine(root, "main.zig");
        File.WriteAllText(mainPath, program);
        var saved = Environment.GetEnvironmentVariable(LibDirEnv);
        Environment.SetEnvironmentVariable(LibDirEnv, libDir);
        try
        {
            return Compiler.EmitCSharp(new[] { mainPath });
        }
        finally
        {
            Environment.SetEnvironmentVariable(LibDirEnv, saved);
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_curated_std_type_is_not_navigated_into_real_source()
    {
        // With a std tree configured, `std.heap.FixedBufferAllocator` resolves BOTH ways. It must take
        // the curated lowering: navigation would try to lower upstream's own `init` signature and die
        // ("zig type 'FixedBufferAllocator' not supported yet"), which is what made a configured std
        // root and the curated allocators mutually exclusive.
        var cs = EmitWithStdTree(
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var buf: [64]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buf);\n" +
            "    const a = fba.allocator();\n" +
            "    const s = a.alloc(u8, 2) catch return 1;\n" +
            "    s[0] = 42;\n" +
            "    const got = s[0];\n" +
            "    a.free(s);\n" +
            "    return got;\n" +
            "}\n");
        cs.ShouldContain("FixedBufferAllocator.Init(");   // dotcc's curated runtime bump allocator
        cs.ShouldNotContain(NavigationMarker);            // …not the navigated upstream file
    }

    [Fact]
    public void A_non_curated_std_path_still_navigates_to_real_source()
    {
        // The other half of the rule: the curated-first guard must not turn into navigate-never.
        // `std.ascii` is not curated, so it still resolves through the module graph and lowers lazily.
        var cs = EmitWithStdTree(
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    if (std.ascii.isDigit('7')) return 42;\n" +
            "    return 0;\n" +
            "}\n");
        cs.ShouldContain("isDigit");   // the navigated leaf lowered into the emitted program
    }

    [Fact]
    public void An_unmodeled_function_on_a_curated_std_type_is_rejected_precisely()
    {
        // Since the curated model claims the path, a member it does not provide must say so by name —
        // not fall through to the instance path and report the type as "a type, not a value", and not
        // silently navigate into upstream source.
        var ex = Should.Throw<CompileException>(() => EmitWithStdTree(
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var buf: [64]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.initNoRetry(&buf);\n" +
            "    _ = fba;\n" +
            "    return 0;\n" +
            "}\n"));
        ex.Message.ShouldContain("std.heap.FixedBufferAllocator");
        ex.Message.ShouldContain("initNoRetry");
    }

    [Fact]
    public void A_non_curated_std_TYPE_navigates_to_real_source()
    {
        // road-to-zig-std S4d — the type-position half of navigation. `std.ascii.Pair` is not a curated
        // type, so it resolves through the module graph to the struct `ascii.zig` declares, lowered from
        // that source. Before S4d every dotted type that wasn't a curated row threw ("zig type
        // `std.ascii.Pair` is not modeled"), so no std TYPE could reach real source at all.
        var cs = EmitWithStdTree(
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    const p: std.ascii.Pair = .{ .a = 40, .b = 2 };\n" +
            "    return p.a + p.b;\n" +
            "}\n");
        cs.ShouldContain("struct Pair");    // the navigated module's own declaration, emitted
        cs.ShouldContain("new Pair {");
    }

    [Fact]
    public void A_curated_std_TYPE_is_not_navigated_in_type_position()
    {
        // S1's ordering rule, now in type position too: `std.mem.Allocator` is curated, so it lowers to
        // dotcc's runtime fat pointer and the navigable `mem.zig` member is never consulted — that
        // file's marker field type would fail the compile loudly if navigation won.
        var cs = EmitWithStdTree(
            "const std = @import(\"std\");\n" +
            "fn takes(a: std.mem.Allocator) u8 { _ = a; return 42; }\n" +
            "pub fn main() u8 {\n" +
            "    var buf: [64]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buf);\n" +
            "    return takes(fba.allocator());\n" +
            "}\n");
        cs.ShouldNotContain(NavigationMarker);
    }

    [Fact]
    public void A_non_curated_std_GENERIC_type_navigates_and_carries_its_methods()
    {
        // The other type-position shape: a module-qualified CALL in a type slot
        // (`std.array_list.Aligned(u8)`) — what `std.ArrayList(T)` is. The template lives in the
        // navigated module, so it reifies THERE, and its methods still have to reach the emitted
        // program: a lazy module must drain the reified-method worklist the way pass 2.5 does for a root.
        var cs = EmitWithStdTree(
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    var box: std.array_list.Aligned(u8) = .{ .first = 2, .len = 40 };\n" +
            "    box.len += 0;\n" +
            "    return @intCast(box.total());\n" +
            "}\n");
        cs.ShouldContain("struct Aligned__u8");                     // reified per resolved type arg
        cs.ShouldContain("Aligned__u8_total(Aligned__u8 self)");    // …and its method BODY was drained
    }

    [Fact]
    public void A_type_the_navigated_module_does_not_declare_is_rejected_by_name()
    {
        // A resolvable module that declares no such type is a loud error naming both — never a
        // fall-through to the old "not a modeled std path" message, which wouldn't say which file was
        // searched.
        var ex = Should.Throw<CompileException>(() => EmitWithStdTree(
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    const p: std.ascii.Nope = .{ .a = 1 };\n" +
            "    _ = p;\n" +
            "    return 0;\n" +
            "}\n"));
        ex.Message.ShouldContain("ascii.zig");
        ex.Message.ShouldContain("Nope");
    }
}
