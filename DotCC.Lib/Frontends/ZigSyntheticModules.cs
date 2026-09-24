#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using DotCC.Ir;
using System.Text;

namespace DotCC.Frontends;

/// <summary>The compiler-provided modules that have no file on disk — <c>@import("builtin")</c> and
/// <c>@import("root")</c> (road-to-zig-std S3). Both are generated as ZIG SOURCE TEXT and fed through
/// the ordinary module path, so they parse, bind and resolve exactly like a file: no special lowering,
/// no second navigation rule, and a member dotcc did not think to provide fails with the same
/// "no such declaration" a real module would give.
///
/// <para><b>Why dotcc writes its own instead of copying zig's.</b> Real zig's generated
/// <c>builtin.zig</c> is only ~55 lines, but every line of it is typed against <c>std.Target</c> —
/// <c>std.Target.Cpu</c>, <c>std.Target.aarch64.featureSet(&amp;.{…})</c>, <c>std.Target.Os</c>. That
/// is <c>Target.zig</c> (3,824 lines) plus the <c>Target/</c> per-architecture feature tables (33,488
/// lines), all of it describing hardware dotcc does not target: the emitted program is C#. So this
/// module is DUCK-TYPED — bare enum literals and anonymous struct literals carrying the fields std
/// reads, and nothing else. What makes that work is that std's platform queries are comptime
/// QUESTIONS (<c>builtin.cpu.arch == .x86_64</c>, <c>switch (builtin.os.tag)</c>), which the comptime
/// aggregate domain answers without either side ever needing a nominal type.</para>
///
/// <para><b>The surface is measured, not guessed.</b> Counted across the pinned std's 195 files that
/// import it: <c>cpu</c> 196 (of which <c>cpu.arch</c> 172), <c>target</c> 100, <c>os</c> 100 (of
/// which <c>os.tag</c> 92), <c>link_libc</c> 99, <c>zig_backend</c> 87, <c>single_threaded</c> 46,
/// <c>mode</c> 43, <c>abi</c> 42, <c>strip_debug_info</c> 23, <c>unwind_tables</c> 21, then a tail of
/// ≤5 each. All of them are here. With a real std the <c>cpu</c> is the exception: it is a TYPED
/// <c>std.Target.Cpu</c> read off the host (<see cref="HostCpuSource"/>, the target-identity segment T3), so
/// <c>cpu.has(…)</c> (×19 in std) and <c>cacheLineForCpu(cpu)</c> are std.Target's own code. The <c>target</c>
/// methods (<c>target.isGnuLibC()</c>, <c>target.ptrBitWidth()</c>, about 10 call sites) are deliberately
/// NOT: a method needs a real type to hang on, and each one is a decision about what dotcc's target
/// actually is. They fail loudly, which is the right outcome until one is needed.</para></summary>
internal static class ZigSyntheticModules
{
    /// <summary>The virtual path the synthetic <c>builtin</c> module is registered under. Not a real
    /// file: the angle brackets make it unmistakable in an error message and unopenable by accident.</summary>
    internal const string BuiltinPath = "<dotcc:builtin>";

    /// <summary>The virtual path of the synthetic <c>root</c> module.</summary>
    internal const string RootPath = "<dotcc:root>";

    /// <summary>True for an <c>@import</c> spec that names a compiler-provided module rather than a
    /// file — the discriminator the import binder and <see cref="ZigLowering.ResolveImport"/> share.</summary>
    internal static bool IsSyntheticSpec(string spec) => spec is "builtin" or "root";

    /// <summary>The virtual path a synthetic spec registers under, or null when the spec is not one.</summary>
    internal static string? PathForSpec(string spec) => spec switch
    {
        "builtin" => BuiltinPath,
        "root" => RootPath,
        _ => null,
    };

    /// <summary>Generate the source of a synthetic module by its virtual path.</summary>
    internal static string SourceForPath(string path, bool withStd = false) => path switch
    {
        BuiltinPath => BuiltinSource(withStd),
        RootPath => RootSource(),
        _ => throw new IrUnsupportedException($"zig: '{path}' is not a synthetic module"),
    };

    /// <summary>The <c>root</c> module — the root compilation unit, which std probes with
    /// <c>@hasDecl(root, "std_options")</c> to let a program override std's defaults. dotcc has no
    /// mechanism for a user to supply those overrides yet, so the honest model is an EMPTY module:
    /// every probe answers "not declared", which is exactly what a program that declared nothing
    /// means, and std takes its default path. A user-facing override hook is a later brick.</summary>
    private static string RootSource() =>
        "// dotcc's synthetic `root` module (road-to-zig-std S3).\n"
        + "//\n"
        + "// The root compilation unit as std sees it. Empty on purpose: std probes this module for\n"
        + "// optional overrides (`std_options`, `panic`), and a program that declares none of them is\n"
        + "// exactly what an empty module describes — std then uses its own defaults.\n";

    /// <summary>The <c>builtin</c> module — the target description. Written entirely as bare enum
    /// literals and anonymous struct literals, so no nominal type is needed on either side (see the
    /// class summary).</summary>
    private static string BuiltinSource(bool withStd)
    {
        var sb = new StringBuilder();
        sb.Append("// dotcc's synthetic `builtin` module (road-to-zig-std S3) — generated, not a file.\n");
        sb.Append("//\n");
        sb.Append("// Duck-typed against what std READS, not against `std.Target`: a faithful copy would\n");
        sb.Append("// pull in ~37,000 lines of per-architecture CPU feature tables describing hardware that\n");
        sb.Append("// dotcc does not target, since the program it emits is C#.\n\n");

        sb.Append("// The compiling toolchain. `zig_backend` is what std switches on to pick a codegen\n");
        sb.Append("// workaround; dotcc is none of zig's own backends, and claiming to be `stage2_llvm` is\n");
        sb.Append("// the closest honest answer — it is the one std treats as fully capable.\n");
        sb.Append("pub const zig_version_string = \"0.17.0-dev\";\n");
        sb.Append("pub const zig_backend = .stage2_llvm;\n\n");

        sb.Append("// ReleaseFast, deliberately: dotcc does not trap integer overflow, and a safe mode\n");
        sb.Append("// would have std emit safety checks whose semantics dotcc does not implement. Claiming\n");
        sb.Append("// the mode dotcc actually behaves like keeps std's own code honest about it.\n");
        sb.Append("pub const mode = .ReleaseFast;\n\n");

        sb.Append("// link_libc = true is the big lever: it biases std toward its libc-backed paths, which\n");
        sb.Append("// bottom out in `extern fn`s dotcc's C-shaped runtime already implements, rather than\n");
        sb.Append("// toward raw syscalls it does not.\n");
        sb.Append("pub const link_libc = true;\n");
        sb.Append("pub const link_libcpp = false;\n\n");

        sb.Append("pub const is_test = false;\n");
        sb.Append("pub const single_threaded = false;\n");
        sb.Append("pub const strip_debug_info = false;\n");
        sb.Append("pub const unwind_tables = .none;\n");
        sb.Append("pub const have_error_return_tracing = false;\n");
        sb.Append("pub const valgrind_support = false;\n");
        sb.Append("pub const sanitize_thread = false;\n");
        sb.Append("pub const fuzz = false;\n");
        sb.Append("pub const position_independent_code = true;\n");
        sb.Append("pub const position_independent_executable = false;\n");
        sb.Append("pub const omit_frame_pointer = false;\n");
        sb.Append("pub const code_model = .default;\n");
        sb.Append("pub const output_mode = .Exe;\n");
        sb.Append("pub const link_mode = .static;\n\n");

        var arch = HostArch();
        var os = HostOsTag();
        var abi = HostAbi();
        var ofmt = HostObjectFormat();

        sb.Append("// The target triple, taken from the HOST that is running dotcc. std reads these to pick\n");
        sb.Append("// path separators, ABI details and per-OS branches; the host is the answer that makes a\n");
        sb.Append("// compile-time question like `std.fs.path.sep` come out right for the machine compiling.\n");
        sb.Append("// Note that the EMITTED program is portable C# regardless — this describes the compile,\n");
        sb.Append("// not the run. Each is a bare enum literal, so a `== .tag` or `switch` over it folds.\n");
        sb.Append("pub const abi = .").Append(abi).Append(";\n");
        sb.Append("pub const object_format = .").Append(ofmt).Append(";\n");
        // With a real std to navigate, the architecture carries the TYPE zig's own builtin gives it
        // (`std.Target.Cpu.Arch`, the target-identity segment T3a), so `builtin.cpu.arch.endian()` is the
        // enum's method from Target.zig; the tag still folds as a bare literal does.
        var archValue = withStd ? $"@as(std.Target.Cpu.Arch, .{arch})" : $".{arch}";
        if (withStd) { sb.Append("const std = @import(\"std\");\n"); }
        // With a real std, the cpu is a TYPED `std.Target.Cpu` read off the host (the target-identity segment
        // T3): its model and feature set come from .NET's own intrinsics, as zig's `-mcpu=native` reads the
        // host's, so std's comptime feature questions (`cpu.has(.x86, .avx2)`) answer for this machine.
        if (withStd && HostCpuSource(arch) is { } typedCpu) { sb.Append(typedCpu); }
        else { sb.Append("pub const cpu = .{ .arch = ").Append(archValue).Append(" };\n"); }
        sb.Append("pub const os = .{ .tag = .").Append(os).Append(" };\n");
        // Spelled out rather than referring to `cpu` / `os` by name: the aggregate recorder reads a
        // literal, and generated text costs nothing to repeat.
        sb.Append("pub const target = .{ .cpu = .{ .arch = ").Append(archValue)
          .Append(" }, .os = .{ .tag = .").Append(os)
          .Append(" }, .abi = .").Append(abi)
          .Append(", .ofmt = .").Append(ofmt).Append(" };\n");
        return sb.ToString();
    }

    /// <summary>The host CPU architecture as zig spells it. An architecture dotcc is not built for
    /// answers <c>.aarch64</c>: the value only steers which std branch is taken at compile time, and a
    /// wrong-but-modern 64-bit guess takes a saner path than an unrecognized tag, which would fall to
    /// std's own <c>else =&gt; @compileError</c>.</summary>
    private static string HostArch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.X86 => "x86",
        Architecture.Arm => "arm",
        _ => "aarch64",
    };

    /// <summary>The host CPU as a typed <c>std.Target.Cpu</c> declaration, for an x86_64 or aarch64 host; null for
    /// another architecture (which keeps the arch-only literal). The model is the x86-64 microarchitecture level
    /// the host reaches (<c>x86_64</c>, <c>_v2</c>, <c>_v3</c>, <c>_v4</c>) or aarch64's <c>generic</c>; the
    /// features are those .NET reports supported. The arm64 half is not verified on an arm64 host yet (backlog
    /// T6, docs/plans/deferred.md).</summary>
    internal static string? HostCpuSource(string arch)
    {
        var (family, model, features) = arch switch
        {
            "x86_64" => ("x86", HostX86Model(), HostX86Features()),
            "aarch64" => ("aarch64", "generic", HostAarch64Features()),
            _ => (null, null, null),
        };
        if (family is null || model is null || features is null) { return null; }
        var sb = new StringBuilder();
        sb.Append("pub const cpu: std.Target.Cpu = .{\n");
        sb.Append("    .arch = @as(std.Target.Cpu.Arch, .").Append(arch).Append("),\n");
        sb.Append("    .model = &std.Target.").Append(family).Append(".cpu.").Append(model).Append(",\n");
        sb.Append("    .features = std.Target.").Append(family).Append(".featureSet(&.{ ");
        sb.Append(string.Join(", ", features.Select(f => "." + f)));
        sb.Append(" }),\n");
        sb.Append("};\n");
        return sb.ToString();
    }

    /// <summary>The x86 features the host supports, as <c>std.Target.x86.Feature</c> names: the x86-64 baseline,
    /// then each ISA extension <c>System.Runtime.Intrinsics.X86</c> reports supported.</summary>
    private static List<string> HostX86Features()
    {
        var features = new List<string> { "@\"64bit\"", "cmov", "cx8", "fxsr", "mmx", "sse", "sse2", "x87" };
        void Add(bool supported, string name) { if (supported) { features.Add(name); } }
        Add(System.Runtime.Intrinsics.X86.Sse3.IsSupported, "sse3");
        Add(System.Runtime.Intrinsics.X86.Ssse3.IsSupported, "ssse3");
        Add(System.Runtime.Intrinsics.X86.Sse41.IsSupported, "sse4_1");
        Add(System.Runtime.Intrinsics.X86.Sse42.IsSupported, "sse4_2");
        Add(System.Runtime.Intrinsics.X86.Sse42.IsSupported, "cx16");   // part of x86-64-v2 with SSE4.2
        Add(System.Runtime.Intrinsics.X86.Popcnt.IsSupported, "popcnt");
        Add(System.Runtime.Intrinsics.X86.Avx.IsSupported, "avx");
        Add(System.Runtime.Intrinsics.X86.Avx2.IsSupported, "avx2");
        Add(System.Runtime.Intrinsics.X86.Fma.IsSupported, "fma");
        Add(System.Runtime.Intrinsics.X86.Bmi1.IsSupported, "bmi");
        Add(System.Runtime.Intrinsics.X86.Bmi2.IsSupported, "bmi2");
        Add(System.Runtime.Intrinsics.X86.Lzcnt.IsSupported, "lzcnt");
        Add(System.Runtime.Intrinsics.X86.Aes.IsSupported, "aes");
        Add(System.Runtime.Intrinsics.X86.Pclmulqdq.IsSupported, "pclmul");
        Add(System.Runtime.Intrinsics.X86.AvxVnni.IsSupported, "avxvnni");
        Add(System.Runtime.Intrinsics.X86.Avx512F.IsSupported, "avx512f");
        Add(System.Runtime.Intrinsics.X86.Avx512BW.IsSupported, "avx512bw");
        Add(System.Runtime.Intrinsics.X86.Avx512CD.IsSupported, "avx512cd");
        Add(System.Runtime.Intrinsics.X86.Avx512DQ.IsSupported, "avx512dq");
        Add(System.Runtime.Intrinsics.X86.Avx512F.VL.IsSupported, "avx512vl");
        Add(System.Runtime.Intrinsics.X86.Avx512Vbmi.IsSupported, "avx512vbmi");
        return features;
    }

    /// <summary>The x86-64 microarchitecture level the host reaches, as a <c>std.Target.x86.cpu</c> model name.</summary>
    private static string HostX86Model()
    {
        if (System.Runtime.Intrinsics.X86.Avx512F.VL.IsSupported && System.Runtime.Intrinsics.X86.Avx512BW.IsSupported
            && System.Runtime.Intrinsics.X86.Avx512CD.IsSupported && System.Runtime.Intrinsics.X86.Avx512DQ.IsSupported)
        {
            return "x86_64_v4";
        }
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && System.Runtime.Intrinsics.X86.Bmi1.IsSupported
            && System.Runtime.Intrinsics.X86.Bmi2.IsSupported && System.Runtime.Intrinsics.X86.Fma.IsSupported
            && System.Runtime.Intrinsics.X86.Lzcnt.IsSupported)
        {
            return "x86_64_v3";
        }
        if (System.Runtime.Intrinsics.X86.Sse42.IsSupported && System.Runtime.Intrinsics.X86.Popcnt.IsSupported
            && System.Runtime.Intrinsics.X86.Ssse3.IsSupported)
        {
            return "x86_64_v2";
        }
        return "x86_64";
    }

    /// <summary>The aarch64 features the host supports, as <c>std.Target.aarch64.Feature</c> names (unverified on
    /// an arm64 host: backlog T6).</summary>
    private static List<string> HostAarch64Features()
    {
        var features = new List<string> { "fp_armv8" };
        void Add(bool supported, string name) { if (supported) { features.Add(name); } }
        Add(System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported, "neon");
        Add(System.Runtime.Intrinsics.Arm.Aes.IsSupported, "aes");
        Add(System.Runtime.Intrinsics.Arm.Crc32.IsSupported, "crc");
        Add(System.Runtime.Intrinsics.Arm.Dp.IsSupported, "dotprod");
        Add(System.Runtime.Intrinsics.Arm.Rdm.IsSupported, "rdm");
        Add(System.Runtime.Intrinsics.Arm.Sha256.IsSupported, "sha2");
        return features;
    }

    /// <summary>The host OS as zig spells its <c>os.tag</c>.</summary>
    private static string HostOsTag()
    {
        if (System.OperatingSystem.IsWindows()) { return "windows"; }
        if (System.OperatingSystem.IsMacOS()) { return "macos"; }
        if (System.OperatingSystem.IsFreeBSD()) { return "freebsd"; }
        return "linux";
    }

    /// <summary>The host ABI as zig spells it. Matched to what real zig picks for the same host —
    /// <c>gnu</c> everywhere it ships a libc (including Windows, where its default toolchain is
    /// MinGW, NOT MSVC), and <c>none</c> on Apple targets. dotcc's runtime is BCL-backed so no ABI is
    /// literally true, but agreeing with zig is what keeps a differential program taking the same
    /// branch in both compilers.</summary>
    private static string HostAbi() => System.OperatingSystem.IsMacOS() ? "none" : "gnu";

    /// <summary>The host object format. dotcc emits no object files, so this exists only because std
    /// branches on it; the host's native format is the consistent answer.</summary>
    private static string HostObjectFormat()
    {
        if (System.OperatingSystem.IsWindows()) { return "coff"; }
        return System.OperatingSystem.IsMacOS() ? "macho" : "elf";
    }
}
