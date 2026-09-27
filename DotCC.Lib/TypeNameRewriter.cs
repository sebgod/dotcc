#nullable enable

using System;
using System.Collections.Generic;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// The C "lexer hack" as a <see cref="RewritingTokenStream"/>. Sits between
/// the preprocessor and the parser's <c>SyncLATokenIterator</c> and swaps an
/// <c>ID</c>'s symbol for <c>TYPE_NAME</c> (content and position unchanged)
/// wherever C reads that identifier as a typedef name. The parser then
/// unambiguously reduces <c>Type -> TYPE_NAME</c> and never confuses
/// <c>Color * x;</c> (a declaration when <c>Color</c> is a typedef name) with a
/// multiplication.
/// </summary>
/// <remarks>
/// Whether an identifier is a typedef name depends on more than a set of
/// names, so the rewriter follows the token stream through a small model of
/// C's declaration syntax and scopes (C11 6.2.1, 6.7):
/// <list type="bullet">
///   <item><b>Declarations.</b> Each frame that can hold declarations (file,
///     block, struct body, parameter list, <c>for</c> head) tracks a
///     <see cref="Phase"/>: at the start of a declaration, after
///     non-type specifiers (<c>static</c>, <c>const</c>, <c>typedef</c>),
///     after a type specifier, inside a declarator, after the declarator's
///     name. A typedef name at the start or after only non-type specifiers is
///     a type specifier (<c>TYPE_NAME</c>). Once a type specifier has been
///     seen, an identifier is the declarator, even when it spells a typedef
///     name (<c>destructor destructor;</c>, <c>PyObject *string</c>), and C
///     allows at most one typedef name among the specifiers, so this is
///     exact.</item>
///   <item><b>Typedefs.</b> A declaration carrying <c>typedef</c> registers
///     each declarator name when the name is reached. The declarator of the
///     typedef is found structurally, so the name of a function-pointer member
///     inside the typedef's struct body (<c>typedef struct { void (*fn)(void *);
///     } Alloc;</c>) never becomes the alias.</item>
///   <item><b>Scopes.</b> A parameter, local or enumerator named like a
///     typedef name hides the typedef in its scope: parameters in the
///     function's body, locals in their block, a <c>for</c>-init declaration
///     in the loop's braced body. Struct members live in their own name space
///     and hide nothing.</item>
///   <item><b>Other name spaces.</b> An identifier after <c>struct</c> /
///     <c>union</c> / <c>enum</c> is a tag; after <c>.</c> or <c>-&gt;</c>
///     (including a designator) it is a member; after <c>goto</c> it is a
///     label; inside <c>[[ … ]]</c> it is an attribute name; after the first
///     comma of <c>offsetof(T, …)</c> it is a member designator. None of
///     these is ever promoted.</item>
/// </list>
/// Expression contexts (initializers, array bounds, conditions, casts,
/// <c>sizeof</c>) look names up through the scope chain and promote only a
/// visible typedef name.
/// <para>
/// All the iterator plumbing (ready queue, look-ahead buffer, exhaustion
/// flag) lives in <see cref="RewritingTokenStream"/>; this class is pure
/// policy.
/// </para>
/// </remarks>
internal sealed class TypeNameRewriter : RewritingTokenStream
{
    /// <summary>What kind of bracketed region a <see cref="Frame"/> models.</summary>
    private enum FrameKind
    {
        /// <summary>File scope: external declarations.</summary>
        File,
        /// <summary>A compound statement (function body, nested block, statement
        /// expression): a block scope holding declarations and statements.</summary>
        Block,
        /// <summary>A struct or union body: member declarations, own name space.</summary>
        Struct,
        /// <summary>An enum body: enumerators, declared in the enclosing scope.</summary>
        Enum,
        /// <summary>A function declarator's parameter list: prototype scope.</summary>
        Params,
        /// <summary>The parentheses of a <c>for</c> statement: the init clause
        /// may declare, the rest are expressions.</summary>
        ForHead,
        /// <summary>Grouping parentheses inside a declarator (<c>(*fn)</c>).</summary>
        Group,
        /// <summary>Parentheses in an expression (call, cast, <c>sizeof</c>,
        /// condition).</summary>
        Paren,
        /// <summary>Square brackets (array bound, subscript, designator).</summary>
        Bracket,
        /// <summary>The braces of an initializer or compound literal.</summary>
        Init,
        /// <summary>A C23 attribute specifier <c>[[ … ]]</c>.</summary>
        Attr,
    }

    /// <summary>Where a declaration frame is in the declaration (or statement)
    /// it is reading.</summary>
    private enum Phase
    {
        /// <summary>At the start of a declaration or statement.</summary>
        Start,
        /// <summary>After specifiers that are not type specifiers
        /// (<c>static</c>, <c>const</c>, <c>typedef</c>, <c>inline</c>).</summary>
        Specs,
        /// <summary>After a type specifier; the next identifier is the declarator.</summary>
        Typed,
        /// <summary>Inside a declarator, before its name (after <c>*</c>).</summary>
        Declarator,
        /// <summary>After the declarator's name (or its abstract equivalent).</summary>
        Named,
        /// <summary>An initializer or bit-field width: an expression up to
        /// <c>,</c> or <c>;</c>.</summary>
        Init,
        /// <summary>An expression statement.</summary>
        Expr,
        /// <summary>The expression of a <c>case</c> label (or <c>default</c>),
        /// up to its <c>:</c>.</summary>
        CaseLabel,
    }

    /// <summary>What the next <c>(</c> opens because of the keyword just seen.</summary>
    private enum ParenIntent
    {
        /// <summary>No keyword pending: decided by the phase.</summary>
        None,
        /// <summary>An expression or type operand (<c>sizeof</c>, <c>typeof</c>,
        /// <c>_Atomic(T)</c>, <c>_Alignas</c>, <c>_Generic</c>, a control
        /// condition).</summary>
        Operand,
        /// <summary>The operand list of <c>offsetof</c>.</summary>
        Offsetof,
        /// <summary>The head of a <c>for</c> statement.</summary>
        For,
    }

    /// <summary>Which aggregate keyword is waiting for its tag or body.</summary>
    private enum Aggregate
    {
        None,
        StructOrUnion,
        Enum,
    }

    /// <summary>One open bracketed region, with its declaration state.</summary>
    private sealed class Frame
    {
        public Frame(FrameKind kind, Phase phase)
        {
            Kind = kind;
            Phase = phase;
            Decl = this;
        }

        public FrameKind Kind { get; }

        /// <summary>The declaration phase (meaningful for declaration frames,
        /// groups and enum bodies).</summary>
        public Phase Phase { get; set; }

        /// <summary>The frame whose declaration a declarator in this frame
        /// belongs to: itself, or for a <see cref="FrameKind.Group"/> the
        /// declaration frame that opened it.</summary>
        public Frame Decl { get; set; }

        /// <summary>The phase the parent takes when this frame closes.</summary>
        public Phase RestorePhase { get; set; }

        /// <summary>The current declaration carries <c>typedef</c>.</summary>
        public bool IsTypedef { get; set; }

        /// <summary>The current declarator has reached its name.</summary>
        public bool NameSeen { get; set; }

        /// <summary>The names shadowed in the first parameter list after the
        /// current declarator's name: the parameters of a function definition,
        /// carried into its body.</summary>
        public HashSet<string>? FirstParams { get; set; }

        /// <summary>The current declarator's first parameter list has closed.</summary>
        public bool FirstParamsTaken { get; set; }

        /// <summary>Scope frames: ordinary identifiers declared here that hide a
        /// typedef name of an enclosing scope.</summary>
        public HashSet<string>? Shadowed { get; set; }

        /// <summary>Scope frames: typedef names declared in this (block) scope.</summary>
        public HashSet<string>? LocalTypedefs { get; set; }

        /// <summary>A block statement began with an identifier that may be a
        /// label (<c>name:</c>).</summary>
        public bool LabelCandidate { get; set; }

        /// <summary><see cref="FrameKind.ForHead"/>: past the init clause's <c>;</c>.</summary>
        public bool ForInitDone { get; set; }

        /// <summary><see cref="FrameKind.Paren"/> of <c>offsetof</c>: past its
        /// first comma, so identifiers are member designators.</summary>
        public bool OffsetofMembers { get; set; }

        /// <summary><see cref="FrameKind.Paren"/> opened by <c>offsetof</c>.</summary>
        public bool IsOffsetof { get; set; }

        /// <summary><see cref="FrameKind.Attr"/>: the first of the two closing
        /// <c>]</c> has been seen.</summary>
        public bool AttrClosing { get; set; }

        /// <summary>Frames that hold declarations and track a phase.</summary>
        public bool IsDeclFrame => Kind is FrameKind.File or FrameKind.Block or FrameKind.Struct
            or FrameKind.Params or FrameKind.ForHead;

        /// <summary>Frames that open a scope for ordinary identifiers.</summary>
        public bool IsScope => Kind is FrameKind.File or FrameKind.Block or FrameKind.Params
            or FrameKind.ForHead;

        /// <summary>Frames whose phase follows the declarator syntax.</summary>
        public bool IsPhased => IsDeclFrame || Kind is FrameKind.Group or FrameKind.Enum;

        /// <summary>Begin a new declaration or statement.</summary>
        public void ResetToStart()
        {
            Phase = Phase.Start;
            IsTypedef = false;
            LabelCandidate = false;
            ResetDeclarator();
        }

        /// <summary>Begin the next declarator of the same declaration (after a
        /// comma): the specifiers carry over.</summary>
        public void ResetDeclarator()
        {
            NameSeen = false;
            FirstParams = null;
            FirstParamsTaken = false;
        }
    }

    private static readonly HashSet<string> NoNames = new(StringComparer.Ordinal);

    private readonly int _idSymbol;
    private readonly int _typeNameSymbol;
    private readonly int _typedefSymbol;
    private readonly int _semiSymbol;
    private readonly int _commaSymbol;
    private readonly int _assignSymbol;
    private readonly int _colonSymbol;
    private readonly int _openBraceSymbol;
    private readonly int _closeBraceSymbol;
    private readonly int _openParenSymbol;
    private readonly int _closeParenSymbol;
    private readonly int _starSymbol;
    private readonly int _openBracketSymbol;
    private readonly int _closeBracketSymbol;
    private readonly int _attrOpenSymbol;
    private readonly int _dotSymbol;
    private readonly int _arrowSymbol;
    private readonly int _structSymbol;
    private readonly int _unionSymbol;
    private readonly int _enumSymbol;
    private readonly int _gotoSymbol;
    private readonly int _caseSymbol;
    private readonly int _defaultSymbol;
    private readonly int _forSymbol;
    private readonly int _offsetofSymbol;
    private readonly int _atomicSymbol;
    private readonly int _typeofSymbol;
    private readonly int _alignasSymbol;

    // Keyword classes, by symbol id.
    private readonly HashSet<int> _typeSpecifiers = new();
    private readonly HashSet<int> _otherSpecifiers = new();
    private readonly HashSet<int> _controlKeywords = new();
    private readonly HashSet<int> _operandKeywords = new();
    private readonly HashSet<int> _startOnlyKeywords = new();
    private readonly HashSet<int> _exprKeywords = new();

    private readonly HashSet<string> _typeNames;
    private readonly HashSet<string> _seedTypeNames;
    private readonly List<Frame> _frames = new();

    // Per-token context carried to the next token.
    private bool _afterTagKeyword;
    private bool _afterMemberAccess;
    private bool _afterGoto;
    private int _previousSymbol = -1;
    private ParenIntent _parenIntent;
    private Phase _parenIntentRestore;
    private Aggregate _aggregate;
    private bool _aggregateTagAllowed;
    private bool _enumBase;
    private bool _anyLocalTypedefs;

    // The scope of a just-closed `for` head, handed to a `{` body that follows
    // immediately.
    private HashSet<string>? _carryScope;

    /// <summary>
    /// Construct a rewriter over <paramref name="inner"/>, optionally
    /// pre-populated with <paramref name="seedTypeNames"/> — identifiers
    /// the user code can reference as types without first seeing a
    /// <c>typedef</c> definition. Used to expose C#-side libc types
    /// (e.g. <c>LongJmpToken</c> for <c>&lt;setjmp.h&gt;</c>) so a
    /// synthetic header can write
    /// <c>typedef LongJmpToken jmp_buf;</c> and have it parse —
    /// without introducing non-C keywords into the grammar.
    /// </summary>
    public TypeNameRewriter(
        ISyncIterator<Item> inner,
        IEnumerable<string>? seedTypeNames = null) : base(inner)
    {
        // Resolve symbol ids by name from the generated grammar's definition.
        // Done once at construction so per-token dispatch stays O(1).
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sym in C.Definition.SymbolNames)
        {
            map[sym.Name] = sym.ID;
        }
        _idSymbol = map["ID"];
        _typeNameSymbol = map["TYPE_NAME"];
        _typedefSymbol = map["typedef"];
        _semiSymbol = map[";"];
        _commaSymbol = map[","];
        _assignSymbol = map["="];
        _colonSymbol = map[":"];
        _openBraceSymbol = map["{"];
        _closeBraceSymbol = map["}"];
        _openParenSymbol = map["("];
        _closeParenSymbol = map[")"];
        _starSymbol = map["*"];
        _openBracketSymbol = map["["];
        _closeBracketSymbol = map["]"];
        _attrOpenSymbol = map["[["];
        _dotSymbol = map["."];
        _arrowSymbol = map["->"];
        _structSymbol = map["struct"];
        _unionSymbol = map["union"];
        _enumSymbol = map["enum"];
        _gotoSymbol = map["goto"];
        _caseSymbol = map["case"];
        _defaultSymbol = map["default"];
        _forSymbol = map["for"];
        _offsetofSymbol = map["offsetof"];
        _atomicSymbol = map["_Atomic"];
        _typeofSymbol = map["typeof"];
        _alignasSymbol = map["_Alignas"];

        foreach (var k in new[] { "int", "char", "float", "double", "void", "short", "long",
                     "unsigned", "signed", "_Bool", "_Float128", "__int128", "_Complex", "typeof" })
        {
            _typeSpecifiers.Add(map[k]);
        }
        foreach (var k in new[] { "const", "volatile", "restrict", "static", "extern", "auto",
                     "inline", "_Noreturn", "_Thread_local", "constexpr", "typedef", "_Atomic",
                     "_Alignas" })
        {
            _otherSpecifiers.Add(map[k]);
        }
        foreach (var k in new[] { "if", "while", "switch" })
        {
            _controlKeywords.Add(map[k]);
        }
        foreach (var k in new[] { "sizeof", "_Alignof", "_Generic", "_Static_assert", "va_arg" })
        {
            _operandKeywords.Add(map[k]);
        }
        foreach (var k in new[] { "else", "do" })
        {
            _startOnlyKeywords.Add(map[k]);
        }
        foreach (var k in new[] { "return", "break", "continue", "goto" })
        {
            _exprKeywords.Add(map[k]);
        }

        // Seed set lives separately from the dynamic set populated by
        // user `typedef`s, so Reset() can clear the dynamic side without
        // forgetting the predefined libc-class names.
        _seedTypeNames = new HashSet<string>(StringComparer.Ordinal);
        if (seedTypeNames is not null)
        {
            foreach (var name in seedTypeNames) { _seedTypeNames.Add(name); }
        }
        _typeNames = new HashSet<string>(_seedTypeNames, StringComparer.Ordinal);
        _frames.Add(new Frame(FrameKind.File, Phase.Start));
    }

    private Frame Top => _frames[^1];

    protected override void ProcessToken(Item token)
    {
        // Context left by the previous token, snapshotted, then refreshed from
        // this token's identity.
        var afterTag = _afterTagKeyword;
        var afterMember = _afterMemberAccess;
        var afterGoto = _afterGoto;
        var previous = _previousSymbol;
        var top = Top;
        var labelCandidate = top.LabelCandidate;
        top.LabelCandidate = false;
        _afterTagKeyword = token.ID == _structSymbol || token.ID == _unionSymbol || token.ID == _enumSymbol;
        _afterMemberAccess = token.ID == _dotSymbol || token.ID == _arrowSymbol;
        _afterGoto = token.ID == _gotoSymbol;
        _previousSymbol = token.ID;
        var carry = _carryScope;
        _carryScope = null;

        if (token.ID == _idSymbol && token.Content is string name)
        {
            if (!(afterTag && _aggregateTagAllowed) && !(_enumBase && IsTypeName(name)))
            {
                ClearAggregate();
            }
            _aggregateTagAllowed = false;
            _parenIntent = ParenIntent.None;
            if (afterTag || afterMember || afterGoto || top.Kind == FrameKind.Attr
                || (top.Kind == FrameKind.Paren && top.OffsetofMembers))
            {
                // A tag, member, label or attribute name: its own name space.
                Emit(token);
                return;
            }
            OnIdentifier(token, name);
            return;
        }

        if (token.ID == _structSymbol || token.ID == _unionSymbol || token.ID == _enumSymbol)
        {
            _aggregate = token.ID == _enumSymbol ? Aggregate.Enum : Aggregate.StructOrUnion;
            _aggregateTagAllowed = true;
            _enumBase = false;
            OnTypeSpecifier();
            Emit(token);
            return;
        }

        var keepAggregate = token.ID == _openBraceSymbol
            || (token.ID == _colonSymbol && _aggregate == Aggregate.Enum && !_enumBase)
            || (_enumBase && (_typeSpecifiers.Contains(token.ID) || _otherSpecifiers.Contains(token.ID)));
        var aggregateBefore = _aggregate;
        if (!keepAggregate)
        {
            ClearAggregate();
        }
        _aggregateTagAllowed = false;

        var intent = _parenIntent;
        _parenIntent = ParenIntent.None;

        if (token.ID == _openParenSymbol) { OnOpenParen(token, intent); return; }
        if (token.ID == _closeParenSymbol) { OnCloseParen(); Emit(token); return; }
        if (token.ID == _openBraceSymbol) { OnOpenBrace(aggregateBefore, previous, carry); Emit(token); return; }
        if (token.ID == _closeBraceSymbol) { OnCloseBrace(); Emit(token); return; }
        if (token.ID == _openBracketSymbol) { Push(FrameKind.Bracket, Phase.Start, RestoreAfterBracket(top)); Emit(token); return; }
        if (token.ID == _closeBracketSymbol) { OnCloseBracket(); Emit(token); return; }
        if (token.ID == _attrOpenSymbol) { Push(FrameKind.Attr, Phase.Start, top.Phase); Emit(token); return; }
        if (token.ID == _semiSymbol) { OnSemicolon(); Emit(token); return; }
        if (token.ID == _commaSymbol) { OnComma(); Emit(token); return; }
        if (token.ID == _assignSymbol) { OnAssign(); Emit(token); return; }
        if (token.ID == _colonSymbol) { OnColon(labelCandidate, aggregateBefore); Emit(token); return; }
        if (token.ID == _starSymbol) { OnStar(); Emit(token); return; }

        if (_typeSpecifiers.Contains(token.ID))
        {
            OnTypeSpecifier();
            if (token.ID == _typeofSymbol) { ArmParen(ParenIntent.Operand, Phase.Typed); }
            Emit(token);
            return;
        }
        if (_otherSpecifiers.Contains(token.ID))
        {
            OnOtherSpecifier(token.ID);
            Emit(token);
            return;
        }
        if (token.ID == _forSymbol && top.Kind == FrameKind.Block)
        {
            ArmParen(ParenIntent.For, Phase.Start);
            Emit(token);
            return;
        }
        if (_controlKeywords.Contains(token.ID) && top.Kind == FrameKind.Block)
        {
            ArmParen(ParenIntent.Operand, Phase.Start);
            Emit(token);
            return;
        }
        if (token.ID == _offsetofSymbol)
        {
            BeginExpressionStatement(top);
            ArmParen(ParenIntent.Offsetof, top.Phase);
            Emit(token);
            return;
        }
        if (_operandKeywords.Contains(token.ID))
        {
            BeginExpressionStatement(top);
            ArmParen(ParenIntent.Operand, top.Phase);
            Emit(token);
            return;
        }
        if ((token.ID == _caseSymbol || token.ID == _defaultSymbol)
            && top.Kind == FrameKind.Block && top.Phase == Phase.Start)
        {
            top.Phase = Phase.CaseLabel;
            Emit(token);
            return;
        }
        if (_startOnlyKeywords.Contains(token.ID) && top.Kind == FrameKind.Block)
        {
            top.ResetToStart();
            Emit(token);
            return;
        }

        // Anything else (an operator, a literal, `return`, `goto`, …) at the
        // start of a block statement begins an expression statement.
        BeginExpressionStatement(top);
        Emit(token);
    }

    /// <summary>An identifier in an ordinary-identifier position: decide
    /// between type specifier, declarator name and expression operand.</summary>
    private void OnIdentifier(Item token, string name)
    {
        var f = Top;
        switch (f.Kind)
        {
            case FrameKind.Paren:
            case FrameKind.Bracket:
            case FrameKind.Init:
                EmitLookup(token, name);
                return;
            case FrameKind.Enum:
                if (f.Phase == Phase.Start)
                {
                    // An enumerator: an ordinary identifier of the enclosing scope.
                    DeclareOrdinary(EnclosingScope(), name);
                    f.Phase = Phase.Named;
                    Emit(token);
                }
                else
                {
                    EmitLookup(token, name);
                }
                return;
        }

        switch (f.Phase)
        {
            case Phase.Start:
                if (IsTypeName(name))
                {
                    f.Phase = Phase.Typed;
                    EmitType(token, name);
                }
                else if (f.Kind == FrameKind.Block || (f.Kind == FrameKind.ForHead && !f.ForInitDone))
                {
                    // An expression statement (or a label, or a `for` init
                    // expression).
                    f.Phase = Phase.Expr;
                    f.LabelCandidate = f.Kind == FrameKind.Block;
                    Emit(token);
                }
                else
                {
                    // File scope, a member or a parameter: an identifier with no
                    // type specifier before it names the declarator (an implicit
                    // int, a K&R identifier list).
                    DeclareDeclaratorName(f, name);
                    f.Phase = Phase.Named;
                    Emit(token);
                }
                return;
            case Phase.Specs:
                if (IsTypeName(name))
                {
                    f.Phase = Phase.Typed;
                    EmitType(token, name);
                }
                else
                {
                    DeclareDeclaratorName(f, name);
                    f.Phase = Phase.Named;
                    Emit(token);
                }
                return;
            case Phase.Typed:
            case Phase.Declarator:
                // The type is already specified, so this is the declarator's
                // name, whatever it spells (C11 6.7.2p2: at most one typedef
                // name in the specifiers).
                DeclareDeclaratorName(f, name);
                f.Phase = Phase.Named;
                Emit(token);
                return;
            default:
                EmitLookup(token, name);
                return;
        }
    }

    /// <summary>A type-specifier keyword (or <c>struct</c> / <c>union</c> /
    /// <c>enum</c>).</summary>
    private void OnTypeSpecifier()
    {
        var f = Top;
        if (f.Kind is FrameKind.Paren or FrameKind.Bracket or FrameKind.Init or FrameKind.Attr or FrameKind.Enum)
        {
            return;
        }
        if (f.Phase is Phase.Start or Phase.Specs or Phase.Typed)
        {
            f.Phase = Phase.Typed;
        }
    }

    /// <summary>A storage class, qualifier, function or alignment specifier.</summary>
    private void OnOtherSpecifier(int symbol)
    {
        var f = Top;
        if (f.Kind is FrameKind.Paren or FrameKind.Bracket or FrameKind.Init or FrameKind.Attr or FrameKind.Enum)
        {
            if (symbol == _atomicSymbol || symbol == _alignasSymbol)
            {
                ArmParen(ParenIntent.Operand, f.Phase);
            }
            return;
        }
        if (symbol == _typedefSymbol)
        {
            f.Decl.IsTypedef = true;
        }
        if (f.Phase == Phase.Start)
        {
            f.Phase = Phase.Specs;
        }
        // `_Atomic ( T )` is a type specifier; `_Alignas ( … )` an alignment
        // specifier. Either way the parenthesis holds a type or expression.
        if (symbol == _atomicSymbol)
        {
            ArmParen(ParenIntent.Operand, Phase.Typed);
        }
        else if (symbol == _alignasSymbol)
        {
            ArmParen(ParenIntent.Operand, f.Phase);
        }
    }

    private void OnOpenParen(Item token, ParenIntent intent)
    {
        var f = Top;
        Emit(token);
        if (intent == ParenIntent.For)
        {
            var head = Push(FrameKind.ForHead, Phase.Start, Phase.Start);
            head.ResetToStart();
            return;
        }
        if (intent is ParenIntent.Operand or ParenIntent.Offsetof)
        {
            // The operand of a keyword: the phase the keyword asked for (a
            // type specifier for `typeof ( … )` / `_Atomic ( T )`, a new
            // statement after a control condition) applies once it closes.
            var paren = Push(FrameKind.Paren, Phase.Start, _parenIntentRestore);
            paren.IsOffsetof = intent == ParenIntent.Offsetof;
            return;
        }
        if (!f.IsPhased || f.Kind == FrameKind.Enum)
        {
            Push(FrameKind.Paren, Phase.Start, f.Phase);
            return;
        }
        switch (f.Phase)
        {
            case Phase.Start when f.Kind is FrameKind.Block or FrameKind.ForHead:
                // `( … )` opening an expression statement: a cast, a call
                // through a parenthesized callee.
                f.Phase = Phase.Expr;
                Push(FrameKind.Paren, Phase.Start, Phase.Expr);
                return;
            case Phase.Start:
            case Phase.Specs:
            case Phase.Typed:
            case Phase.Declarator:
                if (PeekStartsDeclarator())
                {
                    var group = Push(FrameKind.Group, Phase.Declarator, Phase.Named);
                    group.Decl = f.Decl;
                }
                else
                {
                    // An abstract function declarator: `int (int)`, `void (*)(void)`.
                    PushParams(Phase.Named);
                }
                return;
            case Phase.Named:
                PushParams(Phase.Named);
                return;
            default:
                Push(FrameKind.Paren, Phase.Start, f.Phase);
                return;
        }
    }

    /// <summary>After <c>(</c> in a declarator before its name: does the next
    /// token begin a (nested) declarator, making the parenthesis a grouping
    /// one? Per C11 6.7.6.3p11 an identifier that is a typedef name there makes
    /// it a parameter list instead.</summary>
    private bool PeekStartsDeclarator()
    {
        if (!TryReadNext(out var next))
        {
            return false;
        }
        HoldNext(next);
        if (next.ID == _starSymbol || next.ID == _openParenSymbol || next.ID == _attrOpenSymbol)
        {
            return true;
        }
        return next.ID == _idSymbol && next.Content is string n && !IsTypeName(n);
    }

    private void PushParams(Phase restore)
    {
        var p = Push(FrameKind.Params, Phase.Start, restore);
        p.ResetToStart();
    }

    private void OnCloseParen()
    {
        // Pop through anything a `)` cannot sit inside (malformed input or an
        // unbalanced bracket) up to the frame it closes.
        while (_frames.Count > 1)
        {
            var f = Top;
            if (f.Kind is FrameKind.Paren or FrameKind.Params or FrameKind.Group or FrameKind.ForHead)
            {
                Pop();
                if (f.Kind == FrameKind.Params)
                {
                    var owner = Top.Decl;
                    if (owner.NameSeen && !owner.FirstParamsTaken)
                    {
                        owner.FirstParams = f.Shadowed ?? NoNames;
                        owner.FirstParamsTaken = true;
                    }
                }
                else if (f.Kind == FrameKind.ForHead)
                {
                    _carryScope = f.Shadowed;
                }
                return;
            }
            if (f.Kind is FrameKind.Bracket or FrameKind.Attr)
            {
                Pop();
                continue;
            }
            return;
        }
    }

    private void OnOpenBrace(Aggregate aggregate, int previous, HashSet<string>? carry)
    {
        var f = Top;
        ClearAggregate();
        if (aggregate == Aggregate.StructOrUnion)
        {
            var body = Push(FrameKind.Struct, Phase.Start, Phase.Typed);
            body.ResetToStart();
            return;
        }
        if (aggregate == Aggregate.Enum)
        {
            Push(FrameKind.Enum, Phase.Start, Phase.Typed);
            return;
        }
        if (f.IsDeclFrame && f.Phase == Phase.Named && f.Decl.FirstParams is { } parameters
            && f.Kind is FrameKind.File or FrameKind.Block)
        {
            // A function definition's body: its parameters are in scope.
            var body = Push(FrameKind.Block, Phase.Start, Phase.Start);
            body.ResetToStart();
            if (parameters.Count > 0)
            {
                body.Shadowed = new HashSet<string>(parameters, StringComparer.Ordinal);
            }
            return;
        }
        if ((f.Kind is FrameKind.Block or FrameKind.File) && f.Phase == Phase.Start)
        {
            var block = Push(FrameKind.Block, Phase.Start, Phase.Start);
            block.ResetToStart();
            if (carry is { Count: > 0 })
            {
                block.Shadowed = new HashSet<string>(carry, StringComparer.Ordinal);
            }
            return;
        }
        if (f.Kind == FrameKind.Paren && previous == _openParenSymbol)
        {
            // A GNU statement expression `({ … })`.
            var block = Push(FrameKind.Block, Phase.Start, Phase.Start);
            block.ResetToStart();
            return;
        }
        // An initializer or compound literal.
        Push(FrameKind.Init, Phase.Start, f.Phase);
    }

    private void OnCloseBrace()
    {
        while (_frames.Count > 1)
        {
            var f = Top;
            Pop();
            if (f.Kind is FrameKind.Block or FrameKind.Struct or FrameKind.Enum or FrameKind.Init)
            {
                return;
            }
        }
    }

    private void OnCloseBracket()
    {
        var f = Top;
        if (f.Kind == FrameKind.Attr)
        {
            // `[[ … ]]` closes on its second `]`.
            if (f.AttrClosing)
            {
                Pop();
            }
            else
            {
                f.AttrClosing = true;
            }
            return;
        }
        if (f.Kind == FrameKind.Bracket)
        {
            Pop();
        }
    }

    private void OnSemicolon()
    {
        // A `;` ends a declaration or statement; nothing parenthesized or
        // bracketed can hold one, so pop back to the statement-level frame.
        while (_frames.Count > 1 && Top.Kind is not (FrameKind.Block or FrameKind.Struct or FrameKind.ForHead))
        {
            Pop();
        }
        var f = Top;
        if (f.Kind == FrameKind.ForHead)
        {
            f.ForInitDone = true;
            f.IsTypedef = false;
            f.ResetDeclarator();
            f.Phase = Phase.Expr;
            return;
        }
        f.ResetToStart();
    }

    private void OnComma()
    {
        var f = Top;
        if (f.Kind == FrameKind.Paren && f.IsOffsetof)
        {
            f.OffsetofMembers = true;
            return;
        }
        if (f.Kind == FrameKind.Enum)
        {
            f.Phase = Phase.Start;
            return;
        }
        if (!f.IsDeclFrame)
        {
            return;
        }
        if (f.Kind == FrameKind.Params)
        {
            // The next parameter declaration.
            f.ResetToStart();
            return;
        }
        if (f.Phase is Phase.Typed or Phase.Declarator or Phase.Named or Phase.Init)
        {
            // The next declarator of the same declaration.
            f.ResetDeclarator();
            f.Phase = Phase.Typed;
        }
    }

    private void OnAssign()
    {
        var f = Top;
        if ((f.IsDeclFrame || f.Kind == FrameKind.Enum) && f.Phase is Phase.Named or Phase.Typed or Phase.Declarator)
        {
            f.Phase = Phase.Init;
        }
    }

    private void OnColon(bool labelCandidate, Aggregate aggregate)
    {
        var f = Top;
        if (aggregate == Aggregate.Enum && !_enumBase)
        {
            // C23 `enum E : T { … }`: the fixed underlying type follows.
            _enumBase = true;
            _aggregate = Aggregate.Enum;
            if (f.IsPhased && f.Kind != FrameKind.Enum)
            {
                f.Phase = Phase.Specs;
            }
            return;
        }
        if (f.Kind == FrameKind.Block && (labelCandidate || f.Phase == Phase.CaseLabel))
        {
            f.ResetToStart();
            return;
        }
        if (f.Kind == FrameKind.Struct && f.Phase is Phase.Named or Phase.Typed)
        {
            // A bit-field width.
            f.Phase = Phase.Init;
        }
    }

    private void OnStar()
    {
        var f = Top;
        if (!f.IsPhased || f.Kind == FrameKind.Enum)
        {
            return;
        }
        switch (f.Phase)
        {
            case Phase.Start when f.Kind == FrameKind.Block:
                f.Phase = Phase.Expr;
                return;
            case Phase.Specs:
            case Phase.Typed:
                f.Phase = Phase.Declarator;
                return;
        }
    }

    /// <summary>A statement-level token that can only begin an expression.</summary>
    private static void BeginExpressionStatement(Frame f)
    {
        if (f.Kind is FrameKind.Block or FrameKind.ForHead && f.Phase == Phase.Start)
        {
            f.Phase = Phase.Expr;
        }
        else if (f.Kind == FrameKind.File && f.Phase == Phase.Start)
        {
            // `_Static_assert( … );` at file scope.
            f.Phase = Phase.Expr;
        }
    }

    private void ArmParen(ParenIntent intent, Phase restore)
    {
        _parenIntent = intent;
        _parenIntentRestore = restore;
    }

    private void ClearAggregate()
    {
        _aggregate = Aggregate.None;
        _enumBase = false;
    }

    private static Phase RestoreAfterBracket(Frame f) =>
        f.IsPhased && f.Phase is Phase.Typed or Phase.Declarator ? Phase.Named : f.Phase;

    private Frame Push(FrameKind kind, Phase phase, Phase restore)
    {
        var frame = new Frame(kind, phase) { RestorePhase = restore };
        _frames.Add(frame);
        return frame;
    }

    private void Pop()
    {
        var closed = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        var parent = Top;
        if (!parent.IsPhased)
        {
            return;
        }
        if (closed.RestorePhase == Phase.Start)
        {
            parent.ResetToStart();
        }
        else
        {
            parent.Phase = closed.RestorePhase;
        }
    }

    /// <summary>The innermost frame that opens a scope for ordinary identifiers.</summary>
    private Frame EnclosingScope()
    {
        for (var i = _frames.Count - 1; i >= 0; i--)
        {
            if (_frames[i].IsScope) { return _frames[i]; }
        }
        return _frames[0];
    }

    /// <summary>Record <paramref name="name"/> as the name of the current
    /// declarator of <paramref name="f"/>'s declaration.</summary>
    private void DeclareDeclaratorName(Frame f, string name)
    {
        var decl = f.Decl;
        decl.NameSeen = true;
        if (decl.Kind == FrameKind.Struct)
        {
            // Members live in the struct's own name space.
            return;
        }
        if (decl.IsTypedef)
        {
            if (decl.Kind == FrameKind.File)
            {
                _typeNames.Add(name);
            }
            else if (decl.Kind is FrameKind.Block or FrameKind.ForHead)
            {
                (decl.LocalTypedefs ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
                decl.Shadowed?.Remove(name);
                _anyLocalTypedefs = true;
            }
            return;
        }
        DeclareOrdinary(decl, name);
    }

    /// <summary>Declare an ordinary identifier in <paramref name="scope"/>; it
    /// hides a typedef name of an enclosing scope from here to the scope's
    /// end. File scope never needs the entry (redeclaring a file-scope typedef
    /// name there is a constraint violation the binder reports).</summary>
    private void DeclareOrdinary(Frame scope, string name)
    {
        if (scope.Kind == FrameKind.File || !IsTypeName(name))
        {
            return;
        }
        (scope.Shadowed ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
        scope.LocalTypedefs?.Remove(name);
    }

    /// <summary>Is <paramref name="name"/> a typedef name visible here?</summary>
    private bool IsTypeName(string name)
    {
        if (!_anyLocalTypedefs && !_typeNames.Contains(name))
        {
            return false;
        }
        for (var i = _frames.Count - 1; i >= 1; i--)
        {
            var f = _frames[i];
            if (f.Shadowed is { } hidden && hidden.Contains(name)) { return false; }
            if (f.LocalTypedefs is { } local && local.Contains(name)) { return true; }
        }
        return _typeNames.Contains(name);
    }

    private void EmitLookup(Item token, string name)
    {
        if (IsTypeName(name))
        {
            EmitType(token, name);
        }
        else
        {
            Emit(token);
        }
    }

    private void EmitType(Item token, string name) =>
        Emit(new Item(_typeNameSymbol, name, token.Position));

    public override void Reset()
    {
        // Restore to the seed-only state so predefined libc-class type
        // names survive across reuse but per-TU typedef'd aliases don't.
        _typeNames.Clear();
        foreach (var name in _seedTypeNames) { _typeNames.Add(name); }
        _frames.Clear();
        _frames.Add(new Frame(FrameKind.File, Phase.Start));
        _afterTagKeyword = false;
        _afterMemberAccess = false;
        _afterGoto = false;
        _previousSymbol = -1;
        _parenIntent = ParenIntent.None;
        _aggregate = Aggregate.None;
        _aggregateTagAllowed = false;
        _enumBase = false;
        _anyLocalTypedefs = false;
        _carryScope = null;
        base.Reset();
    }
}
