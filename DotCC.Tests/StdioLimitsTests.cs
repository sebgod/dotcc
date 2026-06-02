#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// <c>&lt;stdio.h&gt;</c>'s implementation-defined limit macros (C99 7.21.1):
/// <c>BUFSIZ</c>, <c>FILENAME_MAX</c>, <c>FOPEN_MAX</c>, <c>TMP_MAX</c>,
/// <c>L_tmpnam</c>, and the <c>setvbuf</c> buffering modes
/// <c>_IOFBF</c>/<c>_IOLBF</c>/<c>_IONBF</c>. The exact values are impl-defined;
/// dotcc picks ones satisfying the standard minima. The motivating use is Lua
/// lauxlib's <c>char buff[BUFSIZ];</c> struct member — BUFSIZ must fold to a
/// constant array bound. End-to-end in the <c>stdio-limits/</c> fixture.
/// </summary>
public sealed class StdioLimitsTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-sl-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void BUFSIZ_folds_as_a_constant_array_bound()
    {
        // Lua lauxlib's `struct LoadF { …; char buff[BUFSIZ]; }`.
        var src = WriteTemp("""
            #include <stdio.h>
            struct reader { int n; char buff[BUFSIZ]; };
            int main(void) { struct reader r; r.n = 0; return (int)sizeof(r.buff); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // BUFSIZ (8192) folded into the fixed-buffer bound — not left as an ID
            emitted.ShouldContain("fixed byte buff[8192];");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void limit_macros_are_defined_constants()
    {
        // All must be defined (else these would parse-error as bare identifiers)
        // and satisfy the standard minima.
        var src = WriteTemp("""
            #include <stdio.h>
            int main(void) {
                return (BUFSIZ >= 256) + (FOPEN_MAX >= 8) + (TMP_MAX >= 25)
                     + (FILENAME_MAX > 0) + (L_tmpnam > 0);
            }
            """);
        try
        {
            // Compiles and the macros resolve to integer constants.
            Compiler.EmitCSharp(new[] { src }).ShouldContain("static unsafe int main");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void setvbuf_modes_are_distinct()
    {
        var src = WriteTemp("""
            #include <stdio.h>
            int main(void) { return (_IOFBF != _IOLBF) && (_IOLBF != _IONBF); }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("static unsafe int main");
        }
        finally { File.Delete(src); }
    }
}
