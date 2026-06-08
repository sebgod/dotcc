#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the C11 <c>_Atomic</c> type-specifier (phase A1). An atomic
/// lvalue of eligible scalar type reads/writes through the seq-cst
/// <c>Atomic.*</c> helpers (Interlocked-backed). The named <c>&lt;stdatomic.h&gt;</c>
/// functions are phase A2. End-to-end behaviour is in the <c>atomic-counter/</c>
/// functional fixture.
/// </summary>
public sealed class AtomicTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-atom-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void atomic_load_and_store()
    {
        var src = WriteTemp("""
            int main(void) { _Atomic int x = 0; x = 5; return x; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int x = 0");                 // init is a plain store (non-atomic)
            emitted.ShouldContain("Atomic.Store(ref x, (int)(5))");
            emitted.ShouldContain("Atomic.Load(ref x)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void atomic_compound_assign_uses_fetch_helper()
    {
        var src = WriteTemp("""
            int main(void) { _Atomic int x = 0; x += 3; return x; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // `x += 3` yields the NEW value → AddFetch.
            emitted.ShouldContain("Atomic.AddFetch(ref x, (int)(3))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void atomic_post_increment_is_fetch_add()
    {
        var src = WriteTemp("""
            int main(void) { _Atomic int x = 0; int old = x++; return old; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // post-inc yields the OLD value → FetchAdd.
            emitted.ShouldContain("Atomic.FetchAdd(ref x, (int)1)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void atomic_paren_form_and_bitwise_or()
    {
        var src = WriteTemp("""
            int main(void) { _Atomic(unsigned) m = 0; m |= 4u; return (int)m; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("uint m = 0");
            emitted.ShouldContain("Atomic.OrFetch(ref m, (uint)(4u))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void atomic_struct_member_through_pointer()
    {
        var src = WriteTemp("""
            struct S { _Atomic long hits; int other; };
            static void add(struct S *s, long n) { s->hits += n; }
            int main(void) { struct S s; s.hits = 0; s.other = 0; add(&s, 4); return (int)s.hits; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Atomic.AddFetch(ref s->hits, (long)(n))");
            emitted.ShouldContain("Atomic.Store(ref s.hits, (long)(0))");
            emitted.ShouldContain("Atomic.Load(ref s.hits)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void atomic_is_c11_gated_under_pedantic()
    {
        var src = WriteTemp("""
            int main(void) { _Atomic int x = 0; return x; }
            """);
        try
        {
            var ex = Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c99"), pedantic: true, pedanticErrors: true));
            ex.Message.ShouldContain("_Atomic");
            ex.Message.ShouldContain("C11");
        }
        finally { File.Delete(src); }
    }

    // ---- <stdatomic.h> generic functions (phase A2) -----------------------

    [Fact]
    public void stdatomic_functions_lower_onto_Atomic_helpers()
    {
        var src = WriteTemp("""
            #include <stdatomic.h>
            int main(void) {
                atomic_int x;
                atomic_init(&x, 1);
                int a = atomic_load(&x);
                atomic_store(&x, 9);
                int b = atomic_fetch_add(&x, 2);
                int c = atomic_exchange(&x, 4);
                return a + b + c;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Atomic.Load(ref *(&x))");
            emitted.ShouldContain("Atomic.Store(ref *(&x), (int)(9))");
            emitted.ShouldContain("Atomic.FetchAdd(ref *(&x), (int)(2))");
            emitted.ShouldContain("Atomic.Exchange(ref *(&x), (int)(4))");
            // atomic_init is a plain (non-atomic) store.
            emitted.ShouldContain("*(&x) = (int)(1)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void compare_exchange_returns_cbool_and_explicit_ignores_order()
    {
        var src = WriteTemp("""
            #include <stdatomic.h>
            int main(void) {
                atomic_int x; atomic_store(&x, 1);
                int e = 1;
                _Bool ok = atomic_compare_exchange_strong_explicit(
                    &x, &e, 2, memory_order_seq_cst, memory_order_relaxed);
                return ok;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // _explicit stripped, the two memory_order args ignored; expected is a ref.
            emitted.ShouldContain("(CBool)Atomic.CompareExchange(ref *(&x), ref *(&e), (int)(2))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void atomic_flag_and_fences()
    {
        var src = WriteTemp("""
            #include <stdatomic.h>
            int main(void) {
                atomic_flag f = ATOMIC_FLAG_INIT;
                _Bool was = atomic_flag_test_and_set(&f);
                atomic_flag_clear(&f);
                atomic_thread_fence(memory_order_seq_cst);
                return was;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(CBool)(Atomic.Exchange(ref *(&f), 1) != 0)");
            emitted.ShouldContain("Atomic.Store(ref *(&f), 0)");
            emitted.ShouldContain("Atomic.ThreadFence()");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void atomic_int_typedef_is_treated_as_atomic()
    {
        var src = WriteTemp("""
            #include <stdatomic.h>
            int main(void) { atomic_int n = 0; n = 5; return n; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // the typedef lowers to the underlying int, and the var is atomic.
            emitted.ShouldContain("int n = 0");
            emitted.ShouldContain("Atomic.Store(ref n, (int)(5))");
            emitted.ShouldContain("Atomic.Load(ref n)");
        }
        finally { File.Delete(src); }
    }
}
