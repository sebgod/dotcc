using System.Reflection;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace PyBench;

/// <summary>
/// Entry point: runs <see cref="Snippets"/> under two jobs, the interpreter's JIT
/// build and its ReadyToRun build with tiered compilation off (precompiled,
/// optimized code, the nearest in-process stand-in for the NativeAOT binary).
/// </summary>
public static class Program
{
    /// <summary>Configure the two jobs from PYBENCH_JIT_DLL / PYBENCH_R2R_DLL
    /// (either may be absent) and run the benchmarks.</summary>
    public static void Main(string[] args)
    {
        var config = DefaultConfig.Instance;
        if (Environment.GetEnvironmentVariable("PYBENCH_JIT_DLL") is { Length: > 0 } jit)
        {
            config = config.AddJob(Job.Default.WithId("JIT")
                .WithEnvironmentVariable(Interpreter.DllVariable, jit));
        }
        if (Environment.GetEnvironmentVariable("PYBENCH_R2R_DLL") is { Length: > 0 } r2r)
        {
            config = config.AddJob(Job.Default.WithId("R2R")
                .WithEnvironmentVariable(Interpreter.DllVariable, r2r)
                .WithEnvironmentVariable("DOTNET_TieredCompilation", "0"));
        }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}

/// <summary>
/// The dotcc-built interpreter, loaded from the assembly PYBENCH_DLL names. Its C
/// functions are internal static methods of the generated DotCcProgram classes, so
/// the embedding API (<c>Py_InitializeEx</c>, <c>PyRun_SimpleString</c>) is found by
/// name through reflection; the calls themselves go through delegates, outside the
/// timed path's cost.
/// </summary>
public static unsafe class Interpreter
{
    /// <summary>The environment variable naming the interpreter assembly.</summary>
    public const string DllVariable = "PYBENCH_DLL";

    private delegate void InitFn(int installSigs);
    private delegate int RunFn(byte* code);

    private static RunFn? _run;

    /// <summary>Load the interpreter, turn on its POSIX view of Windows paths (what the
    /// program shell does for an interpreter linked with <c>-fposix-paths</c>; this
    /// harness calls in without the shell), point PYTHONHOME at the build's home and
    /// initialize it once per process.</summary>
    public static void Start()
    {
        if (_run is not null) { return; }
        var dll = Environment.GetEnvironmentVariable(DllVariable)
            ?? throw new InvalidOperationException($"{DllVariable} is not set");
        var home = Environment.GetEnvironmentVariable("PYBENCH_HOME")
            ?? throw new InvalidOperationException("PYBENCH_HOME is not set");
        Environment.SetEnvironmentVariable("PYTHONHOME", Path.GetFullPath(home));

        var asm = Assembly.LoadFrom(dll);
        asm.GetType("Libc")?.GetMethod("EnablePosixPathView")?.Invoke(null, null);
        _run = Find<RunFn>(asm, "PyRun_SimpleString");
        Find<InitFn>(asm, "Py_InitializeEx")(0);
    }

    /// <summary>Run Python source in <c>__main__</c>; throw if it raised.</summary>
    public static void Run(string source)
    {
        var run = _run ?? throw new InvalidOperationException("the interpreter is not started");
        var utf8 = Marshal.StringToCoTaskMemUTF8(source);
        try
        {
            if (run((byte*)utf8) != 0) { throw new InvalidOperationException($"Python raised running: {source}"); }
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static T Find<T>(Assembly asm, string name) where T : Delegate
    {
        foreach (var type in asm.GetTypes())
        {
            if (type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is { } m)
            {
                return m.CreateDelegate<T>();
            }
        }
        throw new MissingMethodException($"{name} is not in {asm.GetName().Name}");
    }
}

/// <summary>
/// Python loops of 100,000 iterations each, defined once and called per benchmark
/// invocation, so compiling the snippet is not what is measured. They separate the
/// interpreter's costs: dispatch alone, small-int and float arithmetic (with the
/// result object each makes), calls, attribute stores and list indexing.
/// </summary>
public class Snippets
{
    private const string Definitions = """
        N = 100_000

        def empty_loop():
            for i in range(N):
                pass

        def int_add():
            x = 0
            for i in range(N):
                x = x + 3

        def float_add():
            x = 0.0
            for i in range(N):
                x = x + 1.5

        def float_mul_add():
            x = 1.0
            y = 0.0
            for i in range(N):
                y = y + x * 0.999

        def _f(a):
            return a

        def calls():
            for i in range(N):
                _f(i)

        class _P:
            __slots__ = ("x",)
            def __init__(self):
                self.x = 0

        def attr():
            p = _P()
            for i in range(N):
                p.x = i

        def list_index():
            xs = [0.0] * 16
            for i in range(N):
                xs[i & 15] = xs[(i + 1) & 15]
        """;

    /// <summary>Start the interpreter and define the loops.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Interpreter.Start();
        Interpreter.Run(Definitions);
    }

    [Benchmark(Baseline = true)] public void EmptyLoop() => Interpreter.Run("empty_loop()");
    [Benchmark] public void IntAdd() => Interpreter.Run("int_add()");
    [Benchmark] public void FloatAdd() => Interpreter.Run("float_add()");
    [Benchmark] public void FloatMulAdd() => Interpreter.Run("float_mul_add()");
    [Benchmark] public void Calls() => Interpreter.Run("calls()");
    [Benchmark] public void Attr() => Interpreter.Run("attr()");
    [Benchmark] public void ListIndex() => Interpreter.Run("list_index()");
}
