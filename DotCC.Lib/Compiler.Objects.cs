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
    // `storage:<name> <local|extern>`: the storage a global's field points at (an array's),
    // which the link places before every object's globals, so any initializer can take
    // its address.
    private const string FragStorage = "//!!dotcc-obj storage:";
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
    // `ctor:<priority> <name>`: a [[gnu::constructor]] function, which the program's entry
    // calls before main, by priority and then in link order.
    private const string FragCtor = "//!!dotcc-obj ctor:";

    // The uniform "magic" first line every dotcc-generated `.cs` carries, so any
    // file can be classified at a glance:
    //   //!dotcc program <v>   — a complete program (file/csproj/build/-shared)
    //   //!dotcc object  <v>   — a per-TU object fragment (--emit=obj), for linking
    // (A file-based program's `#:property` directives precede it; otherwise it's
    // line 1.) Scan the first few lines for these.
    private const string MagicObject = "//!dotcc object";
    // The object format this dotcc writes and links: 4 carries the C# signature of each
    // function a library can export (3 carried none, so a library linked from objects
    // exported nothing); 3 carries one record per definition, with its linkage, and a
    // global's storage apart from its initializer (2 had no storage records, 1 had one
    // section of each kind and no types).
    private const string ObjectFormat = "4";
    // `export:<name>TAB<return type>(TAB<parameter type>TAB<parameter name>)*`: a function with
    // external linkage a shared or managed library exports, in its C# spelling (the export
    // wrapper's signature; a C# type has no tab, though it can have `, `). Not variadic (an
    // [UnmanagedCallersOnly] method takes no `params` array) and not `main`.
    private const string FragExport = "//!!dotcc-obj export:";

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
        IReadOnlyList<(string Name, string FieldType)> importSpecs, IEnumerable<string> defNames,
        IReadOnlyList<EmitHelpers.Export> exports, bool mainReturnsVoid = false,
        bool mainReturnsErrUnion = false, bool mainErrPayloadIsVoid = false, bool pythonShim = false,
        IReadOnlyList<(int Priority, string Name)>? constructors = null)
    {
        var sb = new StringBuilder();
        sb.Append(MagicObject).Append(' ').Append(ObjectFormat).Append(" — link with `dotcc <objs> -o <out>`.\n");
        sb.Append(FragMain).Append(mainArity).Append('\n');
        if (mainReturnsVoid) { sb.Append(FragMainVoid).Append("1").Append('\n'); }
        if (mainReturnsErrUnion) { sb.Append(FragMainErr).Append(mainErrPayloadIsVoid ? "v" : "i").Append('\n'); }
        if (pythonShim) { sb.Append(FragRuntimePython).Append('\n'); }
        foreach (var (priority, name) in constructors ?? [])
        {
            sb.Append(FragCtor).Append(priority).Append(' ').Append(name).Append('\n');
        }
        // Import candidates + defined names, for the link step's resolution. Names
        // have no spaces (C identifiers), so the type — which does (`delegate*
        // unmanaged[Cdecl]<int, int>`) — is everything after the first space.
        foreach (var (name, ft) in importSpecs) { sb.Append(FragImport).Append(name).Append(' ').Append(ft).Append('\n'); }
        foreach (var d in defNames) { sb.Append(FragDef).Append(d).Append('\n'); }
        foreach (var e in exports)
        {
            sb.Append(FragExport).Append(e.Name).Append('\t').Append(e.ReturnType);
            foreach (var p in e.Params) { sb.Append('\t').Append(p.Type).Append('\t').Append(p.Name); }
            sb.Append('\n');
        }
        foreach (var r in records)
        {
            var linkage = r.TuLocal ? " local" : " extern";
            sb.Append(r.Kind switch
            {
                Backends.LinkRecordKind.Type => FragType + r.Name,
                Backends.LinkRecordKind.OpaqueType => FragOpaque + r.Name,
                Backends.LinkRecordKind.Global => FragGlobal + r.Name + linkage,
                Backends.LinkRecordKind.Storage => FragStorage + r.Name + linkage,
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

    /// <summary>One function, object or storage record of an object fragment: its C# text as the
    /// object carries it, whether its name has internal linkage, and the object it came from.</summary>
    private readonly record struct LinkedRecord(string Name, string Text, bool TuLocal, string From);

    /// <summary>
    /// The definitions a set of object fragments carries, read and checked as a C linker would:
    /// every type once (the objects that include one header agree on it, and a type defined
    /// differently in two objects is an error, since the program has one), each function and
    /// object once (a second definition of a name is gcc's "multiple definition" error). Every
    /// text is kept as its object has it; the program or library shell that wraps them decides
    /// their C# accessibility.
    /// </summary>
    private sealed class ObjectSet
    {
        /// <summary>Each type's text and the object that defined it first.</summary>
        public Dictionary<string, (string Text, string From)> Types { get; } = new(StringComparer.Ordinal);

        /// <summary>The types in the order the objects first define them.</summary>
        public List<string> TypeOrder { get; } = new();

        /// <summary>The placeholder of each struct some object only points at.</summary>
        public Dictionary<string, string> OpaqueTypes { get; } = new(StringComparer.Ordinal);

        /// <summary>The storage a global's field points at, placed before every global.</summary>
        public List<LinkedRecord> Storage { get; } = new();

        /// <summary>The file-scope objects, in link order.</summary>
        public List<LinkedRecord> Globals { get; } = new();

        /// <summary>The functions, in link order.</summary>
        public List<LinkedRecord> Functions { get; } = new();

        /// <summary>Import candidates across fragments: a name, and its function-pointer type.</summary>
        public Dictionary<string, string> ImportSpecs { get; } = new(StringComparer.Ordinal);

        /// <summary>Every name some fragment defines (an import candidate survives only if none does).</summary>
        public HashSet<string> DefinedNames { get; } = new(StringComparer.Ordinal);

        /// <summary>The functions a library exports, with their C# signatures.</summary>
        public List<EmitHelpers.Export> Exports { get; } = new();

        /// <summary>The <c>[[gnu::constructor]]</c> functions, in link order.</summary>
        public List<(int Priority, string Name)> Constructors { get; } = new();

        /// <summary>The parameter count of <c>main</c>, or -1 when no object defines it.</summary>
        public int MainArity { get; set; } = -1;

        /// <summary><c>main</c> returns <c>void</c>.</summary>
        public bool MainReturnsVoid { get; set; }

        /// <summary><c>main</c> returns an error union (Zig).</summary>
        public bool MainReturnsErrUnion { get; set; }

        /// <summary><c>main</c>'s error union has a <c>void</c> payload.</summary>
        public bool MainErrPayloadIsVoid { get; set; }

        /// <summary>Some unit included the synthetic <c>&lt;Python.h&gt;</c>.</summary>
        public bool PythonShim { get; set; }

        /// <summary>The type declarations as the objects carry them, in definition order, then a
        /// placeholder for each struct only pointed at, each passed through <paramref name="declare"/>.</summary>
        public string TypeDecls(Func<string, string> declare)
        {
            var sb = new StringBuilder();
            foreach (var name in TypeOrder) { sb.Append(declare(Types[name].Text)); }
            // A struct some object only points at: its definition if any object has one (above),
            // else one placeholder.
            foreach (var (name, text) in OpaqueTypes)
            {
                if (!Types.ContainsKey(name)) { sb.Append(declare(text)); }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Link `.cs` object fragments (from <see cref="EmitObject"/>) into one program, as a C
    /// linker would (see <see cref="ReadObjects"/>), then wrap them in the shell + runtime.
    /// <paramref name="posixPaths"/> is <c>-fposix-paths</c>, as for <see cref="EmitCSharp"/>:
    /// a property of the program, so it is given when linking. A <c>-l</c> name with a manifest
    /// in a <c>-L</c> directory is a managed library (<see cref="ManagedLibraryManifests"/>): the
    /// program uses its functions, objects, types and runtime instead of carrying its own; the
    /// other <c>-l</c> names are native libraries, imported as before.
    /// </summary>
    public static string LinkObjects(
        IReadOnlyList<string> objectPaths, EmitMode emit = EmitMode.File, bool debugHeap = false,
        ImportOptions? imports = null, bool posixPaths = false)
    {
        if (emit == EmitMode.Assembly)
        {
            throw new ArgumentException("a managed library is linked by LinkAssembly, which also writes its manifest", nameof(emit));
        }
        var libraryMode = emit == EmitMode.SharedLib;
        var set = ReadObjects(objectPaths);
        if (!libraryMode && set.MainArity < 0)
        {
            throw new CompileException("no `main` function defined in any linked object.");
        }

        var managed = ManagedLibraryManifests(imports).Select(ReadManagedLibrary).ToList();
        if (managed.Count > 0)
        {
            if (emit != EmitMode.Csproj)
            {
                throw new CompileException("linking against a managed library (-l" + managed[0].AssemblyName
                    + ") needs a project: link with --emit=csproj or --emit=build");
            }
            ResolveAgainst(set, managed);
            // What's left of -l is native.
            var names = managed.Select(l => l.AssemblyName).ToHashSet(StringComparer.Ordinal);
            imports = imports is null ? null : imports with { LinkLibraries = imports.LinkLibraries.Where(n => !names.Contains(n)).ToList() };
        }

        // Import mode at link: bind the candidates no fragment defines (a name defined
        // in any object — function or global — is resolved internally, not imported).
        // Without `-l`, survivors stay unresolved → the same CS0103 as a normal link.
        var importsClass = "";
        if (imports is { LinkLibraries.Count: > 0 })
        {
            var survivors = set.ImportSpecs
                .Where(kv => !set.DefinedNames.Contains(kv.Key))
                .Select(kv => (kv.Key, kv.Value))
                .OrderBy(p => p.Item1, StringComparer.Ordinal)
                .ToList();
            if (survivors.Count > 0) { importsClass = RenderImportsClass(survivors, imports, libraryMode); }
        }
        var globals = string.Concat(set.Storage.Select(r => r.Text)) + string.Concat(set.Globals.Select(r => r.Text));
        return BuildShell(set.MainArity, set.Functions.Select(f => f.Text.TrimEnd('\n')).ToList(), set.TypeDecls(t => t), "", globals,
                          emit, set.Exports, debugHeap, importsClass,
                          importsAreStatic: false, mainReturnsVoid: set.MainReturnsVoid,
                          mainReturnsErrUnion: set.MainReturnsErrUnion, mainErrPayloadIsVoid: set.MainErrPayloadIsVoid,
                          pythonShim: set.PythonShim, posixPaths: posixPaths,
                          libraryClasses: managed.SelectMany(l => l.Classes).ToList(),
                          constructors: set.Constructors);
    }

    /// <summary>A managed library a program links against, as its manifest describes it.</summary>
    /// <param name="Path">The manifest file.</param>
    /// <param name="AssemblyName">The library's assembly.</param>
    /// <param name="Classes">The classes the program surfaces by bare name.</param>
    /// <param name="Defs">The functions and objects with external linkage it defines.</param>
    /// <param name="Types">Each type it defines, with its object text.</param>
    /// <param name="OpaqueTypes">The structs it only points at.</param>
    /// <param name="PythonShim">It carries the runtime's abi3 shim for the synthetic <c>&lt;Python.h&gt;</c>.</param>
    private sealed record ManagedLibrary(
        string Path, string AssemblyName, IReadOnlyList<string> Classes, HashSet<string> Defs,
        Dictionary<string, string> Types, HashSet<string> OpaqueTypes, bool PythonShim);

    /// <summary>
    /// The manifests of the managed libraries among <paramref name="imports"/>' <c>-l</c> names:
    /// <c>&lt;dir&gt;/&lt;name&gt;.dotcc-lib</c> in the first <c>-L</c> directory that has one, as a
    /// linker takes the first match on its search path. A name with no manifest is a native
    /// library. The program's project references each one's project, beside its manifest.
    /// </summary>
    public static IReadOnlyList<string> ManagedLibraryManifests(ImportOptions? imports)
    {
        if (imports is null) { return System.Array.Empty<string>(); }
        var found = new List<string>();
        foreach (var name in imports.LinkLibraries)
        {
            foreach (var dir in imports.LibraryDirs)
            {
                var manifest = Path.Combine(dir, LibraryManifestFile(name));
                if (File.Exists(manifest)) { found.Add(manifest); break; }
            }
        }
        return found;
    }

    /// <summary>Read a managed library's manifest (written by <see cref="LinkAssembly"/>).</summary>
    private static ManagedLibrary ReadManagedLibrary(string manifestPath)
    {
        var text = File.ReadAllText(manifestPath).ReplaceLineEndings("\n");
        var from = Path.GetFileName(manifestPath);
        if (!text.StartsWith(MagicLibrary + " ", StringComparison.Ordinal))
        {
            throw new CompileException($"'{from}' is not a dotcc library manifest — no '{MagicLibrary}' marker");
        }
        var rest = text[(MagicLibrary.Length + 1)..];
        var format = rest[..rest.IndexOfAny([' ', '\n'])];
        if (format != LibraryFormat)
        {
            throw new CompileException($"'{from}' is a dotcc library manifest of format {format}; this dotcc links format {LibraryFormat} (relink the library)");
        }
        string? assembly = null;
        var classes = new List<string>();
        var defs = new HashSet<string>(StringComparer.Ordinal);
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var opaque = new HashSet<string>(StringComparer.Ordinal);
        var pythonShim = false;
        string? typeName = null;
        var body = new StringBuilder();
        void EndType()
        {
            if (typeName is not null) { types[typeName] = body.ToString(); }
            typeName = null;
            body.Clear();
        }
        var lines = text.Split('\n');
        foreach (var line in lines.AsSpan(1, (text.EndsWith('\n') ? lines.Length - 1 : lines.Length) - 1))
        {
            if (line.StartsWith(FragType, StringComparison.Ordinal)) { EndType(); typeName = line[FragType.Length..]; }
            else if (line.StartsWith(FragOpaque, StringComparison.Ordinal)) { EndType(); opaque.Add(line[FragOpaque.Length..]); typeName = null; }
            else if (line.StartsWith(LibAssembly, StringComparison.Ordinal)) { assembly = line[LibAssembly.Length..]; }
            else if (line.StartsWith(LibClass, StringComparison.Ordinal)) { classes.Add(line[LibClass.Length..]); }
            else if (line.StartsWith(LibDef, StringComparison.Ordinal)) { defs.Add(line[LibDef.Length..]); }
            else if (line == LibRuntimePython) { pythonShim = true; }
            else if (typeName is not null) { body.Append(line).Append('\n'); }
        }
        EndType();
        return new ManagedLibrary(manifestPath, assembly ?? throw new CompileException($"'{from}' names no assembly"),
                                  classes, defs, types, opaque, pythonShim);
    }

    /// <summary>
    /// Resolve a program's objects against the managed libraries it links: a type a library
    /// defines is the library's, so the program declares it only if no library does, and must
    /// agree with it where one does (a type the library only points at is the library's too, and
    /// the program cannot complete it, since the two would be different C# types); a function or
    /// object a library defines cannot be defined again; and the library's names count as defined
    /// for import mode. Every conflict is reported, as <see cref="ReadObjects"/> does.
    /// </summary>
    private static void ResolveAgainst(ObjectSet set, IReadOnlyList<ManagedLibrary> libraries)
    {
        var errors = new List<string>();
        var owner = new Dictionary<string, ManagedLibrary>(StringComparer.Ordinal);
        foreach (var lib in libraries)
        {
            foreach (var d in lib.Defs)
            {
                if (owner.TryGetValue(d, out var first))
                {
                    errors.Add($"multiple definition of '{d}' in library '{lib.AssemblyName}', first defined in library '{first.AssemblyName}'");
                }
                else { owner[d] = lib; }
            }
        }
        if (set.PythonShim && !libraries.Any(l => l.PythonShim))
        {
            errors.Add("the program includes <Python.h>, but no managed library it links carries the Python shim");
        }
        foreach (var r in set.Functions.Concat(set.Globals).Where(r => !r.TuLocal))
        {
            if (owner.TryGetValue(r.Name, out var lib))
            {
                errors.Add($"multiple definition of '{r.Name}' in '{r.From}', also defined in library '{lib.AssemblyName}'");
            }
        }
        set.TypeOrder.RemoveAll(name =>
        {
            foreach (var lib in libraries)
            {
                if (lib.Types.TryGetValue(name, out var text))
                {
                    if (text != set.Types[name].Text)
                    {
                        errors.Add($"type '{name}' is defined differently in '{set.Types[name].From}' and library '{lib.AssemblyName}'");
                    }
                    return true;
                }
                if (lib.OpaqueTypes.Contains(name))
                {
                    errors.Add($"type '{name}' is only declared in library '{lib.AssemblyName}', but '{set.Types[name].From}' defines it");
                    return true;
                }
            }
            return false;
        });
        foreach (var name in set.OpaqueTypes.Keys.ToList())
        {
            if (libraries.Any(l => l.Types.ContainsKey(name) || l.OpaqueTypes.Contains(name))) { set.OpaqueTypes.Remove(name); }
        }
        if (errors.Count > 0)
        {
            throw new CompileException("link failed:\n" + string.Join("\n", errors.Take(20))
                + (errors.Count > 20 ? $"\n... and {errors.Count - 20} more" : ""));
        }
        set.DefinedNames.UnionWith(owner.Keys);
    }

    /// <summary>
    /// Read object fragments (from <see cref="EmitObject"/>) into one <see cref="ObjectSet"/>,
    /// failing with every conflict found: a type defined differently in two objects, and a
    /// second definition of a function or object with the same name.
    /// </summary>
    private static ObjectSet ReadObjects(IReadOnlyList<string> objectPaths)
    {
        var set = new ObjectSet();
        var types = set.Types;
        var opaqueTypes = set.OpaqueTypes;
        var definedIn = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        // Import resolution across fragments: a candidate name → its fn-ptr type, and
        // every name some fragment DEFINES. A candidate survives iff no fragment defines it.
        var importSpecs = set.ImportSpecs;
        var definedNames = set.DefinedNames;

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
                if (record.StartsWith(FragStorage, StringComparison.Ordinal))
                {
                    // Its global's record, which follows, is the definition checked below.
                    var (storageName, storageLocal) = RecordSpec(record[FragStorage.Length..]);
                    set.Storage.Add(new LinkedRecord(storageName, body, storageLocal, from));
                    return;
                }
                if (record.StartsWith(FragType, StringComparison.Ordinal))
                {
                    var name = record[FragType.Length..];
                    if (!types.TryGetValue(name, out var first))
                    {
                        types[name] = (body, from);
                        set.TypeOrder.Add(name);
                    }
                    else if (first.Text != body)
                    {
                        errors.Add($"type '{name}' is defined differently in '{from}' and '{first.From}'");
                    }
                    return;
                }
                var isFn = record.StartsWith(FragFn, StringComparison.Ordinal);
                var (symbol, tuLocal) = RecordSpec(record[(isFn ? FragFn : FragGlobal).Length..]);
                if (definedIn.TryGetValue(symbol, out var firstFrom))
                {
                    errors.Add($"multiple definition of '{symbol}' in '{from}', first defined in '{firstFrom}'");
                    return;
                }
                definedIn[symbol] = from;
                (isFn ? set.Functions : set.Globals).Add(new LinkedRecord(symbol, body, tuLocal, from));
            }
            // The file's final newline ends its last line; it opens no empty one.
            var lines = text.Split('\n');
            var lineCount = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
            foreach (var line in lines.AsSpan(0, lineCount))
            {
                if (line.StartsWith(FragType, StringComparison.Ordinal)
                    || line.StartsWith(FragOpaque, StringComparison.Ordinal)
                    || line.StartsWith(FragGlobal, StringComparison.Ordinal)
                    || line.StartsWith(FragStorage, StringComparison.Ordinal)
                    || line.StartsWith(FragFn, StringComparison.Ordinal))
                {
                    Flush();
                    record = line;
                }
                else if (line.StartsWith(FragMainErr, StringComparison.Ordinal))
                {
                    // `main-err:` (v|i) — an error-union main (`!void`/`!<int>`). Disjoint from
                    // the `main-void:` / `main:` markers (the char after "main" differs).
                    set.MainReturnsErrUnion = true;
                    set.MainErrPayloadIsVoid = line[FragMainErr.Length..].Trim() == "v";
                }
                else if (line.StartsWith(FragMainVoid, StringComparison.Ordinal))
                {
                    // `main-void:` and `main:` are disjoint markers (the char after
                    // "main" differs: '-' vs ':'), so this branch and the next don't race.
                    if (line[FragMainVoid.Length..].Trim() == "1") { set.MainReturnsVoid = true; }
                }
                else if (line.StartsWith(FragCtor, StringComparison.Ordinal))
                {
                    var rest = line[FragCtor.Length..];
                    var sp = rest.IndexOf(' ');
                    if (sp > 0 && int.TryParse(rest[..sp], out var priority)) { set.Constructors.Add((priority, rest[(sp + 1)..])); }
                }
                else if (line.StartsWith(FragMain, StringComparison.Ordinal))
                {
                    if (int.TryParse(line[FragMain.Length..], out var m) && m >= 0) { set.MainArity = m; }
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
                else if (line.StartsWith(FragExport, StringComparison.Ordinal))
                {
                    var parts = line[FragExport.Length..].Split('\t');
                    if (parts.Length < 2 || parts.Length % 2 != 0)
                    {
                        throw new CompileException($"malformed export record in object: {line}");
                    }
                    var ps = new List<EmitHelpers.ExportParam>();
                    for (var i = 2; i < parts.Length; i += 2) { ps.Add(new EmitHelpers.ExportParam(parts[i], parts[i + 1])); }
                    set.Exports.Add(new EmitHelpers.Export(parts[0], parts[1], ps));
                }
                else if (line == FragRuntimePython)
                {
                    set.PythonShim = true;
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
        return set;
    }

    /// <summary>Split a record's <c>&lt;name&gt; &lt;local|extern&gt;</c> spec into the name and
    /// whether it has internal linkage.</summary>
    private static (string Name, bool TuLocal) RecordSpec(string spec)
    {
        var sp = spec.IndexOf(' ');
        return sp < 0 ? (spec, false) : (spec[..sp], spec[(sp + 1)..] == "local");
    }

    // ---- managed libraries (`-shared -fassembly`) ------------------------------
    // A managed library is the .NET counterpart of a shared object: an assembly other
    // dotcc programs and extension modules reference, sharing its functions, its objects
    // at their one address, its types and its runtime. Its manifest records what it
    // defines, for the link step of a program built against it.

    // The manifest's first line, and its version.
    private const string MagicLibrary = "//!dotcc library";
    private const string LibraryFormat = "1";
    // `assembly:<name>`: the assembly to reference. `class:<name>`: a class whose members
    // the program surfaces by bare name (`using static`). `def:<name>`: a function or object
    // with external linkage the library defines. The types follow as `type:` and `opaque:`
    // records, with each object's own text, so a program's objects are checked against them.
    private const string LibAssembly = "//!!dotcc-lib assembly:";
    private const string LibClass = "//!!dotcc-lib class:";
    private const string LibDef = "//!!dotcc-lib def:";
    private const string LibRuntimePython = "//!!dotcc-lib runtime:python";

    /// <summary>The file a managed library's manifest is written to, beside its project.</summary>
    public static string LibraryManifestFile(string assemblyName) => assemblyName + ".dotcc-lib";

    /// <summary>
    /// Link object fragments into a managed library named <paramref name="assemblyName"/>
    /// (<see cref="EmitMode.Assembly"/>): the objects are read and checked as for
    /// <see cref="LinkObjects"/>, then made visible to other assemblies the way a C shared
    /// library exports symbols. A function or object with external linkage is <c>public</c>,
    /// one with internal linkage (C <c>static</c>) <c>internal</c>, and every type
    /// <c>public</c>, since a public function's signature can only name public types. The
    /// classes carry the library's name (<see cref="LibraryClassPrefix"/>), so a program's own
    /// <c>DotCcProgram</c> does not hide them. A library can itself link against managed
    /// libraries (<paramref name="imports"/>), as a CPython extension module links against
    /// libpython: it then uses their functions, objects, types and runtime, as a program linked
    /// by <see cref="LinkObjects"/> does, and carries no runtime of its own. Returns the program
    /// and its manifest (<see cref="LibraryManifestFile"/>).
    /// </summary>
    public static (string Program, string Manifest) LinkAssembly(
        IReadOnlyList<string> objectPaths, string assemblyName, ImportOptions? imports = null)
    {
        var set = ReadObjects(objectPaths);
        var managed = ManagedLibraryManifests(imports).Select(ReadManagedLibrary).ToList();
        var managedNames = managed.Select(l => l.AssemblyName).ToHashSet(StringComparer.Ordinal);
        if (imports?.LinkLibraries.FirstOrDefault(n => !managedNames.Contains(n)) is { } native)
        {
            throw new CompileException($"a managed library links only managed libraries, and -l{native} has no "
                + $"{LibraryManifestFile(native)} in any -L directory");
        }
        if (managed.Count > 0) { ResolveAgainst(set, managed); }
        var prefix = LibraryClassPrefix(assemblyName);
        var fns = set.Functions.Select(f => WithAccess(f.Text.TrimEnd('\n'), f.TuLocal ? "internal" : "public")).ToList();
        var globals = string.Concat(set.Storage.Concat(set.Globals).Select(r => r.TuLocal ? InternalMembers(r.Text) : r.Text));
        var carriesRuntime = managed.Count == 0;
        var (program, classes) = BuildAssemblyShell(prefix, fns, set.TypeDecls(PublicTypes), globals, set.PythonShim, set.Exports,
                                                     managed.SelectMany(l => l.Classes).ToList());

        var manifest = new StringBuilder();
        manifest.Append(MagicLibrary).Append(' ').Append(LibraryFormat)
            .Append(" — link a program or extension module against it with `-L<dir> -l").Append(assemblyName).Append("`.\n");
        manifest.Append(LibAssembly).Append(assemblyName).Append('\n');
        foreach (var c in classes) { manifest.Append(LibClass).Append(c).Append('\n'); }
        if (carriesRuntime && set.PythonShim) { manifest.Append(LibRuntimePython).Append('\n'); }
        foreach (var r in set.Functions.Concat(set.Globals).Where(r => !r.TuLocal))
        {
            manifest.Append(LibDef).Append(r.Name).Append('\n');
        }
        foreach (var name in set.TypeOrder)
        {
            manifest.Append(FragType).Append(name).Append('\n').Append(set.Types[name].Text);
        }
        foreach (var (name, text) in set.OpaqueTypes.Where(kv => !set.Types.ContainsKey(kv.Key)))
        {
            manifest.Append(FragOpaque).Append(name).Append('\n').Append(text);
        }
        return (program, manifest.ToString());
    }

    /// <summary>The start of every class name a managed library's shell declares:
    /// <c>DotCcLib_</c> and the assembly name as an identifier.</summary>
    internal static string LibraryClassPrefix(string assemblyName)
        => "DotCcLib_" + new string(assemblyName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>A function record's text with <paramref name="access"/> on its declaration, which
    /// the backend emits as <c>static unsafe</c> with no access modifier (see
    /// <c>CSharpBackend.Run</c>) for the shell to supply.</summary>
    private static string WithAccess(string fnText, string access)
        => fnText.Replace("static unsafe ", access + " static unsafe ", StringComparison.Ordinal);

    /// <summary>A global or storage record's members as <c>internal</c>: the backend declares a
    /// file-scope object's field (or property) and its helpers <c>public</c> in the globals class.</summary>
    private static string InternalMembers(string globalText)
        => string.Join('\n', globalText.Split('\n').Select(line =>
            line.StartsWith("    public static ", StringComparison.Ordinal) ? "    internal static " + line["    public static ".Length..] : line));

    /// <summary>A type record's declarations as <c>public</c>: every type the backend emits
    /// starts a line with <c>unsafe struct</c> or <c>enum</c>, after any attributes.</summary>
    private static string PublicTypes(string typeText)
        => string.Join('\n', typeText.Split('\n').Select(line =>
            line.StartsWith("unsafe struct ", StringComparison.Ordinal) || line.StartsWith("enum ", StringComparison.Ordinal)
                ? "public " + line : line));
}
