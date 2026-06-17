#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the BUFFER arm of the malloc→stack peephole (V1): a
/// sole-declarator <c>char* p = malloc(N)</c> with a compile-time-constant byte
/// count <c>N ≤ 1024</c>, used ONLY via <c>p[i]</c> and <c>free(p)</c>, whose
/// declaration is not inside a loop, is demoted to <c>byte* p = stackalloc
/// byte[N]</c> (a zeroed stack buffer) with the <c>free</c> dropped. The symbol
/// keeps its pointer type, so subscripts are unchanged. The analysis is
/// conservative — every cut below (escape, oversize, non-constant size, in-loop,
/// non-char element, never-freed) keeps the heap form, since a wrong promotion
/// would dangle. The struct arm and its escape rules are exercised in
/// <see cref="CompilerTests"/> (LoweringAndShadowing).
/// </summary>
[Collection("MallocStackalloc")]
public sealed class MallocStackallocTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-msa-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>The user/shell portion of the emitted file — everything before the
    /// spliced DotCC.Libc runtime block, which legitimately *defines* malloc/free.
    /// Use it for "this token must NOT appear" checks.</summary>
    private static string UserPortion(string emitted)
    {
        int i = emitted.IndexOf("// ---- Embedded DotCC.Libc runtime", StringComparison.Ordinal);
        return i < 0 ? emitted : emitted[..i];
    }

    [Fact]
    public void Char_buffer_subscript_only_and_freed_is_promoted_to_stackalloc()
    {
        var src = WriteTemp("""
            int main(void) {
                char *p = malloc(4);
                p[0] = 'a';
                p[1] = 'b';
                p[2] = 'c';
                p[3] = 0;
                int n = p[0] + p[3];
                free(p);
                return n;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Zeroed stack buffer, not a heap allocation.
            emitted.ShouldContain("byte* p = stackalloc byte[4]");
            // Subscripts unchanged (the symbol stays a pointer).
            emitted.ShouldContain("p[0] = 97");
            emitted.ShouldContain("p[3] = 0");
            // The user's malloc and its free are gone. (A bare `malloc(` would
            // false-match a shell *comment* mentioning `malloc(n)` and the runtime's
            // own definition, so use the user's unique constant arg / `free(p)` —
            // neither appears in the trimmed user portion after promotion.)
            emitted.ShouldNotContain("malloc(4)");
            UserPortion(emitted).ShouldNotContain("free(p)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Unsigned_char_buffer_is_also_promoted()
    {
        var src = WriteTemp("""
            int main(void) {
                unsigned char *p = malloc(16);
                p[0] = 200;
                p[15] = 1;
                int n = p[0] + p[15];
                free(p);
                return n;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("byte* p = stackalloc byte[16]");
            emitted.ShouldNotContain("malloc(16)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Buffer_that_escapes_via_return_is_not_promoted()
    {
        // `p` is returned (escapes); a stackalloc would dangle — keep the heap form.
        var src = WriteTemp("""
            char *make(void) {
                char *p = malloc(8);
                p[0] = 'x';
                return p;
            }
            int main(void) { return make()[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            UserPortion(emitted).ShouldNotContain("stackalloc");
            emitted.ShouldContain("malloc(");   // low-level kept
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Buffer_passed_to_a_function_is_not_promoted_in_v1()
    {
        // Passing the bare pointer to any callee is an escape in V1 (no
        // non-capturing-callee allowlist yet — that is the V2 follow-up). Even
        // though strcpy/puts do not retain `p`, dotcc conservatively keeps the heap
        // form rather than assume the callee's capture behavior.
        var src = WriteTemp("""
            #include <string.h>
            #include <stdio.h>
            int main(void) {
                char *p = malloc(8);
                strcpy(p, "hi");
                puts(p);
                free(p);
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            UserPortion(emitted).ShouldNotContain("stackalloc");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Null_checked_buffer_is_not_promoted_in_v1()
    {
        // `if (!p)` reads the bare pointer — an escape in V1. Null-test tolerance is
        // a V2 follow-up (a stackalloc can never be null, so the branch becomes
        // statically dead, which is the accepted semantics of any such promotion).
        var src = WriteTemp("""
            int main(void) {
                char *p = malloc(8);
                if (!p) return 1;
                p[0] = 0;
                free(p);
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            UserPortion(emitted).ShouldNotContain("stackalloc");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Oversized_constant_buffer_is_not_promoted()
    {
        // Above the byte ceiling: malloc(huge) returns NULL (recoverable);
        // stackalloc(huge) overflows the stack (unrecoverable). Keep the heap form.
        var src = WriteTemp("""
            int main(void) {
                char *p = malloc(4096);
                p[0] = 0;
                free(p);
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            UserPortion(emitted).ShouldNotContain("stackalloc");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Non_constant_size_buffer_is_not_promoted()
    {
        // A runtime size is unbounded — the stackalloc could blow the stack. Only a
        // compile-time-constant byte count promotes.
        var src = WriteTemp("""
            int main(int argc, char **argv) {
                char *p = malloc(argc);
                p[0] = 0;
                free(p);
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            UserPortion(emitted).ShouldNotContain("stackalloc");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Buffer_declared_in_a_loop_is_not_promoted()
    {
        // A per-iteration stackalloc leaks a stack frame each pass (CA2014) and is
        // never reclaimed until the method returns — keep the heap form, which
        // free() reclaims each iteration.
        var src = WriteTemp("""
            int main(void) {
                int total = 0;
                for (int i = 0; i < 3; i++) {
                    char *p = malloc(8);
                    p[0] = (char)i;
                    total += p[0];
                    free(p);
                }
                return total;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            UserPortion(emitted).ShouldNotContain("stackalloc");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Non_char_element_buffer_is_not_promoted_in_v1()
    {
        // V1 is char-family only (byte count == element count, no division). A
        // wider element is a clean follow-up.
        var src = WriteTemp("""
            int main(void) {
                int *p = malloc(16);
                p[0] = 1;
                p[1] = 2;
                int n = p[0] + p[1];
                free(p);
                return n;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            UserPortion(emitted).ShouldNotContain("stackalloc");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Never_freed_buffer_is_not_promoted_in_v1()
    {
        // V1 requires a matching free (the alloc/free-pair invariant the struct arm
        // also enforces). A non-escaping leaked buffer is a sound promotion too, but
        // it is left for a follow-up rather than widening the recognizer now.
        var src = WriteTemp("""
            int main(void) {
                char *p = malloc(8);
                p[0] = 7;
                return p[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            UserPortion(emitted).ShouldNotContain("stackalloc");
        }
        finally { File.Delete(src); }
    }
}
