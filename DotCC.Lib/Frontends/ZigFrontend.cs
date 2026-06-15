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
                               request.WarnDiscardedQualifiers);
        var lexerTable = Zig.BuildLexer();

        foreach (var path in request.InputPaths)
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
            new ZigLowering(ir, names).Lower(root);
        }

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
}
