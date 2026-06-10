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
public sealed class WatBackendTests
{
    private static string Wat(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{System.Guid.NewGuid():N}.c");
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
    public void printf_with_a_runtime_format_is_rejected()
    {
        // Only a string-literal format can be expanded at compile time.
        Should.Throw<CompileException>(() => Wat("int main(void){ char *f = \"%d\"; printf(f, 1); return 0; }"));
    }

    [Fact]
    public void printf_unsupported_conversions_are_rejected()
    {
        // The '#' flag and floats aren't wired yet — fail loud, don't miscompile.
        Should.Throw<CompileException>(() => Wat("int main(void){ printf(\"%#x\", 255); return 0; }"));
        Should.Throw<CompileException>(() => Wat("int main(void){ printf(\"%f\", 1.5); return 0; }"));
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
        // must be dropped (the literal is typed double / f64 regardless of the suffix).
        var wat = Wat("int main(void){ double x = 1.5f; return (int)x; }");
        wat.ShouldContain("f64.const 1.5");
        wat.ShouldNotContain("1.5f");
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
