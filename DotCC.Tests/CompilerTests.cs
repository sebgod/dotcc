#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Library-level unit tests for <see cref="Compiler"/>. Each test writes a
/// short C snippet to a temp file, drives <see cref="Compiler.EmitCSharp"/>
/// in-process, and asserts on the returned C# string. No subprocesses.
/// </summary>
[Collection("Compiler")]
public sealed partial class CompilerTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-unit-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void EmitCSharp_minimal_main_emits_unsafe_int_main()
    {
        var src = WriteTemp("""
            int main() { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });

            emitted.ShouldContain("static unsafe int main()");
            emitted.ShouldContain("return 0;");
            // file-based program header so the result is `dotnet run --file`-able
            emitted.ShouldStartWith("#:property AllowUnsafeBlocks=true");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void EmitCSharp_csproj_mode_omits_file_directive()
    {
        var src = WriteTemp("int main() { return 0; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, fileBased: false);
            emitted.ShouldNotContain("#:property");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void EmitCSharp_throws_on_parse_error()
    {
        var src = WriteTemp("int main() { return }"); // missing operand
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("parse failed");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Duffs_device_parses_and_emits_structure()
    {
        // The legendary loop-unrolling trick. Case labels are interleaved
        // into a do-while body inside a switch — `case 7: case 6: …` mixed
        // with code that pre-C99 compilers happily accept. dotcc's grammar
        // handles it (case/default are statement-level labels that can
        // appear anywhere in a switch body).
        //
        // Important: the emit is structurally faithful but C# REJECTS it
        // — both because C# requires case labels at the top of the switch
        // (not inside nested blocks) AND because C# forbids implicit case
        // fall-through (CS0163). Translating Duff's device to runnable C#
        // requires a flat-switch-with-goto-case transformation, which is
        // a known limitation. This test pins down the grammar/emit half:
        // dotcc parses and emits without throwing.
        var src = WriteTemp("""
            void duff_copy(int* dst, int* src, int count) {
                int n = (count + 7) / 8;
                switch (count % 8) {
                case 0: do { *dst = *src; dst = dst + 1; src = src + 1;
                case 7:      *dst = *src; dst = dst + 1; src = src + 1;
                case 6:      *dst = *src; dst = dst + 1; src = src + 1;
                case 5:      *dst = *src; dst = dst + 1; src = src + 1;
                case 4:      *dst = *src; dst = dst + 1; src = src + 1;
                case 3:      *dst = *src; dst = dst + 1; src = src + 1;
                case 2:      *dst = *src; dst = dst + 1; src = src + 1;
                case 1:      *dst = *src; dst = dst + 1; src = src + 1;
                        } while (--n > 0);
                }
            }
            int main() { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Structural assertions — confirms the case-labels-inside-do-while
            // shape made it through parse + emit intact.
            emitted.ShouldContain("switch (count % 8)");
            emitted.ShouldContain("case 0:");
            emitted.ShouldContain("case 7:");
            emitted.ShouldContain("case 1:");
            emitted.ShouldContain("do");
            emitted.ShouldContain("while (Cond.B(");
        }
        finally { File.Delete(src); }
    }

    // ---- Negative tests: invalid programs must be REJECTED ---------------
    // Each verifies that semantically-invalid C — well-formed at the parse
    // level but contradictory at the type-resolution level — fails with a
    // CompileException naming the actual user-typed keywords.

    [Fact]
    public void Conflicting_signedness_throws()
    {
        var src = WriteTemp("int main() { signed unsigned int x = 0; return x; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("cannot combine `signed` and `unsigned`");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Duplicate_unsigned_throws()
    {
        var src = WriteTemp("int main() { unsigned unsigned int x = 0; return x; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("duplicate `unsigned`");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Multiple_base_types_throws()
    {
        var src = WriteTemp("int main() { int float x = 0; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("multiple base types");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Conflicting_size_modifiers_throws()
    {
        var src = WriteTemp("int main() { short long x = 0; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("cannot combine `short` and `long`");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Triple_long_throws()
    {
        var src = WriteTemp("int main() { long long long x = 0; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("more than two `long`");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Bool_combined_with_other_specifier_throws()
    {
        var src = WriteTemp("int main() { unsigned _Bool x = 0; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("`_Bool` cannot be combined");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Long_double_lowers_to_double()
    {
        // `long double` → C# `double` (the CLI's widest IEEE float), mirroring
        // `long long` → `long`. A documented narrowing vs. wider native ABIs.
        var src = WriteTemp("int main() { long double x = 1.5; return (int)x; }");
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("double x = 1.5;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Long_long_double_is_rejected()
    {
        // C allows only a single `long` on `double`; `long long double` is invalid.
        var src = WriteTemp("int main() { long long double x = 0; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("`long long double` is not a valid type");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Short_double_is_rejected()
    {
        var src = WriteTemp("int main() { short double x = 0; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("`double` cannot take sign or `short` modifiers");
        }
        finally { File.Delete(src); }
    }

    // ---- char array initialized from a string literal -------------------

    [Fact]
    public void Char_array_string_init_emits_mutable_byte_copy()
    {
        var src = WriteTemp("int main() { char s[] = \"hi\"; s[0] = 'H'; return s[0]; }");
        try
        {
            // mutable stackalloc copy: 'h','i',NUL
            Compiler.EmitCSharp(new[] { src }).ShouldContain("stackalloc byte[]{ 104, 105, 0 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Char_array_string_init_sized_zero_pads()
    {
        var src = WriteTemp("int main() { char buf[5] = \"hi\"; return buf[4]; }");
        try
        {
            // size 5: 'h','i',NUL, then zero-padded to 5
            Compiler.EmitCSharp(new[] { src }).ShouldContain("stackalloc byte[]{ 104, 105, 0, 0, 0 }");
        }
        finally { File.Delete(src); }
    }

    // ---- auto (C23 inference + pre-C23 storage class) -------------------

    [Fact]
    public void Auto_type_inference_lowers_to_var()
    {
        // The typed IR resolves `auto` to the concrete inferred type rather than
        // emitting `var`. `auto x = 5` → `int x = 5`; `auto y = 3.14` → `double y = 3.14`.
        var src = WriteTemp("int main() { auto x = 5; auto y = 3.14; return x; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int x = 5");
            emitted.ShouldContain("double y = 3.14");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Auto_storage_class_is_dropped()
    {
        // Pre-C23 `auto int x` — redundant storage class, dropped → `int x`.
        var src = WriteTemp("int main() { auto int z = 7; return z; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int z = 7");
            emitted.ShouldNotContain("var z");   // not the inference form
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Auto_inference_gated_as_c23_under_pedantic()
    {
        var src = WriteTemp("int main() { auto x = 5; return x; }");
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17"), pedanticErrors: true))
                .Message.ShouldContain("`auto` type inference");
        }
        finally { File.Delete(src); }
    }

    // ---- restrict (and qualifier after *) -------------------------------

    [Fact]
    public void Restrict_qualifier_is_dropped_after_star()
    {
        var src = WriteTemp("""
            void copy(int *restrict dst, const int *restrict src, int n) {
                for (int i = 0; i < n; i++) dst[i] = src[i];
            }
            int main() { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Both restrict qualifiers dropped; `const int *restrict` → `int*`.
            emitted.ShouldContain("void copy(int* dst, int* src, int n)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Const_after_star_parses_and_drops()
    {
        var src = WriteTemp("int main() { int x = 1; int * const p = &x; return *p; }");
        try
        {
            // The typed IR emits the initializer without an extra paren layer.
            Compiler.EmitCSharp(new[] { src }).ShouldContain("int* p = &x");
        }
        finally { File.Delete(src); }
    }

    // ---- function-pointer declarator + unnamed params ------------------

    [Fact]
    public void Fnptr_local_declarator_lowers_to_delegate_ptr()
    {
        // `int (*op)(int, int) = add;` → `delegate*<int, int, int> op = &add;`
        // (return type last in C#; bare function name gets the C# `&`).
        var src = WriteTemp("""
            int add(int a, int b) { return a + b; }
            int main() { int (*op)(int, int) = add; return op(2, 3); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("delegate*<int, int, int> op = &add");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Fnptr_parameter_lowers_to_delegate_ptr_and_decays_bare_arg()
    {
        var src = WriteTemp("""
            int add(int a, int b) { return a + b; }
            int apply(int (*op)(int, int), int x, int y) { return op(x, y); }
            int main() { return apply(add, 2, 3); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // fn-ptr param → delegate*; the pointed-to type's params don't leak
            emitted.ShouldContain("apply(delegate*<int, int, int> op, int x, int y)");
            // bare function-name arg decays to its address
            emitted.ShouldContain("apply(&add, 2, 3)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Unnamed_parameters_are_synthesized()
    {
        // C allows abstract (unnamed) params; C# needs names — synthesize them.
        var src = WriteTemp("""
            int f(int, int) { return 0; }
            int main() { return f(1, 2); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int f(int _p");   // synthesized param names
        }
        finally { File.Delete(src); }
    }

    // ---- bit-fields -----------------------------------------------------

    [Fact]
    public void Bitfield_packs_same_size_fields_into_one_unit()
    {
        var src = WriteTemp("""
            struct Flags { unsigned ready : 1; unsigned : 2; unsigned mode : 3; };
            int main() { struct Flags f; f.ready = 1; f.mode = 5; return f.mode; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Both same-size named bit-fields share ONE backing field (`__bf0`):
            // packed MSVC-style so sizeof + offsets match C.
            emitted.ShouldContain("private uint __bf0;");
            emitted.ShouldNotContain("__bf1");               // everything fits in one unit
            emitted.ShouldContain("public uint ready {");
            emitted.ShouldContain("& 1u");                   // 1-bit field mask
            emitted.ShouldContain("public uint mode {");
            emitted.ShouldContain("& 7u");                   // 3-bit field mask
            // The anonymous `: 2;` padding shifts `mode` to bit 3 (no member emitted).
            emitted.ShouldContain("(__bf0 >> 3)");
            emitted.ShouldNotContain("public uint  {");
            emitted.ShouldNotContain("public uint  ;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Bitfield_starts_new_unit_when_field_does_not_fit()
    {
        // a:20 + b:20 = 40 bits > 32, so `b` can't share the first uint — it gets
        // a second storage unit (MSVC: no straddling), making sizeof 8.
        var src = WriteTemp("""
            struct Wide { unsigned a : 20; unsigned b : 20; };
            int main() { struct Wide w; w.a = 1; w.b = 2; return (int)sizeof(struct Wide); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("private uint __bf0;");
            emitted.ShouldContain("private uint __bf1;");    // b spills into a fresh unit
            emitted.ShouldContain("(__bf1 >> 0)");           // b lives at offset 0 of unit 1
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Bitfield_signed_field_sign_extends_on_read()
    {
        var src = WriteTemp("""
            struct S { int delta : 4; };
            int main() { struct S s; s.delta = 13; return s.delta; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // A signed bit-field reads back through an arithmetic shift pair that
            // sign-extends the 4-bit value through int (so 0b1101 reads as -3).
            emitted.ShouldContain("private uint __bf0;");
            emitted.ShouldContain("public int delta {");
            emitted.ShouldContain("<< 28) >> 28");           // sign-extend a 4-bit field in int
        }
        finally { File.Delete(src); }
    }

    // ---- nested-brace aggregate initializers ----------------------------

    [Fact]
    public void Nested_array_init_flattens_with_zero_fill()
    {
        var src = WriteTemp("int main() { int part[2][3] = {{1},{4,5}}; return part[1][2]; }");
        try
        {
            // partial nested → per-row zero-fill: {1,0,0, 4,5,0}
            Compiler.EmitCSharp(new[] { src }).ShouldContain("stackalloc int[]{ 1, 0, 0, 4, 5, 0 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Flat_init_of_multidim_array_works()
    {
        // brace elision: a flat list fills row-major.
        var src = WriteTemp("int main() { int m[2][3] = {1,2,3,4,5,6}; return m[1][2]; }");
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("stackalloc int[]{ 1, 2, 3, 4, 5, 6 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Struct_array_init_maps_each_group_to_new_struct()
    {
        var src = WriteTemp("""
            struct P { int x; int y; };
            int main() { struct P pts[2] = {{10,20},{30,40}}; return pts[1].x; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src })
                .ShouldContain("stackalloc P[]{ new P { x = 10, y = 20 }, new P { x = 30, y = 40 } }");
        }
        finally { File.Delete(src); }
    }

    // ---- pointer-to-array declarator ------------------------------------

    [Fact]
    public void Ptr_to_array_lowers_to_flat_pointer_with_stride()
    {
        var src = WriteTemp("""
            int main() {
                int a[2][3];
                int (*p)[3] = a;
                return p[1][2];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int* p = a");          // flat pointer
            emitted.ShouldContain("(p + 1 * 3)[2]");      // strides by the array size
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Ptr_to_array_sizeof_is_pointer_size()
    {
        var src = WriteTemp("int main() { int a[2][3]; int (*p)[3] = a; return (int)sizeof(p); }");
        try
        {
            // pointer, not array → sizeof(int*); deref decays to the base pointer
            Compiler.EmitCSharp(new[] { src }).ShouldContain("sizeof(int*)");
        }
        finally { File.Delete(src); }
    }

    // ---- multi-dimensional arrays ---------------------------------------

    [Fact]
    public void Multidim_array_flattens_to_one_stackalloc()
    {
        var src = WriteTemp("int main() { int a[2][3]; a[0][0] = 1; return a[0][0]; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int* a = stackalloc int[6]");   // 2*3 flattened
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Multidim_subscript_uses_flat_pointer_arithmetic()
    {
        // a[i][j] → (a + i*stride)[j], stride = inner dimension.
        var src = WriteTemp("int main() { int a[2][3]; int i=1,j=2; return a[i][j]; }");
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("(a + i * 3)[j]");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Multidim_array_needs_constant_dimensions()
    {
        var src = WriteTemp("int main() { int n = 3; int a[2][n]; a[0][0]=1; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("non-constant dimension");
        }
        finally { File.Delete(src); }
    }

    // ---- string / char escapes + adjacent concatenation ----------------

    [Fact]
    public void Octal_and_hex_char_escapes_decode_to_byte_value()
    {
        // The typed IR assigns the decoded value directly to the `byte` local;
        // no redundant `(byte)` cast is needed at the initializer.
        var src = WriteTemp("int main() { char a = '\\033'; char b = '\\x41'; return a + b; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("byte a = 27");   // \033 octal decoded
            emitted.ShouldContain("byte b = 65");   // \x41 hex decoded
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Adjacent_string_literals_concatenate()
    {
        var src = WriteTemp("""
            int puts(char *s);
            int main() { char *s = "Hello, " "world" "!"; return puts(s); }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("L(\"Hello, world!\\0\"u8)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Octal_string_escapes_emit_greedy_safe_hex()
    {
        // C# u8 has no octal escape, and its \x is greedy — so octal escapes
        // become \-delimited \xHH (each followed by the next \x, never a digit).
        var src = WriteTemp("int puts(char*s); int main() { return puts(\"\\063\\064\\065\"); }");
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("L(\"\\x33\\x34\\x35\\0\"u8)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void High_byte_string_escape_lowers_to_byte_array()
    {
        // A decoded escape byte > 0x7F can't be one byte in a C# u8 literal (C#
        // UTF-8-encodes \x80+ into two bytes), so dotcc emits the string as a
        // constant byte-array preserving the exact C bytes. Roslyn RVA-optimizes
        // `new byte[]{consts}` in ReadOnlySpan position, so L() still pins a
        // fixed-address pointer. Mixed escape + ASCII, NUL-terminated. (`Z`
        // separates the escape from ASCII: C's `\x` is greedy and would fold a
        // trailing hex digit into the escape, so a non-hex char is used here.)
        var src = WriteTemp("int puts(char*s); int main() { return puts(\"\\xffZ\"); }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("L(new byte[]{ 0xFF, 0x5A, 0 })");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Octal_high_byte_string_escape_lowers_to_byte_array()
    {
        // `\377` (octal 255) is also > 0x7F — same byte-array lowering.
        var src = WriteTemp("int puts(char*s); int main() { return puts(\"\\377\"); }");
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("L(new byte[]{ 0xFF, 0 })");
        }
        finally { File.Delete(src); }
    }

    // ---- extern -----------------------------------------------------------

    [Fact]
    public void Extern_variable_declaration_emits_no_field()
    {
        // `extern int x;` declares without defining — no storage emitted. The
        // real definition (`int x = 5;`) emits the single field.
        var src = WriteTemp("""
            extern int x;
            int x = 5;
            int main() { return x; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // exactly one field for x (from the definition, not the extern decl)
            var fieldCount = emitted.Split("unsafe int x").Length - 1;
            fieldCount.ShouldBe(1);
            emitted.ShouldContain("int x = 5");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Extern_function_prototype_emits_nothing()
    {
        // `extern int f(int);` is a prototype — emits nothing (C# methods hoist).
        var src = WriteTemp("""
            extern int f(int x);
            int f(int x) { return x + 1; }
            int main() { return f(41); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int f(int x)");
            // only one definition of f, not a stray prototype artifact
            (emitted.Split("int f(int x)").Length - 1).ShouldBe(1);
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Extern_function_definition_emits_the_function()
    {
        // `extern T f(...) { ... }` — extern is the default function linkage, so
        // this is a normal definition.
        var src = WriteTemp("""
            extern int twice(int x) { return x * 2; }
            int main() { return twice(21); }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("static unsafe int twice(int x)");
        }
        finally { File.Delete(src); }
    }

    // ---- comma operator -------------------------------------------------

    [Fact]
    public void Comma_operator_value_form_lowers_to_tuple()
    {
        // The typed IR LIFTS value-context comma operators to sequential
        // statements rather than emitting a C# tuple. `int x = (a=1, b=2, a+b)`
        // lowers to: `a = 1;` / `b = 2;` / `int x = a + b;` — the last operand
        // becomes the actual initializer.
        var src = WriteTemp("int main() { int a=0,b=0; int x = (a = 1, b = 2, a + b); return x; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Side-effect subexpressions lifted to statements:
            emitted.ShouldContain("a = 1;");
            emitted.ShouldContain("b = 2;");
            // The final value forms the initializer:
            emitted.ShouldContain("int x = a + b;");
            // Tuple form never produced:
            emitted.ShouldNotContain(".Item3");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Comma_operator_statement_form_splits_into_statements()
    {
        // `a = 1, b = 2;` (result discarded) → two sequential statements.
        var src = WriteTemp("int main() { int a=0,b=0; a = 1, b = 2; return a + b; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("a = 1;");
            emitted.ShouldContain("b = 2;");
            emitted.ShouldNotContain(".Item2");   // not the tuple form in stmt position
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Call_argument_commas_stay_separators()
    {
        // A comma in a call argument list is a separator, NOT the comma
        // operator — `f(a, b)` is two args, no tuple.
        var src = WriteTemp("""
            int add(int x, int y) { return x + y; }
            int main() { return add(2, 3); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("add(2, 3)");
            emitted.ShouldNotContain(".Item2");
        }
        finally { File.Delete(src); }
    }

}
