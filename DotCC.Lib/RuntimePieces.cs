#nullable enable

using System;

namespace DotCC;

/// <summary>
/// The optional pieces of the DotCC.Libc runtime: each is spliced into a program only when one of
/// its units includes the synthetic header that declares it, because a program may define the
/// same C names itself, and its definitions and the runtime's would then be ambiguous wherever a
/// call reaches both by bare name (CPython defines the abi3 API and, configured for WASI-like
/// targets, stubs pthreads out). The pieces travel with an object (<c>runtime:</c> records) and
/// a managed library's manifest, as the program links them.
/// </summary>
[Flags]
public enum RuntimePieces
{
    /// <summary>Only the runtime every program links.</summary>
    None = 0,

    /// <summary>The abi3 shim behind the synthetic <c>&lt;Python.h&gt;</c> (PythonLib.cs).</summary>
    Python = 1,

    /// <summary>The POSIX threads behind the synthetic <c>&lt;pthread.h&gt;</c> and
    /// <c>&lt;sched.h&gt;</c> (PthreadLib.cs).</summary>
    Pthreads = 2,

    /// <summary>Every optional piece.</summary>
    All = Python | Pthreads,
}
