#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for the compiler bricks that running Goose's generated C took (docs/plans/goose.md):
/// GNU <c>__attribute__((...))</c> and <c>__thread</c>, <c>#pragma pack</c>, a ternary whose arm
/// needs a statement (an if/else over a temp instead of a closure), and GCC's <c>__atomic_*</c>
/// builtins. End to end in the <c>gnu-attributes/</c>, <c>pragma-pack/</c>,
/// <c>comma-ternary-temp/</c> and <c>posix-mman/</c> fixtures.
/// </summary>
[Collection("Attributes")]
public sealed class GooseSurfaceTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-goose-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    /// <summary>The user code of an emitted program: everything before the spliced runtime.</summary>
    private static string UserCode(string emitted)
    {
        var at = emitted.IndexOf("// ---- Embedded DotCC.Libc runtime", StringComparison.Ordinal);
        return at < 0 ? emitted : emitted[..at];
    }

    [Fact]
    public void Gnu_noreturn_attribute_lowers_to_DoesNotReturn()
    {
        var cs = UserCode(Emit("""
            #include <stdlib.h>
            static __attribute__((noreturn)) void die(int c) { exit(c); }
            __attribute__((__noreturn__)) static void die2(void) { exit(2); }
            int main(void) { if (0) die2(); die(0); }
            """));
        cs.ShouldMatch(@"\[System\.Diagnostics\.CodeAnalysis\.DoesNotReturn\]\s+internal static (unsafe )?void die\(");
        cs.ShouldMatch(@"\[System\.Diagnostics\.CodeAnalysis\.DoesNotReturn\]\s+internal static (unsafe )?void die2\(");
    }

    [Fact]
    public void Gnu_always_inline_lowers_like_inline_and_the_rest_are_accepted()
    {
        var cs = UserCode(Emit("""
            static __attribute__((always_inline)) int twice(int x) { return 2 * x; }
            int __attribute__((format(printf, 1, 2), deprecated, aligned(8))) say(const char *f, ...);
            __attribute__((noinline, unused)) static int one(void) { return 1; }
            int main(void) { return twice(one()) - 2; }
            """));
        cs.ShouldMatch(@"\[MethodImpl\(MethodImplOptions\.AggressiveInlining\)\]\s+internal static (unsafe )?int twice\(");
        cs.ShouldNotContain("__attribute__");
    }

    [Fact]
    public void A_portable_header_that_defines_attribute_away_still_wins()
    {
        // The keyword is promoted after macro expansion, so the usual non-GNU fallback holds.
        var cs = UserCode(Emit("""
            #define __attribute__(x)
            static __attribute__((noreturn)) int f(void) { return 1; }
            int main(void) { return f() - 1; }
            """));
        cs.ShouldNotContain("DoesNotReturn");
    }

    [Fact]
    public void Gnu_thread_is_thread_local()
    {
        var cs = Emit("""
            static __thread int counter;
            int main(void) { return counter; }
            """);
        cs.ShouldContain("[ThreadStatic]");
    }

    [Fact]
    public void Pragma_pack_caps_alignment_in_the_layout_and_the_emitted_struct()
    {
        var cs = UserCode(Emit("""
            #include <stddef.h>
            #pragma pack(push, 1)
            struct P1 { char c; double d; int i; };
            #pragma pack(pop)
            struct Nat { char c; double d; };
            int main(void) { return (int)offsetof(struct P1, i) + (int)sizeof(struct Nat); }
            """));
        cs.ShouldContain("LayoutKind.Sequential, Pack = 1, Size = 13)]\nstruct P1");
        cs.ShouldNotContain("Pack = 1, Size = 16)]\nstruct Nat");
        // offsetof folds through the same capped layout: 9 + 16.
        cs.ShouldContain("Libc.OffsetOfBase");
    }

    [Fact]
    public void Pragma_pack_reaches_a_struct_a_macro_expands_to_and_a_nested_union()
    {
        var cs = UserCode(Emit("""
            #pragma pack(2)
            #define S struct
            S M { char c; int i; };
            typedef struct { char tag; union { double r; char b[9]; } u; } Shape;
            #pragma pack()
            int main(void) { return sizeof(struct M) + sizeof(Shape); }
            """));
        cs.ShouldContain("Pack = 2, Size = 6)]\nstruct M");
        cs.ShouldContain("LayoutKind.Explicit, Pack = 2, Size = 10)]");
    }

    [Fact]
    public void Preprocess_only_keeps_pragma_pack_as_a_directive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-goose-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, "#pragma pack(push, 4)\nstruct S { char c; double d; };\n#pragma pack(pop)\n");
        try
        {
            using var w = new StringWriter();
            Compiler.Preprocess(new[] { path }, w);
            var text = w.ToString();
            text.ShouldContain("\n#pragma pack(push, 4)\n");
            text.ShouldContain("\n#pragma pack(pop)\n");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_ternary_arm_with_a_void_comma_becomes_an_if_else_over_a_temp()
    {
        var cs = UserCode(Emit("""
            void fail(long i);
            long get(long *p, long i, long n) {
                long local = 1;
                long *q = &local;
                return p[(unsigned long)i < (unsigned long)n ? i : (fail(i), 0L)] + *q;
            }
            void fail(long i) { (void)i; }
            int main(void) { long a[2] = {1, 2}; return (int)get(a, 1, 2) - 3; }
            """));
        cs.ShouldContain("long __ct0;");
        cs.ShouldContain("if ((ulong)i < (ulong)n) { __ct0 = i; } else { fail(i); __ct0 = 0L; }");
        cs.ShouldNotContain("System.Func");
    }

    [Fact]
    public void A_ternary_in_a_short_circuit_operand_keeps_the_inline_form()
    {
        // The right operand of && is conditional as a whole, so no temp can be hoisted out of it.
        var cs = UserCode(Emit("""
            void note(void);
            int f(int a, int b) { return a && (b ? (note(), 1) : 0); }
            void note(void) {}
            int main(void) { return f(1, 1) - 1; }
            """));
        cs.ShouldNotContain("__ct0");
        cs.ShouldContain("System.Func");
    }

    [Fact]
    public void Gnu_atomic_builtins_lower_onto_the_Atomic_helpers()
    {
        var cs = UserCode(Emit("""
            int main(void) {
                long counter = 0;
                long a = __atomic_fetch_add(&counter, 2, __ATOMIC_SEQ_CST);
                long b = __atomic_add_fetch(&counter, 3, __ATOMIC_RELAXED);
                __atomic_store_n(&counter, 7, __ATOMIC_RELEASE);
                long expected = 7;
                int ok = __atomic_compare_exchange_n(&counter, &expected, 9, 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
                return (int)(a + b + __atomic_load_n(&counter, __ATOMIC_ACQUIRE)) + ok + __atomic_always_lock_free(8, 0);
            }
            """));
        cs.ShouldContain("Atomic.FetchAdd(ref *(&counter), (long)(2))");
        cs.ShouldContain("Atomic.AddFetch(ref *(&counter), (long)(3))");
        cs.ShouldContain("Atomic.Store(ref *(&counter), (long)(7))");
        cs.ShouldContain("Atomic.CompareExchange(ref *(&counter), ref *(&expected), (long)(9))");
        cs.ShouldContain("Atomic.Load(ref *(&counter))");
        cs.ShouldContain("((CBool)true)");
    }
}
