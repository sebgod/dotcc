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
    public void Wat_program_returns_expected_value(string source, int expected)
    {
        if (!Requested)
        {
            Assert.Skip($"set {RunEnv}=1 to run the wat execution oracle (needs wabt's wat2wasm + node on PATH).");
        }
        RunWat(source).ShouldBe(expected);
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
