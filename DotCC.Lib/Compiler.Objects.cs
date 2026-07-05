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
    private const string FragType   = "//!!dotcc-obj type:";
    private const string FragSect   = "//!!dotcc-obj section:"; // aliases|globals|functions
    // Import mode in separate compilation: `-l` is known only at LINK time, so each
    // fragment serializes its import CANDIDATES (proto-only, called, non-system,
    // non-variadic — `import:<name> <cs-fn-ptr-type>`) and the names it DEFINES
    // (`def:<name>`, functions + globals). The link step keeps a candidate iff no
    // fragment defines it, then binds the survivors GOT-style (see LinkObjects).
    private const string FragImport = "//!!dotcc-obj import:"; // import:<name> <delegate* unmanaged[Cdecl]<…>>
    private const string FragDef    = "//!!dotcc-obj def:";    // def:<name> (this TU defines it)

    // The uniform "magic" first line every dotcc-generated `.cs` carries, so any
    // file can be classified at a glance:
    //   //!dotcc program <v>   — a complete program (file/csproj/build/-shared)
    //   //!dotcc object  <v>   — a per-TU object fragment (--emit=obj), for linking
    // (A file-based program's `#:property` directives precede it; otherwise it's
    // line 1.) Scan the first few lines for these.
    private const string MagicObject = "//!dotcc object";

    /// <summary>Emit a single translation unit as a `.cs` object fragment.</summary>
    public static string EmitObject(
        string inputPath,
        IReadOnlyList<string>? includeDirs = null,
        IReadOnlyList<string>? defines = null,
        CDialect? dialect = null,
        WarningFlags warnings = WarningFlags.Default)
        => EmitCSharp(new[] { inputPath }, includeDirs, defines,
                      emit: EmitMode.Object, dialect: dialect, warnings: warnings);

    private static string SerializeFragment(
        string functions, IReadOnlyDictionary<string, string> typeDecls, string aliases, string globals, int mainArity,
        IReadOnlyList<(string Name, string FieldType)> importSpecs, IEnumerable<string> defNames, bool mainReturnsVoid = false,
        bool mainReturnsErrUnion = false, bool mainErrPayloadIsVoid = false)
    {
        var sb = new StringBuilder();
        sb.Append(MagicObject).Append(" 1 — link with `dotcc <objs> -o <out>`.\n");
        sb.Append(FragMain).Append(mainArity).Append('\n');
        if (mainReturnsVoid) { sb.Append(FragMainVoid).Append("1").Append('\n'); }
        if (mainReturnsErrUnion) { sb.Append(FragMainErr).Append(mainErrPayloadIsVoid ? "v" : "i").Append('\n'); }
        // Import candidates + defined names, for the link step's resolution. Names
        // have no spaces (C identifiers), so the type — which does (`delegate*
        // unmanaged[Cdecl]<int, int>`) — is everything after the first space.
        foreach (var (name, ft) in importSpecs) { sb.Append(FragImport).Append(name).Append(' ').Append(ft).Append('\n'); }
        foreach (var d in defNames) { sb.Append(FragDef).Append(d).Append('\n'); }
        // Types are tagged by name so the link step can union them across TUs.
        foreach (var (name, text) in typeDecls)
        {
            sb.Append(FragType).Append(name).Append('\n').Append(text);
        }
        sb.Append(FragSect).Append("aliases\n").Append(aliases);
        sb.Append(FragSect).Append("globals\n").Append(globals);
        sb.Append(FragSect).Append("functions\n").Append(functions);
        return sb.ToString();
    }

    /// <summary>
    /// Link `.cs` object fragments (from <see cref="EmitObject"/>) into one
    /// program: concatenate functions, union types/aliases/globals (deduping a
    /// shared header's declarations), then wrap in the shell + runtime.
    /// </summary>
    public static string LinkObjects(
        IReadOnlyList<string> objectPaths, EmitMode emit = EmitMode.File, bool debugHeap = false,
        ImportOptions? imports = null)
    {
        var libraryMode = emit == EmitMode.SharedLib;
        var typeByName = new Dictionary<string, string>(StringComparer.Ordinal); // first wins
        var typeOrder = new List<string>();
        var aliasLines = new List<string>();
        var aliasSeen = new HashSet<string>(StringComparer.Ordinal);
        var globalLines = new List<string>();
        var globalSeen = new HashSet<string>(StringComparer.Ordinal);
        var functions = new StringBuilder();
        var mainArity = -1;
        var mainReturnsVoid = false;
        var mainReturnsErrUnion = false;
        var mainErrPayloadIsVoid = false;
        // Import resolution across fragments: a candidate name → its fn-ptr type, and
        // every name some fragment DEFINES. A candidate survives iff no fragment defines it.
        var importSpecs = new Dictionary<string, string>(StringComparer.Ordinal);
        var definedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in objectPaths)
        {
            var text = File.ReadAllText(path).ReplaceLineEndings("\n");
            if (!text.Contains(MagicObject, StringComparison.Ordinal))
            {
                throw new CompileException(
                    $"'{Path.GetFileName(path)}' is not a dotcc object — no '{MagicObject}' marker. " +
                    "Link expects `--emit=obj` fragments, not a program or hand-written .cs.");
            }
            // Walk the fragment line by line, routing into the current bucket.
            string section = "";            // "type:<name>" | "aliases" | "globals" | "functions"
            var buf = new StringBuilder();
            void FlushType()
            {
                if (section.StartsWith("type:", StringComparison.Ordinal))
                {
                    var name = section["type:".Length..];
                    if (!typeByName.ContainsKey(name)) { typeByName[name] = buf.ToString(); typeOrder.Add(name); }
                }
                buf.Clear();
            }
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith(FragMainErr, StringComparison.Ordinal))
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
                else if (line.StartsWith(FragType, StringComparison.Ordinal))
                {
                    FlushType();
                    section = "type:" + line[FragType.Length..];
                }
                else if (line.StartsWith(FragSect, StringComparison.Ordinal))
                {
                    FlushType();
                    section = line[FragSect.Length..];
                }
                else if (section.StartsWith("type:", StringComparison.Ordinal))
                {
                    buf.Append(line).Append('\n');
                }
                else if (section == "aliases")
                {
                    if (line.Length > 0 && aliasSeen.Add(line)) { aliasLines.Add(line); }
                }
                else if (section == "globals")
                {
                    if (line.Length > 0 && globalSeen.Add(line)) { globalLines.Add(line); }
                }
                else if (section == "functions")
                {
                    functions.Append(line).Append('\n');
                }
            }
            FlushType();
        }

        if (!libraryMode && mainArity < 0)
        {
            throw new CompileException("no `main` function defined in any linked object.");
        }

        var structDecls = new StringBuilder();
        foreach (var name in typeOrder) { structDecls.Append(typeByName[name]); }
        var aliasText = aliasLines.Count > 0 ? string.Join("\n", aliasLines) + "\n" : "";
        var globalText = globalLines.Count > 0 ? string.Join("\n", globalLines) + "\n" : "";
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
        return BuildShell(mainArity, functions.ToString(), structDecls.ToString(), aliasText, globalText,
                          emit, System.Array.Empty<EmitHelpers.Export>(), debugHeap, importsClass,
                          importsAreStatic: false, mainReturnsVoid: mainReturnsVoid,
                          mainReturnsErrUnion: mainReturnsErrUnion, mainErrPayloadIsVoid: mainErrPayloadIsVoid);
    }

}
