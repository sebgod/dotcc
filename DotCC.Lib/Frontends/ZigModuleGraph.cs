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

    public ZigModule(string path, ResilientParseResult parse)
    {
        Path = path;
        Parse = parse;
    }

    /// <summary>Top-level declarations that parsed cleanly, in source order (a skipped decl is absent
    /// here and recorded in <see cref="Errors"/>). Flattened via the shared <see cref="ZigLowering"/>
    /// list walker over the <c>Decls</c> spine.</summary>
    public IReadOnlyList<Item> Decls => _decls ??= ZigLowering.Flatten(Parse.Tree);

    /// <summary>Decls that failed to parse and were skipped during recovery (empty ⇒ a clean parse).</summary>
    public IReadOnlyList<ParseErrorInfo> Errors => Parse.Errors;
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
    private readonly Dictionary<string, ZigModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ZigLowering> _lowerings = new();

    public ZigModuleGraph()
    {
        _parser = Zig.BuildParser(Zig.IdentityVisitor.Instance);
        _lexerTable = Zig.BuildLexer();
        (_syncTerminals, _openBrackets, _closeBrackets) = BuildRecoverySets(_parser.Grammar);
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
        return new ZigModule(path, result);
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
