#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>Separate compilation (--emit=obj + link): object-fragment serialization
/// and the .cs-object linker. One concern of <see cref="Compiler"/> — entry points
/// live in the main file.</summary>
public static partial class Compiler
{
    // ---- separate compilation (`--emit=obj` + link) -------------------------
    // dotcc normally whole-program-compiles all TUs in one pass. To slot into a
    // build system (CMake/make) that compiles each `.c` to an object then links,
    // we split: `EmitObject` emits one TU's C# fragment (the LTO-style
    // intermediate), `LinkObjects` merges fragments — deduping shared types —
    // and wraps them in the shell + runtime exactly as whole-program emit does.

    // Marker lines delimiting a `.cs` object fragment. Comment-prefixed so a
    // fragment is still (almost) valid C#, and so the markers can't collide with
    // real emitted code.
    private const string FragMain   = "//!!dotcc-obj main:";
    private const string FragMainVoid = "//!!dotcc-obj main-void:"; // 1 when main returns void
    private const string FragMainErr = "//!!dotcc-obj main-err:";   // v|i when main returns `!void`|`!<int>`
    // One definition each: `type:<name>`, `global:<name> <local|extern>`, `fn:<name> <local|extern>`,
    // with its C# text on the lines up to the next marker. `local` is a name only its own unit
    // reaches (internal linkage, or a static local's field), qualified by that unit already.
    private const string FragType   = "//!!dotcc-obj type:";
    // `opaque:<name>`: the placeholder of a struct the unit never completes, kept only when
    // no object defines the type.
    private const string FragOpaque = "//!!dotcc-obj opaque:";
    private const string FragGlobal = "//!!dotcc-obj global:";
    private const string FragFn     = "//!!dotcc-obj fn:";
    // Import mode in separate compilation: `-l` is known only at LINK time, so each
    // fragment serializes its import CANDIDATES (proto-only, called, non-system,
    // non-variadic — `import:<name> <cs-fn-ptr-type>`) and the names it DEFINES
    // (`def:<name>`, functions + globals). The link step keeps a candidate iff no
    // fragment defines it, then binds the survivors GOT-style (see LinkObjects).
    private const string FragImport = "//!!dotcc-obj import:"; // import:<name> <delegate* unmanaged[Cdecl]<…>>
    private const string FragDef    = "//!!dotcc-obj def:";    // def:<name> (this TU defines it)
    // `runtime:python`: the unit included the synthetic <Python.h>, so the program links the
    // runtime's abi3 shim.
    private const string FragRuntimePython = "//!!dotcc-obj runtime:python";

    // The uniform "magic" first line every dotcc-generated `.cs` carries, so any
    // file can be classified at a glance:
    //   //!dotcc program <v>   — a complete program (file/csproj/build/-shared)
    //   //!dotcc object  <v>   — a per-TU object fragment (--emit=obj), for linking
    // (A file-based program's `#:property` directives precede it; otherwise it's
    // line 1.) Scan the first few lines for these.
    private const string MagicObject = "//!dotcc object";
    // The object format this dotcc writes and links: 2 carries one record per definition,
    // with its linkage (1 had one section of each kind, and no types).
    private const string ObjectFormat = "2";

    /// <summary>Emit a single translation unit as a `.cs` object fragment.</summary>
    public static string EmitObject(
        string inputPath,
        IReadOnlyList<string>? includeDirs = null,
        IReadOnlyList<string>? defines = null,
        CDialect? dialect = null,
        WarningFlags warnings = WarningFlags.Default)
        => EmitCSharp(new[] { inputPath }, includeDirs, defines,
                      emit: EmitMode.Object, dialect: dialect, warnings: warnings);

    /// <summary>
    /// The key that qualifies an object's internal-linkage names (<see cref="Ir.IrBuilder.ObjectKey"/>):
    /// its source file's name as an identifier, then a hash of its full path, so two units of
    /// one name in different directories differ.
    /// </summary>
    private static string ObjectKeyOf(IReadOnlyList<string> inputPaths)
    {
        var path = Path.GetFullPath(inputPaths[0]).Replace('\\', '/');
        var stem = Path.GetFileNameWithoutExtension(path);
        var ident = new string(stem.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        var hash = 0xCBF29CE484222325UL;
        foreach (var ch in path) { hash = (hash ^ ch) * 0x100000001B3UL; }
        return System.FormattableString.Invariant($"{ident}_{hash & 0xFFFFFF:x6}");
    }

    private static string SerializeFragment(
        IReadOnlyList<Backends.LinkRecord> records, int mainArity,
        IReadOnlyList<(string Name, string FieldType)> importSpecs, IEnumerable<string> defNames, bool mainReturnsVoid = false,
        bool mainReturnsErrUnion = false, bool mainErrPayloadIsVoid = false, bool pythonShim = false)
    {
        var sb = new StringBuilder();
        sb.Append(MagicObject).Append(' ').Append(ObjectFormat).Append(" — link with `dotcc <objs> -o <out>`.\n");
        sb.Append(FragMain).Append(mainArity).Append('\n');
        if (mainReturnsVoid) { sb.Append(FragMainVoid).Append("1").Append('\n'); }
        if (mainReturnsErrUnion) { sb.Append(FragMainErr).Append(mainErrPayloadIsVoid ? "v" : "i").Append('\n'); }
        if (pythonShim) { sb.Append(FragRuntimePython).Append('\n'); }
        // Import candidates + defined names, for the link step's resolution. Names
        // have no spaces (C identifiers), so the type — which does (`delegate*
        // unmanaged[Cdecl]<int, int>`) — is everything after the first space.
        foreach (var (name, ft) in importSpecs) { sb.Append(FragImport).Append(name).Append(' ').Append(ft).Append('\n'); }
        foreach (var d in defNames) { sb.Append(FragDef).Append(d).Append('\n'); }
        foreach (var r in records)
        {
            var linkage = r.TuLocal ? " local" : " extern";
            sb.Append(r.Kind switch
            {
                Backends.LinkRecordKind.Type => FragType + r.Name,
                Backends.LinkRecordKind.OpaqueType => FragOpaque + r.Name,
                Backends.LinkRecordKind.Global => FragGlobal + r.Name + linkage,
                _ => FragFn + r.Name + linkage,
            }).Append('\n');
            sb.Append(r.Text);
            if (r.Text.Length > 0 && r.Text[^1] != '\n') { sb.Append('\n'); }
        }
        return sb.ToString();
    }

    /// <summary>Reject a file that is not an object of the format this dotcc links.</summary>
    private static void CheckObjectFormat(string text, string from)
    {
        var at = text.IndexOf(MagicObject, StringComparison.Ordinal);
        if (at < 0)
        {
            throw new CompileException(
                $"'{from}' is not a dotcc object — no '{MagicObject}' marker. " +
                "Link expects `--emit=obj` fragments, not a program or hand-written .cs.");
        }
        var rest = text[(at + MagicObject.Length)..].TrimStart(' ');
        var end = rest.IndexOfAny([' ', '\n']);
        var format = end < 0 ? rest : rest[..end];
        if (format != ObjectFormat)
        {
            throw new CompileException(
                $"'{from}' is a dotcc object of format {format}; this dotcc links format {ObjectFormat} (recompile it with --emit=obj)");
        }
    }

    /// <summary>
    /// Link `.cs` object fragments (from <see cref="EmitObject"/>) into one program, as a C
    /// linker would: every type once (the objects that include one header agree on it, and a
    /// type defined differently in two objects is an error, since the program has one), each
    /// function and object once (a second definition of a name is gcc's "multiple definition"
    /// error), then wrap them in the shell + runtime.
    /// </summary>
    public static string LinkObjects(
        IReadOnlyList<string> objectPaths, EmitMode emit = EmitMode.File, bool debugHeap = false,
        ImportOptions? imports = null)
    {
        var libraryMode = emit == EmitMode.SharedLib;
        var types = new Dictionary<string, (string Text, string From)>(StringComparer.Ordinal);
        var opaqueTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var structDecls = new StringBuilder();
        var definedIn = new Dictionary<string, string>(StringComparer.Ordinal);
        var globalText = new StringBuilder();
        var functions = new StringBuilder();
        var errors = new List<string>();
        var mainArity = -1;
        var mainReturnsVoid = false;
        var mainReturnsErrUnion = false;
        var mainErrPayloadIsVoid = false;
        var pythonShim = false;
        // Import resolution across fragments: a candidate name → its fn-ptr type, and
        // every name some fragment DEFINES. A candidate survives iff no fragment defines it.
        var importSpecs = new Dictionary<string, string>(StringComparer.Ordinal);
        var definedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in objectPaths)
        {
            var text = File.ReadAllText(path).ReplaceLineEndings("\n");
            var from = Path.GetFileName(path);
            CheckObjectFormat(text, from);
            // The record being read: its marker line, and its text so far.
            string? record = null;
            var buf = new StringBuilder();
            void Flush()
            {
                if (record is null) { return; }
                var body = buf.ToString();
                buf.Clear();
                if (record.StartsWith(FragOpaque, StringComparison.Ordinal))
                {
                    opaqueTypes.TryAdd(record[FragOpaque.Length..], body);
                    return;
                }
                if (record.StartsWith(FragType, StringComparison.Ordinal))
                {
                    var name = record[FragType.Length..];
                    if (!types.TryGetValue(name, out var first))
                    {
                        types[name] = (body, from);
                        structDecls.Append(body);
                    }
                    else if (first.Text != body)
                    {
                        errors.Add($"type '{name}' is defined differently in '{from}' and '{first.From}'");
                    }
                    return;
                }
                var isFn = record.StartsWith(FragFn, StringComparison.Ordinal);
                var spec = record[(isFn ? FragFn : FragGlobal).Length..];
                var sp = spec.IndexOf(' ');
                var symbol = sp < 0 ? spec : spec[..sp];
                if (definedIn.TryGetValue(symbol, out var firstFrom))
                {
                    errors.Add($"multiple definition of '{symbol}' in '{from}', first defined in '{firstFrom}'");
                    return;
                }
                definedIn[symbol] = from;
                if (isFn)
                {
                    if (functions.Length > 0) { functions.Append("\n\n"); }
                    functions.Append(body.TrimEnd('\n'));
                }
                else
                {
                    globalText.Append(body);
                }
            }
            // The file's final newline ends its last line; it opens no empty one.
            var lines = text.Split('\n');
            var lineCount = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
            foreach (var line in lines.AsSpan(0, lineCount))
            {
                if (line.StartsWith(FragType, StringComparison.Ordinal)
                    || line.StartsWith(FragOpaque, StringComparison.Ordinal)
                    || line.StartsWith(FragGlobal, StringComparison.Ordinal)
                    || line.StartsWith(FragFn, StringComparison.Ordinal))
                {
                    Flush();
                    record = line;
                }
                else if (line.StartsWith(FragMainErr, StringComparison.Ordinal))
                {
                    // `main-err:` (v|i) — an error-union main (`!void`/`!<int>`). Disjoint from
                    // the `main-void:` / `main:` markers (the char after "main" differs).
                    mainReturnsErrUnion = true;
                    mainErrPayloadIsVoid = line[FragMainErr.Length..].Trim() == "v";
                }
                else if (line.StartsWith(FragMainVoid, StringComparison.Ordinal))
                {
                    // `main-void:` and `main:` are disjoint markers (the char after
                    // "main" differs: '-' vs ':'), so this branch and the next don't race.
                    if (line[FragMainVoid.Length..].Trim() == "1") { mainReturnsVoid = true; }
                }
                else if (line.StartsWith(FragMain, StringComparison.Ordinal))
                {
                    if (int.TryParse(line[FragMain.Length..], out var m) && m >= 0) { mainArity = m; }
                }
                else if (line.StartsWith(FragImport, StringComparison.Ordinal))
                {
                    // import:<name> <type> — name is space-free, type is the rest.
                    var rest = line[FragImport.Length..];
                    var sp = rest.IndexOf(' ');
                    if (sp > 0) { importSpecs[rest[..sp]] = rest[(sp + 1)..]; }
                }
                else if (line.StartsWith(FragDef, StringComparison.Ordinal))
                {
                    definedNames.Add(line[FragDef.Length..]);
                }
                else if (line == FragRuntimePython)
                {
                    pythonShim = true;
                }
                else if (record is not null)
                {
                    buf.Append(line).Append('\n');
                }
            }
            Flush();
        }
        if (errors.Count > 0)
        {
            const int shown = 20;
            var more = errors.Count > shown ? $"\n... and {errors.Count - shown} more" : "";
            throw new CompileException("link failed:\n" + string.Join("\n", errors.Take(shown)) + more);
        }
        // A struct some object only points at: its definition if any object has one (added
        // above), else one placeholder.
        foreach (var (name, text) in opaqueTypes)
        {
            if (!types.ContainsKey(name)) { structDecls.Append(text); }
        }

        if (!libraryMode && mainArity < 0)
        {
            throw new CompileException("no `main` function defined in any linked object.");
        }

        // Import mode at link: bind the candidates no fragment defines (a name defined
        // in any object — function or global — is resolved internally, not imported).
        // Without `-l`, survivors stay unresolved → the same CS0103 as a normal link.
        var importsClass = "";
        if (imports is { LinkLibraries.Count: > 0 })
        {
            var survivors = importSpecs
                .Where(kv => !definedNames.Contains(kv.Key))
                .Select(kv => (kv.Key, kv.Value))
                .OrderBy(p => p.Item1, StringComparer.Ordinal)
                .ToList();
            if (survivors.Count > 0) { importsClass = RenderImportsClass(survivors, imports, libraryMode); }
        }
        return BuildShell(mainArity, functions.ToString(), structDecls.ToString(), "", globalText.ToString(),
                          emit, System.Array.Empty<EmitHelpers.Export>(), debugHeap, importsClass,
                          importsAreStatic: false, mainReturnsVoid: mainReturnsVoid,
                          mainReturnsErrUnion: mainReturnsErrUnion, mainErrPayloadIsVoid: mainErrPayloadIsVoid, pythonShim: pythonShim);
    }

}
