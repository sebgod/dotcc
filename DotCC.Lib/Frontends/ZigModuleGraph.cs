#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotCC.Ir;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>
/// One Zig source file in the <see cref="ZigModuleGraph"/> — the unit an <c>@import</c> resolves to
/// (road-to-zig-std S1). Holds the file's <em>resilient</em> parse (so a single un-implemented grammar
/// construct doesn't sink the whole module — only the decls a program actually references need to
/// parse) and, lazily, the list of top-level declarations that parsed cleanly. Lowering state is added
/// as the graph grows (S2); this type stays a parse-level holder.
/// </summary>
internal sealed class ZigModule
{
    /// <summary>Canonical absolute path — the graph's registry key (one module per file).</summary>
    public string Path { get; }

    /// <summary>The resilient parse: the tree over the well-formed top-level decls, plus one
    /// <see cref="ParseErrorInfo"/> per decl that failed to parse and was skipped.</summary>
    public ResilientParseResult Parse { get; }

    private IReadOnlyList<Item>? _decls;

    /// <summary>This module's own lazy-lowering context (road-to-zig-std S2), set when the module is
    /// first prepared. A reference resolves + lowers a function on demand through it
    /// (<see cref="ZigLowering.EnsureDeclLowered"/>); null until prepared. Also the once-only memo that
    /// breaks an import cycle (set before preparing).</summary>
    public ZigLowering? Lowering { get; set; }

    public ZigModule(string path, ResilientParseResult parse,
        IReadOnlyDictionary<string, ParseErrorInfo>? skippedDecls = null)
    {
        Path = path;
        Parse = parse;
        SkippedDecls = skippedDecls ?? new Dictionary<string, ParseErrorInfo>(StringComparer.Ordinal);
    }

    /// <summary>Each top-level declaration the resilient parse SKIPPED, by name → the error that
    /// skipped it. A skipped decl is absent from <see cref="Decls"/>, so without this a reference to it
    /// reads as "unresolved name" and hides the real wall, which is a parse gap
    /// (<c>findScalarPos</c> in <c>mem.zig</c>: a <c>comptime if</c> statement).</summary>
    public IReadOnlyDictionary<string, ParseErrorInfo> SkippedDecls { get; }

    /// <summary>Top-level declarations that parsed cleanly, in source order (a skipped decl is absent
    /// here and recorded in <see cref="Errors"/>). Flattened via the shared <see cref="ZigLowering"/>
    /// list walker over the <c>Decls</c> spine.</summary>
    public IReadOnlyList<Item> Decls => _decls ??= ZigLowering.Flatten(Parse.Tree);

    /// <summary>Decls that failed to parse and were skipped during recovery (empty ⇒ a clean parse).</summary>
    public IReadOnlyList<ParseErrorInfo> Errors => Parse.Errors;
}

/// <summary>
/// The lowering tables one <c>@import</c> chain SHARES — a root unit creates one and every module it
/// imports (transitively) is handed the same instance, the way the flat error-code space already was.
/// Each table is keyed by a name that is unique across the emitted program (a container's, an enum's),
/// so sharing is what lets a call site reach a member of a type another module declared — the read side
/// of type-position navigation (road-to-zig-std S4d). Two independent ROOT units get separate scopes:
/// they see each other only through <c>@import</c>, exactly as before.
/// </summary>
internal sealed class ZigImportScope
{
    /// <summary>Per container name, method name → the mangled free function it lowered to.</summary>
    public Dictionary<string, Dictionary<string, Symbol>> Methods { get; } = new(StringComparer.Ordinal);

    /// <summary>Per enum name, member name → its enum-constant symbol.</summary>
    public Dictionary<string, Dictionary<string, Symbol>> EnumMembers { get; } = new(StringComparer.Ordinal);

    /// <summary>(container, method) → the module that owns an as-yet-undeclared lazy method + its AST.</summary>
    public Dictionary<(string container, string method), (ZigLowering owner, Item decl)> LazyMethodDecls { get; } = new();

    /// <summary>A file-as-struct container's IR name → the module whose top-level functions are its
    /// methods (road-to-zig-std G3: <c>Io/Writer.zig</c> declares fields at file scope).</summary>
    public Dictionary<string, ZigLowering> FileStructOwners { get; } = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _modulePrefixes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedPrefixes = new(StringComparer.Ordinal);

    /// <summary>The IR-name prefix for an IMPORTED module's containers (module-qualified container
    /// naming): the module's path relative to the std root when it lives there (<c>fmt</c>,
    /// <c>Io_Writer</c>), else its file stem (<c>util</c>), sanitized to an identifier and de-duplicated
    /// across this import chain (<c>util_2</c>) — so two modules' same-named containers, and a std
    /// container named like one of dotcc's runtime types (<c>std.fmt.Alignment</c>), never share an
    /// emitted C# type name. Stable per module path (memoized).</summary>
    public string ModulePrefixFor(string modulePath, string? stdDir)
    {
        if (_modulePrefixes.TryGetValue(modulePath, out var known)) { return known; }
        string rel;
        if (stdDir is not null
            && modulePath.StartsWith(stdDir, StringComparison.OrdinalIgnoreCase)
            && modulePath.Length > stdDir.Length)
        {
            rel = modulePath[stdDir.Length..].TrimStart('/', '\\');
        }
        else
        {
            rel = System.IO.Path.GetFileName(modulePath);
        }
        if (rel.EndsWith(".zig", StringComparison.OrdinalIgnoreCase)) { rel = rel[..^4]; }
        var sb = new System.Text.StringBuilder(rel.Length);
        foreach (var ch in rel) { sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_'); }
        var baseName = sb.Length == 0 || char.IsAsciiDigit(sb[0]) ? "m" + sb : sb.ToString();
        var prefix = baseName;
        for (var n = 2; !_usedPrefixes.Add(prefix); n++) { prefix = baseName + "_" + n; }
        _modulePrefixes[modulePath] = prefix;
        return prefix;
    }
}

/// <summary>
/// The Zig module graph (road-to-zig-std S1): resolves <c>@import</c> specs to <see cref="ZigModule"/>s
/// and caches one module per canonical path, so an import cycle (legal and common in std) loads each
/// file once. Files are parsed <em>resiliently</em> (<see cref="Parser.ParseInputResilient"/>) so a decl
/// with an un-implemented construct is skipped rather than sinking its whole module — the parse-layer
/// half of lazy, decl-driven compilation. Lowering-on-demand rides on top of this in S2.
/// </summary>
/// <remarks>
/// Pre-baked-safe: the resilient parser is driven with caller-supplied recovery sets — the Zig top-level
/// declaration starters (<c>fn pub const var extern export comptime threadlocal test</c> + <c>IDENT</c>
/// for the file-as-struct field form) and the bracket pairs — resolved by NAME from the generated
/// grammar's symbol table (so the ids track the grammar, never hard-coded).
/// </remarks>
internal sealed class ZigModuleGraph
{
    private readonly Parser _parser;
    private readonly IReadOnlyDictionary<string, LexRule[]> _lexerTable;
    private readonly IReadOnlySet<int> _syncTerminals;
    private readonly IReadOnlySet<int> _openBrackets;
    private readonly IReadOnlySet<int> _closeBrackets;

    /// <summary>The grammar ids <see cref="FindSkippedDecls"/> scans for: a declaration keyword, then
    /// the IDENT it names.</summary>
    private readonly int _identId;
    private readonly HashSet<int> _declKeywordIds;
    private readonly Dictionary<string, ZigModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ZigLowering> _lowerings = new();

    /// <summary>The real <c>std.zig</c> root file path (from <c>--zig-lib-dir</c> / the
    /// <c>DOTCC_ZIG_LIB_DIR</c> env), or null when no std source tree is configured — then
    /// <c>@import("std")</c> stays purely on the curated path and a non-curated <c>std.x</c> errors.</summary>
    internal string? StdRootPath { get; }

    public ZigModuleGraph(string? stdRootPath = null)
    {
        _parser = Zig.BuildParser(Zig.IdentityVisitor.Instance);
        _lexerTable = Zig.BuildLexer();
        (_syncTerminals, _openBrackets, _closeBrackets) = BuildRecoverySets(_parser.Grammar);
        var names = _parser.Grammar.SymbolNames;
        int Id(string name) => Array.FindIndex(names, n => n.Name == name);
        _identId = Id("IDENT");
        _declKeywordIds = new HashSet<int> { Id("fn"), Id("const"), Id("var") };
        StdRootPath = stdRootPath;
    }

    /// <summary>Resolve an <c>@import</c> spec to a module, parsing (resiliently) and caching on first
    /// touch. A relative spec (<c>"./x.zig"</c> / <c>"x.zig"</c>) resolves against
    /// <paramref name="importerDir"/>; an absolute path is used as-is. (<c>"std"</c> and the synthetic
    /// modules are layered on in a later step.) Throws if the file can't be read — a referenced import
    /// that doesn't exist is a loud error, not a silent skip.</summary>
    public ZigModule Load(string spec, string importerDir)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(spec) ? spec : Path.Combine(importerDir, spec));
        return LoadPath(full);
    }

    /// <summary>Resolve a SYNTHETIC module — <c>builtin</c> / <c>root</c>, the compiler-provided
    /// modules with no file on disk (road-to-zig-std S3). Generated as Zig source and cached under a
    /// virtual path, so from here on it is an ordinary <see cref="ZigModule"/>: the same parse, the
    /// same lazy preparation, the same navigation. Null when the spec names no synthetic module.</summary>
    public ZigModule? LoadSynthetic(string spec)
    {
        if (ZigSyntheticModules.PathForSpec(spec) is not { } path) { return null; }
        if (_modules.TryGetValue(path, out var existing)) { return existing; }
        var module = ParseSource(path, ZigSyntheticModules.SourceForPath(path));
        _modules[path] = module;
        return module;
    }

    /// <summary>Resolve a canonical file path to a module (parse + cache on first touch).</summary>
    public ZigModule LoadPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (_modules.TryGetValue(full, out var existing))
        {
            return existing;
        }
        var source = File.ReadAllText(full);
        var module = ParseSource(full, source);
        _modules[full] = module;
        return module;
    }

    /// <summary>Parse <paramref name="source"/> into a module WITHOUT going through the filesystem or the
    /// cache — the seam unit tests drive, and the primitive <see cref="LoadPath"/> builds on.</summary>
    public ZigModule ParseSource(string path, string source)
    {
        using var lexer = BytesLexer.FromString(source, _lexerTable);
        using var tokens = new SyncLATokenIterator(lexer);
        var result = _parser.ParseInputResilient(tokens, _syncTerminals, _openBrackets, _closeBrackets);
        return new ZigModule(path, result, result.Errors.Count == 0 ? null : FindSkippedDecls(source, result.Errors));
    }

    /// <summary>Name the top-level declaration each parse error skipped. The error records only where
    /// skipping began, inside the declaration, so re-lex the file (only a file that HAD errors) and take
    /// the last top-level <c>fn NAME</c> / <c>const NAME</c> / <c>var NAME</c> before that point, at
    /// bracket depth 0 so a method or local inside it does not count. The first error of a declaration
    /// names it.</summary>
    private Dictionary<string, ParseErrorInfo> FindSkippedDecls(string source, IReadOnlyList<ParseErrorInfo> errors)
    {
        var starts = new List<(long offset, string name)>();
        using (var lexer = BytesLexer.FromString(source, _lexerTable))
        {
            var depth = 0;
            var pendingDeclKeyword = false;
            while (lexer.MoveNext() && lexer.Current is { } t && t.ID != Item.EOF.ID)
            {
                if (_openBrackets.Contains(t.ID)) { depth++; }
                else if (_closeBrackets.Contains(t.ID)) { depth = Math.Max(0, depth - 1); }
                if (depth == 0 && pendingDeclKeyword && t.ID == _identId && t.Content is string name)
                {
                    starts.Add((t.Position.ByteOffset, name));
                }
                pendingDeclKeyword = depth == 0 && _declKeywordIds.Contains(t.ID);
            }
        }
        var skipped = new Dictionary<string, ParseErrorInfo>(StringComparer.Ordinal);
        foreach (var error in errors)
        {
            var at = error.SkippedFrom.ByteOffset;
            string? owner = null;
            foreach (var (offset, name) in starts)
            {
                if (offset > at) { break; }
                owner = name;
            }
            if (owner is not null) { skipped.TryAdd(owner, error); }
        }
        return skipped;
    }

    /// <summary>Register a lazily-prepared module's lowering so the top-level drain reaches its pending
    /// function bodies (road-to-zig-std S2).</summary>
    internal void RegisterLowering(ZigLowering lowering) => _lowerings.Add(lowering);

    /// <summary>Drain every lazy module's enqueued function bodies at TOP LEVEL, to a fixpoint. Lowering a
    /// body may reference more decls (in this or another module) or prepare a NEW module, so this loops
    /// until no registered module has pending bodies. Called once after the root units are lowered.</summary>
    internal void DrainAll()
    {
        bool any;
        do
        {
            any = false;
            for (var i = 0; i < _lowerings.Count; i++)
            {
                if (_lowerings[i].HasPendingBodies)
                {
                    _lowerings[i].DrainPendingBodies();
                    any = true;
                }
            }
        }
        while (any);
    }

    /// <summary>The Zig recovery sets for <see cref="Parser.ParseInputResilient"/>, resolved by symbol
    /// NAME from the grammar's symbol table so the numeric ids follow the grammar. Sync terminals are the
    /// top-level declaration starters (a broken decl resynchronises at the next one); brackets track
    /// nesting so a starter <em>inside</em> a broken decl isn't mistaken for a top-level boundary.</summary>
    internal static (IReadOnlySet<int> sync, IReadOnlySet<int> open, IReadOnlySet<int> close) BuildRecoverySets(Grammar grammar)
    {
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = grammar.SymbolNames;
        for (var i = 0; i < names.Length; i++)
        {
            byName[names[i].Name] = i;
        }
        int Id(string name) => byName.TryGetValue(name, out var id)
            ? id
            : throw new InvalidOperationException(
                $"zig grammar has no symbol '{name}' — resilient-parse recovery sets can't be built");

        // Top-level declaration starters: fn/pub/const/var/extern/export/comptime/threadlocal/test, plus
        // IDENT for the file-as-struct top-level FIELD form (`name: T,` at file scope).
        var sync = new HashSet<int>
        {
            Id("fn"), Id("pub"), Id("const"), Id("var"), Id("extern"),
            Id("export"), Id("comptime"), Id("threadlocal"), Id("test"), Id("IDENT"),
        };
        var open = new HashSet<int> { Id("{"), Id("("), Id("[") };
        var close = new HashSet<int> { Id("}"), Id(")"), Id("]") };
        return (sync, open, close);
    }
}
