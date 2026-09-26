#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// The managed-host half of the "pretend to be CPython" probe (docs/FRONTEND-IDEAS.md #1):
/// the <c>python-capi-spam</c> fixture's extension module (<c>spam.c</c>, plain Limited-API
/// C) is compiled by dotcc as a <c>-shared</c> library, loaded, and imported + called from
/// C# through the shim's <c>Libc.PyHost</c> API with ordinary .NET values — no C host in
/// between. This is the seam a managed Python runtime would sit on. The refcount leak
/// probe (<c>PyHost.LiveObjects</c>) checks that every call releases what it allocates,
/// across both the shim and the extension's own reference handling.
/// </summary>
public sealed class PythonHostTests
{
    private sealed class Host
    {
        private readonly Type _host;
        public Host(Type host) => _host = host;

        private object? Invoke(string method, params object?[] args)
        {
            try { return _host.GetMethod(method)!.Invoke(null, args); }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        public nint Import(nint init) => (nint)Invoke("Import", init)!;
        public object? GetAttr(nint obj, string name) => Invoke("GetAttr", obj, name);
        public object? Call(nint obj, string name, object?[] args, IDictionary<string, object?>? kwargs = null)
            => Invoke("Call", obj, name, args, kwargs);
        public string Repr(nint obj) => (string)Invoke("Repr", obj)!;
        public int LiveObjects => (int)_host.GetProperty("LiveObjects")!.GetValue(null)!;
    }

    /// <summary>A raised Python exception, read off the shim's PyError by reflection.</summary>
    private static (string type, string qualified, string message) PyError(Exception e)
    {
        e.GetType().Name.ShouldBe("PyError");
        string Prop(string p) => (string)e.GetType().GetProperty(p)!.GetValue(e)!;
        return (Prop("TypeName"), Prop("QualifiedTypeName"), Prop("PyMessage"));
    }

    [Fact]
    public void Managed_host_imports_and_calls_a_dotcc_compiled_extension_module()
    {
        var spamC = Path.Combine(AppContext.BaseDirectory, "Fixtures", "python-capi-spam", "spam.c");
        var program = Compiler.EmitCSharp(new[] { spamC }, includeDirs: null, defines: null, emit: EmitMode.SharedLib);
        var asm = LibraryModeTests.CompileLibrary(program);

        var init = asm.GetType("DotCcLib", throwOnError: true)!
            .GetMethod("PyInit_spam", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var host = new Host(asm.GetType("Libc", throwOnError: true)!.GetNestedType("PyHost")!);

        var spam = host.Import(init.MethodHandle.GetFunctionPointer());
        host.Repr(spam).ShouldBe("<module 'spam'>");
        host.GetAttr(spam, "ANSWER").ShouldBe(42L);
        host.GetAttr(spam, "BACKEND").ShouldBe("dotcc");
        host.GetAttr(spam, "__doc__").ShouldBe("A small tour of the Python C-API.");

        var baseline = host.LiveObjects;

        host.Call(spam, "add", new object?[] { 2, 40L }).ShouldBe(42L);
        host.Call(spam, "divide", new object?[] { 1, 8 }).ShouldBe(0.125);
        host.Call(spam, "greet", new object?[] { "host" },
                new Dictionary<string, object?> { ["times"] = 2, ["greeting"] = "Hi" })
            .ShouldBe(new List<object?> { "Hi, host! (#1)", "Hi, host! (#2)" });
        host.Call(spam, "stats", new object?[] { new List<object?> { 1L, 2.5 } })
            .ShouldBe(new Dictionary<object, object?> { ["count"] = 2L, ["sum"] = 3.5, ["mean"] = 1.75 });
        host.Call(spam, "version", Array.Empty<object?>()).ShouldBe(new object?[] { 1L, 0L, "dotcc" });
        host.Call(spam, "swap", new object?[] { "a", null }).ShouldBe(new object?[] { null, "a" });

        PyError(Should.Throw<Exception>(() => host.Call(spam, "divide", new object?[] { 1, 0 })))
            .ShouldBe(("ZeroDivisionError", "ZeroDivisionError", "division by zero"));
        PyError(Should.Throw<Exception>(() => host.Call(spam, "stats", new object?[] { new List<object?>() })))
            .ShouldBe(("error", "spam.error", "stats() of an empty sequence"));
        PyError(Should.Throw<Exception>(() => host.Call(spam, "add", new object?[] { "x", 1 })))
            .ShouldBe(("TypeError", "TypeError", "'str' object cannot be interpreted as an integer"));
        PyError(Should.Throw<Exception>(() => host.GetAttr(spam, "nope")))
            .ShouldBe(("AttributeError", "AttributeError", "module 'spam' has no attribute 'nope'"));

        // Every argument tuple, result, temporary and raised exception above was released.
        host.LiveObjects.ShouldBe(baseline);
    }
}
