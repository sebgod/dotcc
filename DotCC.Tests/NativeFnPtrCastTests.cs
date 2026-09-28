#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// With <c>&lt;dlfcn.h&gt;</c> in scope, a cast of a <c>void*</c> value to a function-pointer type warns, since the
/// pointer may have come from <c>dlsym</c> and the managed call would use the wrong calling convention. A null pointer
/// holds no code address: CPython's <c>(destructor)NULL</c> slots must not warn.
/// </summary>
[Collection("Console")]
public sealed class NativeFnPtrCastTests
{
    /// <summary>What compiling <paramref name="body"/> writes to stderr (its warnings).</summary>
    private static string Stderr(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-fnptr-cast-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        var prior = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            Compiler.EmitCSharp(new[] { path });
            return sw.ToString();
        }
        finally
        {
            Console.SetError(prior);
            File.Delete(path);
        }
    }

    [Fact]
    public void A_null_pointer_cast_to_a_function_pointer_does_not_warn() =>
        Stderr("""
            #include <dlfcn.h>
            #include <stdio.h>
            typedef void (*destructor)(void*);
            int main(void) { destructor d = (destructor)NULL; return d == 0 ? 0 : 1; }
            """).ShouldNotContain("cannot be verified as native code");

    [Fact]
    public void A_void_pointer_value_cast_to_a_function_pointer_warns() =>
        Stderr("""
            #include <dlfcn.h>
            typedef int (*fn)(int);
            int main(void) { void* p = 0; fn f = (fn)p; return f == 0 ? 0 : 1; }
            """).ShouldContain("cast of void* to a function-pointer type cannot be verified as native code");
}
