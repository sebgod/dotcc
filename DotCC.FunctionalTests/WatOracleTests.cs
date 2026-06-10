#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// Opt-in EXECUTION oracle for the WebAssembly-text backend: for each C program,
/// <see cref="Compiler.EmitWat"/> → <c>wat2wasm</c> (parse + typecheck + assemble)
/// → <c>node</c> (instantiate + call <c>main</c>), asserting <c>main</c>'s return
/// value equals what the C program computes.
/// </summary>
/// <remarks>
/// Mirrors the MSVC / gcc oracles' discipline: <see cref="Process.Start"/> lives
/// ONLY here, behind the <c>DOTCC_RUN_WAT</c> env gate, and the test skips cleanly
/// when the toolchain (wabt's <c>wat2wasm</c> + <c>node</c>) isn't on the host. The
/// always-on coverage of the emitter is the pure-text <c>WatBackendTests</c> in the
/// unit suite; this is the round-trip that proves the modules actually run.
/// Enable with <c>DOTCC_RUN_WAT=1</c>.
/// </remarks>
public sealed class WatOracleTests
{
    private const string RunEnv = "DOTCC_RUN_WAT";
    private static bool Requested => Environment.GetEnvironmentVariable(RunEnv) == "1";

    [Theory]
    [InlineData("int main(void){ return (2+3)*4 - 1; }", 19)]
    [InlineData("int main(void){ int s=0; for(int i=0;i<10;i++) s+=i; return s; }", 45)]
    [InlineData("int main(void){ int n=5,f=1; while(n>1){ f*=n; n--; } return f; }", 120)]
    [InlineData("int main(void){ int i=0,s=0; do { s+=2; i++; } while(i<5); return s; }", 10)]
    [InlineData("int main(void){ int a=42,b=56; while(b){ int t=a%b; a=b; b=t; } return a; }", 14)]
    [InlineData("int fib(int n){ if(n<2) return n; return fib(n-1)+fib(n-2); } int main(void){ return fib(10); }", 55)]
    [InlineData("int gcd(int a,int b){ return b==0?a:gcd(b,a%b); } int main(void){ return gcd(48,36); }", 12)]
    [InlineData("int main(void){ int s=0; for(int i=0;i<5;i++){ if(i==2) continue; s+=i; } return s; }", 8)]
    [InlineData("int main(void){ int x=4; return x>3 ? x+5 : x-5; }", 9)]
    [InlineData("int main(void){ long a=5,b=6; return (int)(a*b); }", 30)]
    [InlineData("int is_even(int n); int is_odd(int n){ return n==0?0:is_even(n-1);} int is_even(int n){ return n==0?1:is_odd(n-1);} int main(void){ return is_even(10); }", 1)]
    // milestone 2 — linear memory, string data segments, pointer load/arithmetic
    [InlineData("int main(void){ char *s = \"ABC\"; return s[0] + s[2]; }", 132)]
    [InlineData("int main(void){ char *s = \"hello\"; int n=0; while(*s){ n++; s++; } return n; }", 5)]
    [InlineData("int slen(char *p){ char *q=p; while(*q) q++; return (int)(q-p); } int main(void){ return slen(\"world\"); }", 5)]
    [InlineData("int at(char *s, int i){ return s[i]; } int main(void){ return at(\"ABCDE\", 3); }", 68)]
    [InlineData("int idx(char *s, char c){ int i=0; while(s[i]){ if(s[i]==c) return i; i++; } return -1; } int main(void){ return idx(\"abcd\", 'c'); }", 2)]
    // shadow stack — address-of-local, stores through pointers, local arrays
    [InlineData("int main(void){ int x=5; int *p=&x; *p=10; return x; }", 10)]
    [InlineData("void swap(int*a,int*b){ int t=*a; *a=*b; *b=t; } int main(void){ int x=1,y=2; swap(&x,&y); return x*10+y; }", 21)]
    [InlineData("int main(void){ int a[5]={1,2,3,4,5}; int s=0; for(int i=0;i<5;i++) s+=a[i]; return s; }", 15)]
    [InlineData("int main(void){ int a[3]={5,5,5}; a[1]+=10; return a[0]+a[1]+a[2]; }", 25)]
    [InlineData("int main(void){ int a[4]={4,2,3,1}; for(int i=0;i<4;i++) for(int j=0;j<3;j++) if(a[j]>a[j+1]){int t=a[j];a[j]=a[j+1];a[j+1]=t;} return a[0]*1000+a[1]*100+a[2]*10+a[3]; }", 1234)]
    [InlineData("int dbl(int n){ int *p=&n; *p=*p*2; return n; } int main(void){ return dbl(4); }", 8)]
    // milestone 2 — the heap: a bump allocator (malloc/free/calloc/realloc) over
    // linear memory grown on demand above the shadow stack.
    [InlineData("#include <stdlib.h>\nint main(void){ int *p = malloc(sizeof(int)); *p = 42; return *p; }", 42)]
    [InlineData("#include <stdlib.h>\nint main(void){ int *a = malloc(5*sizeof(int)); for(int i=0;i<5;i++) a[i]=i*i; int s=0; for(int i=0;i<5;i++) s+=a[i]; return s; }", 30)]
    [InlineData("#include <stdlib.h>\nint main(void){ int *x=malloc(sizeof(int)); int *y=malloc(sizeof(int)); *x=10; *y=20; return *x*100+*y; }", 1020)]
    [InlineData("#include <stdlib.h>\nint main(void){ int *x=malloc(sizeof(int)); *x=5; free(x); int *y=malloc(sizeof(int)); *y=7; return *y; }", 7)]
    [InlineData("#include <stdlib.h>\nint main(void){ int *a=calloc(3,sizeof(int)); a[0]+=1; a[1]+=2; a[2]+=3; return a[0]+a[1]+a[2]; }", 6)]
    [InlineData("#include <stdlib.h>\nint main(void){ int *a=malloc(2*sizeof(int)); a[0]=11; a[1]=22; a=realloc(a,4*sizeof(int)); a[2]=33; a[3]=44; return a[0]+a[1]+a[2]+a[3]; }", 110)]
    // floating point — f64/f32 arithmetic observed by truncating the result to int
    // (the oracle asserts main()'s int return).
    [InlineData("int main(void){ return (int)(1.5 + 2.5); }", 4)]
    [InlineData("int main(void){ return (int)(7.0/2.0 * 10); }", 35)]
    [InlineData("int main(void){ int n=5; double d=2.5; return (int)(n*d); }", 12)]
    [InlineData("int main(void){ return (int)(-2.9); }", -2)]                       // truncation toward zero
    [InlineData("int main(void){ double a=1.5,b=2.5; return a<b ? 10 : 20; }", 10)]
    [InlineData("int main(void){ float f=0.5f, g=0.25f; return (int)((f+g)*100); }", 75)]
    [InlineData("int main(void){ double d=1.0; d+=2.5; d*=2.0; return (int)d; }", 7)]
    [InlineData("int main(void){ double d=2.0; double *p=&d; *p+=0.5; return (int)(*p*10); }", 25)]
    [InlineData("int main(void){ double a[3]={1.5,2.5,3.0}; double s=0; for(int i=0;i<3;i++) s+=a[i]; return (int)s; }", 7)]
    [InlineData("double sq(double x){ return x*x; } int main(void){ return (int)sq(3.0); }", 9)]
    [InlineData("int main(void){ double d=3.5; d++; return (int)d; }", 4)]
    public void Wat_program_returns_expected_value(string source, int expected)
    {
        if (!Requested)
        {
            Assert.Skip($"set {RunEnv}=1 to run the wat execution oracle (needs wabt's wat2wasm + node on PATH).");
        }
        RunWat(source).ShouldBe(expected);
    }

    // Byte-level stdout (putchar/puts over the WASI fd_write import). A second oracle
    // mode: instead of main()'s return value, capture what the program writes to fd 1
    // and assert the exact bytes. The node shim provides the fd_write import and reads
    // the iovecs out of the module's exported memory.
    [Theory]
    [InlineData("int main(void){ puts(\"hello\"); return 0; }", "hello\n")]
    [InlineData("int main(void){ putchar('A'); putchar('B'); putchar('\\n'); return 0; }", "AB\n")]
    [InlineData("int main(void){ puts(\"line1\"); puts(\"line2\"); return 0; }", "line1\nline2\n")]
    [InlineData("int main(void){ puts(\"hi\"); putchar('!'); return 0; }", "hi\n!")]
    [InlineData("void greet(char *s){ puts(s); } int main(void){ greet(\"hi\"); greet(\"yo\"); return 0; }", "hi\nyo\n")]
    [InlineData("int main(void){ char *s = \"abc\"; while(*s) putchar(*s++); return 0; }", "abc")]
    [InlineData("int main(void){ for(int i=0;i<3;i++) putchar('0'+i); return 0; }", "012")]
    // printf — string-literal format expanded at compile time
    [InlineData("int main(void){ printf(\"hello\\n\"); return 0; }", "hello\n")]
    [InlineData("int main(void){ printf(\"n=%d\\n\", 42); return 0; }", "n=42\n")]
    [InlineData("int main(void){ printf(\"%d+%d=%d\\n\", 2, 3, 2+3); return 0; }", "2+3=5\n")]
    [InlineData("int main(void){ printf(\"%d\\n\", -7); return 0; }", "-7\n")]
    [InlineData("int main(void){ printf(\"%u\\n\", 4000000000u); return 0; }", "4000000000\n")]
    [InlineData("int main(void){ printf(\"hex=%x cap=%X oct=%o\\n\", 255, 255, 64); return 0; }", "hex=ff cap=FF oct=100\n")]
    [InlineData("int main(void){ printf(\"%s %s%c\\n\", \"hello\", \"world\", '!'); return 0; }", "hello world!\n")]
    [InlineData("int main(void){ printf(\"100%% done\\n\"); return 0; }", "100% done\n")]
    [InlineData("int main(void){ long n = 10000000000; printf(\"%ld\\n\", n); return 0; }", "10000000000\n")]
    [InlineData("int main(void){ for(int i=1;i<=3;i++) printf(\"[%d]\", i*i); return 0; }", "[1][4][9]")]
    // printf field formatting — width / precision / flags (compile-time constants)
    [InlineData("int main(void){ printf(\"[%5d][%-5d]\", 42, 42); return 0; }", "[   42][42   ]")]
    [InlineData("int main(void){ printf(\"[%05d][%05d]\", 42, -42); return 0; }", "[00042][-0042]")]
    [InlineData("int main(void){ printf(\"[%+d][% d]\", 7, 7); return 0; }", "[+7][ 7]")]
    [InlineData("int main(void){ printf(\"[%.3d][%5.3d][%05.3d]\", 42, 42, 42); return 0; }", "[042][  042][  042]")]
    [InlineData("int main(void){ printf(\"[%8x][%08x]\", 255, 255); return 0; }", "[      ff][000000ff]")]
    [InlineData("int main(void){ printf(\"[%10s][%-10s][%.3s]\", \"hi\", \"hi\", \"hello\"); return 0; }", "[        hi][hi        ][hel]")]
    [InlineData("int main(void){ printf(\"[%3c][%-3c]\", 'x', 'x'); return 0; }", "[  x][x  ]")]
    [InlineData("int main(void){ printf(\"[%.0d][%.0d]\", 0, 5); return 0; }", "[][5]")]
    // sprintf / snprintf — same expansion, into a buffer instead of fd 1
    [InlineData("int main(void){ char b[32]; int n = sprintf(b, \"%d-%s\", 7, \"ok\"); printf(\"%s|%d\\n\", b, n); return 0; }", "7-ok|4\n")]
    [InlineData("int main(void){ char b[32]; sprintf(b, \"[%5d]\", 3); printf(\"%s\\n\", b); return 0; }", "[    3]\n")]
    [InlineData("int main(void){ char b[8]; int n = snprintf(b, 5, \"%d\", 123456); printf(\"%s,%d\\n\", b, n); return 0; }", "1234,6\n")]
    [InlineData("int main(void){ char z[1]; int q = snprintf(z, 0, \"abc\"); printf(\"q=%d\\n\", q); return 0; }", "q=3\n")]
    // heap + I/O together: both runtimes coexist (bump pointer global, exported memory,
    // the WASI import) in one module.
    [InlineData("#include <stdlib.h>\nint main(void){ int *a=malloc(3*sizeof(int)); a[0]=1; a[1]=2; a[2]=3; printf(\"%d%d%d\\n\", a[0], a[1], a[2]); return 0; }", "123\n")]
    // printf %f — a correctly-rounded (round-half-to-even) formatter over exact
    // big-integer arithmetic. Expected strings are glibc/Python references; the digits
    // match because the conversion never goes through lossy f64 math.
    [InlineData("int main(void){ printf(\"%f\\n\", 1.5); return 0; }", "1.500000\n")]
    [InlineData("int main(void){ printf(\"[%.2f]\", 3.14159); return 0; }", "[3.14]")]
    [InlineData("int main(void){ printf(\"%.0f %.0f\", 2.5, 3.5); return 0; }", "2 4")]          // round half to even
    [InlineData("int main(void){ printf(\"%.1f %.1f\", 0.25, 0.35); return 0; }", "0.2 0.3")]   // 0.35 is < 0.35 exactly
    [InlineData("int main(void){ printf(\"[%8.2f][%-8.2f][%08.2f]\", 3.14, 3.14, -3.14); return 0; }", "[    3.14][3.14    ][-0003.14]")]
    [InlineData("int main(void){ printf(\"%+.2f % .2f\", 3.0, 3.0); return 0; }", "+3.00  3.00")]
    [InlineData("int main(void){ printf(\"%.3f\", 2.0/3.0); return 0; }", "0.667")]
    [InlineData("int main(void){ printf(\"%f\", -0.0); return 0; }", "-0.000000")]
    [InlineData("int main(void){ printf(\"%.0f\", 0.0); return 0; }", "0")]
    [InlineData("int main(void){ printf(\"%#.0f\", 5.0); return 0; }", "5.")]
    [InlineData("int main(void){ double x=10.0, y=3.0; printf(\"%.4f\", x/y); return 0; }", "3.3333")]
    [InlineData("int main(void){ printf(\"%.1f\", 1.0e20); return 0; }", "100000000000000000000.0")]
    [InlineData("int main(void){ printf(\"%.10f\", 0.1); return 0; }", "0.1000000000")]
    // printf %e / %g — the scaled-Dragon formatter (significant digits + exponent),
    // also correctly rounded against glibc/Python references.
    [InlineData("int main(void){ printf(\"%e\", 1.5); return 0; }", "1.500000e+00")]
    [InlineData("int main(void){ printf(\"%.2e\", 3.14159); return 0; }", "3.14e+00")]
    [InlineData("int main(void){ printf(\"%.0e\", 9.99); return 0; }", "1e+01")]            // carry bumps the exponent
    [InlineData("int main(void){ printf(\"%e\", -0.0); return 0; }", "-0.000000e+00")]
    [InlineData("int main(void){ printf(\"%.3e\", 0.000123456); return 0; }", "1.235e-04")]
    [InlineData("int main(void){ printf(\"%.0e\", 2.5); return 0; }", "2e+00")]              // round half to even
    [InlineData("int main(void){ printf(\"%+.2e\", 3.14); return 0; }", "+3.14e+00")]
    [InlineData("int main(void){ printf(\"%e\", 0.0); return 0; }", "0.000000e+00")]
    [InlineData("int main(void){ printf(\"%g\", 1.5); return 0; }", "1.5")]
    [InlineData("int main(void){ printf(\"%g\", 100.0); return 0; }", "100")]               // trailing zeros stripped
    [InlineData("int main(void){ printf(\"%g %g\", 0.0001, 0.00001); return 0; }", "0.0001 1e-05")]   // %f/%e boundary
    [InlineData("int main(void){ printf(\"%g\", 1234567.0); return 0; }", "1.23457e+06")]
    [InlineData("int main(void){ printf(\"%.14g\", 2.0/3.0); return 0; }", "0.66666666666667")]   // Lua's default
    [InlineData("int main(void){ printf(\"%#.3g\", 1.0); return 0; }", "1.00")]             // '#' keeps trailing zeros
    [InlineData("int main(void){ printf(\"%g\", 0.0); return 0; }", "0")]
    [InlineData("int main(void){ printf(\"%.3g\", 3.14159); return 0; }", "3.14")]
    public void Wat_program_writes_expected_stdout(string source, string expected)
    {
        if (!Requested)
        {
            Assert.Skip($"set {RunEnv}=1 to run the wat execution oracle (needs wabt's wat2wasm + node on PATH).");
        }
        RunWatStdout(source).ShouldBe(expected);
    }

    /// <summary>EmitWat → wat2wasm → node, returning <c>main()</c>'s value.</summary>
    private static int RunWat(string source)
    {
        var stem = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{Guid.NewGuid():N}");
        string c = stem + ".c", wat = stem + ".wat", wasm = stem + ".wasm";
        File.WriteAllText(c, source);
        try
        {
            File.WriteAllText(wat, Compiler.EmitWat(new[] { c }));
            Exec("wat2wasm", wat, "-o", wasm);   // validates (parse + typecheck) and assembles
            const string js =
                "const fs=require('fs');" +
                "WebAssembly.instantiate(fs.readFileSync(process.argv[1]))" +
                ".then(r=>{const v=r.instance.exports.main();" +
                "process.stdout.write((typeof v==='bigint'?Number(v):v|0).toString());})" +
                ".catch(e=>{console.error(e);process.exit(1);});";
            var output = Exec("node", "-e", js, wasm);
            return int.Parse(output.Trim(), CultureInfo.InvariantCulture);
        }
        finally
        {
            foreach (var f in new[] { c, wat, wasm }) { try { File.Delete(f); } catch { /* best effort */ } }
        }
    }

    /// <summary>EmitWat → wat2wasm → node with a WASI <c>fd_write</c> shim, returning
    /// the bytes the program wrote to fd 1 (stdout). The shim reads each iovec out of
    /// the module's exported memory, accumulates fd-1 writes, and reports the byte
    /// count back through <c>nwritten</c> — the minimal slice of WASI putchar/puts
    /// need. The captured bytes are what the test asserts (not main's return value).</summary>
    private static string RunWatStdout(string source)
    {
        var stem = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{Guid.NewGuid():N}");
        string c = stem + ".c", wat = stem + ".wat", wasm = stem + ".wasm";
        File.WriteAllText(c, source);
        try
        {
            File.WriteAllText(wat, Compiler.EmitWat(new[] { c }));
            Exec("wat2wasm", wat, "-o", wasm);
            const string js =
                "const fs=require('fs');" +
                "let inst; const out=[];" +
                "const fd_write=(fd,iovs,iovsLen,nwrittenPtr)=>{" +
                "const dv=new DataView(inst.exports.memory.buffer);" +
                "const bytes=new Uint8Array(inst.exports.memory.buffer);" +
                "let written=0;" +
                "for(let i=0;i<iovsLen;i++){" +
                "const ptr=dv.getUint32(iovs+i*8,true);" +
                "const len=dv.getUint32(iovs+i*8+4,true);" +
                "for(let j=0;j<len;j++){ if(fd===1) out.push(bytes[ptr+j]); }" +
                "written+=len;}" +
                "dv.setUint32(nwrittenPtr,written,true);return 0;};" +
                "WebAssembly.instantiate(fs.readFileSync(process.argv[1]),{wasi_snapshot_preview1:{fd_write}})" +
                ".then(r=>{inst=r.instance;inst.exports.main();" +
                "process.stdout.write(Buffer.from(out).toString('latin1'));})" +
                ".catch(e=>{console.error(e);process.exit(1);});";
            return Exec("node", "-e", js, wasm);
        }
        finally
        {
            foreach (var f in new[] { c, wat, wasm }) { try { File.Delete(f); } catch { /* best effort */ } }
        }
    }

    /// <summary>Run a tool and return its stdout. A missing tool
    /// (<see cref="System.ComponentModel.Win32Exception"/>) skips the test, like the
    /// other oracles when their compiler is absent; a non-zero exit fails it.</summary>
    private static string Exec(string file, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) { psi.ArgumentList.Add(a); }

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip($"'{file}' not found on PATH — install wabt (wat2wasm) and node to run the wat oracle.");
            throw; // unreachable: Assert.Skip throws
        }

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} exited {proc.ExitCode}: {stderr}");
        }
        return stdout;
    }
}
