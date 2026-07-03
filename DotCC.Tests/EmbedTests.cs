#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// C23 <c>#embed</c>. The preprocessor reads the named file's RAW bytes and emits
/// ONE synthetic carrier token (not the standard's comma-list of integer
/// constants — that would explode a multi-MB file into millions of tokens); the
/// IR expands it to byte constants in initializer position
/// (<c>IrBuilder.ParseInitList</c>). These tests assert the EMISSION SHAPE +
/// the <c>limit</c>/<c>if_empty</c> parameters + the V1 cuts; the end-to-end
/// "compiles and prints the bytes" path is the <c>embed-basic</c> functional
/// fixture.
/// </summary>
/// <remarks>
/// A <c>const</c> <c>#embed</c>'d byte array lowers to the zero-copy RVA path
/// (<c>Libc.L</c> over the PE <c>.rodata</c> blob — no startup copy), since writing
/// to a const object is UB and the const-correctness check rejects in-source
/// writes; a NON-const <c>#embed</c> array keeps the writable
/// <c>GlobalArrayFrom&lt;byte&gt;</c> POH copy. Tests pin distinctive byte sequences
/// (0xDE 0xAD …) so a match can't collide with an unrelated constant in the spliced
/// runtime block.
/// </remarks>
[Collection("Console")]
public sealed class EmbedTests
{
    /// <summary>Emit <paramref name="mainC"/> with <paramref name="embedName"/>
    /// written (as raw bytes) alongside it, capturing stderr so the parameter /
    /// gate diagnostics can be asserted. The unit assembly is serialized
    /// (AssemblyInfo), so the process-global <c>Console.Error</c> swap is race-free.</summary>
    private static (string Emit, string Stderr) EmitEmbed(string mainC, string embedName, byte[] embedBytes)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-embed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, embedName), embedBytes);
            var mainPath = Path.Combine(dir, "main.c");
            File.WriteAllText(mainPath, mainC);

            var prior = Console.Error;
            var sw = new StringWriter();
            Console.SetError(sw);
            try
            {
                var emit = Compiler.EmitCSharp(new[] { mainPath }, includeDirs: new[] { dir });
                return (emit, sw.ToString());
            }
            finally { Console.SetError(prior); }
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // A `static const` char array gives a deterministic backing-store emit
    // (block-auto arrays differ); const → the zero-copy `Libc.L(new byte[]{ … })`
    // RVA form. The fixture covers runtime.
    private const string FillTemplate = """
        int main(void) {
            static const unsigned char d[] = {
                #embed "blob.bin"PARAMS
            };
            return d[0];
        }
        """;

    private static (string Emit, string Stderr) Fill(byte[] bytes, string @params = "")
        => EmitEmbed(FillTemplate.Replace("PARAMS", @params), "blob.bin", bytes);

    // ---- basic: a #embed fills a char array with the file bytes ------------

    [Fact]
    public void embed_fills_char_array_with_file_bytes()
    {
        var (emit, _) = Fill(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        // One backing byte[] carrying exactly the file's bytes — not exploded.
        // The array is `const`, so it lowers to the zero-copy RVA path (Libc.L
        // over PE .rodata), not the writable GlobalArrayFrom copy.
        emit.ShouldContain("Libc.L(new byte[]{ 222, 173, 190, 239 }");
    }

    // ---- limit(N): embed only the first N bytes ----------------------------

    [Fact]
    public void embed_limit_clamps_to_leading_bytes()
    {
        var (emit, _) = Fill(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, " limit(2)");
        emit.ShouldContain("new byte[]{ 222, 173 }");
        emit.ShouldNotContain("190, 239");
    }

    // ---- if_empty: substitute when the resource is empty (here via limit(0)) -

    [Fact]
    public void embed_if_empty_substitutes_when_resource_is_empty()
    {
        // limit(0) forces an empty resource → the if_empty tokens take its place.
        var (emit, _) = EmitEmbed("""
            int main(void) {
                static const unsigned char e[] = { 241,
                    #embed "blob.bin" limit(0) if_empty(99, 100)
                    , 242 };
                return e[0];
            }
            """, "blob.bin", new byte[] { 1, 2, 3 });
        emit.ShouldContain("new byte[]{ 241, 99, 100, 242 }");
    }

    // ---- mixed list: #embed bytes splice between scalar elements ------------

    [Fact]
    public void embed_splices_into_a_mixed_initializer_list()
    {
        var (emit, _) = EmitEmbed("""
            int main(void) {
                static const int mixed[] = { 17,
                    #embed "blob.bin"
                    , 19 };
                return mixed[0];
            }
            """, "blob.bin", new byte[] { 0xDE });
        emit.ShouldContain("new int[]{ 17, 222, 19 }");
    }

    // ---- V1 cut: #embed in scalar / non-brace position is rejected loudly ---

    [Fact]
    public void embed_in_scalar_position_throws()
    {
        var ex = Should.Throw<CompileException>(() => EmitEmbed("""
            int main(void) {
                int x =
                #embed "blob.bin"
                ;
                return x;
            }
            """, "blob.bin", new byte[] { 5 }));
        ex.Message.ShouldContain("#embed");
        ex.Message.ShouldContain("brace initializer");
    }

    // ---- a missing resource is reported, not silently miscompiled ----------

    [Fact]
    public void embed_missing_file_warns()
    {
        var (_, stderr) = EmitEmbed("""
            int main(void) {
                static const unsigned char d[] = { 0,
                    #embed "does-not-exist.bin"
                    , 0 };
                return d[0];
            }
            """, "blob.bin", new byte[] { 1 });
        stderr.ShouldContain("does-not-exist.bin");
        stderr.ShouldContain("not found");
    }

    // ---- unsupported parameters warn (and are ignored) ---------------------

    [Fact]
    public void embed_prefix_parameter_warns_and_is_ignored()
    {
        var (emit, stderr) = Fill(new byte[] { 0xDE, 0xAD }, " prefix(1, 2)");
        stderr.ShouldContain("prefix");
        stderr.ShouldContain("not yet supported");
        // The bytes still embed; only the prefix clause is dropped.
        emit.ShouldContain("new byte[]{ 222, 173 }");
    }

    // ---- dialect gate: #embed is C23 --------------------------------------

    [Fact]
    public void embed_is_c23_gated_under_pedantic()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-embed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "blob.bin"), new byte[] { 1, 2 });
            var mainPath = Path.Combine(dir, "main.c");
            File.WriteAllText(mainPath, """
                int main(void) {
                    static const unsigned char d[] = {
                        #embed "blob.bin"
                    };
                    return d[0];
                }
                """);
            var ex = Should.Throw<CompileException>(() => Compiler.EmitCSharp(
                new[] { mainPath }, includeDirs: new[] { dir },
                dialect: CDialect.Parse("c17"), warnings: WarningFlags.Default | WarningFlags.PedanticErrors));
            ex.Message.ShouldContain("#embed");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // ---- __has_embed / __has_include (C23 #if operators) -------------------
    // Routed through CPreprocessor.ExpandFuncMacro by LALR.CC's #if evaluator
    // (any IDENT(args) in a conditional) — no LALR.CC change. A #if selects a
    // branch at preprocess time, so the emit contains ONLY the taken branch:
    // assert the distinctive marker of the expected arm is present / absent.

    [Fact]
    public void has_embed_is_true_when_the_resource_exists()
    {
        var (emit, _) = EmitEmbed("""
            int main(void) {
            #if __has_embed("blob.bin")
                return 31337;
            #else
                return 42424;
            #endif
            }
            """, "blob.bin", new byte[] { 1, 2, 3 });
        emit.ShouldContain("31337");
        emit.ShouldNotContain("42424");
    }

    [Fact]
    public void has_embed_is_false_when_the_resource_is_missing()
    {
        var (emit, _) = EmitEmbed("""
            int main(void) {
            #if __has_embed("absent.bin")
                return 31337;
            #else
                return 42424;
            #endif
            }
            """, "blob.bin", new byte[] { 1 });
        emit.ShouldContain("42424");
        emit.ShouldNotContain("31337");
    }

    [Fact]
    public void has_embed_reports_empty_resource_as_stdc_embed_empty()
    {
        // An existing but 0-byte resource → __STDC_EMBED_EMPTY__ (2), not FOUND.
        var (emit, _) = EmitEmbed("""
            int main(void) {
            #if __has_embed("blob.bin") == __STDC_EMBED_EMPTY__
                return 31337;
            #else
                return 42424;
            #endif
            }
            """, "blob.bin", Array.Empty<byte>());
        emit.ShouldContain("31337");
        emit.ShouldNotContain("42424");
    }

    [Fact]
    public void has_include_is_true_for_a_system_header()
    {
        var (emit, _) = EmitEmbed("""
            #include <stdio.h>
            int main(void) {
            #if __has_include(<stdio.h>)
                return 31337;
            #else
                return 42424;
            #endif
            }
            """, "blob.bin", new byte[] { 1 });
        emit.ShouldContain("31337");
        emit.ShouldNotContain("42424");
    }

    [Fact]
    public void has_include_is_false_for_a_missing_header()
    {
        var (emit, _) = EmitEmbed("""
            int main(void) {
            #if __has_include("totally-not-here.h")
                return 31337;
            #else
                return 42424;
            #endif
            }
            """, "blob.bin", new byte[] { 1 });
        emit.ShouldContain("42424");
        emit.ShouldNotContain("31337");
    }
}
