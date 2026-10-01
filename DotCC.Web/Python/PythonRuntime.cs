#if DOTCC_PYTHON
using System.IO.Compression;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Components.WebAssembly.Services;

namespace DotCC.Web.Python;

/// <summary>
/// The dotcc-built CPython 3.13, run in this tab. libpython (the shared build's
/// <c>python3.13</c> assembly, CPython's C compiled to C# by dotcc) is loaded lazily on
/// the first run, and the standard library is unpacked into the tab's in-memory file
/// system under <see cref="Home"/>, which <c>PYTHONHOME</c> points the interpreter at.
/// Each run is a whole interpreter lifetime, <c>Py_BytesMain</c> from start to
/// finalization, with its standard streams captured.
/// </summary>
public sealed class PythonRuntime(HttpClient http, LazyAssemblyLoader loader)
{
    /// <summary>The interpreter's prefix: the standard library is at <c>lib/python3.13</c> under it.</summary>
    public const string Home = "/python";

    /// <summary>The file a run's source is written to and run from.</summary>
    private const string Script = "/tmp/main.py";

    private Task? _ready;

    /// <summary>True once libpython and the standard library are in place.</summary>
    public bool IsLoaded => _ready is { IsCompletedSuccessfully: true };

    /// <summary>What one run printed, and how it ended.</summary>
    public sealed record Result(string Stdout, string Stderr, int ExitCode, TimeSpan Elapsed);

    /// <summary>Load libpython and unpack the standard library, once.</summary>
    public Task EnsureLoadedAsync() => _ready ??= LoadAsync();

    private async Task LoadAsync()
    {
        await loader.LoadAssembliesAsync(["python3.13.wasm"]);
        var lib = Path.Combine(Home, "lib", "python3.13");
        if (!Directory.Exists(Path.Combine(lib, "encodings")))
        {
            await using var zip = await http.GetStreamAsync("cpython/stdlib.zip");
            using var buffered = new MemoryStream();
            await zip.CopyToAsync(buffered);
            buffered.Position = 0;
            ZipFile.ExtractToDirectory(buffered, lib, overwriteFiles: true);
        }
        // getpath looks for the platform-dependent libraries here and warns when it is missing.
        Directory.CreateDirectory(Path.Combine(lib, "lib-dynload"));
        Directory.CreateDirectory("/tmp");
        Environment.SetEnvironmentVariable("PYTHONHOME", Home);
        Environment.SetEnvironmentVariable("PYTHONDONTWRITEBYTECODE", "1");
        Environment.SetEnvironmentVariable("PYTHONIOENCODING", "utf-8");
    }

    /// <summary>Run <paramref name="source"/> as <c>python main.py</c>, with its output captured.</summary>
    public async Task<Result> RunAsync(string source)
    {
        await EnsureLoadedAsync();
        await File.WriteAllTextAsync(Script, source);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int code;
        try
        {
            code = PyBytesMain("python", "-u", Script);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"[dotcc web] the interpreter threw {ex}");
            code = -1;
        }
        finally
        {
            Console.Out.Flush();
            Console.Error.Flush();
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
        return new Result(stdout.ToString(), stderr.ToString(), code, watch.Elapsed);
    }

    /// <summary><c>Py_BytesMain</c> over a C <c>argv</c>. The function is found by name: dotcc
    /// spreads a large library's functions over several classes, and which one holds it
    /// moves as CPython's sources do.</summary>
    private static unsafe int PyBytesMain(params string[] args)
    {
        var entry = s_main ??= FindEntry();
        var argv = (byte**)NativeMemory.AllocZeroed((nuint)(args.Length + 1), (nuint)sizeof(byte*));
        try
        {
            for (var i = 0; i < args.Length; i++) { argv[i] = (byte*)Marshal.StringToCoTaskMemUTF8(args[i]); }
            return (int)entry.Invoke(null, [args.Length, System.Reflection.Pointer.Box(argv, typeof(byte**))])!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(inner);
            throw;
        }
        finally
        {
            for (var i = 0; i < args.Length; i++) { Marshal.FreeCoTaskMem((nint)argv[i]); }
            NativeMemory.Free(argv);
        }
    }

    private static System.Reflection.MethodInfo? s_main;

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "libpython is not trimmed: it is lazily loaded whole.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "libpython is not trimmed: it is lazily loaded whole.")]
    private static System.Reflection.MethodInfo FindEntry()
    {
        var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "python3.13")
            ?? throw new InvalidOperationException("libpython (python3.13) is not loaded");
        return asm.GetExportedTypes()
            .Where(t => t.Name.StartsWith("DotCcLib_python3_13_Program", StringComparison.Ordinal))
            .Select(t => t.GetMethod("Py_BytesMain", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            .FirstOrDefault(m => m is not null)
            ?? throw new MissingMethodException("python3.13", "Py_BytesMain");
    }
}
#endif
