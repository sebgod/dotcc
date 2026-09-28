#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// GH #223: a struct, union or enum definition is a type specifier (C11 6.7.2.1,
/// 6.7.2.2), so it combines with storage classes, qualifiers and declarators
/// (CPython's <c>static const struct X { … } t[] = { … };</c>); a declaration may
/// have no declarators (<c>struct Node { … };</c>, <c>enum { A, B };</c>, a C11
/// anonymous member); and <c>typedef</c> is a storage class over an ordinary
/// declarator list at file and block scope (C11 6.7.1, 6.7.8), a block-scope
/// typedef ending with its block. End-to-end in <c>tag-declarations/</c>
/// (gcc-matched).
/// </summary>
[Collection("Console")]
public sealed class TagDeclarationTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-tags-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    /// <summary>Emit and capture stderr, where warnings land (the unit assembly is
    /// serialized, so swapping the process-global <c>Console.Error</c> is race-free).</summary>
    private static string Stderr(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-tags-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        var prior = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try { Compiler.EmitCSharp(new[] { path }); return sw.ToString(); }
        finally { Console.SetError(prior); File.Delete(path); }
    }

    [Fact]
    public void A_struct_definition_combines_with_storage_class_qualifier_and_declarators()
    {
        var emitted = Emit("""
            static const struct constdef { const char *name; int value; } conf_table[] = { { "a", 1 } };
            int main(void) { return conf_table[0].value; }
            """);
        emitted.ShouldContain("unsafe struct constdef");
        emitted.ShouldContain("public static unsafe constdef* conf_table = Libc.GlobalArrayFrom<constdef>(new constdef[]{ new constdef { name = ");
    }

    [Fact]
    public void An_anonymous_struct_takes_its_typedef_name()
    {
        var emitted = Emit("""
            typedef struct { int x; } Foo, *FooPtr;
            int main(void) { Foo f = { 1 }; FooPtr p = &f; return p->x; }
            """);
        emitted.ShouldContain("unsafe struct Foo");
        emitted.ShouldContain("Foo* p = &f;");
        emitted.ShouldNotContain("__Anon");
    }

    [Fact]
    public void The_typedef_name_never_names_an_aggregate_nested_inside_a_tagged_one()
    {
        // Lua's `typedef union StackValue { … struct { … } tbc; } StackValue;`: the
        // union is tagged, so the nested anonymous struct keeps a synthesized name.
        var emitted = Emit("""
            typedef union StackValue { long val; struct { int lo; unsigned short delta; } tbc; } StackValue;
            int main(void) { StackValue sv; sv.tbc.lo = 7; return sv.tbc.lo; }
            """);
        emitted.ShouldContain("unsafe struct StackValue");
        emitted.ShouldContain(" tbc;");
        emitted.ShouldContain("unsafe struct __Anon");
    }

    [Fact]
    public void A_typedef_takes_a_whole_declarator_list()
    {
        var emitted = Emit("""
            static int add(int a, int b) { return a + b; }
            typedef int Color, *ColorPtr, Row[3], (*Combine)(int, int);
            int main(void) { Color c = 1; ColorPtr p = &c; Row r = { 1, 2, 3 }; Combine f = add; return f(*p, r[2]); }
            """);
        emitted.ShouldContain("int* p = &c;");
        emitted.ShouldContain("int* r = stackalloc int[]{ 1, 2, 3 };");
        emitted.ShouldContain("delegate*<int, int, int> f = &add;");
    }

    [Fact]
    public void A_block_scope_typedef_shadows_to_the_end_of_its_block()
    {
        var emitted = Emit("""
            typedef double scale;
            static int f(void) { typedef int scale; scale h = 3; return h; }
            int main(void) { scale s = 1.5; return f() + (int)s; }
            """);
        emitted.ShouldContain("int h = 3;");
        emitted.ShouldContain("double s = 1.5;");
    }

    [Fact]
    public void An_untagged_enum_declares_plain_constants()
    {
        var emitted = Emit("""
            enum { OP_NOP, OP_LOAD, OP_STORE };
            int main(void) { enum { NONE, SOME } flag = SOME; return OP_STORE + flag; }
            """);
        emitted.ShouldContain("int flag = ");
        emitted.ShouldNotContain("enum __Anon");
    }

    [Fact]
    public void A_typed_enum_defines_at_block_scope()
    {
        // Before GH #223 LALR.CC settled `enum ID :` inside a function by reducing
        // `enum ID` (group order), so this did not parse.
        var emitted = Emit("""
            int main(void) { enum small : unsigned char { S0, S1 }; return S1; }
            """);
        emitted.ShouldContain("enum small : byte");
    }

    [Fact]
    public void A_nested_tagged_member_defines_its_tag_at_file_scope()
    {
        var emitted = Emit("""
            struct outer { struct inner { int v; } in; union { int i; unsigned u; }; int n; };
            int main(void) { struct outer o; struct inner copy; o.in.v = 2; copy = o.in; o.i = 1; return copy.v + o.u; }
            """);
        emitted.ShouldContain("unsafe struct inner");
        emitted.ShouldContain("public inner @in;");
    }

    [Fact]
    public void A_forward_declaration_then_a_definition_with_declarators()
    {
        var emitted = Emit("""
            int main(void) {
                struct node;
                struct node { int key; struct node *next; } second = { 2, 0 }, first = { 1, &second };
                return first.next->key;
            }
            """);
        emitted.ShouldContain("unsafe struct node");
        emitted.ShouldContain("node second = new node { key = 2, next = null }, first = new node { key = 1, next = &second };");
    }

    [Theory]
    [InlineData("int; int main(void) { return 0; }", "useless type name in empty declaration")]
    [InlineData("struct { int a; }; int main(void) { return 0; }", "unnamed struct/union that defines no instances")]
    [InlineData("struct s { int a; int; }; int main(void) { return 0; }", "declaration does not declare anything")]
    public void Declarations_that_declare_nothing_warn(string source, string warning)
        => Stderr(source).ShouldContain(warning);

    [Fact]
    public void An_initialized_typedef_is_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-tags-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, "typedef int T = 3; int main(void) { return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { path })).Message.ShouldContain("typedef 'T' is initialized");
        }
        finally { File.Delete(path); }
    }
}
