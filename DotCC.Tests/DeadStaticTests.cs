#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// A TU-local definition (a <c>static</c> function or object, a block-scope static)
/// that nothing reaches from a definition with external linkage is left out of the
/// output, as a C compiler emits no code for an unused static function. Every header
/// <c>static inline</c> a unit includes is one: a CPython object carried 600 of them,
/// most never called, which the browser's AOT compiler then had to compile.
/// </summary>
[Collection("DeadStatic")]
public sealed class DeadStaticTests
{
    private static string Emit(string body, System.Func<string, string> emit)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-dead-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return emit(path); }
        finally { File.Delete(path); }
    }

    private static string Cs(string body) => Emit(body, p => Compiler.EmitCSharp(new[] { p }));

    [Fact]
    public void An_unused_static_function_is_not_emitted()
    {
        var cs = Cs("""
            static int never_called(int x) { return x + 1; }
            static inline int inline_never_called(void) { return 2; }
            int main(void) { return 0; }
            """);
        cs.ShouldNotContain("never_called(");
        cs.ShouldContain("main(");
    }

    [Fact]
    public void A_static_function_reached_from_main_is_emitted_with_what_it_calls()
    {
        var cs = Cs("""
            static int leaf_fn(int x) { return x * 2; }
            static int middle_fn(int x) { return leaf_fn(x) + 1; }
            static int other_fn(int x) { return leaf_fn(x) - 1; }
            int main(void) { return middle_fn(3); }
            """);
        cs.ShouldContain("middle_fn(int x)");
        cs.ShouldContain("leaf_fn(int x)");
        cs.ShouldNotContain("other_fn(");
    }

    [Fact]
    public void A_static_chain_nothing_reaches_is_left_out_whole()
    {
        var cs = Cs("""
            static int chain_b(int x);
            static int chain_a(int x) { return chain_b(x) + 1; }
            static int chain_b(int x) { return x > 0 ? chain_a(x - 1) : 0; }
            int main(void) { return 0; }
            """);
        cs.ShouldNotContain("chain_a(");
        cs.ShouldNotContain("chain_b(");
    }

    [Fact]
    public void A_static_function_in_a_reached_table_is_kept()
    {
        var cs = Cs("""
            static int slot_fn(int x) { return x + 7; }
            static int (*slots[])(int) = { slot_fn };
            int main(void) { return slots[0](1); }
            """);
        cs.ShouldContain("slot_fn(int x)");
        cs.ShouldContain("slots");
    }

    [Fact]
    public void A_static_table_nothing_reaches_takes_its_functions_with_it()
    {
        var cs = Cs("""
            static int unused_slot_fn(int x) { return x + 7; }
            static int (*unused_slots[])(int) = { unused_slot_fn };
            static struct { int (*fn)(int); } unused_holder = { unused_slot_fn };
            int main(void) { return 0; }
            """);
        cs.ShouldNotContain("unused_slot_fn");
        cs.ShouldNotContain("unused_slots");
        cs.ShouldNotContain("unused_holder");
    }

    [Fact]
    public void An_external_object_keeps_the_static_functions_its_initializer_names()
    {
        var cs = Cs("""
            static int hook_impl(int x) { return x; }
            int (*public_hook)(int) = hook_impl;
            int main(void) { return 0; }
            """);
        cs.ShouldContain("hook_impl(int x)");
    }

    [Fact]
    public void A_static_object_read_by_a_reached_function_is_kept()
    {
        var cs = Cs("""
            static int kept_counter = 5;
            static int dropped_counter = 6;
            int main(void) { return kept_counter; }
            """);
        cs.ShouldContain("kept_counter");
        cs.ShouldNotContain("dropped_counter");
    }

    [Fact]
    public void A_dead_functions_block_scope_static_goes_with_it()
    {
        var cs = Cs("""
            static int dead_counter_fn(void) { static int dead_count = 0; return ++dead_count; }
            static int live_counter_fn(void) { static int live_count = 0; return ++live_count; }
            int main(void) { return live_counter_fn(); }
            """);
        cs.ShouldNotContain("dead_counter_fn");
        cs.ShouldNotContain("dead_count");
        cs.ShouldContain("live_count");
    }

    [Fact]
    public void A_name_used_outside_any_definition_counts_as_reached()
    {
        // A file-scope array dimension names the function (unevaluated, but resolved):
        // keeping it is the safe side.
        var cs = Cs("""
            static int sized_fn(void) { return 4; }
            static char buffer[sizeof(sized_fn())];
            int main(void) { return (int)sizeof buffer; }
            """);
        cs.ShouldContain("sized_fn(");
    }

    [Fact]
    public void An_object_file_carries_no_unreached_static()
    {
        var obj = Emit("""
            static inline int obj_unused_inline(int x) { return x; }
            static int obj_used(int x) { return x + 1; }
            int obj_entry(int x) { return obj_used(x); }
            """, p => Compiler.EmitObject(p));
        obj.ShouldNotContain("obj_unused_inline");
        // An object qualifies a static's name with its unit (`obj_used__<unit>`).
        obj.ShouldContain("obj_used__");
        obj.ShouldContain("obj_entry(");
    }

    [Fact]
    public void The_wat_backend_leaves_unreached_statics_out_too()
    {
        var wat = Emit("""
            static int wat_unused(int x) { return x; }
            static int wat_used(int x) { return x + 1; }
            int main(void) { return wat_used(1); }
            """, p => Compiler.EmitWat(new[] { p }));
        wat.ShouldNotContain("$wat_unused");
        wat.ShouldContain("$wat_used");
    }
}
