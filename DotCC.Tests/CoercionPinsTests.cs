#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// The conversions C performs implicitly (or defines outright) that C# spells out, each met
/// compiling CPython: a void pointer or another function pointer into a function pointer, a
/// function named as a value where only a function-pointer type gives it one, a null pointer as
/// an integer, a negated negative constant, an unsigned constant expression that wraps, an
/// unsigned shift count, the address of a pointer member of a global, and <c>*fp</c>.
/// </summary>
public sealed class CoercionPinsTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-coerce-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_void_pointer_converts_to_a_function_pointer()
    {
        // CPython's `freefunc f = PyType_GetSlot(tp, Py_tp_free);`: gcc converts it (a warning
        // only under -pedantic).
        var emitted = Emit("""
            typedef void (*freefunc)(void *);
            void *get_slot(void);
            int main(void) { freefunc f = get_slot(); return f != 0; }
            """);
        emitted.ShouldContain("delegate*<void*, void> f = (delegate*<void*, void>)(get_slot());");
    }

    [Fact]
    public void A_function_pointer_converts_to_another_function_pointer_type()
    {
        // dynload_shlib.c stores a dlsym'd pointer into its `dl_funcptr`.
        var emitted = Emit("""
            typedef void (*dl_funcptr)(void);
            typedef int (*init_fn)(int);
            int main(void) { init_fn g = 0; dl_funcptr p = (dl_funcptr)g; return p == 0; }
            """);
        emitted.ShouldContain("(delegate*<void>)");
    }

    [Fact]
    public void A_function_compared_with_a_function_pointer_takes_its_pointer_type()
    {
        // typeobject.c's `tp->tp_iternext != &_PyObject_NextNotImplemented`.
        var emitted = Emit("""
            static int next_none(int x) { return x; }
            int main(void) { int (*fp)(int) = next_none; return fp != &next_none; }
            """);
        emitted.ShouldContain("(delegate*<int, int>)(&next_none)");
    }

    [Fact]
    public void A_function_in_a_variadic_tail_travels_as_a_pointer()
    {
        // Py_BuildValue's "O&" converter argument.
        var emitted = Emit("""
            static int conv(int x) { return x; }
            int build(const char *fmt, ...);
            int main(void) { return build("O&", conv); }
            """);
        emitted.ShouldContain("(void*)(delegate*<int, int>)&conv");
    }

    [Fact]
    public void A_null_pointer_converted_to_an_integer_is_zero()
    {
        // obmalloc.c's `(uintptr_t)NULL`.
        var emitted = Emit("""
            #include <stddef.h>
            #include <stdint.h>
            int main(void) { uintptr_t z = (uintptr_t)NULL; return (int)z; }
            """);
        emitted.ShouldContain("= (ulong)0;");
    }

    [Fact]
    public void A_negated_negative_constant_is_not_a_decrement()
    {
        var emitted = Emit("int main(void) { return -(-5) - 5; }");
        emitted.ShouldContain("-(-5)");
        emitted.ShouldNotContain("--5");
    }

    [Fact]
    public void An_unsigned_constant_expression_that_wraps_is_unchecked()
    {
        // longobject.c's `(unsigned long)0 - (unsigned long)LONG_MIN`: C wraps unsigned
        // arithmetic, where C# rejects the constant's overflow (CS0220). One that fits is
        // left alone.
        var emitted = Emit("""
            int main(void) {
                unsigned long wraps = (unsigned long)0 - (unsigned long)1;
                unsigned long fits = (unsigned long)3 - (unsigned long)1;
                return (int)(wraps + fits);
            }
            """);
        emitted.ShouldContain("wraps = unchecked(");
        emitted.ShouldNotContain("fits = unchecked(");
    }

    [Fact]
    public void An_unsigned_shift_count_in_a_compound_shift_is_an_int()
    {
        // longobject.c's `accum >>= remshift` with an unsigned count.
        var emitted = Emit("""
            int main(void) { unsigned long accum = 256; unsigned int n = 4; accum >>= n; return (int)accum; }
            """);
        emitted.ShouldContain("accum >>= (int)(n);");
    }

    [Fact]
    public void The_address_of_a_pointer_member_of_a_global_goes_through_its_struct()
    {
        // Unsafe.AsPointer cannot take a pointer type argument (CS0306).
        var emitted = Emit("""
            struct Box { int *p; int n; };
            static struct Box box;
            int main(void) { int **pp = &box.p; return pp != 0; }
            """);
        emitted.ShouldContain("&((Box*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref box))->p");
    }

    [Fact]
    public void Dereferencing_a_function_pointer_designates_the_function_it_points_at()
    {
        // `iternext = *tp->tp_iternext;` stores the pointer itself (C11 6.5.3.2p4, 6.3.2.1p4).
        var emitted = Emit("""
            static int one(void) { return 1; }
            int main(void) { int (*fp)(void) = one; int (*q)(void) = *fp; return q(); }
            """);
        emitted.ShouldContain("delegate*<int> q = fp;");
    }
}
