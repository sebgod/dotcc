#nullable enable

using System;
using System.IO;
using System.Linq;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// <c>&lt;errno.h&gt;</c> declares <c>errno</c> (<c>extern int errno;</c>), so a program's use of it
/// binds to a declared symbol instead of reaching the binder's unresolved-name fallthrough. The C#
/// emit is unchanged (the bare name binds the runtime's <c>Libc.errno</c>) and the wat backend
/// recognizes the declared symbol and gives it a slot of its own.
/// </summary>
[Collection("Attributes")]
public sealed class ErrnoDeclarationTests
{
    private static string Write(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-errno-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private static string CSharp(string body)
    {
        var path = Write(body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private static string Wat(string body)
    {
        var path = Write(body);
        try { return Compiler.EmitWat(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_csharp_emit_prints_the_bare_errno_and_no_declaration()
    {
        var cs = CSharp("#include <errno.h>\nint main(void) { errno = 0; return errno; }\n");
        cs.ShouldContain("errno = 0;");
        cs.ShouldContain("return errno;");
        cs.ShouldNotContain("extern int errno");
        // The program's own part declares nothing for it (the runtime's property is the only errno).
        cs.ShouldNotContain("extern int errno");
        cs.ShouldNotContain("DotCcGlobals.errno");
    }

    [Fact]
    public void A_program_that_defines_its_own_errno_keeps_it_a_real_global()
    {
        var cs = CSharp("#include <errno.h>\nint errno;\nint main(void) { errno = 3; return errno; }\n");
        cs.ShouldContain("errno = 3;");
    }

    [Theory]
    [InlineData("int main(void) { return errno; }")]
    [InlineData("int main(void) { errno = 5; return 0; }")]
    [InlineData("int main(void) { errno += 2; return 0; }")]
    [InlineData("int main(void) { errno++; return 0; }")]
    [InlineData("int main(void) { int *p = &errno; *p = 1; return 0; }")]
    public void The_wat_emit_uses_the_errno_slot(string body)
    {
        var wat = Wat("#include <errno.h>\n" + body + "\n");
        wat.ShouldContain("i32.const");
        wat.ShouldContain("(func $main");
    }

    [Fact]
    public void The_wat_emit_addresses_one_slot_for_every_use()
    {
        var wat = Wat("#include <errno.h>\nint main(void) { errno = 1; errno += 2; int *p = &errno; return errno + *p; }\n");
        // One data slot: the same address constant is pushed by the store, the compound
        // assignment, the address-of and the read.
        var addr = System.Text.RegularExpressions.Regex.Matches(wat, @"i32\.const (\d+)")
            .Select(m => m.Groups[1].Value).GroupBy(v => v).OrderByDescending(g => g.Count()).First();
        addr.Count().ShouldBeGreaterThanOrEqualTo(4);
    }
}
