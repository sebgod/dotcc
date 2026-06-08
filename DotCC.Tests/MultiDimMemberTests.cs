#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for phase 4l — multi-dimensional struct array members
/// (<c>T grid[N][M];</c>). The member flattens to a single buffer (a
/// <c>fixed T[N*M]</c> for a primitive element, <c>[InlineArray(N*M)]</c> for a
/// non-primitive one) and <c>s.grid[i][j]</c> rewrites to flat pointer striding.
/// End-to-end in the <c>multidim-member/</c> fixture.
/// </summary>
public sealed class MultiDimMemberTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-mdm-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void primitive_2d_member_flattens_to_fixed_buffer()
    {
        var src = WriteTemp("""
            struct G { int cells[2][3]; };
            int main(void) { struct G g; g.cells[1][2] = 7; return g.cells[1][2]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // 2x3 → one fixed int[6]; access strides by the inner dim (3).
            emitted.ShouldContain("fixed int cells[6]");
            emitted.ShouldContain("(g.cells + 1 * 3)[2]");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void pointer_2d_member_flattens_to_inline_array()
    {
        var src = WriteTemp("""
            struct G { char *names[2][2]; };
            int main(void) { struct G g; g.names[1][1] = "x"; return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // 2x2 → [InlineArray(4)] of pointer (byte* element); access via (byte**)&.
            emitted.ShouldContain("InlineArray(4)");
            emitted.ShouldContain("((byte**)&g.names + 1 * 2)[1]");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void multidim_member_through_pointer()
    {
        var src = WriteTemp("""
            struct G { int m[2][2]; };
            static int get(struct G *p) { return p->m[1][0]; }
            int main(void) { struct G g; g.m[1][0] = 9; return get(&g); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(p->m + 1 * 2)[0]");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void multidim_member_dims_are_const_folded()
    {
        // Dimensions that are constant expressions (enum + arithmetic) fold to the
        // flat literal count.
        var src = WriteTemp("""
            enum E { R = 2, C = 3 };
            struct G { long t[R][C]; };
            int main(void) { struct G g; g.t[1][2] = 5; return (int)g.t[1][2]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("fixed long t[6]");
        }
        finally { File.Delete(src); }
    }
}
