#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Always-on unit tests for the WebAssembly-text backend (<c>--target=wat</c> /
/// <see cref="Compiler.EmitWat"/>). They assert the SHAPE of the emitted module
/// text — no subprocess, since Process.Start is confined to opt-in oracle modes;
/// actually assembling (wat2wasm) and executing (node) the module is the opt-in
/// <c>WatOracleTests</c> in the functional suite. These cover the milestone-1
/// integer slice and that out-of-slice constructs fail loudly rather than
/// miscompile.
/// </summary>
[Collection("WatBackend")]
public sealed class WatBackendTests
{
    private static string Wat(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitWat(new[] { path }); }
        finally { File.Delete(path); }
    }

    /// <summary>Emit wat for a Zig translation unit (the <c>.zig</c> extension routes
    /// <see cref="Compiler.EmitWat"/> to the Zig front-end, same shared IR + backend).</summary>
    private static string WatZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{System.Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitWat(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void module_defines_and_exports_main()
    {
        var wat = Wat("int main(void){ return 0; }");
        wat.ShouldContain("(module");
        wat.ShouldContain("(func $main (result i32)");
        wat.ShouldContain("(export \"main\" (func $main))");
    }

    [Fact]
    public void binary_arithmetic_lowers_post_order()
    {
        // 2, then (3 4 mul), then add — operands precede their operator on the stack.
        var wat = Wat("int main(void){ return 2 + 3 * 4; }");
        wat.ShouldContain("i32.const 2");
        wat.ShouldContain("i32.mul");
        wat.ShouldContain("i32.add");
    }

    [Fact]
    public void division_picks_signed_or_unsigned_from_the_operand()
    {
        Wat("int main(void){ int a=7,b=2; return a/b; }").ShouldContain("i32.div_s");
        Wat("int main(void){ unsigned a=7,b=2; return a/b; }").ShouldContain("i32.div_u");
    }

    [Fact]
    public void long_arithmetic_uses_i64_and_casts_back_with_wrap()
    {
        var wat = Wat("int main(void){ long a=5,b=6; return (int)(a*b); }");
        wat.ShouldContain("i64.mul");
        wat.ShouldContain("i32.wrap_i64");
    }

    [Fact]
    public void an_over_int_decimal_literal_is_typed_long()
    {
        // C99 6.4.4.1: a decimal constant that doesn't fit `int` climbs to `long`, so
        // it lowers to i64 — not a too-big `i32.const` (which wat2wasm rejects).
        var wat = Wat("int main(void){ long n = 10000000000; return (int)(n % 7); }");
        wat.ShouldContain("i64.const 10000000000");
        wat.ShouldNotContain("i32.const 10000000000");
    }

    [Fact]
    public void locals_are_declared_before_the_body()
    {
        var wat = Wat("int main(void){ int x = 41; return x + 1; }");
        wat.ShouldContain("(local $x i32)");
        wat.ShouldContain("local.set $x");
        wat.ShouldContain("local.get $x");
    }

    [Fact]
    public void for_loop_emits_structured_block_and_loop()
    {
        var wat = Wat("int main(void){ int s=0; for(int i=0;i<3;i++) s+=i; return s; }");
        wat.ShouldContain("block $brk");
        wat.ShouldContain("loop $loop");
        wat.ShouldContain("block $cont");   // continue target / for-post sequencing
        wat.ShouldContain("br_if $brk");
    }

    [Fact]
    public void switch_lowers_to_nested_blocks_with_a_comparison_dispatch()
    {
        // A $swbrk block wraps per-section blocks; the dispatch compares the (cached)
        // subject to each case value and branches into the matching section.
        var wat = Wat("int f(int x){ switch(x){ case 1: return 1; case 2: return 2; default: return 0; } } int main(void){ return f(2); }");
        wat.ShouldContain("block $swbrk");
        wat.ShouldContain("block $sw");
        wat.ShouldContain("i32.eq");
        wat.ShouldContain("br_if $sw");
    }

    [Fact]
    public void break_in_a_switch_targets_the_switch_while_continue_targets_the_loop()
    {
        // A switch nested in a loop: `break` exits the switch ($swbrk), `continue`
        // steps the loop ($cont) — the switch never captures continue.
        var wat = Wat("int main(void){ int t=0; for(int i=0;i<3;i++){ switch(i){ case 0: continue; case 1: break; default: t++; } t+=10; } return t; }");
        wat.ShouldContain("br $swbrk");   // the switch's break
        wat.ShouldContain("br $cont");    // the switch's continue still steps the loop
    }

    [Fact]
    public void goto_using_function_lowers_to_a_cfg_dispatch_loop()
    {
        // A function with a label can't be emitted structurally; it becomes a CFG
        // dispatch loop ($__disp + a br_table on $__lbl).
        var wat = Wat("int f(int x){ int r=0; if(x<0) goto bad; r=x; return r; bad: return -1; } int main(void){ return f(-2); }");
        wat.ShouldContain("loop $__disp");
        wat.ShouldContain("br_table");
        wat.ShouldContain("(local $__lbl i32)");
    }

    [Fact]
    public void goto_free_function_keeps_the_structured_emit()
    {
        // No label -> no dispatch loop; the clean structured control flow is unchanged.
        var wat = Wat("int main(void){ int s=0; for(int i=0;i<3;i++) s+=i; return s; }");
        wat.ShouldNotContain("$__disp");
        wat.ShouldNotContain("$__lbl");
    }

    [Fact]
    public void A_switch_in_a_goto_function_dispatches_through_the_cfg()
    {
        // The subject goes into a local of its own once; a chain of CFG blocks compares it
        // with each case, and the sections fall into each other as C's do.
        var wat = Wat("int f(int x){ int r = 0; if (x < 0) goto neg; switch (x) { case 1: r = 10; case 2: r += 2; break; default: r = 99; } return r; neg: return -1; }\nint main(void){ return f(1); }");
        wat.ShouldContain("(local $__sw0 i32)");
        wat.ShouldContain("local.tee $__sw0");
        wat.ShouldContain("loop $__disp");
    }

    [Fact]
    public void A_value_waiting_in_a_scratch_is_not_overwritten_by_a_nested_one()
    {
        // `log_[li++] = v`: v waits in a scratch while the target's address is computed, and
        // that computation (li++ on a global) needs scratch locals of its own. It gets
        // $__t32_1, not the $__t32 holding v (which it used to overwrite with li).
        var wat = Wat("int log_[4]; int li;\nvoid record(int v){ log_[li++] = v; }\nint main(void){ record(7); return log_[0]; }");
        wat.ShouldContain("(local $__t32 i32)\n    (local $__t32_1 i32)");
        wat.ShouldContain("local.get $v\n    local.set $__t32\n");
    }

    [Fact]
    public void A_comma_operator_discards_all_but_its_last_operand()
    {
        // `(g(&a), g(&a), a * 10)`: each leading operand runs and its value is dropped;
        // a `(void)` cast drops its operand's value too.
        var wat = Wat("int g(int *p){ return ++*p; }\nint main(void){ int a = 1, b; (void)g(&a); b = (g(&a), g(&a), a * 10); return b; }");
        wat.ShouldContain("call $g\n    drop\n    global.get $__sp\n    call $g\n    drop\n    global.get $__sp\n    call $g\n    drop");
    }

    [Fact]
    public void switch_with_a_duffs_device_nested_case_fails_loud()
    {
        // A case label nested inside another statement (Duff's device) is unsupported
        // on wat (as on the C# backend) — fail loud rather than miscompile.
        Should.Throw<CompileException>(() => Wat(
            "int main(void){ int n=4,i=0; switch(n%2){ case 0: do { i++; case 1: i++; } while(--n>0); } return i; }"));
    }

    [Fact]
    public void direct_call_emits_dollar_name_and_param_signature()
    {
        var wat = Wat("int add(int a,int b){ return a+b; } int main(void){ return add(3,4); }");
        wat.ShouldContain("(func $add (param $a i32) (param $b i32) (result i32)");
        wat.ShouldContain("call $add");
    }

    [Fact]
    public void short_circuit_and_lowers_to_value_if()
    {
        Wat("int main(void){ int a=1,b=2; return a && b; }").ShouldContain("if (result i32)");
    }

    [Fact]
    public void block_shadowed_local_is_uniquified()
    {
        // Two C `i`s in nested scopes become distinct flat wasm locals — the wat
        // name legalizer forbids shadowing (flat function locals), so the inner one
        // is renamed.
        var wat = Wat("int main(void){ int i=1; { int i=2; return i; } }");
        wat.ShouldContain("(local $i i32)");
        wat.ShouldContain("(local $i__1 i32)");
    }

    [Fact]
    public void library_call_is_rejected_not_miscompiled()
    {
        // An unwired libc call (scanf needs host imports we don't emit) fails loudly
        // rather than miscompiling.
        Should.Throw<CompileException>(() => Wat("int main(void){ int x; scanf(\"%d\", &x); return 0; }"));
    }

    [Fact]
    public void printf_with_a_string_literal_format_expands_inline()
    {
        // A string-literal printf is expanded at the call site — no $printf function,
        // direct writes for literal runs, a formatting-helper call for the conversion.
        var wat = Wat("int main(void){ printf(\"n=%d\\n\", 42); return 0; }");
        wat.ShouldContain("(import \"wasi_snapshot_preview1\" \"fd_write\"");
        wat.ShouldContain("call $__pf_int_s");      // the %d conversion
        wat.ShouldContain("call $__write");         // a literal run
        wat.ShouldNotContain("call $printf");       // not a runtime function
    }

    [Fact]
    public void printf_string_and_char_conversions_reuse_the_io_runtime()
    {
        var wat = Wat("int main(void){ printf(\"%s%c\", \"hi\", 33); return 0; }");
        wat.ShouldContain("call $__emit_str");
        wat.ShouldContain("call $__emit_char");
    }

    [Fact]
    public void printf_field_width_passes_the_constant_through_to_the_formatter()
    {
        // Width/precision/flags are compile-time constants from the literal format —
        // resolved here (width 5, zero-pad mode 2) and handed to the formatter.
        var wat = Wat("int main(void){ printf(\"%05d\", 42); return 0; }");
        wat.ShouldContain("call $__pf_int_s");
        wat.ShouldContain("i32.const 5");   // the width immediate
    }

    [Fact]
    public void sprintf_expands_inline_with_a_buffer_sink()
    {
        // sprintf points the output sink at the destination buffer, runs the shared
        // expansion, then NUL-terminates — no $sprintf runtime function.
        var wat = Wat("int main(void){ char b[16]; sprintf(b, \"%d\", 42); return 0; }");
        wat.ShouldContain("global.set $__ob");     // sink aimed at the buffer
        wat.ShouldContain("call $__sink_end");
        wat.ShouldNotContain("call $sprintf");
    }

    [Fact]
    public void fprintf_to_stdout_reuses_the_printf_expansion_at_the_default_fd()
    {
        // fprintf(stdout, …) is the same inline expansion as printf; stdout is the
        // sink's default fd (1), so no $__fd flip is emitted.
        var wat = Wat("#include <stdio.h>\nint main(void){ fprintf(stdout, \"n=%d\\n\", 42); return 0; }");
        wat.ShouldContain("call $__pf_int_s");
        wat.ShouldContain("call $__write");
        wat.ShouldNotContain("call $fprintf");        // not a runtime function
    }

    [Fact]
    public void fprintf_to_stderr_flips_the_sink_fd_to_2_and_restores_it()
    {
        // fprintf(stderr, …) points the sink at fd 2 for the duration of the call,
        // then restores fd 1 (stdout) so later prints are unaffected.
        var wat = Wat("#include <stdio.h>\nint main(void){ fprintf(stderr, \"e=%d\\n\", 7); return 0; }");
        wat.ShouldContain("(global $__fd");           // the selectable output fd
        wat.ShouldContain("i32.const 2");             // stderr fd
        wat.ShouldContain("global.set $__fd");
        wat.ShouldContain("global.get $__fd");        // $__write consults it
    }

    [Fact]
    public void fprintf_to_a_non_standard_stream_is_rejected()
    {
        // Only stdout/stderr map to WASI fds; a real FILE* has no wat runtime, so it
        // fails loud rather than miscompiling to the wrong fd.
        Should.Throw<CompileException>(() => Wat(
            "#include <stdio.h>\nint main(void){ FILE* f = fopen(\"x\", \"w\"); fprintf(f, \"hi\"); return 0; }"));
    }

    [Fact]
    public void zig_std_debug_print_lowers_to_a_void_fprintf_on_stderr()
    {
        // std.debug.print → fprintf(stderr, …), and that fprintf is VOID-typed (unlike
        // C's int fprintf) — so the expansion must route to fd 2 AND leave nothing on
        // the stack (a stray result would unbalance a void statement and the wasm
        // validator would reject the module). Pins the emission; the void stack-balance
        // is exercised at runtime by the libwabt harness.
        var wat = WatZig("const std = @import(\"std\");\npub fn main() void {\n    std.debug.print(\"x={d}\\n\", .{@as(i32, 5)});\n}\n");
        wat.ShouldContain("global.set $__fd");
        wat.ShouldContain("i32.const 2");            // stderr fd
        wat.ShouldContain("call $__pf_int_s");       // the {d} conversion
    }

    [Fact]
    public void printf_with_a_runtime_format_is_rejected()
    {
        // Only a string-literal format can be expanded at compile time.
        Should.Throw<CompileException>(() => Wat("int main(void){ char *f = \"%d\"; printf(f, 1); return 0; }"));
    }

    [Fact]
    public void printf_unsupported_conversions_are_rejected()
    {
        // '#' on a d/i/u/c/s conversion (where C leaves it undefined) and the
        // unsupported %n still fail loud rather than miscompile. (%E/%F/%G, %#x/%#X/%#o,
        // %a/%A and %p are all supported now.)
        Should.Throw<CompileException>(() => Wat("int main(void){ printf(\"%#d\", 255); return 0; }"));
        Should.Throw<CompileException>(() => Wat("int main(void){ int n; printf(\"%n\", &n); return 0; }"));
    }

    [Fact]
    public void printf_percent_p_routes_to_the_pointer_formatter()
    {
        // %p lowers to the glibc-shaped pointer helper ("(nil)" / "0x"+hex).
        Wat("int main(void){ printf(\"%p\", (void*)0); return 0; }").ShouldContain("call $__pf_p");
    }

    [Fact]
    public void printf_uppercase_float_conversions_route_to_the_same_formatters()
    {
        // %E/%F/%G reuse the %e/%f/%g formatters with an extra uppercase flag (the
        // last `i32.const` immediate before the call) selecting 'E' / INF / NAN.
        Wat("int main(void){ printf(\"%E\", 1.5); return 0; }").ShouldContain("call $__pf_e");
        Wat("int main(void){ printf(\"%G\", 1.5); return 0; }").ShouldContain("call $__pf_g");
        Wat("int main(void){ printf(\"%F\", 1.5); return 0; }").ShouldContain("call $__pf_f");
    }

    [Fact]
    public void printf_percent_a_emits_the_hex_float_formatter_without_bignum()
    {
        // %a is an exact IEEE-754 bit-dump, so it routes to $__pf_a and pulls in only
        // the shared field layout — never the %f big-integer block or the %e/%g Dragon.
        var wat = Wat("int main(void){ printf(\"%a\", 1.5); return 0; }");
        wat.ShouldContain("call $__pf_a");
        wat.ShouldNotContain("call $__dragon");
        wat.ShouldNotContain("call $__bn_mul");
    }

    [Fact]
    public void printf_hash_hex_packs_the_radix_prefix_into_the_sign_slot()
    {
        // %#x packs the "0x" prefix low-byte-first into the same i32 slot $__pf_emit
        // reads a sign from: '0' | ('x' << 8) == 48 | (120 << 8) == 30768. %#X uses
        // 'X' (88) → 48 | (88 << 8) == 22576.
        Wat("int main(void){ printf(\"%#x\", 255); return 0; }").ShouldContain("i32.const 30768");
        Wat("int main(void){ printf(\"%#X\", 255); return 0; }").ShouldContain("i32.const 22576");
    }

    [Fact]
    public void printf_percent_f_emits_the_float_formatter_and_bignum_block()
    {
        // %f drives the hand-written formatter ($__pf_f) over the big-integer helper
        // block; the value's bits are decomposed (reinterpret) rather than converted
        // through f64, and the limb-count global is declared.
        var wat = Wat("int main(void){ printf(\"%f\", 1.5); return 0; }");
        wat.ShouldContain("call $__pf_f");
        wat.ShouldContain("(func $__pf_f");
        wat.ShouldContain("(func $__bn_mul");
        wat.ShouldContain("(func $__bn_divmod");
        wat.ShouldContain("(global $__bnlen");
        wat.ShouldContain("i64.reinterpret_f64");
    }

    [Fact]
    public void printf_float_default_precision_is_six()
    {
        // No explicit precision → C's default of 6, passed to the formatter as an immediate.
        var wat = Wat("int main(void){ printf(\"%f\", 1.5); return 0; }");
        wat.ShouldContain("i32.const 6");
        wat.ShouldContain("call $__pf_f");
    }

    [Fact]
    public void printf_float_runtime_is_emitted_only_on_demand()
    {
        // An integer-only printf pulls in none of the float machinery.
        var wat = Wat("int main(void){ printf(\"%d\", 42); return 0; }");
        wat.ShouldNotContain("__pf_f");
        wat.ShouldNotContain("__bnlen");
        wat.ShouldNotContain("__bn_mul");
        wat.ShouldNotContain("__dragon");   // (__pf_e is a prefix of __pf_emit, so check $__dragon / $__r_cmp)
        wat.ShouldNotContain("__r_cmp");
    }

    [Fact]
    public void printf_percent_e_and_g_emit_the_dragon_formatter()
    {
        // %e/%g drive the scaled-Dragon digit generator over the region big-integer
        // helpers — a different machine from %f's single-register bignum.
        var e = Wat("int main(void){ printf(\"%e\", 1.5); return 0; }");
        e.ShouldContain("call $__pf_e");
        e.ShouldContain("(func $__dragon");
        e.ShouldContain("(func $__r_cmp");
        e.ShouldContain("(func $__r_sub");

        var g = Wat("int main(void){ printf(\"%g\", 1.5); return 0; }");
        g.ShouldContain("call $__pf_g");
        g.ShouldContain("(func $__dragon");
    }

    [Fact]
    public void printf_g_precision_zero_is_treated_as_one()
    {
        // C99: a %g precision of 0 is taken as 1 significant digit.
        var wat = Wat("int main(void){ printf(\"%.0g\", 1.5); return 0; }");
        wat.ShouldContain("i32.const 1");   // the precision immediate passed to $__pf_g
        wat.ShouldContain("call $__pf_g");
    }

    [Fact]
    public void putchar_emits_the_fd_write_import_and_runtime_function()
    {
        // Byte-level stdout: a WASI fd_write import (exported memory so the host can
        // read the iovec), the call, and the hand-written runtime definition.
        var wat = Wat("int main(void){ putchar('A'); return 0; }");
        wat.ShouldContain("(import \"wasi_snapshot_preview1\" \"fd_write\"");
        wat.ShouldContain("(memory (export \"memory\") 1)");
        wat.ShouldContain("call $putchar");
        wat.ShouldContain("(func $putchar (param $c i32) (result i32)");
    }

    [Fact]
    public void puts_emits_an_inline_strlen_loop_and_runtime_function()
    {
        var wat = Wat("int main(void){ puts(\"hi\"); return 0; }");
        wat.ShouldContain("(func $puts (param $s i32) (result i32)");
        wat.ShouldContain("loop $scan");      // the inline strlen
        wat.ShouldContain("call $fd_write");
    }

    [Fact]
    public void io_runtime_is_emitted_only_on_demand()
    {
        // No I/O → byte-identical plain memory, no import, no runtime functions.
        var wat = Wat("int main(void){ return 7; }");
        wat.ShouldNotContain("fd_write");
        wat.ShouldNotContain("export \"memory\"");
        wat.ShouldContain("(memory 1)");
    }

    [Fact]
    public void a_user_defined_runtime_name_wins_over_the_builtin()
    {
        // The program supplies its own puts → call it directly, no import, no
        // hand-written runtime puts spliced in.
        var wat = Wat("int puts(char *s){ return 0; } int main(void){ return puts(\"x\"); }");
        wat.ShouldNotContain("fd_write");
        wat.ShouldContain("call $puts");
    }

    [Fact]
    public void string_literal_lowers_to_a_data_segment_and_loads_bytes()
    {
        var wat = Wat("int main(void){ char *s = \"hi\"; return s[0]; }");
        wat.ShouldContain("(memory 1)");
        wat.ShouldContain("(data (i32.const 1024)");
        wat.ShouldContain("i32.load8_s");   // a char read
    }

    [Fact]
    public void pointer_index_scales_by_the_element_size()
    {
        // An int* subscript multiplies the index by sizeof(int)=4 before the load.
        var wat = Wat("int at(int *a){ return a[3]; } int main(void){ return 0; }");
        wat.ShouldContain("i32.const 4");
        wat.ShouldContain("i32.mul");
        wat.ShouldContain("i32.load");
    }

    [Fact]
    public void address_taken_local_lives_in_the_shadow_stack()
    {
        // &x forces x into a linear-memory frame slot off the $__sp shadow stack;
        // *p = 10 is a store through that address.
        var wat = Wat("int main(void){ int x=5; int *p=&x; *p=10; return x; }");
        wat.ShouldContain("global $__sp");
        wat.ShouldContain("$__fp");      // the saved frame pointer
        wat.ShouldContain("i32.store");
    }

    [Fact]
    public void local_array_is_frame_allocated_and_indexable()
    {
        var wat = Wat("int main(void){ int a[3]; a[0]=7; return a[0]; }");
        wat.ShouldContain("global.get $__sp");
        wat.ShouldContain("i32.store");
        wat.ShouldContain("i32.load");
    }

    [Fact]
    public void a_non_address_taken_scalar_stays_a_fast_wasm_local()
    {
        // No & and not an array → a plain value local, no shadow-stack frame.
        var wat = Wat("int main(void){ int x = 41; return x + 1; }");
        wat.ShouldContain("(local $x i32)");
        wat.ShouldNotContain("$__fp");
    }

    [Fact]
    public void malloc_emits_a_bump_allocator_with_a_heap_pointer_global()
    {
        // A non-struct malloc reaches the backend (the IR's malloc->stack peephole
        // only fires for struct pointees) and lowers to the bump allocator: a
        // heap-pointer global and a $malloc that grows linear memory. No I/O is used,
        // so no fd_write import and the memory stays unexported.
        var wat = Wat("#include <stdlib.h>\nint main(void){ int *p = malloc(sizeof(int)); *p = 42; return *p; }");
        wat.ShouldContain("(global $__hp");
        wat.ShouldContain("(func $malloc (param $n i32) (result i32)");
        wat.ShouldContain("memory.grow");
        wat.ShouldContain("call $malloc");
        wat.ShouldNotContain("fd_write");
        wat.ShouldNotContain("export \"memory\"");
    }

    [Fact]
    public void free_lowers_to_a_drop_with_no_runtime_function()
    {
        // free is a no-op for the bump allocator: evaluate the argument and drop it,
        // emitting no $free function (and no call to one).
        var wat = Wat("#include <stdlib.h>\nint main(void){ int *p = malloc(sizeof(int)); *p = 1; free(p); return 0; }");
        wat.ShouldContain("call $malloc");
        wat.ShouldNotContain("(func $free");
        wat.ShouldNotContain("call $free");
    }

    [Fact]
    public void calloc_and_realloc_are_built_on_malloc()
    {
        var calloc = Wat("#include <stdlib.h>\nint main(void){ int *a = calloc(4, sizeof(int)); return a[0]; }");
        calloc.ShouldContain("(func $calloc");
        calloc.ShouldContain("(func $malloc");   // calloc bottoms out at malloc
        calloc.ShouldContain("call $malloc");

        var realloc = Wat("#include <stdlib.h>\nint main(void){ int *a = malloc(8); a = realloc(a, 16); return a[0]; }");
        realloc.ShouldContain("(func $realloc");
        realloc.ShouldContain("(func $malloc");
    }

    [Fact]
    public void heap_runtime_is_emitted_only_on_demand()
    {
        // No heap use → no bump-pointer global, no allocator, no memory growth.
        var wat = Wat("int main(void){ return 7; }");
        wat.ShouldNotContain("$__hp");
        wat.ShouldNotContain("$malloc");
        wat.ShouldNotContain("memory.grow");
    }

    [Fact]
    public void a_user_defined_malloc_wins_over_the_bump_allocator()
    {
        // The program supplies its own malloc → call it directly; no bump allocator
        // (no $__hp global, no memory growth).
        var wat = Wat("void *malloc(int n){ return 0; } int main(void){ void *p = malloc(4); return p == 0; }");
        wat.ShouldContain("call $malloc");
        wat.ShouldNotContain("(global $__hp");
        wat.ShouldNotContain("memory.grow");
    }

    // ---- floating point --------------------------------------------------

    [Fact]
    public void double_literal_and_arithmetic_lower_to_f64_ops()
    {
        // A floating literal is an f64.const; arithmetic uses the f64 instruction set;
        // the (int) cast truncates toward zero (saturating, so NaN/overflow can't trap).
        var wat = Wat("int main(void){ return (int)(1.5 + 2.5); }");
        wat.ShouldContain("f64.const 1.5");
        wat.ShouldContain("f64.const 2.5");
        wat.ShouldContain("f64.add");
        wat.ShouldContain("i32.trunc_sat_f64_s");
    }

    [Fact]
    public void float_literal_f_suffix_is_stripped_for_wat()
    {
        // wat carries the float width on the instruction prefix, so the C `f` suffix
        // must be dropped. An f-suffixed literal is float (§6.4.4.2), so it lands as
        // an f32.const promoted to f64 at the double-typed store — clang's lowering.
        var wat = Wat("int main(void){ double x = 1.5f; return (int)x; }");
        wat.ShouldContain("f32.const 1.5");
        wat.ShouldContain("f64.promote_f32");
        wat.ShouldNotContain("1.5f");
    }

    [Fact]
    public void decimal_float_literal_forms_render_as_legal_wat_consts()
    {
        // The C99 decimal-float spellings without two digit runs around the point:
        // a point-free exponent (`1e10`), a leading dot (`.5`), a trailing dot (`1.`),
        // and a dot-before-exponent (`2.e3`). wat's float-const grammar needs a digit
        // before and after every point, so RenderFloatLit splices `0`s as needed —
        // `.5`→`0.5`, `1.`→`1.0`, `2.e3`→`2.0e3` — while `1e10` passes straight through.
        Wat("int main(void){ double a = 1e10;  return (int)(a/1e9); }").ShouldContain("f64.const 1e10");
        Wat("int main(void){ double c = .5;    return (int)(c*10); }").ShouldContain("f64.const 0.5");
        Wat("int main(void){ double d = 1.;    return (int)d; }").ShouldContain("f64.const 1.0");
        Wat("int main(void){ double e = 2.e3;  return (int)e; }").ShouldContain("f64.const 2.0e3");
    }

    [Fact]
    public void point_free_exponent_literal_is_a_float_not_an_int()
    {
        // `1e3` has no decimal point, so the mandatory-exponent FLOAT rule (not NUM)
        // must claim it — it lowers to an f64 const, never an i32 one.
        var wat = Wat("int main(void){ double d = 1e3; return (int)d; }");
        wat.ShouldContain("f64.const 1e3");
        wat.ShouldNotContain("i32.const 1000");
    }

    [Fact]
    public void float_type_uses_f32_storage_and_demotes_the_double_literal()
    {
        // `float` is f32; the double-typed literal demotes on the store into it.
        var wat = Wat("int main(void){ float f = 1.5; return (int)f; }");
        wat.ShouldContain("(local $f f32)");
        wat.ShouldContain("f32.demote_f64");
        wat.ShouldContain("i32.trunc_sat_f32_s");
    }

    [Fact]
    public void int_promotes_to_double_in_a_mixed_expression()
    {
        // The int operand widens to f64 before the f64 add (usual arithmetic).
        var wat = Wat("int main(void){ int n = 3; double d = 2.0; return (int)(d + n); }");
        wat.ShouldContain("f64.convert_i32_s");
        wat.ShouldContain("f64.add");
    }

    [Fact]
    public void float_comparison_has_no_signedness_suffix()
    {
        var wat = Wat("int main(void){ double a = 1.5, b = 2.5; return a < b; }");
        wat.ShouldContain("f64.lt");
        wat.ShouldNotContain("f64.lt_s");
        wat.ShouldNotContain("f64.lt_u");
    }

    [Fact]
    public void float_truthiness_compares_against_zero()
    {
        // A float condition isn't a wasm i32; it reduces to (x != 0).
        var wat = Wat("int main(void){ double d = 3.0; if (d) return 1; return 0; }");
        wat.ShouldContain("f64.const 0");
        wat.ShouldContain("f64.ne");
    }

    [Fact]
    public void float_negation_uses_neg_for_a_correct_signed_zero()
    {
        // `f64.neg` (not `0 - x`, which would turn -0.0 into +0.0).
        var wat = Wat("int main(void){ double d = 1.5; return (int)(-d); }");
        wat.ShouldContain("f64.neg");
    }

    [Fact]
    public void float_memory_lvalue_compound_assign_uses_a_float_scratch()
    {
        // An array element is a memory lvalue; `+=` on a double element
        // read-modify-writes through the address, staging in an f64 scratch (not i32).
        var wat = Wat("int main(void){ double a[2]; a[0] = 1.0; a[0] += 2.5; return (int)a[0]; }");
        wat.ShouldContain("(local $__tf64 f64)");
        wat.ShouldContain("f64.load");
        wat.ShouldContain("f64.add");
        wat.ShouldContain("f64.store");
    }

    [Fact]
    public void mixed_width_integer_op_extends_the_narrower_operand()
    {
        // The IR doesn't pre-coerce binary operands, so the backend widens the int to
        // i64 before the i64 op — without it the stack types wouldn't match.
        var wat = Wat("int main(void){ long a = 5; int b = 3; return (int)(a + b); }");
        wat.ShouldContain("i64.extend_i32_s");
        wat.ShouldContain("i64.add");
    }

    [Fact]
    public void A_struct_lives_in_the_frame_and_its_members_are_offsets()
    {
        // The layout model places the members (int x @0, long y @8, char *n @16; size 24),
        // the one sizeof and offsetof fold from. A struct's value is its address: a brace
        // initializer zeroes the slot then stores each member, an assignment copies bytes.
        var wat = Wat("#include <stddef.h>\nstruct P { int x; long y; char *n; };\n"
            + "int main(void){ struct P p; p.x = 3; p.y = 4; struct P q = { 1, 2, \"hi\" }; p = q; "
            + "return (int)(sizeof(struct P) + offsetof(struct P, n) + p.x + p.y + p.n[1]); }");
        wat.ShouldContain("i64.const 24\n    i64.const 16\n    i64.add");      // sizeof, offsetof
        wat.ShouldContain("global.get $__sp\n    i32.const 8\n    i32.add\n    local.get $__t64\n    i64.store");  // p.y = 4
        wat.ShouldContain("i32.const 0\n    i32.const 24\n    memory.fill");   // q's initializer
        wat.ShouldContain("i32.const 24\n    memory.copy");                      // p = q
    }

    [Fact]
    public void A_global_lives_at_a_fixed_address_and_a_start_function_initializes_it()
    {
        // C's static storage: a fixed address in the data area (from 1024 up), zero until
        // the start function stores its initializer, and read and written through memory.
        var wat = Wat("int counter = 5;\nint main(void){ counter++; return counter; }");
        wat.ShouldContain("(start $__init_globals)");
        wat.ShouldContain("(func $__init_globals\n    i32.const 1024\n    i32.const 5\n    i32.store");
        wat.ShouldContain("i32.const 1024\n    i32.load");
        wat.ShouldNotContain("local.get $counter");
    }

    [Fact]
    public void A_zero_global_needs_no_start_function()
    {
        // Linear memory starts zeroed, as C's static storage does.
        Wat("int z;\nint main(void){ return z; }").ShouldNotContain("__init_globals");
    }

    [Fact]
    public void Data_past_half_the_first_page_moves_the_stack_past_it()
    {
        // 40 KB of globals would run into a stack topped at 64 KB: the stack then starts
        // past the data with 1 MiB of its own, and the memory grows to hold it.
        var wat = Wat("static char big[40000];\nint main(void){ big[39999] = 1; return big[39999]; }");
        wat.ShouldContain("(memory 17)");
        wat.ShouldContain("(global $__sp (mut i32) (i32.const 1089600))");
    }

    [Fact]
    public void A_struct_crosses_a_call_by_value_as_the_address_of_a_copy()
    {
        // clang's wasm32 convention: a struct argument is the address of a copy the caller
        // makes in its frame (the callee may change its parameter), and a struct result goes to
        // a slot of the caller's whose address is a hidden first parameter ($__sret), which the
        // function also returns.
        var wat = Wat("struct P { int x, y; };\nstruct P make(int a){ struct P p = {a, a * 2}; return p; }\n"
            + "int sum(struct P p){ p.x += 100; return p.x + p.y; }\n"
            + "int main(void){ struct P q = make(3); int s = sum(q); return s * 10 + q.x; }");
        wat.ShouldContain("(func $make (param $__sret i32) (param $a i32) (result i32)");
        wat.ShouldContain("(func $sum (param $p i32) (result i32)");
        wat.ShouldContain("local.get $__sret\n    global.get $__sp\n    i32.const 8\n    memory.copy\n    local.get $__sret");
    }

    [Fact]
    public void A_function_pointer_is_a_table_index_called_through_call_indirect()
    {
        // A function used as a value gets a slot in the module's funcref table (from 1; 0 is
        // the null pointer, which traps when called) and a call through a pointer checks the
        // signature, declared once as a module type.
        var wat = Wat("int add(int a, int b){ return a + b; }\nint (*op)(int, int) = add;\n"
            + "int main(void){ int (*f)(int, int) = &add; return f(2, 3) + op(4, 5); }");
        wat.ShouldContain("(type $__sig0 (func (param i32) (param i32) (result i32)))");
        wat.ShouldContain("(table 2 funcref)");
        wat.ShouldContain("(elem (i32.const 1) func $add)");
        wat.ShouldContain("call_indirect (type $__sig0)");
    }

    [Fact]
    public void A_libc_function_is_compiled_from_c_only_when_the_program_calls_it()
    {
        // The wat target's libc is C (DotCC.Lib/WatLibc, one function per file) compiled with
        // the program: a called function brings its file, and what that calls, in turn
        // (atoi -> strtol -> __strtox), and nothing else comes along.
        var wat = Wat("#include <stdlib.h>\n#include <string.h>\nint main(void){ return atoi(\"42\") + (int)strlen(\"ab\"); }");
        wat.ShouldContain("(func $atoi ");
        wat.ShouldContain("(func $strtol ");
        wat.ShouldContain("(func $__strtox ");
        wat.ShouldContain("(func $strlen ");
        wat.ShouldNotContain("$strcmp");
        Wat("int main(void){ return 0; }").ShouldNotContain("$strlen");
    }

    [Fact]
    public void A_programs_own_definition_of_a_libc_name_wins()
    {
        // The library's definitions are weak: the program's strlen is the only one.
        var wat = Wat("#include <string.h>\nint strlen(char *s){ return 7; }\nint main(void){ return strlen(\"ab\"); }");
        wat.ShouldContain("(func $strlen (param $s i32) (result i32)\n    i32.const 7");
        wat.Split("(func $strlen ").Length.ShouldBe(2);
    }

    [Fact]
    public void Bulk_memory_and_exit_are_instructions_not_library_calls()
    {
        // memcpy/memmove are memory.copy, memset memory.fill (each leaving its destination),
        // exit is WASI's proc_exit, abort traps.
        var wat = Wat("#include <string.h>\n#include <stdlib.h>\nint main(void){ char a[8], b[8]; memset(a, 'x', 8); memcpy(b, a, 8); if (b[7] != 'x') abort(); exit(b[0]); }");
        wat.ShouldContain("memory.fill");
        wat.ShouldContain("memory.copy");
        wat.ShouldContain("(import \"wasi_snapshot_preview1\" \"proc_exit\" (func $proc_exit (param i32)))");
        wat.ShouldContain("call $proc_exit\n    unreachable");
        wat.ShouldNotContain("(func $memcpy");
    }

    [Fact]
    public void Assert_tests_its_condition_by_type_and_a_void_conditional_runs_for_effect()
    {
        // assert(d) on a double tests d != 0.0 (an int conversion would fail on 0.5) and traps;
        // a void `?:` is an if/else with no value; sqrt and floor are wasm instructions.
        var wat = Wat("#include <assert.h>\n#include <math.h>\nint n;\nvoid bump(int *p){ *p += 1; }\n"
            + "int main(void){ double d = 0.5; assert(d); n == 0 ? bump(&n) : (void)0; return n * 100 + (int)sqrt(16.0) * 10 + (int)floor(2.7); }");
        wat.ShouldContain("f64.ne\n    i32.eqz\n    if\n      unreachable\n    end");
        wat.ShouldContain("    if\n      i32.const 1024\n      call $bump");
        wat.ShouldContain("f64.sqrt");
        wat.ShouldContain("f64.floor");
        wat.ShouldNotContain("(func $sqrt");
    }

    [Fact]
    public void A_pointer_takes_eight_bytes_in_memory()
    {
        // C's LP64 view (sizeof(void*) == 8, the layout model's) holds on wasm32 too: a
        // pointer in memory is the i32 address zero-extended to eight bytes, so an array of
        // pointers steps by 8 and sizeof agrees with the storage.
        var wat = Wat("int main(void){ char *n[3] = {\"a\", \"bb\", \"ccc\"}; return (int)((char *)&n[1] - (char *)&n[0]) + n[2][1]; }");
        wat.ShouldContain("i64.extend_i32_u i64.store");
        wat.ShouldContain("i64.load i32.wrap_i64");
        wat.ShouldContain("i32.const 8\n");   // the element step
    }

    [Fact]
    public void An_extern_declaration_reaches_the_global_another_unit_defines()
    {
        // `extern int g;` in one unit and `int g = 5;` in another are two symbols with one
        // name: both are the one address in the data area.
        var a = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{System.Guid.NewGuid():N}.c");
        var b = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(a, "int g = 5;\nvoid bump(void) { g++; }\n");
        File.WriteAllText(b, "extern int g;\nvoid bump(void);\nint main(void) { bump(); return g; }\n");
        try
        {
            var wat = Compiler.EmitWat(new[] { a, b });
            wat.ShouldContain("i32.const 1024\n    i32.const 5\n    i32.store");
            wat.ShouldContain("(func $main (result i32)\n    call $bump\n    i32.const 1024\n    i32.load");
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void Two_units_statics_of_one_name_are_two_objects()
    {
        // musl's __sin.c and __sindf.c both define `static const double S1`: each unit reads
        // its own, at its own address (one shared slot made sin() wrong from the 10th digit).
        var a = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{System.Guid.NewGuid():N}.c");
        var b = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(a, "static const double K = 1.5;\ndouble fa(void) { return K; }\n");
        File.WriteAllText(b, "static const double K = 2.5;\ndouble fb(void) { return K; }\nint main(void) { return fb() > 0; }\n");
        try
        {
            var wat = Compiler.EmitWat(new[] { a, b });
            wat.ShouldContain("(func $fa (result f64)\n    i32.const 1024\n    f64.load");
            wat.ShouldContain("(func $fb (result f64)\n    i32.const 1032\n    f64.load");
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void A_math_function_comes_from_musl_with_the_kernels_it_calls()
    {
        var wat = Wat("#include <math.h>\ndouble f(double x) { return sin(x); }\nint main(void) { return f(0.5) > 0; }");
        wat.ShouldContain("(func $sin ");
        wat.ShouldContain("(func $__sin ");
        wat.ShouldContain("(func $__rem_pio2 ");
        wat.ShouldContain("(func $__rem_pio2_large ");
        wat.ShouldNotContain("(func $exp ");
    }

    [Fact]
    public void A_library_data_object_is_bound_by_its_name()
    {
        // exp reads __exp_data, a table defined by a unit of its own: the library binds that
        // unit for the extern object as it binds a unit for a called function.
        var wat = Wat("#include <math.h>\nint main(void) { return exp(1.0) > 2.0; }");
        wat.ShouldContain("(func $exp ");
        wat.ShouldContain("(func $__init_globals");
    }

    [Fact]
    public void The_library_builds_the_same_whatever_the_program_includes()
    {
        // A library unit's includes find the library's headers and dotcc's, never the
        // program's -I directories, so a user libm.h, math.h or features.h cannot reach musl.
        var dir = Directory.CreateTempSubdirectory("dotcc-wat-inc").FullName;
        var src = Path.Combine(dir, "main.c");
        foreach (var h in new[] { "libm.h", "math.h", "features.h", "stdint.h" })
        {
            File.WriteAllText(Path.Combine(dir, h), $"#error the program's {h}\n");
        }
        File.WriteAllText(src, "double cos(double);\nint main(void) { return cos(0.0) == 1.0; }\n");
        try
        {
            var wat = Compiler.EmitWat(new[] { src }, includeDirs: new[] { dir });
            wat.ShouldContain("(func $__cos ");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_float_literal_too_large_for_its_type_is_infinity()
    {
        // musl's INFINITY is 1e5000f; wat's const grammar rejects an out-of-range literal.
        var wat = Wat("float f(void) { return 1e5000f; }\nfloat g(void) { return 1e39f; }\ndouble h(void) { return 1e999; }\nint main(void) { return f() > h(); }");
        wat.ShouldContain("f32.const inf\n");
        wat.ShouldContain("f64.const inf\n");
        wat.ShouldNotContain("1e5000");
        wat.ShouldNotContain("1e39");
    }

    [Fact]
    public void Copysign_and_fabsl_are_single_instructions()
    {
        var wat = Wat("#include <math.h>\ndouble f(double x, double y) { return copysign(x, y) + fabsl(x); }\nfloat g(float x, float y) { return copysignf(x, y); }\nint main(void) { return f(1.0, -2.0) < 0; }");
        wat.ShouldContain("f64.copysign");
        wat.ShouldContain("f32.copysign");
        wat.ShouldContain("f64.abs");
        wat.ShouldNotContain("call $copysign");
        wat.ShouldNotContain("call $fabsl");
    }

    [Fact]
    public void An_atomic_read_modify_write_is_a_plain_load_and_store()
    {
        // One thread, unshared memory: atomic_fetch_add loads the old value, stores the sum
        // and yields the old, with no call and no wasm threads instruction.
        var wat = Wat("#include <stdatomic.h>\nint main(void) { atomic_int n = 40; int old = atomic_fetch_add(&n, 2); return old + atomic_load(&n); }");
        wat.ShouldContain("i32.add\n    i32.store");
        wat.ShouldNotContain("call $atomic_");
        wat.ShouldNotContain("atomic.rmw");
    }

    [Fact]
    public void An_atomic_load_yields_the_objects_type_not_an_implicit_int()
    {
        // No header declares the atomic generics; the IR gives atomic_load the object's own
        // type, so a long is read whole rather than narrowed to an int.
        var wat = Wat("#include <stdatomic.h>\nlong f(atomic_long *p) { return atomic_load(p); }\nint main(void) { atomic_long v = 5000000000; return f(&v) == 5000000000; }");
        wat.ShouldContain("(func $f (param $p i32) (result i64)");
        wat.ShouldContain("i64.load\n");
        wat.ShouldNotContain("i32.wrap_i64\n    i64.extend_i32_s");
    }

    [Fact]
    public void An_undefined_wasi_prototype_is_a_wasi_import()
    {
        // __wasi_<name>, declared and never defined, is wasi_snapshot_preview1.<name>, typed
        // by the prototype; the memory it writes into is exported.
        var wat = Wat("int __wasi_clock_time_get(unsigned id, unsigned long precision, unsigned long *time);\n"
            + "int main(void) { unsigned long ns; return __wasi_clock_time_get(0, 1, &ns) == 0 && ns > 0; }");
        wat.ShouldContain("(import \"wasi_snapshot_preview1\" \"clock_time_get\" (func $__wasi_clock_time_get (param i32) (param i64) (param i32) (result i32)))");
        wat.ShouldContain("(memory (export \"memory\")");
        wat.ShouldContain("call $__wasi_clock_time_get");
    }

    [Fact]
    public void Time_reads_the_wasi_realtime_clock()
    {
        var wat = Wat("#include <time.h>\nint main(void) { return time(0) > 1000000000; }");
        wat.ShouldContain("(func $time ");
        wat.ShouldContain("\"clock_time_get\"");
    }

    [Fact]
    public void A_binary_literal_is_written_in_decimal()
    {
        // wat reads decimal and 0x hex, not C23's 0b: wat2wasm rejected `i32.const 0b1011`.
        var wat = Wat("int main(void){ return 0b1011 + 0B1; }");
        wat.ShouldContain("i32.const 11");
        wat.ShouldContain("i32.const 1\n");
        wat.ShouldNotContain("0b");
        wat.ShouldNotContain("0B");
    }

    [Fact]
    public void A_renamed_static_is_called_by_its_emitted_name()
    {
        // A static `helper` in one unit and an external `helper` in another: the static
        // one is renamed out of the way, and its unit's call must reach that body.
        var a = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{System.Guid.NewGuid():N}.c");
        var b = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(a, "static int helper(int x) { return x + 1; }\nint use_static(void) { return helper(10); }\n");
        File.WriteAllText(b, "int helper(int a, int b) { return a * b; }\nint use_static(void);\nint main(void) { return use_static() + helper(6, 7); }\n");
        try
        {
            var wat = Compiler.EmitWat(new[] { a, b });
            wat.ShouldContain("(func $helper__1 (param $x i32) (result i32)");
            wat.ShouldContain("call $helper__1");
            wat.ShouldContain("(func $helper (param $a i32) (param $b i32) (result i32)");
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void shift_result_keeps_the_left_operand_width()
    {
        // C99 6.5.7: a shift's type is the promoted LEFT operand's, regardless of the
        // count's type. `int << long` stays i32 — the i64 count is wrapped to i32.
        var wat = Wat("int main(void){ int x = 1; long s = 2; return x << s; }");
        wat.ShouldContain("i32.shl");
        wat.ShouldNotContain("i64.shl");
        wat.ShouldContain("i32.wrap_i64");
    }
}
