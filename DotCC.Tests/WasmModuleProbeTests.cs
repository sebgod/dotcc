#nullable enable

using System;
using System.Linq;
using DotCC.Wasm;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Always-on pins for <see cref="WasmModuleProbe"/> (fable-wasm.md WF0/WF1): modules
/// are hand-assembled from raw bytes by the tiny builder below — the seed of the
/// planned test-side <c>WasmBytes</c> builder — so the reader gets coverage with no
/// toolchain on the host (wabt appears only in the opt-in surface-probe leg).
/// Probe semantics are the contract under test: correct inventory on well-formed
/// input, fail-soft (a note, never a throw) on malformed input.
/// </summary>
[Collection("WasmModuleProbe")]
public sealed class WasmModuleProbeTests
{
    // ---- the byte builder (LEB128 + section scaffolding) ----------------------

    private static byte[] Leb(uint v)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) { b |= 0x80; }
            bytes.Add(b);
        } while (v != 0);
        return bytes.ToArray();
    }

    private static byte[] Str(string s)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(s);
        return Cat(Leb((uint)utf8.Length), utf8);
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Section(byte id, params byte[][] body)
    {
        var payload = Cat(body);
        return Cat(new[] { id }, Leb((uint)payload.Length), payload);
    }

    private static byte[] Module(params byte[][] sections) =>
        Cat(new byte[] { 0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00 }, Cat(sections));

    /// <summary>A code-section entry: size-prefixed body of local groups + instructions.</summary>
    private static byte[] FuncBody(byte[] locals, byte[] code)
    {
        var body = Cat(locals, code);
        return Cat(Leb((uint)body.Length), body);
    }

    private static readonly byte[] NoLocals = { 0x00 };

    // ---- pins -----------------------------------------------------------------

    [Fact]
    public void Empty_module_probes_clean()
    {
        var report = WasmModuleProbe.Probe(Module());

        report.Status.ShouldBe(WasmProbeStatus.Ok);
        report.Sections.ShouldBeEmpty();
        report.FunctionCount.ShouldBe(0);
        report.Notes.ShouldBeEmpty();
    }

    [Fact]
    public void Add_function_module_inventories_opcodes_and_export()
    {
        // (module (func (export "add") (param i32 i32) (result i32)
        //   local.get 0 local.get 1 i32.add))
        var module = Module(
            Section(1, Leb(1), new byte[] { 0x60, 0x02, 0x7F, 0x7F, 0x01, 0x7F }),
            Section(3, Leb(1), Leb(0)),
            Section(7, Leb(1), Str("add"), new byte[] { 0x00 }, Leb(0)),
            Section(10, Leb(1), FuncBody(NoLocals, new byte[] { 0x20, 0x00, 0x20, 0x01, 0x6A, 0x0B })));

        var report = WasmModuleProbe.Probe(module);

        report.Status.ShouldBe(WasmProbeStatus.Ok);
        report.TypeCount.ShouldBe(1);
        report.FunctionCount.ShouldBe(1);
        report.Exports.ShouldBe(new[] { new WasmProbeExport("add", "func") });
        report.Opcodes["local.get"].ShouldBe(2);
        report.Opcodes["i32.add"].ShouldBe(1);
        report.Opcodes["end"].ShouldBe(1);
        report.InstructionCount.ShouldBe(4);
        report.Features.ShouldBeEmpty(); // pure MVP — nothing post-MVP to flag
    }

    [Fact]
    public void Compiler_ProbeWasm_renders_a_readable_summary()
    {
        // The public Compiler.ProbeWasm shim (behind the sandbox's wasm tab, fable-web.md WEB6)
        // formats the WF0 inventory as human-readable text. Same "add" module as above.
        var module = Module(
            Section(1, Leb(1), new byte[] { 0x60, 0x02, 0x7F, 0x7F, 0x01, 0x7F }),
            Section(3, Leb(1), Leb(0)),
            Section(7, Leb(1), Str("add"), new byte[] { 0x00 }, Leb(0)),
            Section(10, Leb(1), FuncBody(NoLocals, new byte[] { 0x20, 0x00, 0x20, 0x01, 0x6A, 0x0B })));

        var summary = DotCC.Compiler.ProbeWasm(module);

        summary.ShouldContain("status   : ok");
        summary.ShouldContain("1 funcs");
        summary.ShouldContain("add (func)");
        summary.ShouldContain("local.get");  // the ranked opcode histogram is rendered
        summary.ShouldContain("read-only");   // states plainly that dotcc can't lift wasm yet
    }

    [Fact]
    public void Compiler_ProbeWasm_reports_malformed_without_throwing()
    {
        // The probe's fail-soft contract survives the public shim: bad bytes yield a summary
        // that says MALFORMED rather than throwing (the sandbox must never crash on a bad blob).
        var summary = DotCC.Compiler.ProbeWasm(new byte[] { 0x01, 0x02, 0x03, 0x04 });

        summary.ShouldContain("MALFORMED");
    }

    [Fact]
    public void Import_surface_is_enumerated()
    {
        // (import "wasi_snapshot_preview1" "fd_write" (func (type 0)))
        var module = Module(
            Section(1, Leb(1), new byte[] { 0x60, 0x00, 0x00 }),
            Section(2, Leb(1), Str("wasi_snapshot_preview1"), Str("fd_write"), new byte[] { 0x00 }, Leb(0)));

        var report = WasmModuleProbe.Probe(module);

        report.Status.ShouldBe(WasmProbeStatus.Ok);
        report.Imports.ShouldBe(new[] { new WasmProbeImport("wasi_snapshot_preview1", "fd_write", "func") });
    }

    [Fact]
    public void Sign_extension_opcode_flags_the_feature()
    {
        // (func (param i32) (result i32) local.get 0 i32.extend8_s)
        var module = Module(
            Section(1, Leb(1), new byte[] { 0x60, 0x01, 0x7F, 0x01, 0x7F }),
            Section(3, Leb(1), Leb(0)),
            Section(10, Leb(1), FuncBody(NoLocals, new byte[] { 0x20, 0x00, 0xC0, 0x0B })));

        var report = WasmModuleProbe.Probe(module);

        report.Status.ShouldBe(WasmProbeStatus.Ok);
        report.Features.ShouldContain("sign-extension");
        report.Opcodes["i32.extend8_s"].ShouldBe(1);
    }

    [Fact]
    public void Trunc_sat_prefix_opcode_flags_nontrapping_fptoint()
    {
        // (func (param f64) (result i32) local.get 0 i32.trunc_sat_f64_s)
        var module = Module(
            Section(1, Leb(1), new byte[] { 0x60, 0x01, 0x7C, 0x01, 0x7F }),
            Section(3, Leb(1), Leb(0)),
            Section(10, Leb(1), FuncBody(NoLocals, new byte[] { 0x20, 0x00, 0xFC, 0x02, 0x0B })));

        var report = WasmModuleProbe.Probe(module);

        report.Status.ShouldBe(WasmProbeStatus.Ok);
        report.Features.ShouldContain("nontrapping-fptoint");
        report.Opcodes["i32.trunc_sat_f64_s"].ShouldBe(1);
    }

    [Fact]
    public void Multi_result_type_flags_multivalue()
    {
        // (type (func (result i32 i32))) — two results is the multivalue encoding.
        var module = Module(
            Section(1, Leb(1), new byte[] { 0x60, 0x00, 0x02, 0x7F, 0x7F }));

        var report = WasmModuleProbe.Probe(module);

        report.Status.ShouldBe(WasmProbeStatus.Ok);
        report.Features.ShouldContain("multivalue");
    }

    [Fact]
    public void Memory_data_and_control_flow_are_inventoried()
    {
        // (memory 1) (data (i32.const 8) "hi")
        // (func (result i32) block (result i32) i32.const 8 i32.load br 0 end)
        var module = Module(
            Section(1, Leb(1), new byte[] { 0x60, 0x00, 0x01, 0x7F }),
            Section(3, Leb(1), Leb(0)),
            Section(5, Leb(1), new byte[] { 0x00, 0x01 }),
            Section(10, Leb(1), FuncBody(NoLocals, new byte[]
            {
                0x02, 0x7F,             // block (result i32)
                0x41, 0x08,             // i32.const 8
                0x28, 0x02, 0x00,       // i32.load align=2 offset=0
                0x0C, 0x00,             // br 0
                0x0B,                   // end (block)
                0x0B,                   // end (func)
            })),
            Section(11, Leb(1), Leb(0), new byte[] { 0x41, 0x08, 0x0B }, Leb(2), new byte[] { (byte)'h', (byte)'i' }));

        var report = WasmModuleProbe.Probe(module);

        report.Status.ShouldBe(WasmProbeStatus.Ok);
        report.MemoryCount.ShouldBe(1);
        report.DataCount.ShouldBe(1);
        report.Opcodes["block"].ShouldBe(1);
        report.Opcodes["br"].ShouldBe(1);
        report.Opcodes["i32.load"].ShouldBe(1);
        // Both function 'end's plus the data-offset initializer's 'end'.
        report.Opcodes["end"].ShouldBe(3);
        report.Opcodes["i32.const"].ShouldBe(2); // body + data offset expr
    }

    [Fact]
    public void Target_features_custom_section_is_parsed()
    {
        var module = Module(
            Section(0, Str("target_features"),
                Leb(2),
                new[] { (byte)'+' }, Str("bulk-memory"),
                new[] { (byte)'+' }, Str("sign-ext")));

        var report = WasmModuleProbe.Probe(module);

        report.Status.ShouldBe(WasmProbeStatus.Ok);
        report.TargetFeatures.ShouldBe(new[] { "+bulk-memory", "+sign-ext" });
    }

    [Fact]
    public void Name_custom_section_is_recognized()
    {
        var module = Module(Section(0, Str("name"), new byte[] { 0x00, 0x01, 0x00 }));

        var report = WasmModuleProbe.Probe(module);

        report.HasNameSection.ShouldBeTrue();
        report.Sections.Single().Name.ShouldBe("custom:name");
    }

    [Fact]
    public void Bad_magic_is_a_note_not_a_throw()
    {
        var report = WasmModuleProbe.Probe(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x00, 0x00, 0x00 });

        report.Status.ShouldBe(WasmProbeStatus.Malformed);
        report.Notes.Single().ShouldContain("magic");
    }

    [Fact]
    public void Truncated_section_is_a_note_not_a_throw()
    {
        // A type section declaring 100 bytes of payload with nothing behind it.
        var module = Cat(
            new byte[] { 0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00 },
            new byte[] { 0x01, 100 });

        var report = WasmModuleProbe.Probe(module);

        report.Status.ShouldBe(WasmProbeStatus.Malformed);
        report.Notes.Single().ShouldContain("declares size 100");
    }

    [Fact]
    public void Unknown_opcode_skips_that_body_and_keeps_the_rest()
    {
        // func[0] hits an undefined opcode (0x27); func[1] is fine. The probe must
        // recover at the code-entry boundary and still count func[1]'s body.
        var module = Module(
            Section(1, Leb(1), new byte[] { 0x60, 0x00, 0x00 }),
            Section(3, Leb(2), Leb(0), Leb(0)),
            Section(10, Leb(2),
                FuncBody(NoLocals, new byte[] { 0x27, 0x0B }),
                FuncBody(NoLocals, new byte[] { 0x01, 0x0B })));

        var report = WasmModuleProbe.Probe(module);

        report.Status.ShouldBe(WasmProbeStatus.Malformed);
        report.Notes.Single().ShouldContain("unknown opcode 0x27");
        report.FunctionCount.ShouldBe(2);
        report.Opcodes["nop"].ShouldBe(1); // func[1] still walked
    }

    [Fact]
    public void Unterminated_body_is_fail_soft()
    {
        // A body whose declared size ends before its 'end' opcode arrives.
        var module = Module(
            Section(1, Leb(1), new byte[] { 0x60, 0x00, 0x00 }),
            Section(3, Leb(1), Leb(0)),
            Section(10, Leb(1), FuncBody(NoLocals, new byte[] { 0x01 })));

        var report = WasmModuleProbe.Probe(module);

        report.Status.ShouldBe(WasmProbeStatus.Malformed);
        report.Notes.Single().ShouldContain("without closing 'end'");
    }
}
