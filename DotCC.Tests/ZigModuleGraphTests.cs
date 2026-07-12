#nullable enable

using DotCC.Frontends;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for <see cref="ZigModuleGraph"/> — the road-to-zig-std S1 module graph. These lock the
/// parse-layer half: a module is parsed <em>resiliently</em> (via LALR.CC's
/// <c>ParseInputResilient</c>), so a top-level decl with an un-implemented construct is skipped and
/// recorded rather than sinking the whole file. That is what lets lazy lowering compile only the
/// decls a program actually references.
/// </summary>
public sealed class ZigModuleGraphTests
{
    [Fact]
    public void Clean_source_yields_all_decls_and_no_errors()
    {
        var graph = new ZigModuleGraph();
        var module = graph.ParseSource("clean.zig",
            "pub fn a() u8 { return 1; }\npub fn b() u8 { return 2; }\n");
        module.Errors.ShouldBeEmpty();
        module.Decls.Count.ShouldBe(2);
    }

    [Fact]
    public void A_broken_decl_is_skipped_and_recorded_leaving_the_good_ones()
    {
        // The middle decl is invalid (a `fn` body must be a block `{ … }`, not a bare `return`), so it
        // fails to parse. Resilient recovery skips it and resyncs at the next `pub`, so `a` and `b`
        // still lower — a single un-implemented/invalid decl no longer sinks the whole module.
        var graph = new ZigModuleGraph();
        var module = graph.ParseSource("mixed.zig",
            "pub fn a() u8 { return 1; }\n" +
            "pub fn bad() u8 return 2;\n" +
            "pub fn b() u8 { return 3; }\n");
        module.Errors.Count.ShouldBe(1);
        module.Decls.Count.ShouldBe(2);
        module.Parse.Tree.IsError.ShouldBeFalse();
    }

    [Fact]
    public void A_broken_decl_inside_braces_does_not_over_resync_at_a_nested_starter()
    {
        // The broken container's body holds nested `const`/`fn` decls (top-level sync terminals). Depth
        // tracking must keep those from being mistaken for a top-level boundary, so the WHOLE broken
        // container is skipped and only the trailing `pub fn ok` survives.
        var graph = new ZigModuleGraph();
        var module = graph.ParseSource("nested.zig",
            "pub const Broken = struct {\n" +
            "    const inner = 1;\n" +
            "    pub fn m() u8 return 7;\n" +   // invalid body → the whole `Broken` decl is skipped
            "};\n" +
            "pub fn ok() u8 { return 9; }\n");
        module.Errors.Count.ShouldBe(1);
        module.Decls.Count.ShouldBe(1);   // only `ok` — the nested const/fn were not resync points
    }
}
