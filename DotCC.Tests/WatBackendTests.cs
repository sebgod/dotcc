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
        // printf needs linear memory + host imports (milestone 2): fail loudly.
        Should.Throw<CompileException>(() => Wat("int main(void){ printf(\"hi\"); return 0; }"));
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
    public void address_of_local_is_gated_until_the_shadow_stack()
    {
        // &x of a local needs a linear-memory frame; until that lands it fails loudly
        // rather than miscompiling. (Reading through a pointer already works.)
        Should.Throw<CompileException>(() => Wat("int main(void){ int x=0; int *p=&x; return *p; }"));
    }
}
