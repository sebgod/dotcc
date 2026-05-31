#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

public sealed partial class CompilerTests
{
    // ---- _Static_assert / static_assert -----------------------------------
    // `_Static_assert` is a C11 keyword (always reserved). The C23 lowercase
    // `static_assert` is promoted onto it by the rewriter under -std=c23.
    // Compile-time only: dotcc parses it and drops it to an inert comment.

    [Fact]
    public void Static_assert_file_scope_emits_a_comment_not_a_call()
    {
        // C11 two-arg form at file scope — always a keyword, so no -std needed.
        var src = WriteTemp("""
            _Static_assert(1, "always true");
            int main() { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static_assert (compile-time, not evaluated): \"always true\"");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Static_assert_block_scope_and_message_optional_c23_form()
    {
        // Block scope + the C23 message-less arity. `_Static_assert` is a
        // keyword in every dialect, so this parses even under the default c17.
        var src = WriteTemp("""
            int main() {
                _Static_assert(sizeof(int) >= 2, "int too small");
                _Static_assert(1);
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static_assert (compile-time, not evaluated): \"int too small\"");
            emitted.ShouldContain("static_assert (compile-time, not evaluated) */");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Lowercase_static_assert_is_a_keyword_under_c23()
    {
        // C23 promotes lowercase `static_assert` onto the `_Static_assert`
        // terminal — same comment lowering, no <assert.h> needed.
        var src = WriteTemp("""
            int main() {
                static_assert(1 + 1 == 2, "math works");
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23"));
            emitted.ShouldContain("static_assert (compile-time, not evaluated): \"math works\"");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Lowercase_static_assert_is_a_plain_call_before_c23()
    {
        // Pre-C23 with no <assert.h> macro, `static_assert` is an ordinary
        // identifier: `static_assert(1, "x")` parses as a function-call
        // expression statement, NOT the keyword declaration. The gate didn't
        // promote it, so it is emitted verbatim as a call (not the comment).
        var src = WriteTemp("""
            int main() {
                static_assert(1, "x");
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17"));
            emitted.ShouldContain("static_assert");
            emitted.ShouldNotContain("compile-time, not evaluated");
        }
        finally { File.Delete(src); }
    }

    // ---- malloc/free → stack-value peephole --------------------------------
    // `S* p = (S*)malloc(sizeof(S))` used only via `->` and freed in the same
    // function (no escape) lowers to a stack struct value `S p = new S();`,
    // `->` becomes `.`, and the free() is dropped. Any escaping use disqualifies.

    /// <summary>
    /// The user/shell portion of the emitted file — everything before the
    /// spliced DotCC.Libc runtime block. Use this for "this token must NOT
    /// appear" checks: the runtime legitimately contains <c>-&gt;</c>
    /// (e.g. <c>fp-&gt;_slot</c> in FileLib), which would false-trip a
    /// whole-file substring scan.
    /// </summary>
    private static string UserPortion(string emitted)
    {
        int i = emitted.IndexOf("// ---- Embedded DotCC.Libc runtime", StringComparison.Ordinal);
        return i < 0 ? emitted : emitted[..i];
    }

    [Fact]
    public void Malloc_struct_used_only_via_arrow_and_freed_is_promoted()
    {
        var src = WriteTemp("""
            struct Point { int x; int y; };
            int main() {
                struct Point* p = (struct Point*)malloc(sizeof(struct Point));
                p->x = 3;
                p->y = 4;
                free(p);
                return p->x;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Stack value, not a heap allocation.
            emitted.ShouldContain("Point p = new Point()");
            // Arrow accesses lowered to `.` (the binop wrapper adds parens:
            // `(p.x) = 3`), and the pointer `->` form is gone for this var.
            emitted.ShouldContain("(p.x)");
            emitted.ShouldContain("(p.y)");
            UserPortion(emitted).ShouldNotContain("p->");
            // The user's cast-malloc is gone (the runtime still *defines*
            // malloc/free, but `(Point*)malloc` is unique to user code).
            emitted.ShouldNotContain("(Point*)malloc");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Malloc_struct_that_escapes_via_return_is_not_promoted()
    {
        // `p` is returned (escapes the function), so the stack-value rewrite
        // would dangle — dotcc must keep the low-level heap form.
        var src = WriteTemp("""
            struct Node { int v; struct Node* next; };
            struct Node* make(int v) {
                struct Node* p = (struct Node*)malloc(sizeof(struct Node));
                p->v = v;
                return p;
            }
            int main() { return make(5)->v; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(Node*)malloc");   // low-level kept
            emitted.ShouldContain("p->v");            // arrow kept (pointer)
            emitted.ShouldNotContain("new Node()");   // not promoted
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Cast_less_malloc_gets_an_inserted_pointer_cast()
    {
        // `T* p = malloc(...)` (no `(T*)`) is valid C (void* → T* is implicit) but
        // C# needs the cast. dotcc inserts it when a void*-typed initializer lands
        // in a pointer-typed declaration. Complex-arg malloc (void* call) and an
        // escaping struct malloc (stays a heap pointer) both get the cast.
        var src = WriteTemp("""
            #include <stdlib.h>
            struct Box { int v; };
            struct Box* make(int v) { struct Box* b = malloc(sizeof(struct Box)); b->v = v; return b; }
            int main() { int* a = malloc(4 * sizeof(int)); a[0] = 1; return a[0] + make(2)->v; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int* a = (int*)(");        // void* call cast-inserted
            emitted.ShouldContain("(Box*)(malloc(sizeof(Box)))");  // escaping struct malloc cast-inserted
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Malloc_struct_without_matching_free_is_not_promoted()
    {
        // No free() — the plan requires a matching free in the same function.
        // Without it we keep the heap form (changing lifetime silently would be
        // surprising), so no promotion.
        var src = WriteTemp("""
            struct Box { int n; };
            int main() {
                struct Box* b = (struct Box*)malloc(sizeof(struct Box));
                b->n = 9;
                return b->n;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(Box*)malloc");
            emitted.ShouldNotContain("new Box()");
        }
        finally { File.Delete(src); }
    }

    // ---- static storage duration -------------------------------------------
    // Block-scope `static` locals lower to mangled DotCcGlobals static fields
    // (one instance, persists across calls), with in-function references
    // rewritten to the mangled name. File-scope `static` is a passthrough to a
    // plain global (internal linkage is a no-op for non-exported variables).

    [Fact]
    public void Function_static_lowers_to_mangled_global_field()
    {
        var src = WriteTemp("""
            int next_id(void) {
                static int counter = 0;
                counter++;
                return counter;
            }
            int main() { return next_id(); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The static became a mangled field initialised once...
            emitted.ShouldContain("__static_next_id_counter = 0");
            // ...and in-function references resolve to it.
            emitted.ShouldContain("__static_next_id_counter++");
            // The declaration emits no in-body local of the source name.
            emitted.ShouldNotContain("int counter = 0");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Same_named_function_statics_are_mangled_per_function()
    {
        // Two functions each with `static int counter` must not collide.
        var src = WriteTemp("""
            int a(void) { static int counter = 1; return ++counter; }
            int b(void) { static int counter = 2; return ++counter; }
            int main() { return a() + b(); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("__static_a_counter = 1");
            emitted.ShouldContain("__static_b_counter = 2");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void File_scope_static_lowers_like_a_plain_global()
    {
        // `static` at file scope is internal linkage — a no-op for a variable
        // in dotcc's single-program model; it lowers to a DotCcGlobals field
        // under its own (un-mangled) name, the keyword simply consumed.
        var src = WriteTemp("""
            static int g = 7;
            int main() { return g; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("public static unsafe int g = 7;");
        }
        finally { File.Delete(src); }
    }

    // ---- <threads.h> -------------------------------------------------------
    // thrd_t / mtx_t are seeded type names (Libc structs); thrd_start_t is a
    // function-pointer typedef; calls pass through to the Libc runtime.

    [Fact]
    public void Threads_header_types_and_calls_lower_to_libc()
    {
        var src = WriteTemp("""
            #include <threads.h>
            int worker(void* arg) { return 0; }
            int main() {
                mtx_t mux;
                thrd_t t;
                mtx_init(&mux, mtx_plain);
                thrd_create(&t, &worker, &mux);
                thrd_join(t, 0);
                mtx_destroy(&mux);
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Opaque handles are the seeded Libc value structs, stack-allocated.
            emitted.ShouldContain("thrd_t t");
            emitted.ShouldContain("mtx_t mux");
            // Function name → C# function pointer for thrd_create (the `&`
            // operator wraps each operand in parens).
            emitted.ShouldContain("thrd_create((&t), (&worker), (&mux))");
            // mtx_plain is the <threads.h> macro constant (0).
            emitted.ShouldContain("mtx_init((&mux), 0)");
        }
        finally { File.Delete(src); }
    }

    // ---- C#-keyword identifier escaping ------------------------------------
    // C identifiers that are C# reserved keywords are @-escaped on emit, at
    // both declaration and reference sites (consistent because the escape is a
    // pure function of the name).

    [Fact]
    public void C_identifiers_that_are_csharp_keywords_are_escaped()
    {
        var src = WriteTemp("""
            struct rec { int new; int lock; };
            int object(int ref) { return ref * 2; }
            int main() {
                int new = 10;
                int string = object(new);
                struct rec ev;
                ev.new = new;
                return string + ev.lock;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("@new = 10");                 // keyword local decl
            emitted.ShouldContain("@object(@new)");             // keyword fn call + arg ref
            emitted.ShouldContain("int @object(int @ref)");     // keyword fn name + param decl
            emitted.ShouldContain("@ref * 2");                  // keyword param ref
            emitted.ShouldContain("public int @new;");          // keyword struct field decl
            emitted.ShouldContain("(ev.@new)");                 // keyword member access
            emitted.ShouldContain("int @string =");             // keyword local
        }
        finally { File.Delete(src); }
    }

    // ---- shadowing fixes ---------------------------------------------------

    [Fact]
    public void Local_shadowing_an_enum_constant_resolves_to_the_local()
    {
        // A local/param named like an enum constant must emit the bare local
        // name, NOT EnumName.Member (a const, not an lvalue).
        var src = WriteTemp("""
            enum E { X, Y };
            int f(int X) { return X + Y; }
            int main() {
                int Y = 5;
                Y = Y + 1;
                return f(Y);
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The shadowing local `Y` is assigned (lvalue) — must be bare `Y`,
            // never `E.Y`. The param `X` likewise stays bare inside f.
            emitted.ShouldContain("Y = (Y + 1)");
            emitted.ShouldNotContain("E.Y =");
            // The genuine, un-shadowed enum use `Y` inside f still resolves to
            // the enum constant.
            emitted.ShouldContain("E.Y");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Enum_lowers_to_a_real_csharp_enum_with_int_casts()
    {
        // `enum Color { … }` → a real C# `enum Color : int`; enum↔int flows get
        // the casts C# requires (C lets them mix freely): `int n = c` decays to
        // (int), `c + 1` decays the enum operand, `enum Color e = 2` casts (Color).
        var src = WriteTemp("""
            enum Color { Red, Green, Blue = 5 };
            int main() {
                enum Color c = Green;
                int n = c;
                int m = c + 1;
                enum Color e = 2;
                return n + m + e;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("enum Color : int");     // real C# enum, not const-int
            emitted.ShouldContain("Color c = Color.Green;");// enum = enum, no cast
            emitted.ShouldContain("int n = (int)(c);");     // enum → int decay
            emitted.ShouldContain("int m = ((int)c + 1);"); // enum operand decays in +
            emitted.ShouldContain("Color e = (Color)(2);"); // int → enum cast
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void C23_enum_with_underlying_type_maps_the_base()
    {
        // C23 `enum Name : Type` → C# `enum Name : <mapped base>` (unsigned char → byte).
        var src = WriteTemp("enum Color : unsigned char { Red, Green, Blue = 200 };\nint main() { return 0; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23"));
            emitted.ShouldContain("enum Color : byte");
            emitted.ShouldContain("Blue = 200,");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Local_shadowing_a_libc_builtin_name_is_a_plain_call()
    {
        // A function-pointer local named like a libc builtin must be called as
        // an ordinary call, not lowered as the builtin. `setjmp` is the sharp
        // case: without the guard its special-case returns a SetjmpCall marker
        // that throws when used outside an if/else — so a plain `return
        // setjmp(x)` would fail to emit at all.
        var src = WriteTemp("""
            typedef int (*fn)(int a);
            int dbl(int x) { return x * 2; }
            int main() {
                fn setjmp = &dbl;
                return setjmp(21);
            }
            """);
        try
        {
            EmitContentShouldBePlainCall(src);
        }
        finally { File.Delete(src); }
    }

    private static void EmitContentShouldBePlainCall(string src)
    {
        var emitted = Compiler.EmitCSharp(new[] { src });  // must not throw
        emitted.ShouldContain("setjmp(21)");
        emitted.ShouldContain("return setjmp(21);");
    }

    [Fact]
    public void Malloc_promote_interoperates_with_keyword_escaped_var_name()
    {
        // A promotable malloc'd pointer named with a C# keyword (`new`): the
        // promoted decl, the `.` accesses, and the dropped free must all agree
        // on the @-escaped name — the peephole maps are keyed by the raw name.
        var src = WriteTemp("""
            struct S { int x; };
            int main() {
                struct S* new = (struct S*)malloc(sizeof(struct S));
                new->x = 7;
                free(new);
                return new->x;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("S @new = new S()");  // promoted + escaped
            emitted.ShouldContain("(@new.x)");          // arrow -> dot, escaped
            UserPortion(emitted).ShouldNotContain("@new->"); // no pointer arrow left
            emitted.ShouldNotContain("S new = new S()"); // raw name never emitted
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Variadic_macro_with_named_params_plus_extras()
    {
        // Named param `level` + variadic extras. `level` substitutes
        // by name; the extras land in `__VA_ARGS__`.
        var src = WriteTemp("""
            #define WARN(level, ...) printf("[%d] ", level); printf(__VA_ARGS__)
            int main() {
                WARN(7, "x=%d\n", 42);
                return 0;
            }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            dumped.ShouldContain(" 7 ");
            dumped.ShouldContain(" 42 ");
            dumped.ShouldNotContain("__VA_ARGS__");
            dumped.ShouldNotContain("WARN(");
        }
        finally { File.Delete(src); }
    }

    // ---- block-scope local shadow renaming (CS0136 avoidance) -----------

    [Fact]
    public void Shadowed_local_in_nested_then_enclosing_scope_is_renamed_apart()
    {
        // C lets a `v` in the inner block and a separate `v` in the function
        // body coexist (the inner reduces FIRST), but C# rejects the pair as
        // CS0136. dotcc keeps the first-seen `v` and renames the later one.
        var src = WriteTemp("""
            int f(int c) {
                if (c) { int v = 1; return v; }
                int v = 2;
                return v;
            }
            int main() { return f(0); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Inner block keeps `v`; the function-body decl is renamed to v__1,
            // and its `return` resolves to the renamed name.
            emitted.ShouldContain("int v = 1");
            emitted.ShouldContain("int v__1 = 2");
            emitted.ShouldContain("return v__1;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Nested_local_shadowing_a_param_renames_the_local_not_the_param()
    {
        // A param `x` and a nested-block local `x` — valid C, CS0136 in C#.
        // The param keeps its spelling (signature unchanged); the inner local
        // is the one renamed, and references resolve to the right binding.
        var src = WriteTemp("""
            int f(int x) {
                { int x = 9; return x; }
            }
            int main() { return f(3); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int f(int x)");   // param keeps its name
            emitted.ShouldContain("int x__1 = 9");    // inner local renamed
            emitted.ShouldContain("return x__1;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Unshadowed_locals_keep_their_names()
    {
        // No collision → no renaming. Distinct names stay verbatim.
        var src = WriteTemp("""
            int main() {
                int a = 1;
                int b = 2;
                return a + b;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int a = 1");
            emitted.ShouldContain("int b = 2");
            emitted.ShouldNotContain("a__1");
            emitted.ShouldNotContain("b__1");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Fnptr_typedef_params_do_not_leak_into_next_function()
    {
        // Regression: a `typedef int (*Cmp)(int a, int b);` runs the Param
        // visitors for a/b, but a typedef has no function scope to adopt them.
        // They must not leak into the next function's parameter scope (which
        // would rename its real `a`/`b` params and dangle their references).
        var src = WriteTemp("""
            typedef int (*Cmp)(int a, int b);
            int ascending(int a, int b) { return a - b; }
            int main() { return ascending(2, 5); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int ascending(int a, int b)");
            emitted.ShouldContain("return (a - b);");
            emitted.ShouldNotContain("a__1");
            emitted.ShouldNotContain("b__1");
        }
        finally { File.Delete(src); }
    }
}
