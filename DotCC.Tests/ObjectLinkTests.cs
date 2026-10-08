#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Separate compilation links as C does (GH #226): an object carries its types, and each
/// function and object with its linkage. The linker keeps one copy of a type the objects
/// share, rejects a second external definition, and never merges two units' internal names:
/// every unit has its own `static` function, object and static local.
/// </summary>
public sealed class ObjectLinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dotcc-link-{Guid.NewGuid():N}");

    public ObjectLinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Compile each (file name, source) pair to an object, then link them.</summary>
    private string Link(params (string Name, string Source)[] units) => Compiler.LinkObjects(Objects(units));

    /// <summary>Compile each (file name, source) pair to an object; the object paths.</summary>
    private List<string> Objects((string Name, string Source)[] units)
    {
        var objects = new List<string>();
        foreach (var (name, source) in units)
        {
            var src = Path.Combine(_dir, name);
            File.WriteAllText(src, source);
            var obj = Path.ChangeExtension(src, ".o.cs");
            File.WriteAllText(obj, Compiler.EmitObject(src, includeDirs: new[] { _dir }));
            objects.Add(obj);
        }
        return objects;
    }

    [Fact]
    public void A_struct_both_objects_define_is_emitted_once()
    {
        var program = Link(
            ("a.c", "struct P { int x; }; int side(void) { struct P p; p.x = 1; return p.x; }"),
            ("b.c", "struct P { int x; }; int side(void); int main(void) { return side(); }"));
        Regex.Matches(program, @"struct P\b").Count.ShouldBe(1);
        program.ShouldContain("static int side(");
        program.ShouldContain("return main();");
    }

    [Fact]
    public void Each_object_keeps_its_own_static_function_object_and_static_local()
    {
        var program = Link(
            ("a.c", "static int helper(void) { return 1; } static int counter = 10;\n"
                  + "int tick(void) { static int n; return ++n; }\n"
                  + "int from_a(void) { return helper() + counter + tick(); }"),
            ("b.c", "static int helper(void) { return 2; } static int counter = 20;\n"
                  + "int tock(void) { static int n; return n += 2; }\n"
                  + "int from_a(void);\nint main(void) { return from_a() + helper() + counter + tock(); }"));
        Regex.Matches(program, @"int helper__a_[0-9a-f]{6}\(").Count.ShouldBe(1);
        Regex.Matches(program, @"int helper__b_[0-9a-f]{6}\(").Count.ShouldBe(1);
        Regex.IsMatch(program, @"int counter__a_[0-9a-f]{6} = 10;").ShouldBeTrue();
        Regex.IsMatch(program, @"int counter__b_[0-9a-f]{6} = 20;").ShouldBeTrue();
        Regex.Matches(program, @"int n__s\d+__[ab]_[0-9a-f]{6};").Count.ShouldBe(2);
        // External names are the C names, so the call from b.c reaches a.c's definition.
        program.ShouldContain("return from_a() + helper__b_");
    }

    [Fact]
    public void A_header_struct_with_an_anonymous_union_agrees_across_objects()
    {
        File.WriteAllText(Path.Combine(_dir, "shape.h"), "struct Shape { int kind; union { int side; float radius; }; };\n");
        var program = Link(
            ("a.c", "#include \"shape.h\"\nint side_of(struct Shape* s) { return s->side; }"),
            ("b.c", "#include \"shape.h\"\nint side_of(struct Shape* s);\nint main(void) { struct Shape s; s.side = 3; return side_of(&s); }"));
        Regex.Matches(program, @"struct Shape\b").Count.ShouldBe(1);
        Regex.Matches(program, @"struct __AnonU_[0-9a-f]{16}\b").Count.ShouldBe(1);
    }

    [Fact]
    public void A_tentative_definition_and_the_definition_in_one_object_are_one_object()
    {
        // CPython's shape: a forward `static PyModuleDef m;` that code points at, then the
        // initialized definition (C11 6.9.2p2).
        var program = Link(
            ("a.c", "struct Def { int size; };\nstatic struct Def module;\n"
                  + "struct Def* get(void) { return &module; }\n"
                  + "static struct Def module = { 7 };\nint shared;\nint shared = 3;\n"
                  + "int main(void) { return get()->size + shared; }"));
        Regex.Matches(program, @"Def module__a_[0-9a-f]{6}\b").Count.ShouldBe(1);
        Regex.Matches(program, @"int shared\b").Count.ShouldBe(1);
        program.ShouldContain("int shared = 3;");
    }

    [Fact]
    public void Two_initializers_of_one_object_are_a_redefinition()
        => Should.Throw<CompileException>(() => Link(("a.c", "int x = 1;\nint x = 2;\nint main(void) { return x; }")))
            .Message.ShouldContain("redefinition of 'x'");

    [Fact]
    public void An_objects_array_member_struct_carries_its_init_helper_unused()
    {
        // A type is the same in every object that defines it, so an object renders its init
        // helper even when its own code never needed one (the linker compares the texts).
        var program = Link(
            ("a.c", "struct Buf { int n; char data[4]; };\nint len(struct Buf* b) { return b->n; }"),
            ("b.c", "struct Buf { int n; char data[4]; };\nint len(struct Buf* b);\n"
                  + "int main(void) { struct Buf b = { 2, { 'h', 'i' } }; return len(&b); }"));
        Regex.Matches(program, @"struct Buf\b").Count.ShouldBe(1);
        program.ShouldContain("Buf __dotcc_init(");
    }

    [Fact]
    public void A_struct_no_unit_completes_gets_one_opaque_placeholder()
    {
        // Only ever pointed at (C11 6.7.2.3), as CPython's PyCriticalSection outside a
        // free-threaded build: the pointers still need a C# type.
        var program = Link(
            ("a.c", "typedef struct Lock Lock;\nint held(Lock* l) { return l != 0; }"),
            ("b.c", "typedef struct Lock Lock;\nint held(Lock* l);\nint main(void) { return held(0); }"));
        Regex.Matches(program, @"struct Lock\b").Count.ShouldBe(1);
    }

    [Fact]
    public void A_struct_one_unit_completes_replaces_the_others_placeholder()
    {
        var program = Link(
            ("a.c", "struct Node;\nint count(struct Node* n);\nint main(void) { return count(0); }"),
            ("b.c", "struct Node { int value; struct Node* next; };\n"
                  + "int count(struct Node* n) { return n ? 1 + count(n->next) : 0; }"));
        Regex.Matches(program, @"struct Node\b").Count.ShouldBe(1);
        program.ShouldContain("public int value;");
    }

    [Fact]
    public void A_second_external_definition_is_a_multiple_definition()
        => Should.Throw<CompileException>(() => Link(
                ("a.c", "int twice(void) { return 1; }"),
                ("b.c", "int twice(void) { return 2; } int main(void) { return twice(); }")))
            .Message.ShouldContain("multiple definition of 'twice' in 'b.o.cs', first defined in 'a.o.cs'");

    [Fact]
    public void A_second_external_object_is_a_multiple_definition()
        => Should.Throw<CompileException>(() => Link(
                ("a.c", "int shared;"),
                ("b.c", "int shared; int main(void) { return shared; }")))
            .Message.ShouldContain("multiple definition of 'shared'");

    [Fact]
    public void A_type_two_objects_define_differently_is_rejected()
        => Should.Throw<CompileException>(() => Link(
                ("a.c", "struct P { int x; }; int side(void) { struct P p; p.x = 1; return p.x; }"),
                ("b.c", "struct P { long y; }; int main(void) { struct P p; p.y = 2; return (int)p.y; }")))
            .Message.ShouldContain("type 'P' is defined differently in 'b.o.cs' and 'a.o.cs'");

    [Fact]
    public void Every_objects_array_storage_is_allocated_before_any_objects_initializers()
    {
        // `a.c` takes the address of an array `b.c` defines: the link places b's storage
        // ahead of a's initializer, as the objects' own order would not.
        var program = Link(
            ("a.c", "extern int table[]; int *first = table; int main(void) { return *first; }"),
            ("b.c", "int table[2] = { 5, 6 };"));
        var table = program.IndexOf("int* table = Libc.GlobalArrayFrom<int>(new int[]{ 5, 6 });", StringComparison.Ordinal);
        table.ShouldBeGreaterThanOrEqualTo(0);
        // An external pointer global is an nint slot in an object (every object must agree on it).
        program.IndexOf("nint first = (nint)((int*)table);", StringComparison.Ordinal).ShouldBeGreaterThan(table);
    }

    [Fact]
    public void An_external_function_pointer_global_is_called_through_its_slot()
    {
        // CPython's PyOS_InputHook: the nint slot reads back to its pointer type before the
        // call, and a function stored into it (initializer or assignment) goes through its
        // function-pointer type, since a method group converts to nothing else.
        var program = Link(
            ("a.c", "static int one(void) { return 1; }\nint (*hook)(void) = one;\n"
                  + "int call_hook(void) { return hook ? hook() : -1; }"),
            ("b.c", "extern int (*hook)(void); int call_hook(void);\nstatic int seven(void) { return 7; }\n"
                  + "int main(void) { hook = seven; return call_hook(); }"));
        program.ShouldContain("((delegate*<int>)hook)()");
        Regex.IsMatch(program, @"nint hook = \(nint\)\(delegate\*<int>\)\(&one__a_[0-9a-f]{6}\);").ShouldBeTrue();
        Regex.IsMatch(program, @"hook = \(nint\)\(delegate\*<int>\)\(&seven__b_[0-9a-f]{6}\);").ShouldBeTrue();
    }

    [Fact]
    public void An_object_of_another_format_asks_to_be_recompiled()
    {
        var obj = Objects(new[] { ("a.c", "int main(void) { return 0; }") })[0];
        File.WriteAllText(obj, File.ReadAllText(obj).Replace("//!dotcc object 4", "//!dotcc object 3"));
        Should.Throw<CompileException>(() => Compiler.LinkObjects(new[] { obj }))
            .Message.ShouldContain("format 3; this dotcc links format 4");
    }
}
