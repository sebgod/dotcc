#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace DotCC.Wasm;

/// <summary>How a whole-module <see cref="WasmModuleProbe.Probe"/> ended.</summary>
internal enum WasmProbeStatus
{
    /// <summary>Every section decoded and every function body walked to its end.</summary>
    Ok,
    /// <summary>The module violated the binary format somewhere; the report still
    /// carries everything decoded up to (and recovered after) that point — see
    /// <see cref="WasmProbeReport.Notes"/> for what and where.</summary>
    Malformed,
}

/// <summary>One imported entity: <c>module.name</c> plus the import kind
/// (<c>func</c>/<c>table</c>/<c>memory</c>/<c>global</c>/<c>tag</c>).</summary>
internal readonly record struct WasmProbeImport(string Module, string Name, string Kind);

/// <summary>One exported entity: its public name and kind.</summary>
internal readonly record struct WasmProbeExport(string Name, string Kind);

/// <summary>
/// The inventory <see cref="WasmModuleProbe"/> extracts from one binary module:
/// section list, import/export surfaces, entity counts, a per-mnemonic opcode
/// histogram, and the set of post-MVP features the encoding actually uses. This is
/// the data the WF0 surface report ranks — which features real producers emit,
/// how big the import surface is, whether export names are usable.
/// </summary>
internal sealed class WasmProbeReport
{
    /// <summary>Overall outcome; <see cref="WasmProbeStatus.Malformed"/> keeps partial data.</summary>
    public WasmProbeStatus Status { get; internal set; } = WasmProbeStatus.Ok;

    /// <summary>The sections in file order: id, spec name (custom sections show
    /// <c>custom:&lt;name&gt;</c>), and payload size in bytes.</summary>
    public List<(byte Id, string Name, int Size)> Sections { get; } = new();

    /// <summary>Every import in declaration order.</summary>
    public List<WasmProbeImport> Imports { get; } = new();

    /// <summary>Every export in declaration order.</summary>
    public List<WasmProbeExport> Exports { get; } = new();

    /// <summary>Function-type count (the type section).</summary>
    public int TypeCount { get; internal set; }

    /// <summary>Functions <i>defined</i> in this module (code section entries; imports excluded).</summary>
    public int FunctionCount { get; internal set; }

    /// <summary>Defined tables / memories / globals / element segments / data segments.</summary>
    public int TableCount { get; internal set; }

    /// <inheritdoc cref="TableCount"/>
    public int MemoryCount { get; internal set; }

    /// <inheritdoc cref="TableCount"/>
    public int GlobalCount { get; internal set; }

    /// <inheritdoc cref="TableCount"/>
    public int ElemCount { get; internal set; }

    /// <inheritdoc cref="TableCount"/>
    public int DataCount { get; internal set; }

    /// <summary>True when a <c>start</c> section nominates an initializer function.</summary>
    public bool HasStart { get; internal set; }

    /// <summary>True when a <c>name</c> custom section is present (debug names for
    /// functions/locals — the fallback naming source when an export name is absent).</summary>
    public bool HasNameSection { get; internal set; }

    /// <summary>The <c>target_features</c> custom section entries (LLVM records what it
    /// used, e.g. <c>+bulk-memory</c>), verbatim; empty when the section is absent.</summary>
    public List<string> TargetFeatures { get; } = new();

    /// <summary>Opcode histogram over every decoded instruction (function bodies plus
    /// global/element/data initializer expressions), keyed by spec mnemonic.</summary>
    public SortedDictionary<string, int> Opcodes { get; } = new(StringComparer.Ordinal);

    /// <summary>The post-MVP features this module's <i>encoding</i> actually exercises
    /// (detected structurally — from opcodes, type shapes, and section flags — not
    /// trusted from <see cref="TargetFeatures"/>).</summary>
    public SortedSet<string> Features { get; } = new(StringComparer.Ordinal);

    /// <summary>Free-form findings: unknown custom sections, undecodable opcodes (with
    /// offsets), malformed-format details. Every fail-soft recovery leaves a note.</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Total instructions counted into <see cref="Opcodes"/>.</summary>
    public int InstructionCount { get; internal set; }
}

/// <summary>
/// A fail-soft binary <c>.wasm</c> inventory reader — the engine behind the WF0
/// surface probe (fable-wasm.md) and the first increment of the WF1 reader. It
/// decodes the section layout, the import/export surfaces, and every instruction
/// (with immediates, including the <c>0xFC</c>/<c>0xFD</c>/<c>0xFE</c> prefix
/// spaces) into a mnemonic histogram, and flags which post-MVP features the
/// encoding uses (multivalue, sign-extension, nontrapping float→int, bulk memory,
/// reference types, SIMD, threads, tail calls, exception handling, memory64, GC).
/// <para>Probe semantics, deliberately unlike the strict WF1 reader to come: it
/// <b>never throws</b>. A malformed byte or an opcode it cannot skip becomes a
/// positioned note plus <see cref="WasmProbeStatus.Malformed"/> (bodies recover at
/// the next code entry via the entry's declared size), so a corpus walk over
/// hundreds of modules never has to guard the call — the
/// <see cref="Frontends.ZigParseProbe"/> posture.</para>
/// <para>Pure and AOT-clean: no I/O, no <c>Process.Start</c>, zero dependencies.
/// The opt-in test that assembles the corpus and writes the ranked report lives in
/// the functional test project (<c>WasmSurfaceProbeTests</c>).</para>
/// </summary>
internal static class WasmModuleProbe
{
    /// <summary>Decode <paramref name="module"/> and inventory it. Never throws.</summary>
    public static WasmProbeReport Probe(ReadOnlySpan<byte> module)
    {
        var report = new WasmProbeReport();
        var r = new Reader(module);
        try
        {
            ProbeModule(ref r, report);
        }
        catch (MalformedModuleException ex)
        {
            report.Status = WasmProbeStatus.Malformed;
            report.Notes.Add($"malformed: {ex.Message} (at byte offset {ex.Offset})");
        }
        catch (Exception ex)
        {
            // Defensive, like ZigParseProbe's OtherError: a probe walking a whole
            // corpus must fold *any* failure into the report, never crash the walk.
            report.Status = WasmProbeStatus.Malformed;
            report.Notes.Add($"malformed: {ex.GetType().Name}: {ex.Message}");
        }
        return report;
    }

    private static void ProbeModule(ref Reader r, WasmProbeReport report)
    {
        if (r.Remaining < 8 ||
            r.ReadByte() != 0x00 || r.ReadByte() != 0x61 || r.ReadByte() != 0x73 || r.ReadByte() != 0x6D)
        {
            throw new MalformedModuleException("not a wasm module (bad \\0asm magic)", 0);
        }
        var version = r.ReadByte() | (r.ReadByte() << 8) | (r.ReadByte() << 16) | (r.ReadByte() << 24);
        if (version != 1)
        {
            report.Notes.Add($"unexpected wasm version {version} (expected 1)");
        }

        // Globals' mutability (imported first, then defined) so an exported mutable
        // global can be recognized when the export section arrives.
        var globalMutability = new List<bool>();

        while (r.Remaining > 0)
        {
            var sectionStart = r.Offset;
            var id = r.ReadByte();
            var size = checked((int)r.ReadU32());
            if (size > r.Remaining)
            {
                throw new MalformedModuleException(
                    $"section id {id} declares size {size} but only {r.Remaining} bytes remain", sectionStart);
            }
            var body = r.Slice(size);
            var sec = new Reader(body, r.Offset - size);
            switch (id)
            {
                case 0: ProbeCustomSection(ref sec, report, size); break;
                case 1: ProbeTypeSection(ref sec, report); break;
                case 2: ProbeImportSection(ref sec, report, globalMutability); break;
                case 3: report.Sections.Add((id, "function", size)); _ = sec.ReadU32(); break;
                case 4: ProbeTableSection(ref sec, report); break;
                case 5: ProbeMemorySection(ref sec, report); break;
                case 6: ProbeGlobalSection(ref sec, report, globalMutability); break;
                case 7: ProbeExportSection(ref sec, report, globalMutability); break;
                case 8: report.Sections.Add((id, "start", size)); report.HasStart = true; break;
                case 9: ProbeElemSection(ref sec, report); break;
                case 10: ProbeCodeSection(ref sec, report); break;
                case 11: ProbeDataSection(ref sec, report); break;
                case 12: report.Sections.Add((id, "datacount", size)); report.Features.Add("bulk-memory"); break;
                default:
                    report.Sections.Add((id, $"unknown:{id}", size));
                    report.Notes.Add($"unknown section id {id} ({size} bytes) — skipped");
                    break;
            }
        }
    }

    private static void ProbeCustomSection(ref Reader sec, WasmProbeReport report, int size)
    {
        var name = sec.ReadName();
        report.Sections.Add((0, $"custom:{name}", size));
        switch (name)
        {
            case "name":
                report.HasNameSection = true;
                break;
            case "target_features":
                var n = checked((int)sec.ReadU32());
                for (var i = 0; i < n; i++)
                {
                    var prefix = (char)sec.ReadByte(); // '+' used, '-' disallowed
                    report.TargetFeatures.Add(prefix + sec.ReadName());
                }
                break;
            case "producers":
                break; // provenance only — the section list already records it
            default:
                report.Notes.Add($"custom section \"{name}\" ({size} bytes) — skipped");
                break;
        }
    }

    private static void ProbeTypeSection(ref Reader sec, WasmProbeReport report)
    {
        report.Sections.Add((1, "type", sec.Remaining));
        var count = checked((int)sec.ReadU32());
        report.TypeCount = count;
        for (var i = 0; i < count; i++)
        {
            var form = sec.ReadByte();
            if (form != 0x60)
            {
                // GC proposal composite types (0x5F struct / 0x5E array / 0x4E rec …).
                report.Features.Add("gc");
                report.Notes.Add($"type[{i}] has non-func form 0x{form:X2} — stopped decoding the type section");
                return;
            }
            var paramCount = checked((int)sec.ReadU32());
            for (var p = 0; p < paramCount; p++) { ProbeValType(ref sec, report); }
            var resultCount = checked((int)sec.ReadU32());
            for (var q = 0; q < resultCount; q++) { ProbeValType(ref sec, report); }
            if (resultCount > 1)
            {
                report.Features.Add("multivalue");
            }
        }
    }

    private static void ProbeImportSection(ref Reader sec, WasmProbeReport report, List<bool> globalMutability)
    {
        report.Sections.Add((2, "import", sec.Remaining));
        var count = checked((int)sec.ReadU32());
        for (var i = 0; i < count; i++)
        {
            var module = sec.ReadName();
            var name = sec.ReadName();
            var kind = sec.ReadByte();
            switch (kind)
            {
                case 0x00:
                    _ = sec.ReadU32();
                    report.Imports.Add(new WasmProbeImport(module, name, "func"));
                    break;
                case 0x01:
                    ProbeValType(ref sec, report);
                    ProbeLimits(ref sec, report);
                    report.Imports.Add(new WasmProbeImport(module, name, "table"));
                    break;
                case 0x02:
                    ProbeLimits(ref sec, report);
                    report.Imports.Add(new WasmProbeImport(module, name, "memory"));
                    break;
                case 0x03:
                    ProbeValType(ref sec, report);
                    var mutable = sec.ReadByte() == 0x01;
                    globalMutability.Add(mutable);
                    if (mutable) { report.Features.Add("mutable-globals (imported)"); }
                    report.Imports.Add(new WasmProbeImport(module, name, mutable ? "global mut" : "global"));
                    break;
                case 0x04:
                    _ = sec.ReadByte();
                    _ = sec.ReadU32();
                    report.Features.Add("exception-handling");
                    report.Imports.Add(new WasmProbeImport(module, name, "tag"));
                    break;
                default:
                    throw new MalformedModuleException($"import[{i}] has unknown kind 0x{kind:X2}", sec.Offset);
            }
        }
    }

    private static void ProbeTableSection(ref Reader sec, WasmProbeReport report)
    {
        report.Sections.Add((4, "table", sec.Remaining));
        var count = checked((int)sec.ReadU32());
        report.TableCount = count;
        for (var i = 0; i < count; i++)
        {
            ProbeValType(ref sec, report); // reftype
            ProbeLimits(ref sec, report);
        }
        if (count > 1) { report.Features.Add("reference-types (multiple tables)"); }
    }

    private static void ProbeMemorySection(ref Reader sec, WasmProbeReport report)
    {
        report.Sections.Add((5, "memory", sec.Remaining));
        var count = checked((int)sec.ReadU32());
        report.MemoryCount = count;
        for (var i = 0; i < count; i++) { ProbeLimits(ref sec, report); }
        if (count > 1) { report.Features.Add("multi-memory"); }
    }

    private static void ProbeGlobalSection(ref Reader sec, WasmProbeReport report, List<bool> globalMutability)
    {
        report.Sections.Add((6, "global", sec.Remaining));
        var count = checked((int)sec.ReadU32());
        report.GlobalCount = count;
        for (var i = 0; i < count; i++)
        {
            ProbeValType(ref sec, report);
            globalMutability.Add(sec.ReadByte() == 0x01);
            WalkExpr(ref sec, report, $"global[{i}] init");
        }
    }

    private static void ProbeExportSection(ref Reader sec, WasmProbeReport report, List<bool> globalMutability)
    {
        report.Sections.Add((7, "export", sec.Remaining));
        var count = checked((int)sec.ReadU32());
        for (var i = 0; i < count; i++)
        {
            var name = sec.ReadName();
            var kind = sec.ReadByte();
            var index = checked((int)sec.ReadU32());
            var kindName = kind switch
            {
                0x00 => "func",
                0x01 => "table",
                0x02 => "memory",
                0x03 => "global",
                0x04 => "tag",
                _ => $"unknown:0x{kind:X2}",
            };
            if (kind == 0x03 && index < globalMutability.Count && globalMutability[index])
            {
                report.Features.Add("mutable-globals (exported)");
                kindName = "global mut";
            }
            report.Exports.Add(new WasmProbeExport(name, kindName));
        }
    }

    private static void ProbeElemSection(ref Reader sec, WasmProbeReport report)
    {
        report.Sections.Add((9, "elem", sec.Remaining));
        var count = checked((int)sec.ReadU32());
        report.ElemCount = count;
        for (var i = 0; i < count; i++)
        {
            var flags = sec.ReadU32();
            if (flags > 7)
            {
                throw new MalformedModuleException($"elem[{i}] has unknown flags {flags}", sec.Offset);
            }
            if (flags != 0)
            {
                // Anything beyond the MVP active-funcref-vector encoding comes from the
                // reference-types / bulk-memory era (passive/declared segments, expr lists).
                report.Features.Add($"reference-types (elem flags {flags})");
            }
            // Field layout per flags (spec §5.5.12): [table idx] [offset expr]
            // [elemkind/reftype] [funcidx vec | expr vec].
            if (flags == 2 || flags == 6) { _ = sec.ReadU32(); }
            if (flags == 0 || flags == 2 || flags == 4 || flags == 6) { WalkExpr(ref sec, report, $"elem[{i}] offset"); }
            if (flags == 1 || flags == 2 || flags == 3) { _ = sec.ReadByte(); }   // elemkind
            if (flags == 5 || flags == 6 || flags == 7) { ProbeValType(ref sec, report); } // reftype
            var n = checked((int)sec.ReadU32());
            if (flags <= 3)
            {
                for (var j = 0; j < n; j++) { _ = sec.ReadU32(); } // funcidx vector
            }
            else
            {
                for (var j = 0; j < n; j++) { WalkExpr(ref sec, report, $"elem[{i}] expr[{j}]"); }
            }
        }
    }

    private static void ProbeCodeSection(ref Reader sec, WasmProbeReport report)
    {
        report.Sections.Add((10, "code", sec.Remaining));
        var count = checked((int)sec.ReadU32());
        report.FunctionCount = count;
        for (var i = 0; i < count; i++)
        {
            var bodySize = checked((int)sec.ReadU32());
            var body = sec.Slice(bodySize);
            var b = new Reader(body, sec.Offset - bodySize);
            try
            {
                var localGroups = checked((int)b.ReadU32());
                for (var g = 0; g < localGroups; g++)
                {
                    _ = b.ReadU32();
                    ProbeValType(ref b, report);
                }
                WalkInstructions(ref b, report, $"func[{i}]");
            }
            catch (MalformedModuleException ex)
            {
                // Recover at the next code entry — the declared body size bounds the loss.
                report.Status = WasmProbeStatus.Malformed;
                report.Notes.Add($"func[{i}]: {ex.Message} (at byte offset {ex.Offset}) — skipped rest of body");
            }
        }
    }

    private static void ProbeDataSection(ref Reader sec, WasmProbeReport report)
    {
        report.Sections.Add((11, "data", sec.Remaining));
        var count = checked((int)sec.ReadU32());
        report.DataCount = count;
        for (var i = 0; i < count; i++)
        {
            var flags = sec.ReadU32();
            switch (flags)
            {
                case 0:
                    WalkExpr(ref sec, report, $"data[{i}] offset");
                    break;
                case 1:
                    report.Features.Add("bulk-memory (passive data)");
                    break;
                case 2:
                    _ = sec.ReadU32();
                    WalkExpr(ref sec, report, $"data[{i}] offset");
                    break;
                default:
                    throw new MalformedModuleException($"data[{i}] has unknown flags {flags}", sec.Offset);
            }
            var len = checked((int)sec.ReadU32());
            _ = sec.Slice(len);
        }
    }

    // ---- shared shapes -------------------------------------------------------

    /// <summary>Consume one value type byte, flagging the post-MVP type spaces
    /// (v128 → simd, funcref/externref → reference-types, GC heap types → gc).</summary>
    private static void ProbeValType(ref Reader r, WasmProbeReport report)
    {
        var b = r.ReadByte();
        switch (b)
        {
            case 0x7F: case 0x7E: case 0x7D: case 0x7C:
                break;
            case 0x7B:
                report.Features.Add("simd");
                break;
            case 0x70: case 0x6F:
                report.Features.Add("reference-types");
                break;
            default:
                report.Features.Add("gc");
                report.Notes.Add($"non-MVP value type 0x{b:X2}");
                break;
        }
    }

    /// <summary>Consume a limits record; flag bit 0x02 = shared (threads),
    /// 0x04 = 64-bit index space (memory64).</summary>
    private static void ProbeLimits(ref Reader r, WasmProbeReport report)
    {
        var flags = r.ReadByte();
        if ((flags & 0x02) != 0) { report.Features.Add("threads (shared memory)"); }
        if ((flags & 0x04) != 0) { report.Features.Add("memory64"); }
        _ = r.ReadU32();
        if ((flags & 0x01) != 0) { _ = r.ReadU32(); }
    }

    /// <summary>Walk one initializer expression (terminated by <c>end</c> at depth 0),
    /// counting its instructions into the histogram like any body.</summary>
    private static void WalkExpr(ref Reader r, WasmProbeReport report, string context)
        => WalkInstructions(ref r, report, context);

    // ---- the instruction walker ---------------------------------------------

    /// <summary>What immediate bytes follow an opcode — the walker's whole knowledge
    /// of instruction encoding.</summary>
    private enum Imm
    {
        None,
        BlockType,      // s33: 0x40 empty | valtype | type index (→ multivalue)
        LabelIdx,       // u32
        BrTable,        // vec(u32) + u32
        Idx,            // one u32 (func/local/global/table/elem/data index)
        CallIndirect,   // u32 typeidx + u32 tableidx
        SelectT,        // vec(valtype)
        MemArg,         // u32 align + u32 offset
        MemIdx,         // one flag/index byte (memory.size/grow)
        TwoBytes,       // two flag/index bytes (memory.copy)
        I32Const,       // s32
        I64Const,       // s64
        F32Const,       // 4 bytes
        F64Const,       // 8 bytes
        RefNull,        // heap type (s33-shaped)
        TryTable,       // blocktype + vec(catch clause)
    }

    /// <summary>Walk instructions until the <c>end</c> that closes the entry frame
    /// (depth −1) or the reader runs out. Every decoded mnemonic lands in the
    /// histogram; undecodable bytes throw <see cref="MalformedModuleException"/>
    /// (the caller recovers at a section/body boundary).</summary>
    private static void WalkInstructions(ref Reader r, WasmProbeReport report, string context)
    {
        var depth = 0;
        while (true)
        {
            if (r.Remaining == 0)
            {
                throw new MalformedModuleException($"{context}: body ended without closing 'end'", r.Offset);
            }
            var at = r.Offset;
            var op = r.ReadByte();
            string name;
            Imm imm;
            switch (op)
            {
                case 0xFC: (name, imm) = DecodeFC(ref r, report); break;
                case 0xFD: (name, imm) = DecodeSimd(ref r, report, context, at); break;
                case 0xFE: (name, imm) = DecodeAtomic(ref r, report); break;
                default:
                    (name, imm) = DecodeCore(op);
                    if (name.Length == 0)
                    {
                        throw new MalformedModuleException($"{context}: unknown opcode 0x{op:X2}", at);
                    }
                    break;
            }

            Count(report, name);
            NoteCoreFeature(report, op);

            switch (imm)
            {
                case Imm.None: break;
                case Imm.BlockType: ProbeBlockType(ref r, report); depth++; break;
                case Imm.LabelIdx: _ = r.ReadU32(); break;
                case Imm.BrTable:
                    var n = checked((int)r.ReadU32());
                    for (var i = 0; i <= n; i++) { _ = r.ReadU32(); }
                    break;
                case Imm.Idx: _ = r.ReadU32(); break;
                case Imm.CallIndirect: _ = r.ReadU32(); _ = r.ReadU32(); break;
                case Imm.SelectT:
                    var types = checked((int)r.ReadU32());
                    for (var i = 0; i < types; i++) { ProbeValType(ref r, report); }
                    break;
                case Imm.MemArg: _ = r.ReadU32(); _ = r.ReadU32(); break;
                case Imm.MemIdx: _ = r.ReadByte(); break;
                case Imm.TwoBytes: _ = r.ReadByte(); _ = r.ReadByte(); break;
                case Imm.I32Const: _ = r.ReadS64(); break;
                case Imm.I64Const: _ = r.ReadS64(); break;
                case Imm.F32Const: _ = r.Slice(4); break;
                case Imm.F64Const: _ = r.Slice(8); break;
                case Imm.RefNull: _ = r.ReadS64(); break;
                case Imm.TryTable:
                    ProbeBlockType(ref r, report);
                    var clauses = checked((int)r.ReadU32());
                    for (var i = 0; i < clauses; i++)
                    {
                        var kind = r.ReadByte();               // catch/catch_ref/catch_all/catch_all_ref
                        if (kind is 0x00 or 0x01) { _ = r.ReadU32(); } // tag index
                        _ = r.ReadU32();                       // label
                    }
                    depth++;
                    break;
                default: break;
            }

            // 'end' closes a frame; legacy-EH 'delegate' closes its try the same way.
            if (op is 0x0B or 0x18)
            {
                depth--;
                if (depth < 0) { return; }
            }
        }
    }

    /// <summary>Block type: <c>0x40</c> (empty) or a value type consumes one byte; a
    /// positive s33 is a type-section index — the multivalue encoding.</summary>
    private static void ProbeBlockType(ref Reader r, WasmProbeReport report)
    {
        var b = r.PeekByte();
        if (b == 0x40 || b == 0x7F || b == 0x7E || b == 0x7D || b == 0x7C || b == 0x7B || b == 0x70 || b == 0x6F)
        {
            _ = r.ReadByte();
            if (b == 0x7B) { report.Features.Add("simd"); }
            if (b is 0x70 or 0x6F) { report.Features.Add("reference-types"); }
            return;
        }
        var typeIndex = r.ReadS64(); // s33 in spec; s64 decode covers it
        if (typeIndex >= 0) { report.Features.Add("multivalue (block type index)"); }
    }

    private static void Count(WasmProbeReport report, string mnemonic)
    {
        report.Opcodes.TryGetValue(mnemonic, out var c);
        report.Opcodes[mnemonic] = c + 1;
        report.InstructionCount++;
    }

    /// <summary>Feature flags keyed off single-byte opcodes (the prefixed spaces flag
    /// themselves in their decoders).</summary>
    private static void NoteCoreFeature(WasmProbeReport report, byte op)
    {
        switch (op)
        {
            case >= 0xC0 and <= 0xC4: report.Features.Add("sign-extension"); break;
            case 0x12 or 0x13: report.Features.Add("tail-call"); break;
            case 0xD0 or 0xD1 or 0xD2: report.Features.Add("reference-types"); break;
            case 0x1C: report.Features.Add("reference-types (typed select)"); break;
            case 0x25 or 0x26: report.Features.Add("reference-types (table.get/set)"); break;
            case 0x06 or 0x07 or 0x08 or 0x09 or 0x0A or 0x18 or 0x19 or 0x1F:
                report.Features.Add("exception-handling");
                break;
            case 0x14 or 0x15: report.Features.Add("function-references"); break;
        }
    }

    /// <summary>The single-byte (core) opcode space → (mnemonic, immediate shape).
    /// An empty name means "not a defined opcode".</summary>
    private static (string, Imm) DecodeCore(byte op) => op switch
    {
        0x00 => ("unreachable", Imm.None),
        0x01 => ("nop", Imm.None),
        0x02 => ("block", Imm.BlockType),
        0x03 => ("loop", Imm.BlockType),
        0x04 => ("if", Imm.BlockType),
        0x05 => ("else", Imm.None),
        0x06 => ("try", Imm.BlockType),          // legacy exception handling
        0x07 => ("catch", Imm.Idx),
        0x08 => ("throw", Imm.Idx),
        0x09 => ("rethrow", Imm.Idx),
        0x0A => ("throw_ref", Imm.None),
        0x0B => ("end", Imm.None),
        0x0C => ("br", Imm.LabelIdx),
        0x0D => ("br_if", Imm.LabelIdx),
        0x0E => ("br_table", Imm.BrTable),
        0x0F => ("return", Imm.None),
        0x10 => ("call", Imm.Idx),
        0x11 => ("call_indirect", Imm.CallIndirect),
        0x12 => ("return_call", Imm.Idx),
        0x13 => ("return_call_indirect", Imm.CallIndirect),
        0x14 => ("call_ref", Imm.Idx),
        0x15 => ("return_call_ref", Imm.Idx),
        0x18 => ("delegate", Imm.LabelIdx),      // legacy exception handling
        0x19 => ("catch_all", Imm.None),
        0x1A => ("drop", Imm.None),
        0x1B => ("select", Imm.None),
        0x1C => ("select_t", Imm.SelectT),
        0x1F => ("try_table", Imm.TryTable),
        0x20 => ("local.get", Imm.Idx),
        0x21 => ("local.set", Imm.Idx),
        0x22 => ("local.tee", Imm.Idx),
        0x23 => ("global.get", Imm.Idx),
        0x24 => ("global.set", Imm.Idx),
        0x25 => ("table.get", Imm.Idx),
        0x26 => ("table.set", Imm.Idx),
        0x28 => ("i32.load", Imm.MemArg),
        0x29 => ("i64.load", Imm.MemArg),
        0x2A => ("f32.load", Imm.MemArg),
        0x2B => ("f64.load", Imm.MemArg),
        0x2C => ("i32.load8_s", Imm.MemArg),
        0x2D => ("i32.load8_u", Imm.MemArg),
        0x2E => ("i32.load16_s", Imm.MemArg),
        0x2F => ("i32.load16_u", Imm.MemArg),
        0x30 => ("i64.load8_s", Imm.MemArg),
        0x31 => ("i64.load8_u", Imm.MemArg),
        0x32 => ("i64.load16_s", Imm.MemArg),
        0x33 => ("i64.load16_u", Imm.MemArg),
        0x34 => ("i64.load32_s", Imm.MemArg),
        0x35 => ("i64.load32_u", Imm.MemArg),
        0x36 => ("i32.store", Imm.MemArg),
        0x37 => ("i64.store", Imm.MemArg),
        0x38 => ("f32.store", Imm.MemArg),
        0x39 => ("f64.store", Imm.MemArg),
        0x3A => ("i32.store8", Imm.MemArg),
        0x3B => ("i32.store16", Imm.MemArg),
        0x3C => ("i64.store8", Imm.MemArg),
        0x3D => ("i64.store16", Imm.MemArg),
        0x3E => ("i64.store32", Imm.MemArg),
        0x3F => ("memory.size", Imm.MemIdx),
        0x40 => ("memory.grow", Imm.MemIdx),
        0x41 => ("i32.const", Imm.I32Const),
        0x42 => ("i64.const", Imm.I64Const),
        0x43 => ("f32.const", Imm.F32Const),
        0x44 => ("f64.const", Imm.F64Const),
        0x45 => ("i32.eqz", Imm.None),
        0x46 => ("i32.eq", Imm.None),
        0x47 => ("i32.ne", Imm.None),
        0x48 => ("i32.lt_s", Imm.None),
        0x49 => ("i32.lt_u", Imm.None),
        0x4A => ("i32.gt_s", Imm.None),
        0x4B => ("i32.gt_u", Imm.None),
        0x4C => ("i32.le_s", Imm.None),
        0x4D => ("i32.le_u", Imm.None),
        0x4E => ("i32.ge_s", Imm.None),
        0x4F => ("i32.ge_u", Imm.None),
        0x50 => ("i64.eqz", Imm.None),
        0x51 => ("i64.eq", Imm.None),
        0x52 => ("i64.ne", Imm.None),
        0x53 => ("i64.lt_s", Imm.None),
        0x54 => ("i64.lt_u", Imm.None),
        0x55 => ("i64.gt_s", Imm.None),
        0x56 => ("i64.gt_u", Imm.None),
        0x57 => ("i64.le_s", Imm.None),
        0x58 => ("i64.le_u", Imm.None),
        0x59 => ("i64.ge_s", Imm.None),
        0x5A => ("i64.ge_u", Imm.None),
        0x5B => ("f32.eq", Imm.None),
        0x5C => ("f32.ne", Imm.None),
        0x5D => ("f32.lt", Imm.None),
        0x5E => ("f32.gt", Imm.None),
        0x5F => ("f32.le", Imm.None),
        0x60 => ("f32.ge", Imm.None),
        0x61 => ("f64.eq", Imm.None),
        0x62 => ("f64.ne", Imm.None),
        0x63 => ("f64.lt", Imm.None),
        0x64 => ("f64.gt", Imm.None),
        0x65 => ("f64.le", Imm.None),
        0x66 => ("f64.ge", Imm.None),
        0x67 => ("i32.clz", Imm.None),
        0x68 => ("i32.ctz", Imm.None),
        0x69 => ("i32.popcnt", Imm.None),
        0x6A => ("i32.add", Imm.None),
        0x6B => ("i32.sub", Imm.None),
        0x6C => ("i32.mul", Imm.None),
        0x6D => ("i32.div_s", Imm.None),
        0x6E => ("i32.div_u", Imm.None),
        0x6F => ("i32.rem_s", Imm.None),
        0x70 => ("i32.rem_u", Imm.None),
        0x71 => ("i32.and", Imm.None),
        0x72 => ("i32.or", Imm.None),
        0x73 => ("i32.xor", Imm.None),
        0x74 => ("i32.shl", Imm.None),
        0x75 => ("i32.shr_s", Imm.None),
        0x76 => ("i32.shr_u", Imm.None),
        0x77 => ("i32.rotl", Imm.None),
        0x78 => ("i32.rotr", Imm.None),
        0x79 => ("i64.clz", Imm.None),
        0x7A => ("i64.ctz", Imm.None),
        0x7B => ("i64.popcnt", Imm.None),
        0x7C => ("i64.add", Imm.None),
        0x7D => ("i64.sub", Imm.None),
        0x7E => ("i64.mul", Imm.None),
        0x7F => ("i64.div_s", Imm.None),
        0x80 => ("i64.div_u", Imm.None),
        0x81 => ("i64.rem_s", Imm.None),
        0x82 => ("i64.rem_u", Imm.None),
        0x83 => ("i64.and", Imm.None),
        0x84 => ("i64.or", Imm.None),
        0x85 => ("i64.xor", Imm.None),
        0x86 => ("i64.shl", Imm.None),
        0x87 => ("i64.shr_s", Imm.None),
        0x88 => ("i64.shr_u", Imm.None),
        0x89 => ("i64.rotl", Imm.None),
        0x8A => ("i64.rotr", Imm.None),
        0x8B => ("f32.abs", Imm.None),
        0x8C => ("f32.neg", Imm.None),
        0x8D => ("f32.ceil", Imm.None),
        0x8E => ("f32.floor", Imm.None),
        0x8F => ("f32.trunc", Imm.None),
        0x90 => ("f32.nearest", Imm.None),
        0x91 => ("f32.sqrt", Imm.None),
        0x92 => ("f32.add", Imm.None),
        0x93 => ("f32.sub", Imm.None),
        0x94 => ("f32.mul", Imm.None),
        0x95 => ("f32.div", Imm.None),
        0x96 => ("f32.min", Imm.None),
        0x97 => ("f32.max", Imm.None),
        0x98 => ("f32.copysign", Imm.None),
        0x99 => ("f64.abs", Imm.None),
        0x9A => ("f64.neg", Imm.None),
        0x9B => ("f64.ceil", Imm.None),
        0x9C => ("f64.floor", Imm.None),
        0x9D => ("f64.trunc", Imm.None),
        0x9E => ("f64.nearest", Imm.None),
        0x9F => ("f64.sqrt", Imm.None),
        0xA0 => ("f64.add", Imm.None),
        0xA1 => ("f64.sub", Imm.None),
        0xA2 => ("f64.mul", Imm.None),
        0xA3 => ("f64.div", Imm.None),
        0xA4 => ("f64.min", Imm.None),
        0xA5 => ("f64.max", Imm.None),
        0xA6 => ("f64.copysign", Imm.None),
        0xA7 => ("i32.wrap_i64", Imm.None),
        0xA8 => ("i32.trunc_f32_s", Imm.None),
        0xA9 => ("i32.trunc_f32_u", Imm.None),
        0xAA => ("i32.trunc_f64_s", Imm.None),
        0xAB => ("i32.trunc_f64_u", Imm.None),
        0xAC => ("i64.extend_i32_s", Imm.None),
        0xAD => ("i64.extend_i32_u", Imm.None),
        0xAE => ("i64.trunc_f32_s", Imm.None),
        0xAF => ("i64.trunc_f32_u", Imm.None),
        0xB0 => ("i64.trunc_f64_s", Imm.None),
        0xB1 => ("i64.trunc_f64_u", Imm.None),
        0xB2 => ("f32.convert_i32_s", Imm.None),
        0xB3 => ("f32.convert_i32_u", Imm.None),
        0xB4 => ("f32.convert_i64_s", Imm.None),
        0xB5 => ("f32.convert_i64_u", Imm.None),
        0xB6 => ("f32.demote_f64", Imm.None),
        0xB7 => ("f64.convert_i32_s", Imm.None),
        0xB8 => ("f64.convert_i32_u", Imm.None),
        0xB9 => ("f64.convert_i64_s", Imm.None),
        0xBA => ("f64.convert_i64_u", Imm.None),
        0xBB => ("f64.promote_f32", Imm.None),
        0xBC => ("i32.reinterpret_f32", Imm.None),
        0xBD => ("i64.reinterpret_f64", Imm.None),
        0xBE => ("f32.reinterpret_i32", Imm.None),
        0xBF => ("f64.reinterpret_i64", Imm.None),
        0xC0 => ("i32.extend8_s", Imm.None),
        0xC1 => ("i32.extend16_s", Imm.None),
        0xC2 => ("i64.extend8_s", Imm.None),
        0xC3 => ("i64.extend16_s", Imm.None),
        0xC4 => ("i64.extend32_s", Imm.None),
        0xD0 => ("ref.null", Imm.RefNull),
        0xD1 => ("ref.is_null", Imm.None),
        0xD2 => ("ref.func", Imm.Idx),
        0xD4 => ("ref.as_non_null", Imm.None),
        0xD5 => ("br_on_null", Imm.LabelIdx),
        0xD6 => ("br_on_non_null", Imm.LabelIdx),
        _ => ("", Imm.None),
    };

    /// <summary>The <c>0xFC</c> prefix space: nontrapping float→int truncations and the
    /// bulk-memory / table instructions.</summary>
    private static (string, Imm) DecodeFC(ref Reader r, WasmProbeReport report)
    {
        var sub = r.ReadU32();
        switch (sub)
        {
            case 0: report.Features.Add("nontrapping-fptoint"); return ("i32.trunc_sat_f32_s", Imm.None);
            case 1: report.Features.Add("nontrapping-fptoint"); return ("i32.trunc_sat_f32_u", Imm.None);
            case 2: report.Features.Add("nontrapping-fptoint"); return ("i32.trunc_sat_f64_s", Imm.None);
            case 3: report.Features.Add("nontrapping-fptoint"); return ("i32.trunc_sat_f64_u", Imm.None);
            case 4: report.Features.Add("nontrapping-fptoint"); return ("i64.trunc_sat_f32_s", Imm.None);
            case 5: report.Features.Add("nontrapping-fptoint"); return ("i64.trunc_sat_f32_u", Imm.None);
            case 6: report.Features.Add("nontrapping-fptoint"); return ("i64.trunc_sat_f64_s", Imm.None);
            case 7: report.Features.Add("nontrapping-fptoint"); return ("i64.trunc_sat_f64_u", Imm.None);
            case 8: report.Features.Add("bulk-memory"); return DrainMemoryInit(ref r);
            case 9: report.Features.Add("bulk-memory"); _ = r.ReadU32(); return ("data.drop", Imm.None);
            case 10: report.Features.Add("bulk-memory"); return ("memory.copy", Imm.TwoBytes);
            case 11: report.Features.Add("bulk-memory"); return ("memory.fill", Imm.MemIdx);
            case 12: report.Features.Add("bulk-memory (table.init)"); _ = r.ReadU32(); _ = r.ReadU32(); return ("table.init", Imm.None);
            case 13: report.Features.Add("bulk-memory (elem.drop)"); _ = r.ReadU32(); return ("elem.drop", Imm.None);
            case 14: report.Features.Add("bulk-memory (table.copy)"); _ = r.ReadU32(); _ = r.ReadU32(); return ("table.copy", Imm.None);
            case 15: report.Features.Add("reference-types"); _ = r.ReadU32(); return ("table.grow", Imm.None);
            case 16: report.Features.Add("reference-types"); _ = r.ReadU32(); return ("table.size", Imm.None);
            case 17: report.Features.Add("reference-types"); _ = r.ReadU32(); return ("table.fill", Imm.None);
            default:
                throw new MalformedModuleException($"unknown 0xFC sub-opcode {sub}", r.Offset);
        }
    }

    /// <summary>memory.init's immediates are data index (u32) then memory index (byte).</summary>
    private static (string, Imm) DrainMemoryInit(ref Reader r)
    {
        _ = r.ReadU32();
        _ = r.ReadByte();
        return ("memory.init", Imm.None);
    }

    /// <summary>The <c>0xFD</c> SIMD space: histogram as one bucket per sub-opcode class;
    /// immediates decoded just enough to keep walking.</summary>
    private static (string, Imm) DecodeSimd(ref Reader r, WasmProbeReport report, string context, int at)
    {
        report.Features.Add("simd");
        var sub = r.ReadU32();
        switch (sub)
        {
            case <= 11 or 92 or 93:                       // v128.load* / v128.store
                return ($"simd.0x{sub:X2}", Imm.MemArg);
            case >= 84 and <= 91:                          // lane load/store: memarg + lane
                _ = r.ReadU32(); _ = r.ReadU32(); _ = r.ReadByte();
                return ($"simd.0x{sub:X2}", Imm.None);
            case 12 or 13:                                 // v128.const / i8x16.shuffle: 16 bytes
                _ = r.Slice(16);
                return (sub == 12 ? "v128.const" : "i8x16.shuffle", Imm.None);
            case >= 21 and <= 34:                          // extract/replace lane: 1 lane byte
                _ = r.ReadByte();
                return ($"simd.0x{sub:X2}", Imm.None);
            case <= 275:                                   // plain ops, no immediates
                return ($"simd.0x{sub:X2}", Imm.None);
            default:
                throw new MalformedModuleException($"{context}: unknown SIMD sub-opcode {sub}", at);
        }
    }

    /// <summary>The <c>0xFE</c> threads/atomics space: everything is subop + memarg,
    /// except <c>atomic.fence</c> (one flag byte).</summary>
    private static (string, Imm) DecodeAtomic(ref Reader r, WasmProbeReport report)
    {
        report.Features.Add("threads (atomics)");
        var sub = r.ReadU32();
        return sub == 0x03 ? ($"atomic.0x{sub:X2}", Imm.MemIdx) : ($"atomic.0x{sub:X2}", Imm.MemArg);
    }

    // ---- the byte reader ------------------------------------------------------

    /// <summary>Thrown (internally only — <see cref="Probe"/> catches every instance)
    /// when the bytes violate the binary format; carries the absolute offset.</summary>
    private sealed class MalformedModuleException : Exception
    {
        public MalformedModuleException(string message, int offset) : base(message) => Offset = offset;
        public int Offset { get; }
    }

    /// <summary>A bounds-checked LEB128/byte cursor over the module (or a section
    /// slice of it). <see cref="Offset"/> is absolute in the original module so
    /// every diagnostic can name a real byte position.</summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly int _base;
        private int _pos;

        public Reader(ReadOnlySpan<byte> bytes) : this(bytes, 0) { }

        public Reader(ReadOnlySpan<byte> bytes, int absoluteBase)
        {
            _bytes = bytes;
            _base = absoluteBase;
            _pos = 0;
        }

        public readonly int Remaining => _bytes.Length - _pos;
        public readonly int Offset => _base + _pos;

        public byte ReadByte()
        {
            if (_pos >= _bytes.Length) { throw new MalformedModuleException("unexpected end of input", Offset); }
            return _bytes[_pos++];
        }

        public readonly byte PeekByte()
        {
            if (_pos >= _bytes.Length) { throw new MalformedModuleException("unexpected end of input", Offset); }
            return _bytes[_pos];
        }

        /// <summary>Unsigned LEB128, at most 5 bytes (u32).</summary>
        public uint ReadU32()
        {
            uint result = 0;
            var shift = 0;
            for (var i = 0; i < 5; i++)
            {
                var b = ReadByte();
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) { return result; }
                shift += 7;
            }
            throw new MalformedModuleException("unterminated LEB128 u32", Offset);
        }

        /// <summary>Signed LEB128, at most 10 bytes — wide enough for s33 and s64.</summary>
        public long ReadS64()
        {
            long result = 0;
            var shift = 0;
            for (var i = 0; i < 10; i++)
            {
                var b = ReadByte();
                result |= (long)(b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0)
                {
                    if (shift < 64 && (b & 0x40) != 0) { result |= -1L << shift; }
                    return result;
                }
            }
            throw new MalformedModuleException("unterminated LEB128 s64", Offset);
        }

        /// <summary>A length-prefixed UTF-8 name.</summary>
        public string ReadName()
        {
            var len = checked((int)ReadU32());
            var bytes = Slice(len);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Consume <paramref name="length"/> bytes and return them.</summary>
        public ReadOnlySpan<byte> Slice(int length)
        {
            if (length < 0 || length > Remaining)
            {
                throw new MalformedModuleException($"slice of {length} bytes exceeds the {Remaining} remaining", Offset);
            }
            var s = _bytes.Slice(_pos, length);
            _pos += length;
            return s;
        }
    }
}
