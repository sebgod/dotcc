#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>Embedded-resource material: the synthetic system headers, the spliced
/// DotCC.Libc runtime block, and the predefined typedef seeds. One concern of
/// <see cref="Compiler"/> — entry points live in the main file.</summary>
public static partial class Compiler
{
    /// <summary>
    /// Synthetic system headers — resolved by <see cref="CPreprocessor.OnInclude"/>
    /// alongside any user <c>.h</c> files found on the include path. User
    /// headers win on name collisions (mirrors clang's local-first rule for
    /// quoted includes).
    /// </summary>
    /// <remarks>
    /// Source: real <c>.h</c> files under <c>DotCC.Lib/include/</c>, embedded
    /// into this assembly as resources at build time (manifest names of the
    /// form <c>DotCC.SystemHeaders.&lt;filename&gt;</c>) — same shape as
    /// clang's <c>lib/clang/&lt;ver&gt;/include/</c> tree, just loaded from
    /// the assembly manifest instead of disk. Edit the file, rebuild, and
    /// the new content is picked up. Lazy-initialized so the loader cost
    /// hits the first <c>EmitCSharp</c>/<c>Preprocess</c> call rather than
    /// every type-init.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> SystemHeaders => _systemHeaders.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> _systemHeaders =
        new(LoadEmbeddedSystemHeaders);

    private static Dictionary<string, string> LoadEmbeddedSystemHeaders()
    {
        const string prefix = "DotCC.SystemHeaders.";
        var asm = typeof(Compiler).Assembly;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) { continue; }
            var fileName = name[prefix.Length..];
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"missing embedded header resource: {name}");
            using var reader = new StreamReader(stream);
            // Splice line continuations here so the synthetic headers (some of
            // which use multi-line macros) lex like any other source.
            map[fileName] = SpliceLineContinuations(reader.ReadToEnd());
        }
        return map;
    }

    /// <summary>
    /// True iff <paramref name="content"/> is the body of one of dotcc's synthetic
    /// system headers — NOT a user <c>-I</c> header that happens to share the name.
    /// The include map seeds <see cref="SystemHeaders"/>'s string REFERENCES and a
    /// user header on the path overwrites the slot with a freshly-read string, so
    /// reference identity distinguishes the embedded original from a shadowing copy.
    /// <see cref="CPreprocessor.OnInclude"/> uses this to lex synthetic headers in the
    /// reserved line band (see <see cref="Ir.SrcPos.SyntheticLineBase"/>), which is what
    /// flags every prototype they declare as runtime-provided rather than importable.
    /// </summary>
    internal static bool IsSyntheticHeaderContent(string name, string content)
        => SystemHeaders.TryGetValue(name, out var sys) && ReferenceEquals(sys, content);

    /// <summary>
    /// Concatenated DotCC.Libc runtime source — the embedded <c>.cs</c>
    /// files from <c>../DotCC.Libc/*.cs</c> with their file-scope
    /// artifacts (<c>#nullable enable</c>, <c>using</c> directives,
    /// <c>namespace DotCC.Libc;</c>) stripped so the contained class +
    /// struct declarations land cleanly inside the emitted file's
    /// type-declarations section. <see cref="BuildShell"/> splices this
    /// block in once per emit. Single source of truth: editing
    /// <c>../DotCC.Libc/Libc.cs</c> updates BOTH the unit-tested DLL
    /// AND every emitted program.
    /// </summary>
    private static readonly Lazy<string> _runtimeBlock = new(LoadRuntimeBlock);

    private static string LoadRuntimeBlock()
    {
        const string prefix = "DotCC.Runtime.";
        var asm = typeof(Compiler).Assembly;
        var pieces = new List<(string FileName, string Content)>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) { continue; }
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"missing embedded runtime resource: {name}");
            using var reader = new StreamReader(stream);
            pieces.Add((name[prefix.Length..], reader.ReadToEnd()));
        }
        // Deterministic order — emitted code should be byte-identical
        // across runs given the same inputs.
        pieces.Sort((a, b) => StringComparer.Ordinal.Compare(a.FileName, b.FileName));

        var sb = new StringBuilder();
        sb.AppendLine("// ---- Embedded DotCC.Libc runtime — single source of truth.");
        sb.AppendLine("//      Edits to ../DotCC.Libc/*.cs land here automatically on next build.");
        foreach (var (fileName, content) in pieces)
        {
            sb.AppendLine($"// ---- {fileName} ----");
            // The DotCC.Libc sources are all `#nullable enable`, but
            // StripFileScopeArtifacts removed that file-scope directive (with
            // the usings/namespace) so the types concatenate cleanly into the
            // emitted file's type-decls region. Re-establish a per-file
            // nullable context with an explicit enable/restore pair (push/pop):
            // the original `?`-annotated signatures keep their meaning and the
            // emitted program compiles warning-free even though its project
            // sets <Nullable>disable</Nullable> (without this, every annotation
            // in the runtime trips CS8669 — "nullable annotation outside a
            // #nullable context"). `restore` (not `disable`) is the pop — it
            // returns the context to that project default.
            sb.AppendLine("#nullable enable");
            sb.AppendLine(StripFileScopeArtifacts(content));
            sb.AppendLine("#nullable restore");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Remove file-scope artifacts from a source string so the remaining
    /// type declarations can be concatenated into a different file's
    /// type-declaration region. Stripped:
    /// <list type="bullet">
    ///   <item><c>#nullable enable</c> / <c>#nullable disable</c></item>
    ///   <item>top-level <c>using</c> directives (the shell already
    ///     declares the union it needs)</item>
    ///   <item><c>namespace DotCC.Libc;</c> (so the contained classes
    ///     land at file scope in the emitted program)</item>
    /// </list>
    /// </summary>
    private static string StripFileScopeArtifacts(string src)
    {
        var sb = new StringBuilder(src.Length);
        foreach (var rawLine in src.Split('\n'))
        {
            var trimmed = rawLine.TrimStart();
            if (trimmed.StartsWith("#nullable", StringComparison.Ordinal)) { continue; }
            // Strip file-scope `using` DIRECTIVES only — these sit at column 0.
            // An indented `using ` is a statement (`using var x = …;` /
            // `using (…)`) inside a method body and must be kept, so match the
            // raw (un-trimmed) line, not the trimmed one.
            if (rawLine.StartsWith("using ", StringComparison.Ordinal)) { continue; }
            if (trimmed.StartsWith("namespace ", StringComparison.Ordinal)) { continue; }
            sb.Append(rawLine).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// C#-side type names that the parser should recognise as type
    /// identifiers from the start — without first seeing them on the
    /// LHS of a <c>typedef</c>. Used to expose opaque libc-class types
    /// (mainly through synthetic headers like <c>&lt;setjmp.h&gt;</c>
    /// which writes <c>typedef LongJmpToken jmp_buf;</c>). dotcc
    /// deliberately avoids inventing new C keywords for these — the
    /// grammar stays a pure C subset, and the seed mechanism here is
    /// what lets typedef-chain a C-side alias to a C#-side class.
    /// </summary>
    internal static readonly string[] PredefinedTypeNames =
    {
        "LongJmpToken", // <setjmp.h> — opaque jmp_buf target
        "VaList",       // <stdarg.h> — va_list cursor (Libc.VaList value type)
        "thrd_t",       // <threads.h> — opaque thread handle (Libc.thrd_t struct)
        "mtx_t",        // <threads.h> — opaque mutex handle (Libc.mtx_t struct)
        "cnd_t",        // <threads.h> — opaque condition-variable handle (Libc.cnd_t)
        "tss_t",        // <threads.h> — opaque thread-specific-storage key (Libc.tss_t)
        "div_t",        // <stdlib.h> — div() result (Libc.div_t struct)
        "ldiv_t",       // <stdlib.h> — ldiv() result (Libc.ldiv_t struct)
        "lldiv_t",      // <stdlib.h> — lldiv() result (Libc.lldiv_t struct)
        "imaxdiv_t",    // <inttypes.h> — imaxdiv() result (Libc.imaxdiv_t struct)
        "FILE",         // <stdio.h> — opaque stream handle; FILE* stays a real
                        // pointer-to-struct (Libc.FILE), so NULL/==/if(fp) all
                        // work through the normal pointer machinery.
        "char16_t",     // <uchar.h> — C11 UTF-16 code unit. Unlike the names above
                        // (which resolve to a verbatim Libc type), char16_t is
                        // pre-seeded in IrBuilder._typedefs to CType.Char16 → C# char.
        "wchar_t",      // <wchar.h> — likewise pre-seeded in IrBuilder._typedefs to
                        // CType.WChar → C# char (dotcc's MSVC-shaped 16-bit wchar_t).
        "char32_t",     // <uchar.h> — C11 UTF-32 code unit. Pre-seeded in
                        // IrBuilder._typedefs to CType.Char32 → C# uint.
        "char8_t",      // <uchar.h> — C23 UTF-8 code unit. Pre-seeded in
                        // IrBuilder._typedefs to CType.Char8 → C# byte.
    };

}
