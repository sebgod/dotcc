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
/// The Zig front-end behind the <see cref="IFrontend"/> seam — the N-axis peer of
/// <see cref="CFrontend"/>. Parses each <c>.zig</c> unit with the generated
/// <see cref="DotCC.Zig"/> grammar (lexer → LA iterator → LALR parser; no
/// preprocessor/rewriters — Zig has none) and lowers it to the neutral IR via
/// <see cref="ZigLowering"/>, returning the same <see cref="IrBuilder"/> any backend
/// consumes. So a Zig program flows through the existing C#/wat targets unchanged.
/// </summary>
internal sealed class ZigFrontend : IFrontend
{
    public IrBuilder BuildIr(FrontendRequest request)
    {
        var names = request.Names ?? new Backends.CSharpNameLegalizer();
        var ir = new IrBuilder(null, names, new Dictionary<string, byte[]>(StringComparer.Ordinal),
                               request.Warnings);
        AddUnits(ir, request.InputPaths, names, request.TestMode);

        var errors = ir.Diagnostics.Where(d => d.Severity == Severity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new CompileException(string.Join("\n", errors.Select(d => "error: " + d)));
        }
        foreach (var w in ir.Diagnostics.Where(d => d.Severity == Severity.Warning))
        {
            Console.Error.WriteLine("dotcc: warning: " + w);
        }
        return ir;
    }

    /// <summary>Parse every <c>.zig</c> unit and lower it INTO <paramref name="ir"/>
    /// (no builder creation, no diagnostic flush). Factored out of
    /// <see cref="BuildIr"/> so a mixed <c>.c</c> + <c>.zig</c> whole-program build can
    /// lower the Zig units into the C front-end's already-built <see cref="IrBuilder"/>
    /// — one shared module, one emit (the C side's structs/enums/globals preserved),
    /// cross-language calls resolving as bare-name <c>DotCcProgram</c> methods. The
    /// caller owns diagnostic flushing.</summary>
    internal static void AddUnits(IrBuilder ir, IReadOnlyList<string> paths, INameLegalizer names, bool testMode = false)
    {
        var lexerTable = Zig.BuildLexer();
        // One error-code registry shared across the build's units — a given `error.Foo`
        // name maps to one code program-wide (V1 erases the error set into a flat space).
        var errorCodes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var source = File.ReadAllText(path);
            Item root;
            try
            {
                var parser = Zig.BuildParser(Zig.IdentityVisitor.Instance);
                using var lexer = BytesLexer.FromString(source, lexerTable);
                using var tokens = new SyncLATokenIterator(lexer);
                root = parser.ParseInput(tokens, debugger: null, trimReductions: true);
            }
            catch (ParseErrorException ex)
            {
                throw new CompileException($"parse failed in {Path.GetFileName(path)}: {ex.Message}", ex);
            }
            catch (LexerException ex)
            {
                throw new CompileException($"lex failed in {Path.GetFileName(path)}: {ex.Message}", ex);
            }
            if (root.IsError)
            {
                throw new CompileException($"parse failed in {Path.GetFileName(path)}: {root}");
            }
            new ZigLowering(ir, names, errorCodes, testMode).Lower(root);
        }
        // Carry the flat error set to the backend so it can emit the `@errorName` code→name
        // table (Milestone X). Merge into any existing map (a mixed build lowers C first, but C
        // contributes no Zig error names; this also stays correct if a future C path adds some).
        if (errorCodes.Count > 0)
        {
            if (ir.ZigErrorCodes is { Count: > 0 } existing)
            {
                var merged = new Dictionary<string, int>(existing, StringComparer.Ordinal);
                foreach (var kv in errorCodes) { merged[kv.Key] = kv.Value; }
                ir.ZigErrorCodes = merged;
            }
            else
            {
                ir.ZigErrorCodes = errorCodes;
            }
        }
    }
}
