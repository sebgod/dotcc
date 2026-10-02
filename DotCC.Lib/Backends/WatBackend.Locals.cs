#nullable enable

using System;
using System.Collections.Generic;

namespace DotCC.Backends;

using DotCC.Ir;

/// <summary>
/// Value locals shared across scopes. A C local lives from entry into its block until the block
/// is left (C11 6.2.4p6), so two locals whose blocks do not overlap are never alive at once and
/// can live in one wasm local of their type. A function as large as CPython's eval loop has
/// thousands of block locals (each opcode body's own, and each inlined <c>Py_DECREF</c>'s), which
/// V8's optimizing compiler carries through every one of the function's thousands of dispatch
/// blocks (GH #276); shared, they are a few dozen. A shared wasm local keeps the name of the first
/// C local placed in it, so a function with no disjoint scopes is emitted as before.
/// </summary>
internal sealed partial class WatBackend
{
    /// <summary>Each value local of the function being emitted → the wasm local it lives in (see
    /// <see cref="AssignLocalSlots"/>). A symbol not listed, a parameter or a synthetic local, is
    /// its own.</summary>
    private readonly Dictionary<Symbol, string> _localSlots = new(ReferenceEqualityComparer.Instance);

    /// <summary>The wasm local <paramref name="sym"/> lives in.</summary>
    private string LocalName(Symbol sym) => _localSlots.TryGetValue(sym, out var name) ? name : sym.TargetName;

    /// <summary>Place each of a function's <paramref name="valueLocals"/> in a wasm local, filling
    /// <see cref="_localSlots"/>, and return the wasm locals to declare, name and type, in the
    /// order of the locals that first took them. A local takes a wasm local of its type when its
    /// block is entered, not where it is declared, since its lifetime is the whole block: a
    /// <c>goto</c> may jump over its declaration and still find its value. When the block is
    /// left, its wasm locals are free for the next block to take. A local the walk cannot vouch for keeps a wasm local of its
    /// own: one declared in two places (a statement the IR repeats), or one used where its block
    /// is not open, among the expressions <see cref="VisitExpr"/> reaches.</summary>
    private List<(string Name, string Type)> AssignLocalSlots(IReadOnlyList<CStmt> body, IReadOnlyList<Symbol> valueLocals)
    {
        _localSlots.Clear();
        var isValue = new HashSet<Symbol>(valueLocals, ReferenceEqualityComparer.Instance);
        var own = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);

        // Which block declares each local, and the locals each block declares.
        var scopeOf = new Dictionary<Symbol, int>(ReferenceEqualityComparer.Instance);
        var declaredIn = new List<List<Symbol>>();
        var open = new List<int>();
        WalkScopes(body,
            enter: id => { declaredIn.Add(new List<Symbol>()); open.Add(id); },
            exit: _ => open.RemoveAt(open.Count - 1),
            declare: s =>
            {
                if (!isValue.Contains(s)) { return; }
                if (scopeOf.TryAdd(s, open[^1])) { declaredIn[open[^1]].Add(s); }
                else { own.Add(s); }
            },
            use: _ => { });

        // Every use must fall inside its local's block.
        var isOpen = new HashSet<int>();
        WalkScopes(body,
            enter: id => isOpen.Add(id),
            exit: id => isOpen.Remove(id),
            declare: _ => { },
            use: s =>
            {
                if (isValue.Contains(s) && !(scopeOf.TryGetValue(s, out var id) && isOpen.Contains(id))) { own.Add(s); }
            });

        var order = new Dictionary<Symbol, int>(ReferenceEqualityComparer.Instance);
        foreach (var s in valueLocals) { order.TryAdd(s, order.Count); }
        var decls = new List<(int Order, string Name, string Type)>();
        void Own(Symbol s, string type)
        {
            _localSlots[s] = s.TargetName;
            decls.Add((order[s], s.TargetName, type));
        }
        var free = new Dictionary<string, Stack<string>>(StringComparer.Ordinal);
        var held = new List<List<(string Type, string Name)>>();
        WalkScopes(body,
            enter: id =>
            {
                var taken = new List<(string Type, string Name)>();
                foreach (var s in declaredIn[id])
                {
                    var type = _wat.RenderType(s.Type);
                    if (!own.Contains(s) && free.TryGetValue(type, out var pool) && pool.TryPop(out var shared))
                    {
                        _localSlots[s] = shared;
                    }
                    else
                    {
                        Own(s, type);
                    }
                    if (!own.Contains(s)) { taken.Add((type, _localSlots[s])); }
                }
                held.Add(taken);
            },
            exit: _ =>
            {
                foreach (var (type, name) in held[^1])
                {
                    if (!free.TryGetValue(type, out var pool)) { free[type] = pool = new Stack<string>(); }
                    pool.Push(name);
                }
                held.RemoveAt(held.Count - 1);
            },
            declare: _ => { },
            use: _ => { });
        // A local the walk never reached a declaration of has a wasm local of its own.
        foreach (var s in valueLocals)
        {
            if (!_localSlots.ContainsKey(s)) { Own(s, _wat.RenderType(s.Type)); }
        }
        decls.Sort((x, y) => x.Order.CompareTo(y.Order));
        return decls.ConvertAll(d => (d.Name, d.Type));
    }

    /// <summary>Walk a function body's blocks in a fixed order, numbering each from 0 as it is
    /// entered: the body itself, each compound statement, a <c>for</c> (whose declarations are
    /// its own) and a <c>switch</c> body, and the arms of a selection or iteration statement,
    /// each a block in C. <paramref name="declare"/> sees each declared local in the block that
    /// declares it, and <paramref name="use"/> each local an expression names.</summary>
    private static void WalkScopes(IReadOnlyList<CStmt> body, Action<int> enter, Action<int> exit,
        Action<Symbol> declare, Action<Symbol> use)
    {
        var next = 0;
        void Scoped(Action walk)
        {
            var id = next++;
            enter(id);
            walk();
            exit(id);
        }
        void E(CExpr? e) => VisitExpr(e, x => { if (x is VarRef v) { use(v.Sym); } });
        void S(CStmt? st)
        {
            switch (st)
            {
                case null: break;
                case Block b: Scoped(() => { foreach (var x in b.Stmts) { S(x); } }); break;
                case Seq q: foreach (var x in q.Stmts) { S(x); } break;
                case DeclStmt d:
                    foreach (var ld in d.Decls) { declare(ld.Sym); E(ld.Init); }
                    break;
                case ArrayDecl ad:
                    declare(ad.Sym);
                    E(ad.CountExpr);
                    if (ad.Inits is { } inits) { foreach (var x in inits) { E(x); } }
                    break;
                case ExprStmt es: E(es.Expr); break;
                case If i: E(i.Cond); Scoped(() => S(i.Then)); Scoped(() => S(i.Else)); break;
                case While w: E(w.Cond); Scoped(() => S(w.Body)); break;
                case DoWhile dw: Scoped(() => S(dw.Body)); E(dw.Cond); break;
                case For f: Scoped(() => { S(f.Init); E(f.Cond); E(f.Post); Scoped(() => S(f.Body)); }); break;
                case Return r: E(r.Value); break;
                case Switch sw:
                    E(sw.Subject);
                    Scoped(() => { foreach (var sec in sw.Sections) { foreach (var x in sec.Body) { S(x); } } });
                    break;
                case Labeled lab: S(lab.Body); break;
                case CaseLabelStmt cl: E(cl.CaseExpr); S(cl.Body); break;
                case SetjmpGuard sj: E(sj.Env); Scoped(() => S(sj.TryBody)); Scoped(() => S(sj.CatchBody)); break;
                case SetjmpCapture sc: E(sc.Env); E(sc.Target); S(sc.Body); break;
            }
        }
        Scoped(() => { foreach (var x in body) { S(x); } });
    }
}
