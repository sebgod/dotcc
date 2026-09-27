#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the declaration- and scope-aware lexer hack in
/// <see cref="TypeNameRewriter"/> (GH #215, #216). A typedef's alias is found
/// structurally, and a typedef name reused as a parameter, local, member or
/// enumerator is an ordinary identifier where C says so. Every shape here is
/// from the CPython 3.13 headers. End-to-end behavior is in the
/// <c>typedef-name-reuse/</c> fixture.
/// </summary>
[Collection("TypedefNameScope")]
public sealed class TypedefNameScopeTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-tns-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_typedef_of_a_struct_with_a_function_pointer_member_names_the_typedef()
    {
        // pymem.h's PyMemAllocatorEx: the function-pointer member's name used to
        // win as the alias (GH #215), so `Alloc *a` below failed to parse.
        var emitted = Emit("""
            typedef struct {
                void *ctx;
                void (*release)(void *ctx, void *ptr);
            } Alloc;
            int get(Alloc *a) { return a->ctx == 0; }
            int main(void) { Alloc a = { 0, 0 }; return get(&a); }
            """);
        emitted.ShouldContain("int get(Alloc* a)");
    }

    [Fact]
    public void A_typedef_name_is_a_valid_parameter_name_throughout_the_function()
    {
        // pycore_bytesobject.h: `PyObject *string` with pycore_asdl.h's `string`
        // typedef in scope.
        var emitted = Emit("""
            typedef int string;
            static int first(string *string) { return string[0]; }
            int main(void) { string s = 4; return first(&s); }
            """);
        emitted.ShouldContain("int first(int* @string)");
        emitted.ShouldContain("return @string[0];");
    }

    [Fact]
    public void A_typedef_name_is_a_valid_local_name_until_the_block_ends()
    {
        // genobject.c: `unaryfunc getter = NULL;` with a `getter` typedef in scope.
        var emitted = Emit("""
            typedef int getter;
            static int shadow(void) { getter getter = 5; getter += 2; return getter; }
            static int after(void) { getter g = 40; return g + 2; }
            int main(void) { return shadow() + after(); }
            """);
        emitted.ShouldContain("int getter = 5;");
        emitted.ShouldContain("getter += 2;");
        emitted.ShouldContain("int g = 40;");
    }

    [Fact]
    public void A_typedef_name_is_a_valid_member_name()
    {
        // capsule.c: `PyCapsule_Destructor destructor;`, then `->destructor`, a
        // designator and offsetof all name the member.
        var emitted = Emit("""
            #include <stddef.h>
            typedef void (*destructor)(int *);
            struct capsule { destructor destructor; int digit; };
            static void noop(int *p) { (void)p; }
            int main(void) {
                struct capsule c = { .destructor = noop, .digit = 1 };
                struct capsule *pc = &c;
                int v = 0;
                pc->destructor(&v);
                return (int)offsetof(struct capsule, destructor);
            }
            """);
        emitted.ShouldContain("pc->destructor(&v);");
        emitted.ShouldContain("destructor = &noop, digit = 1");
    }

    [Fact]
    public void A_parameter_name_in_a_prototype_hides_the_typedef_only_in_the_prototype()
    {
        var emitted = Emit("""
            typedef int string;
            void g(int string);
            string s = 3;
            int main(void) { return s; }
            """);
        emitted.ShouldContain("int s = 3;");
    }

    [Fact]
    public void A_for_init_declaration_hides_the_typedef_in_the_loop_body()
    {
        var emitted = Emit("""
            typedef int digit;
            int main(void) {
                int total = 0;
                for (int digit = 0; digit < 3; digit++) { total += digit; }
                digit d = total;
                return d;
            }
            """);
        emitted.ShouldContain("total += digit;");
        emitted.ShouldContain("int d = total;");
    }

    [Fact]
    public void A_local_that_hides_a_typedef_name_cannot_then_declare_with_it()
    {
        // `T` names the int variable from its declarator to the block's end, so
        // `T x` is not a declaration (gcc: "expected ';' before 'x'").
        var ex = Should.Throw<CompileException>(() => Emit("""
            typedef int T;
            int f(void) { int T = 1; T x = 2; return x; }
            """));
        ex.Message.ShouldContain("line 2");
    }

    [Fact]
    public void A_parameter_that_hides_a_typedef_name_cannot_then_declare_with_it()
    {
        var ex = Should.Throw<CompileException>(() => Emit("""
            typedef int T;
            int f(int T) { T x = 2; return x; }
            """));
        ex.Message.ShouldContain("line 2");
    }
}
