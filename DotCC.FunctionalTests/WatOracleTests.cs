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
    // switch / case / default — nested-block lowering, comparison dispatch, fall-through
    [InlineData("int f(int x){ switch(x){ case 1: return 10; case 2: case 3: return 23; default: return -1; } } int main(void){ return f(3); }", 23)]
    [InlineData("int f(int x){ int s=0; switch(x){ case 0: s+=1; case 1: s+=2; case 2: s+=4; break; default: s+=100; } return s; } int main(void){ return f(0); }", 7)]
    [InlineData("int f(int x){ int r=-7; switch(x){ case 1: r=1; break; case 2: r=2; break; } return r; } int main(void){ return f(9); }", -7)]
    [InlineData("int f(int x){ switch(x){ case -1: return 100; case -2: return 200; default: return 0; } } int main(void){ return f(-2); }", 200)]
    [InlineData("long f(long x){ switch(x){ case 10000000000: return 1; default: return 0; } } int main(void){ return (int)f(10000000000); }", 1)]
    [InlineData("int main(void){ int t=0; for(int i=0;i<5;i++){ switch(i){ case 2: continue; case 4: break; default: t+=i; } t+=100; } return t; }", 404)]
    [InlineData("enum C{R,G=5,B}; int f(enum C c){ switch(c){ case R: return 1; case G: return 5; case B: return 6; } return -1; } int main(void){ return f(B); }", 6)]
    // goto / labels — lowered via the CFG dispatch loop (correct for forward, backward,
    // out-of-nested-loop, skip-init, and irreducible control flow).
    [InlineData("int f(int x){ int r=0; if(x<0) goto fail; r=x*2; return r; fail: return -1; } int main(void){ return f(-3)+f(5); }", 9)]
    [InlineData("int main(void){ int s=0,i=1; loop: if(i>5) goto done; s+=i; i++; goto loop; done: return s; }", 15)]
    [InlineData("int main(void){ int found=-1; for(int i=0;i<5;i++){ for(int j=0;j<5;j++){ if(i*j==6){ found=i*10+j; goto out; } } } out: return found; }", 23)]
    [InlineData("int main(void){ int a=5; if(1) goto end; a=99; end: return a; }", 5)]
    [InlineData("int irr(int s){ int n=0; if(s==1) goto L2; L1: n+=1; if(n>20) goto d; L2: n+=10; if(n>20) goto d; goto L1; d: return n; } int main(void){ return irr(0)*100+irr(1); }", 2221)] // irreducible: irr(0)=22, irr(1)=21
    // goto coexisting with a shadow-stack frame (array + &x) and with recursion (frame
    // saved/restored per call): the dispatch loop runs after the one-time frame setup.
    [InlineData("int f(int n){ int arr[4]; int x=7; int* px=&x; int i=0; loop: if(i>=n) goto done; arr[i]=i*i; i++; goto loop; done: { int s=*px; for(int k=0;k<n;k++) s+=arr[k]; return s; } } int main(void){ return f(4); }", 21)]
    [InlineData("int sumrec(int n){ if(n<=0) goto base; return n+sumrec(n-1); base: return 0; } int main(void){ return sumrec(5); }", 15)]
    // decimal-float literal spellings without two digit runs around the point: a
    // point-free exponent (`1e10`/`1e-7` — the documented lexer gap), a leading dot
    // (`.5`), a trailing dot (`1.`), and a dot-before-exponent (`2.e3`).
    [InlineData("int main(void){ double a=1e10; return (int)(a/1e9); }", 10)]
    [InlineData("int main(void){ double t=1e-7; return (int)(t*1e8); }", 10)]
    [InlineData("int main(void){ return (int)(.5 * 10); }", 5)]
    [InlineData("int main(void){ return (int)(1. + 2.); }", 3)]
    [InlineData("int main(void){ return (int)(2.e3); }", 2000)]
    [InlineData("int main(void){ float f=1e3f; return (int)f; }", 1000)]
    // structs and unions in linear memory: members at the layout model's offsets, a brace
    // initializer zeroing then storing, assignment copying bytes, `->` through a pointer.
    [InlineData("#include <stddef.h>\nstruct P { int x; long y; char *n; };\nint main(void){ struct P p; p.x = 3; p.y = 4; struct P q = { 1, 2, \"hi\" }; p = q; return (int)(sizeof(struct P) + offsetof(struct P, n) + p.x + p.y + p.n[1]); }", 148)]
    [InlineData("struct N { int v; struct N *next; };\nint main(void){ struct N c = {3, 0}, b = {2, &c}, a = {1, &b}; int s = 0; for (struct N *p = &a; p; p = p->next) s += p->v * 10; return s; }", 60)]
    [InlineData("union U { int i; unsigned char b[4]; };\nint main(void){ union U u; u.i = 0x01020304; return u.b[0] + u.b[3]; }", 5)]
    [InlineData("struct In { int a, b; }; struct Out { struct In in[2]; int t; };\nint main(void){ struct Out o = { { {1, 2}, {3, 4} }, 5 }; struct Out *p = &o; return p->in[1].b * 10 + o.in[0].a + p->t; }", 46)]
    // file-scope objects and block-scope statics at fixed addresses, initialized by the
    // module's start function; 40 KB of data moves the stack past it.
    [InlineData("struct P { int x; const char *name; };\nint counter = 5;\nstatic struct P origin = { 7, \"origin\" };\nconst char *greeting = \"hey\";\nint table[4] = { 1, 2, 3, 4 };\nstatic char big[40000];\nint next(void){ static int n = 100; return n++; }\nint main(void){ counter++; next(); big[39999] = 2; return counter + next() + origin.x + origin.name[1] + greeting[2] + table[3] + big[39999]; }", 355)]
    [InlineData("int next(void){ static int n = 10; return n++; } int main(void){ next(); next(); return next(); }", 12)]
    // a value waiting in a scratch survives a nested use (log_[li++] = v), comma operators,
    // a switch inside a goto function (CFG dispatch, with fall-through).
    [InlineData("int log_[4]; int li;\nvoid record(int v){ log_[li++] = v; }\nint main(void){ record(7); record(9); return log_[0] * 10 + log_[1] + li * 100; }", 279)]
    [InlineData("int f(int x){ int r = 0; if (x < 0) goto neg; switch (x) { case 1: r = 10; case 2: r += 2; break; default: r = 99; } return r; neg: return -1; }\nint main(void){ return f(1) * 1000 + f(2) * 100 + f(3) + f(-5); }", 12298)]
    [InlineData("int g(int *p){ return ++*p; }\nint main(void){ int a = 1, b; (void)g(&a); b = (g(&a), g(&a), a * 10); for (int i = 0, j = 3; i < j; i++, j--) b += i; return b; }", 41)]
    // function pointers: a table index, called through call_indirect (a local, a global,
    // a struct member, a table of them).
    [InlineData("int add(int a, int b){ return a + b; }\nint (*op)(int, int) = add;\nint main(void){ int (*f)(int, int) = &add; return f(2, 3) + op(4, 5); }", 14)]
    [InlineData("struct V { int (*f)(int); };\nint twice(int x){ return 2 * x; }\nint neg(int x){ return -x; }\nstatic int (*ops[2])(int) = { twice, neg };\nint main(void){ struct V v = { twice }; return v.f(21) + ops[1](2) + ops[0](3); }", 46)]
    // assert by the condition's own type, a void conditional, the instruction math functions.
    [InlineData("#include <assert.h>\n#include <math.h>\nint n;\nvoid bump(int *p){ *p += 1; }\nint main(void){ double d = 0.5; assert(d); n == 0 ? bump(&n) : (void)0; return n * 100 + (int)sqrt(16.0) * 10 + (int)floor(2.7); }", 142)]
    // structs by value (a copy's address in, a caller's slot out), compound literals, div.
    [InlineData("struct P { int x, y; };\nstruct P make(int a){ struct P p = {a, a * 2}; return p; }\nint sum(struct P p){ p.x += 100; return p.x + p.y; }\nint main(void){ struct P q = make(3); int s = sum(q); return s * 10 + q.x; }", 1093)]
    [InlineData("struct P { int x, y; };\nint sum(struct P p){ return p.x + p.y; }\nint main(void){ int *a = (int[]){4, 5, 6}; return sum((struct P){1, 2}) * 100 + a[2] + (&(struct P){7, 8})->y; }", 314)]
    [InlineData("#include <stdlib.h>\nint main(void){ div_t d = div(17, 5); ldiv_t l = ldiv(-17L, 5L); return d.quot * 100 + d.rem * 10 + (int)l.rem; }", 318)]
    // a promoted malloc (a stack struct, a stack buffer), wide literals, a parenthesized
    // assignment target, errno.
    [InlineData("#include <stdlib.h>\nstruct P { int x, y; };\nint main(void){ struct P *p = malloc(sizeof(struct P)); p->x = 3; p->y = 4; int r = p->x * 10 + p->y; free(p); return r; }", 34)]
    [InlineData("#include <stdlib.h>\n#include <string.h>\nint main(void){ char *b = malloc(16); strcpy(b, \"hello\"); int n = (int)strlen(b); free(b); return n; }", 5)]
    // (the non-ASCII forms run in the char16-literals and c11-char32 fixtures)
    [InlineData("#include <uchar.h>\nint main(void){ const char16_t *s = u\"hi\"; const char32_t *t = U\"ok\"; return (s[1] == 'i') * 100 + (t[1] == 'k') * 10 + (t[2] == 0); }", 111)]
    [InlineData("#include <errno.h>\nint x;\nint main(void){ (x) = 5; errno = 0; errno = 34; return (x) * 100 + errno; }", 534)]
    // Pointer compound assignment steps by elements: a local, through memory, and -=.
    [InlineData("int main(void){ int a[4] = {1,2,3,4}; int *p = a; p += 2; int *q = &a[3]; q -= 1; int *arr[1]; arr[0] = a; arr[0] += 3;"
        + " long *l = (long *)0 + 0; l += 1; return *p * 100 + *q * 10 + *arr[0] + (int)(long)l; }", 342)]
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
    // switch fall-through, observed by what each iteration prints
    [InlineData("int main(void){ for(int i=0;i<4;i++){ switch(i){ case 0: putchar('a'); case 1: putchar('b'); break; default: putchar('x'); } } return 0; }", "abbxx")]
    // goto loop, observed by what it prints
    [InlineData("int main(void){ int i=0; again: putchar('0'+i); i++; if(i<3) goto again; putchar('\\n'); return 0; }", "012\n")]
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
    // '#' alternate form on hex / octal: a 0x/0X prefix (suppressed for a zero value),
    // a forced leading 0 for octal; the prefix sits before the zero-padding.
    [InlineData("int main(void){ printf(\"%#x %#X %#o\", 255, 255, 64); return 0; }", "0xff 0XFF 0100")]
    [InlineData("int main(void){ printf(\"%#x %#o\", 0, 0); return 0; }", "0 0")]
    [InlineData("int main(void){ printf(\"[%#08x][%#8x][%-#8x]\", 255, 255, 255); return 0; }", "[0x0000ff][    0xff][0xff    ]")]
    [InlineData("int main(void){ printf(\"[%#.4x][%#o]\", 255, 8); return 0; }", "[0x00ff][010]")]
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
    // point-free / dangling-dot literal spellings flow through to the formatter
    [InlineData("int main(void){ printf(\"%.1f %.8f %.1f %.1f\", 1e10, 1e-7, .5, 1.); return 0; }", "10000000000.0 0.00000010 0.5 1.0")]
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
    // uppercase float conversions — same digits as %e/%f/%g, an uppercase 'E'
    // exponent and uppercase INF/NAN.
    [InlineData("int main(void){ printf(\"%E\", 1.5); return 0; }", "1.500000E+00")]
    [InlineData("int main(void){ printf(\"%.2E\", 31415.9); return 0; }", "3.14E+04")]
    [InlineData("int main(void){ printf(\"%G\", 1234567.0); return 0; }", "1.23457E+06")]
    [InlineData("int main(void){ printf(\"%G\", 100.0); return 0; }", "100")]
    [InlineData("int main(void){ printf(\"%F\", 3.5); return 0; }", "3.500000")]
    [InlineData("int main(void){ printf(\"%e|%E\", 1.0, 1.0); return 0; }", "1.000000e+00|1.000000E+00")]
    [InlineData("int main(void){ double z=0.0; printf(\"%e %f %g\", 1.0/z, 1.0/z, 1.0/z); return 0; }", "inf inf inf")]
    [InlineData("int main(void){ double z=0.0; printf(\"%E %F %G\", 1.0/z, 1.0/z, 1.0/z); return 0; }", "INF INF INF")]
    // %a / %A — exact hexadecimal float (a direct IEEE-754 bit dump). Default precision
    // emits every significant nibble (round-trippable); an explicit one rounds
    // half-to-even, with the carry able to bump the leading digit / exponent.
    [InlineData("int main(void){ printf(\"%a %a %a\", 1.0, 2.0, 0.5); return 0; }", "0x1p+0 0x1p+1 0x1p-1")]
    [InlineData("int main(void){ printf(\"%a %a\", 3.0, -2.5); return 0; }", "0x1.8p+1 -0x1.4p+1")]
    [InlineData("int main(void){ printf(\"%a\", 0.1); return 0; }", "0x1.999999999999ap-4")]
    [InlineData("int main(void){ printf(\"%a %a\", 0.0, -0.0); return 0; }", "0x0p+0 -0x0p+0")]
    [InlineData("int main(void){ printf(\"%A %A\", 1.0, 0.1); return 0; }", "0X1P+0 0X1.999999999999AP-4")]
    [InlineData("int main(void){ printf(\"%.2a\", 3.14159); return 0; }", "0x1.92p+1")]
    [InlineData("int main(void){ printf(\"%.0a %.0a\", 1.5, 2.5); return 0; }", "0x2p+0 0x1p+1")]   // round half to even
    [InlineData("int main(void){ printf(\"%.1a\", 1.984375); return 0; }", "0x2.0p+0")]            // carry bumps the leading digit
    [InlineData("int main(void){ printf(\"%#.0a\", 1.0); return 0; }", "0x1.p+0")]                 // '#' forces the point
    [InlineData("int main(void){ printf(\"[%12a][%-12a]\", 1.0, 1.0); return 0; }", "[      0x1p+0][0x1p+0      ]")]
    [InlineData("int main(void){ printf(\"%a\", 5e-324); return 0; }", "0x0.0000000000001p-1022")] // smallest subnormal
    // %p — glibc-shaped pointer (deterministic via integer-cast pointers): "0x"+lowercase
    // hex for a nonzero address, "(nil)" for null; width / left-justify apply.
    [InlineData("int main(void){ printf(\"%p %p %p\", (void*)255, (void*)0, (void*)0x1234); return 0; }", "0xff (nil) 0x1234")]
    [InlineData("int main(void){ printf(\"[%10p][%-10p]\", (void*)255, (void*)255); return 0; }", "[      0xff][0xff      ]")]
    // the wat libc, compiled from C with the program (string, ctype, stdlib), the bulk-memory
    // intrinsics, and exit ending the run with what was printed so far.
    [InlineData("#include <stdio.h>\n#include <string.h>\n#include <stdlib.h>\n#include <ctype.h>\n"
        + "static int cmp(const void *a, const void *b){ return *(const int *)a - *(const int *)b; }\n"
        + "int main(void){ char buf[32]; strcpy(buf, \"Hello\"); strcat(buf, \", wasm\"); int a[6] = {5, 3, 9, 1, 7, 3}; qsort(a, 6, sizeof(int), cmp);"
        + " printf(\"%s %d %d %d %d|\", buf, (int)strlen(buf), strcmp(\"abc\", \"abd\") < 0, atoi(\"  -42x\"), (int)strtol(\"0x1F\", NULL, 0));"
        + " for (int i = 0; i < 6; i++) printf(\"%d \", a[i]);"
        + " printf(\"%c%c %s|\", toupper('q'), isdigit('7') ? 'Y' : 'N', strstr(buf, \"was\")); memset(buf, 'z', 3); printf(\"%.5s\", buf); exit(3); }",
        "Hello, wasm 11 1 -42 31|1 3 3 5 7 9 QY wasm|zzzlo")]
    [InlineData("#include <stdio.h>\n#include <string.h>\n#include <inttypes.h>\n"
        + "int main(void){ char s[] = \"  a,bb,,ccc \"; for (char *t = strtok(s, \" ,\"); t; t = strtok(NULL, \" ,\")) printf(\"[%s]\", t);"
        + " printf(\" %d\", (int)imaxabs(-7)); return 0; }",
        "[a][bb][ccc] 7")]
    // musl's libm, compiled with the program. The expected text is the same musl sources built
    // natively by gcc (bit-identical); C's fmin/fmax skip a NaN, and isnan/isinf/isfinite are
    // functions of the program's <math.h>.
    [InlineData("#include <stdio.h>\n#include <math.h>\n"
        + "int main(void){ double z = 0.0; int e; double ip;"
        + " printf(\"%.17g %.17g %.17g %.17g\\n\", sin(0.5), cos(0.5), tan(1.0), sin(1e6));"
        + " printf(\"%.17g %.17g %.17g %.17g\\n\", exp(1.0), log(10.0), log10(2.0), log2(10.0));"
        + " printf(\"%.17g %.17g %.17g %.17g\\n\", pow(2.0, 0.5), cbrt(2.0), atan2(1.0, 1.0) * 4, hypot(3.0, 4.0));"
        + " printf(\"%.17g %.17g %.17g\\n\", asin(0.5), acos(0.5), atan(1.0));"
        + " printf(\"%.17g %.17g %.17g\\n\", sinh(1.0), cosh(1.0), tanh(0.5));"
        + " printf(\"%g %g %g %g %g\\n\", round(2.5), round(-2.5), fmod(7.5, 2.0), fmin(z / z, 2.0), fmax(3.0, z / z));"
        + " double m = frexp(48.0, &e); double frac = modf(3.25, &ip);"
        + " printf(\"%g %d %g %g %g\\n\", m, e, ldexp(0.75, 6), frac, ip);"
        + " printf(\"%d %d %d %d\\n\", isnan(z / z), isinf(1.0 / z), isfinite(1.0), isnan(1.0));"
        + " printf(\"%.9g %.9g %.9g %.9g %.9g\\n\", sinf(1.0f), cosf(1.0f), expf(1.0f), logf(10.0f), powf(2.0f, 0.5f));"
        + " printf(\"%.9g %.9g %g\", cbrtf(2.0f), atan2f(1.0f, 2.0f), roundf(-0.5f)); return 0; }",
        "0.47942553860420301 0.87758256189037276 1.5574077246549023 -0.34999350217129294\n"
        + "2.7182818284590451 2.3025850929940459 0.3010299956639812 3.3219280948873622\n"
        + "1.4142135623730951 1.2599210498948732 3.1415926535897931 5\n"
        + "0.52359877559829893 1.0471975511965979 0.78539816339744828\n"
        + "1.1752011936438014 1.5430806348152437 0.46211715726000974\n"
        + "3 -3 1.5 2 3\n"
        + "0.75 6 48 0.25 3\n"
        + "1 1 1 0\n"
        + "0.841470957 0.540302277 2.71828175 2.30258512 1.41421354\n"
        + "1.25992107 0.463647604 -1")]
    // <stdatomic.h> on one thread.
    [InlineData("#include <stdio.h>\n#include <stdatomic.h>\n"
        + "int main(void){ atomic_long n = 5000000000; long old = atomic_fetch_add(&n, 7); long e = 1; int ok = atomic_compare_exchange_strong(&n, &e, 0);"
        + " atomic_flag f = ATOMIC_FLAG_INIT; int first = atomic_flag_test_and_set(&f), second = atomic_flag_test_and_set(&f);"
        + " long now = atomic_load(&n); long swapped = atomic_exchange(&n, 3);"
        + " printf(\"%ld %ld %d %ld %d %d %ld %ld\", old, now, ok, e, first, second, swapped, atomic_load(&n)); return 0; }",
        "5000000000 5000000007 0 5000000007 0 1 5000000007 3")]
    // Variadic functions: each argument promoted into an 8-byte slot of the caller's buffer;
    // va_copy, a va_list handed on, a call through a pointer, one nested in another's arguments.
    [InlineData("#include <stdio.h>\n#include <stdarg.h>\n"
        + "static long mix(int n, ...){ va_list ap, again; va_start(ap, n); va_copy(again, ap); long t = 0;"
        + " for (int i = 0; i < n; i++) t += va_arg(ap, int); t *= 1000; t += va_arg(again, int); va_end(again); va_end(ap); return t; }\n"
        + "static double avg(const char *tag, ...){ va_list ap; va_start(ap, tag); char c = (char)va_arg(ap, int); float f = (float)va_arg(ap, double);"
        + " long l = va_arg(ap, long); unsigned u = va_arg(ap, unsigned); const char *s = va_arg(ap, const char *); va_end(ap);"
        + " printf(\"%s:%c %.2f %ld %u %s|\", tag, c, f, l, u, s); return (f + (double)l) / 2; }\n"
        + "static int count(int n, ...){ return n; }\n"
        + "int main(void){ char c = 'q'; float f = 2.5f; long (*fp)(int, ...) = mix;"
        + " printf(\"%ld|\", fp(3, 1, 2, 3)); printf(\"%.2f|\", avg(\"t\", c, f, 10000000000L, 4000000000u, \"ok\"));"
        + " printf(\"%d\", count(count(7, 1.0, 'x'), mix(1, 5))); return 0; }",
        "6001|t:q 2.50 10000000000 4000000000 ok|5000000001.25|7")]
    // setjmp/longjmp on wasm exception handling: a longjmp out of a deep recursion whose
    // frames hold arrays leaves the stack pointer where the setjmp's frame had it (the calls
    // after it still get sound frames), longjmp(env, 0) resumes with 1, and a jmp_buf local.
    [InlineData("#include <stdio.h>\n#include <setjmp.h>\n"
        + "static jmp_buf top;\n"
        + "static int dive(int n){ int pad[16]; for (int i = 0; i < 16; i++) pad[i] = n + i; if (n == 0) longjmp(top, 0); return dive(n - 1) + pad[15]; }\n"
        + "static int sum(int n){ int a[8]; int s = 0; for (int i = 0; i < 8; i++) a[i] = i * n; for (int i = 0; i < 8; i++) s += a[i]; return s; }\n"
        + "int main(void){ int r = setjmp(top); if (r == 0) { dive(20); printf(\"unreachable\"); }"
        + " printf(\"r=%d sum=%d|\", r, sum(3)); jmp_buf local; volatile int tries = 0;"
        + " if (setjmp(local) == 0) { tries++; longjmp(local, 5); } else { tries += 10; } printf(\"tries=%d\", tries); return 0; }",
        "r=1 sum=84|tries=11")]
    // Bit-fields: a signed field sign-extends, a store truncates to the width (and the
    // assignment's value is what was stored), compound and ++ read-modify-write the unit,
    // 64-bit units, a global's initializer, and the offset of a member after a bit-field
    // run (== gcc's output).
    [InlineData("#include <stdio.h>\n#include <stddef.h>\n"
        + "struct S { int a : 3; unsigned b : 5; int c; unsigned long long big : 40; long long sbig : 20; };\n"
        + "static struct S g = { -2, 17, 99, 0xABCDE12345ULL, -5 };\n"
        + "int main(void){ struct S s = { 3, 31, 7, 1, 0 }; s.a = 5; s.b += 3; int post = s.a++; int pre = ++s.b;"
        + " s.sbig = -300000; s.big = (1ULL << 40) + 7;"
        + " printf(\"%d %u %d %d %d %llu %lld|\", s.a, s.b, s.c, post, pre, s.big, (long long)s.sbig);"
        + " printf(\"%d %u %d %llx %lld|\", g.a, g.b, g.c, g.big, (long long)g.sbig);"
        + " printf(\"%d %d\", (int)offsetof(struct S, c), (int)sizeof(struct S)); return 0; }",
        "-2 3 7 -3 3 7 -300000|-2 17 99 abcde12345 -5|4 16")]
    // The calendar in C: gmtime of a leap day and of the second before the Epoch, strftime's
    // conversions (and 0 when the result does not fit), asctime (== gcc with TZ=UTC).
    [InlineData("#include <stdio.h>\n#include <time.h>\n"
        + "int main(void){ time_t leap = 951782400; time_t before = -1; char b[160]; struct tm g = *gmtime(&leap);"
        + " strftime(b, sizeof b, \"%F %T %j %u %w %a %A %b %B %e %I%p %y %C %D %R %%\", &g); printf(\"%s|\", b);"
        + " struct tm *n = gmtime(&before);"
        + " printf(\"%d-%d-%d %d:%d:%d wday=%d yday=%d|\", n->tm_year + 1900, n->tm_mon + 1, n->tm_mday, n->tm_hour, n->tm_min, n->tm_sec, n->tm_wday, n->tm_yday);"
        + " printf(\"%d|\", (int)strftime(b, 5, \"%Y-%m\", &g)); printf(\"%s\", asctime(&g)); printf(\"%s\", asctime(n)); return 0; }",
        "2000-02-29 00:00:00 060 2 2 Tue Tuesday Feb February 29 12AM 00 20 02/29/00 00:00 %|1969-12-31 23:59:59 wday=3 yday=364|0|"
        + "Tue Feb 29 00:00:00 2000\nWed Dec 31 23:59:59 1969\n")]
    // strtod and strtof: musl's __floatscan through a string pseudo-FILE. Correct rounding at a
    // float halfway point, the largest subnormal's neighbour, hex floats, an overflow, endptr.
    [InlineData("#include <stdio.h>\n#include <stdlib.h>\n"
        + "static unsigned long long b(double d){ union { double d; unsigned long long i; } u; u.d = d; return u.i; }\n"
        + "static unsigned bf(float f){ union { float f; unsigned i; } u; u.f = f; return u.i; }\n"
        + "int main(void){ char *end; const char *s = \"  -12.5e+2xyz\"; double d = strtod(s, &end);"
        + " printf(\"%g %d|\", d, (int)(end - s));"
        + " printf(\"%08x %016llx|\", bf(strtof(\"1.0000001788139343261718749999\", 0)), b(strtod(\"2.2250738585072011e-308\", 0)));"
        + " printf(\"%g %g %g %g|\", strtod(\"0x1.8p3\", 0), strtod(\"1e400\", 0), strtod(\"-nan\", 0) != strtod(\"-nan\", 0) ? 1.0 : 0.0, atof(\"4.9e-324\") > 0 ? 1.0 : 0.0);"
        + " printf(\"%.17g\", strtod(\"0.1\", 0)); return 0; }",
        "-1250 10|3f800001 000fffffffffffff|12 inf 1 1|0.10000000000000001")]
    // double _Complex as two doubles: division both ways (Smith's algorithm, as the C# build's
    // System.Numerics.Complex), real operands, negation, ==, by-value passing and returning, a
    // real converted where a complex is wanted, a complex member, and the casts to real.
    [InlineData("#include <complex.h>\n#include <stdio.h>\n"
        + "struct holder { int tag; double _Complex z; };\n"
        + "static double _Complex twice(double _Complex z) { return z * 2.0; }\n"
        + "static double _Complex lift(double r) { return r; }\n"
        + "static double re_of(double _Complex z) { return creal(z); }\n"
        + "int main(void){ double _Complex a = 1.0 + 2.0 * I; double _Complex b = 3.0 - 4.0 * I; double _Complex q = a / b;"
        + " double _Complex q2 = b / (5.0 + 1.0 * I); double _Complex r = 10.0 / a; double _Complex s = a / 2.0; double _Complex n = -a;"
        + " double _Complex t; t = 7.0; struct holder h = { 1, 0 }; h.z = twice(a); double x = (double)b; _Bool nz = (_Bool)(0.0 * I), nz2 = (_Bool)a;"
        + " printf(\"%.6f %.6f|%.6f %.6f|%.6f %.6f|%.6f %.6f\\n\", creal(q), cimag(q), creal(q2), cimag(q2), creal(r), cimag(r), creal(s), cimag(s));"
        + " printf(\"%.1f %.1f|%.1f %.1f|%.1f %.1f|%.1f|%d %d|%d %d\\n\", creal(n), cimag(n), creal(t), cimag(t), creal(h.z), cimag(h.z), x, nz, nz2, a == a, a != b);"
        + " printf(\"%.1f %.1f %.1f %d\", re_of(4.5), creal(lift(2.5)), cimag(lift(2.5)), (int)sizeof(double _Complex)); return 0; }",
        "-0.200000 0.400000|0.423077 -0.884615|2.000000 -4.000000|0.500000 1.000000\n"
        + "-1.0 -2.0|7.0 0.0|2.0 4.0|3.0|0 1|1 1\n"
        + "4.5 2.5 0.0 16")]
    // Real threads (wasi-threads over node's worker_threads): an atomic counter four threads bump,
    // a thread-local each thread starts at its initial value, malloc from every thread at once,
    // and sprintf's float formatting in each thread's own scratch at the same time.
    [InlineData("#include <stdio.h>\n#include <stdlib.h>\n#include <stdatomic.h>\n#include <threads.h>\n"
        + "static atomic_int hits; static _Thread_local int mine = 100; static char out[4][64]; static long sums[4];\n"
        + "static int work(void *arg){ int id = (int)(long)arg; for (int i = 0; i < 20000; i++) atomic_fetch_add(&hits, 1);"
        + " for (int i = 0; i < 100; i++) mine += id; long s = 0;"
        + " for (int i = 0; i < 200; i++) { int *p = malloc(sizeof(int) * 4); p[0] = i; s += p[0]; } sums[id] = s;"
        + " for (int i = 0; i < 200; i++) sprintf(out[id], \"%d:%.3f:%d\", id, id * 1.5 + 0.125, mine); return id * 10; }\n"
        + "int main(void){ thrd_t t[4]; int r[4]; for (long i = 0; i < 4; i++) thrd_create(&t[i], work, (void *)i);"
        + " for (int i = 0; i < 4; i++) thrd_join(t[i], &r[i]); printf(\"%d %d|\", atomic_load(&hits), mine);"
        + " for (int i = 0; i < 4; i++) printf(\"%s %ld %d|\", out[i], sums[i], r[i]); return 0; }",
        "80000 100|0:0.125:100 19900 0|1:1.625:200 19900 10|2:3.125:300 19900 20|3:4.625:400 19900 30|")]
    // A format read at run time (the libc's printf over vsnprintf) prints what the same format
    // as a literal (expanded inline) does, flag for flag, and glibc's text: widths, precisions,
    // '#', length modifiers, %a, %p, a '*' width and precision, %n, and snprintf's truncation.
    [InlineData("#include <stdio.h>\n"
        + "int main(void){ const char *f[] = { \"%5d|%-5d|%05d|%+d|% d|\", \"%x %X %#x %#o %.3d|\", \"%.3f %e %g %a %G|\", \"%10.4s|%-3c|%%|%p|\", \"%hhd %hd %ld %lld %zu|\" };\n"
        + " printf(f[0], 42, 42, 42, 42, 42); printf(\"%5d|%-5d|%05d|%+d|% d|\", 42, 42, 42, 42, 42);\n"
        + " printf(f[1], 255u, 255u, 255u, 8u, 7); printf(\"%x %X %#x %#o %.3d|\", 255u, 255u, 255u, 8u, 7);\n"
        + " printf(f[2], 3.14159, 31415.9, 0.0001234, 1.5, 1e-10); printf(\"%.3f %e %g %a %G|\", 3.14159, 31415.9, 0.0001234, 1.5, 1e-10);\n"
        + " printf(f[3], \"abcdefg\", 'z', (void *)0); printf(\"%10.4s|%-3c|%%|%p|\", \"abcdefg\", 'z', (void *)0);\n"
        + " printf(f[4], (signed char)-3, (short)-300, -5L, -6LL, (size_t)7); printf(\"%hhd %hd %ld %lld %zu|\", (signed char)-3, (short)-300, -5L, -6LL, (size_t)7);\n"
        + " char b[8]; int n = snprintf(b, sizeof b, f[0], 1, 2, 3, 4, 5); printf(\"[%s] %d|\", b, n);\n"
        + " printf(\"[%*.*f]%n\", 8, 2, 2.71828, &n); printf(\"%d\", n); return 0; }",
        "   42|42   |00042|+42| 42|   42|42   |00042|+42| 42|ff FF 0xff 010 007|ff FF 0xff 010 007|3.142 3.141590e+04 0.0001234 0x1.8p+0 1E-10|3.142 3.141590e+04 0.0001234 0x1.8p+0 1E-10|      abcd|z  |%|(nil)|      abcd|z  |%|(nil)|-3 -300 -5 -6 7|-3 -300 -5 -6 7|[    1|2] 24|[    2.72]10")]
    // The stdio FILE layer (musl's, over WASI): a tmpfile in memory written with fwrite and
    // fprintf, read back with fgetc, ungetc, fgets and fread, positioned with fseek, ftell and
    // rewind (a write past the end leaves zeros), EOF and clearerr; printf's count, its
    // argument's own output first; fputs, fputc and putc to stdout.
    [InlineData("#include <stdio.h>\n"
        + "#include <string.h>\n"
        + "static int g(void){ return printf(\"<g>\"); }\n"
        + "int main(void){ FILE *t = tmpfile(); char s[32]; int c;\n"
        + " fwrite(\"hello\\nworld\\n\", 1, 12, t); fprintf(t, \"%d-%s\\n\", 7, \"x\"); long end = ftell(t);\n"
        + " fseek(t, 6, SEEK_SET); c = fgetc(t); ungetc('W', t); fgets(s, sizeof s, t); printf(\"%ld %c [%s] \", end, c, strtok(s, \"\\n\"));\n"
        + " rewind(t); size_t got = fread(s, 1, 5, t); s[got] = 0; printf(\"%zu [%s] %ld \", got, s, ftell(t));\n"
        + " fseek(t, 3, SEEK_END); fputc('!', t); printf(\"%ld \", ftell(t)); fseek(t, -4, SEEK_END);\n"
        + " int a = fgetc(t), b = fgetc(t), d = fgetc(t), e = fgetc(t), z = fgetc(t), eof = feof(t); clearerr(t);\n"
        + " printf(\"%d %d %d %d %d %d %d \", a, b, d, e, z, eof, feof(t));\n"
        + " fclose(t); int n = printf(\"[%d]\", g()); printf(\" %d \", n); fputs(\"put\", stdout); fputc('c', stdout); putc('\\n', stdout); return 0; }",
        "16 w [World] 5 [hello] 5 20 0 0 0 33 -1 1 0 <g>[3] 3 putc\n")]
    // The scanf family (musl's vfscanf): %d %i %s %lf %x, scansets, %n, assignment suppression,
    // a failed match and an empty input, %f/%c/%hhd/%hd, octal and prefixed %i, inf/nan/hex
    // floats; and the strtol family clamping out-of-range values with ERANGE. glibc's text.
    [InlineData("#include <stdio.h>\n"
        + "#include <stdlib.h>\n"
        + "#include <errno.h>\n"
        + "#include <limits.h>\n"
        + "int main(void) {\n"
        + "    int a, b, n; char w[16], rest[32]; double d; float f; unsigned u; long l; char c; short h; signed char hh;\n"
        + "    int r = sscanf(\"  42 -17 hello 3.25e2 0x1f\", \"%d %i %15s %lf %x\", &a, &b, w, &d, &u);\n"
        + "    printf(\"%d|%d %d [%s] %g %u\\n\", r, a, b, w, d, u);\n"
        + "    r = sscanf(\"abc:123;rest of it\", \"%[a-z]:%ld;%[^\\n]%n\", w, &l, rest, &n);\n"
        + "    printf(\"%d|[%s] %ld [%s] %d\\n\", r, w, l, rest, n);\n"
        + "    r = sscanf(\"x\", \"%d\", &a); printf(\"%d|\", r);\n"
        + "    r = sscanf(\"\", \"%d\", &a); printf(\"%d|\", r);\n"
        + "    r = sscanf(\"12 34\", \"%*d %d\", &a); printf(\"%d %d|\", r, a);\n"
        + "    r = sscanf(\"7.5 Z -5 300\", \"%f %c %hhd %hd\", &f, &c, &hh, &h); printf(\"%d %g %c %d %d\\n\", r, f, c, hh, h);\n"
        + "    r = sscanf(\"0755 0x10 010\", \"%o %i %i\", &u, &a, &b); printf(\"%d %u %d %d|\", r, u, a, b);\n"
        + "    r = sscanf(\"  inf nan -0x1p-3\", \"%lf %lf %lf\", &d, &d, &d); printf(\"%d %g\\n\", r, d);\n"
        + "    char *e; errno = 0;\n"
        + "    long big = strtol(\"99999999999999999999\", &e, 10); printf(\"%ld %d %d|\", big == LONG_MAX, errno == ERANGE, *e);\n"
        + "    errno = 0; long neg = strtol(\"-99999999999999999999x\", &e, 10); printf(\"%d %d %c|\", neg == LONG_MIN, errno == ERANGE, *e);\n"
        + "    errno = 0; unsigned long ub = strtoul(\"-1\", &e, 10); printf(\"%d %d|\", ub == ULONG_MAX, errno);\n"
        + "    printf(\"%ld %ld %ld|\", strtol(\"0x\", &e, 16), strtol(\"  +077\", NULL, 0), strtol(\"z\", &e, 36));\n"
        + "    printf(\"%lld %llu\\n\", strtoll(\"-9223372036854775808\", NULL, 10), strtoull(\"18446744073709551615\", NULL, 10));\n"
        + "    return 0;\n"
        + "}",
        "5|42 -17 [hello] 325 31\n3|[abc] 123 [rest of it] 18\n0|-1|1 34|4 7.5 Z -5 300\n3 493 16 8|3 -0.125\n1 1 0|1 1 x|1 0|0 63 35|-9223372036854775808 18446744073709551615\n")]
    // Wide stdio over UTF-8, as ISO C has it: %ls/%lc in a narrow printf (a precision bounds
    // the bytes, whole characters only), swprintf's count and its -1 when the text does not
    // fit, swscanf into a wide and a narrow string, wprintf's count in wide characters,
    // fputws and putwchar. (The expected text is the UTF-8 bytes, one char each.)
    [InlineData("#include <stdio.h>\n"
        + "#include <wchar.h>\n"
        + "int main(void) {\n"
        + "    const wchar_t *ws = L\"h\u00e9llo\";\n"
        + "    printf(\"[%ls] [%5ls] [%-7ls|] [%.2ls] [%lc]\\n\", ws, L\"ab\", L\"cd\", ws, (wint_t)L'\u00e9');\n"
        + "    wchar_t buf[8];\n"
        + "    int n = swprintf(buf, 8, L\"%d-%ls\", 12345, L\"xyz\");\n"
        + "    printf(\"%d %d\\n\", n, (int)wcslen(buf));\n"
        + "    n = swprintf(buf, 8, L\"%d\", 42);\n"
        + "    printf(\"%d %d %d\\n\", n, buf[0], buf[2]);\n"
        + "    wchar_t w1[16]; int a; char narrow[16];\n"
        + "    n = swscanf(L\"  77 \u00e9t\u00e9 abc\", L\"%d %ls %s\", &a, w1, narrow);\n"
        + "    printf(\"%d %d %d %d %d [%s]\\n\", n, a, (int)wcslen(w1), w1[0], w1[1], narrow);\n"
        + "    n = wprintf(L\"w:%ls %d\\n\", L\"\u00e9\", 5);\n"
        + "    printf(\"%d\\n\", n);\n"
        + "    fputws(L\"\u00e9\u20ac!\\n\", stdout);\n"
        + "    putwchar(L'Z'); putwchar(L'\\n');\n"
        + "    return 0;\n"
        + "}",
        "[h\u00c3\u00a9llo] [   ab] [cd     |] [h] [\u00c3\u00a9]\n-1 7\n2 52 0\n3 77 3 233 116 [abc]\nw:\u00c3\u00a9 5\n6\n\u00c3\u00a9\u00e2\u0082\u00ac!\nZ\n")]
    // Compound literals in static initializers have static storage (CPython's parser.c keeps its
    // keyword tables as an array of pointers to array compound literals).
    [InlineData("#include <stdio.h>\n"
        + "typedef struct { const char *s; int n; } KW;\n"
        + "struct P { int x, y; };\n"
        + "static KW *table[] = { (KW[]) {{0, -1}}, (KW[]) {{\"if\", 1}, {\"in\", 2}, {0, -1}} };\n"
        + "static struct P *pp = &(struct P){ 3, 4 };\n"
        + "static int *ints = (int[]){ 7, 8, 9 };\n"
        + "int main(void) {\n"
        + "    for (KW *k = table[1]; k->s; k++) printf(\"%s=%d \", k->s, k->n);\n"
        + "    printf(\"%d %d %d %d %d\\n\", table[0][0].n, pp->x, pp->y, ints[0], ints[2]);\n"
        + "    return 0;\n"
        + "}",
        "if=1 in=2 -1 3 4 7 9\n")]
    // strerror's glibc wording, strtoimax past 32 bits, the one "C" locale, and time() from
    // WASI's clock.
    [InlineData("#include <stdio.h>\n#include <string.h>\n#include <errno.h>\n#include <inttypes.h>\n#include <locale.h>\n#include <time.h>\n"
        + "int main(void){ printf(\"%s|%s|%s|\", strerror(ERANGE), strerror(0), strerror(9999));"
        + " printf(\"%\" PRIdMAX \" %\" PRIuMAX \"|\", strtoimax(\"-9000000000\", NULL, 10), strtoumax(\"0xFFFFFFFFFF\", NULL, 16));"
        + " printf(\"%s %s %d|\", setlocale(LC_ALL, NULL), localeconv()->decimal_point, setlocale(LC_ALL, \"fr_FR\") == NULL);"
        + " printf(\"%.1f %d\", difftime(1000, 400), time(NULL) > 1000000000); return 0; }",
        "Numerical result out of range|Success|Unknown error|-9000000000 1099511627775|C . 1|600.0 1")]
    public void Wat_program_writes_expected_stdout(string source, string expected)
    {
        if (!Requested)
        {
            Assert.Skip($"set {RunEnv}=1 to run the wat execution oracle (needs wabt's wat2wasm + node on PATH).");
        }
        RunWatStdout(source).ShouldBe(expected);
    }

    /// <summary>The node runner the wat oracle and <c>Scripts/wat-probe.sh</c> share
    /// (<c>Scripts/wat-run.js</c>, copied next to the tests): a WASI preview1 shim, wasi-threads
    /// over worker_threads for a threaded module, and the program's fd 1 and 2 straight to node's,
    /// from every thread in order.</summary>
    private static string Runner => Path.Combine(AppContext.BaseDirectory, "wat-run.js");

    /// <summary>The flags every assembly takes: a module may use wasm's exception handling
    /// (setjmp/longjmp) and, threaded, its atomics and shared memory.</summary>
    private static readonly string[] Wat2WasmFeatures = { "--enable-threads", "--enable-exceptions" };

    /// <summary>EmitWat → wat2wasm → node, returning <c>main()</c>'s value (exit's status when it
    /// exits).</summary>
    private static int RunWat(string source)
    {
        var stem = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{Guid.NewGuid():N}");
        string c = stem + ".c", wat = stem + ".wat", wasm = stem + ".wasm";
        File.WriteAllText(c, source);
        try
        {
            File.WriteAllText(wat, Compiler.EmitWat(new[] { c }));
            Exec("wat2wasm", [.. Wat2WasmFeatures, wat, "-o", wasm]);   // validates (parse + typecheck) and assembles
            var output = Exec("node", [Runner, "--result", wasm]);
            return int.Parse(output.Trim(), CultureInfo.InvariantCulture);
        }
        finally
        {
            foreach (var f in new[] { c, wat, wasm }) { try { File.Delete(f); } catch { /* best effort */ } }
        }
    }

    /// <summary>EmitWat → wat2wasm → node, returning what the program wrote to fd 1 (stdout),
    /// one char per byte: what the test asserts (not main's return value).</summary>
    private static string RunWatStdout(string source)
    {
        var c = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{Guid.NewGuid():N}.c");
        File.WriteAllText(c, source);
        try { return RunWatModuleStdout(Compiler.EmitWat(new[] { c }), latin1: true); }
        finally { try { File.Delete(c); } catch { /* best effort */ } }
    }

    /// <summary>wat2wasm → node over an emitted module, returning what it wrote to fd 1: its bytes
    /// decoded as UTF-8 (as a fixture's <c>expected-stdout.txt</c> is read), or, for the inline
    /// programs above, one char per byte (<paramref name="latin1"/>). Its exit status is its own
    /// (main's value, or exit's); a trap fails the test.</summary>
    internal static string RunWatModuleStdout(string watText, bool latin1 = false)
    {
        var stem = Path.Combine(Path.GetTempPath(), $"dotcc-wat-{Guid.NewGuid():N}");
        string wat = stem + ".wat", wasm = stem + ".wasm";
        try
        {
            File.WriteAllText(wat, watText);
            Exec("wat2wasm", [.. Wat2WasmFeatures, wat, "-o", wasm]);
            return Exec("node", [Runner, wasm], latin1 ? System.Text.Encoding.Latin1 : System.Text.Encoding.UTF8, anyStatus: true);
        }
        finally
        {
            foreach (var f in new[] { wat, wasm }) { try { File.Delete(f); } catch { /* best effort */ } }
        }
    }

    /// <summary>Run a tool and return its stdout, decoded as <paramref name="encoding"/> (UTF-8 by
    /// default). A missing tool (<see cref="System.ComponentModel.Win32Exception"/>) skips the test,
    /// like the other oracles when their compiler is absent. A non-zero exit fails it, unless
    /// <paramref name="anyStatus"/> (a program's own status), when only the runner's own failures
    /// do: a trap (134) or an import it cannot resolve (127). A run past two minutes is killed and
    /// fails it, so a thread that never finishes cannot hang the suite.</summary>
    private static string Exec(string file, string[] args, System.Text.Encoding? encoding = null, bool anyStatus = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = encoding ?? System.Text.Encoding.UTF8,
        };
        foreach (var a in args) { psi.ArgumentList.Add(a); }

        Process? started;
        try
        {
            started = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip($"'{file}' not found on PATH — install wabt (wat2wasm) and node to run the wat oracle.");
            throw; // unreachable: Assert.Skip throws
        }
        if (started is not { } proc) { throw new InvalidOperationException($"{file} did not start"); }

        using (proc)
        {
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                proc.Kill(entireProcessTree: true);
                throw new TimeoutException($"{file} ran past two minutes");
            }
            var failed = anyStatus ? proc.ExitCode is 134 or 127 : proc.ExitCode != 0;
            if (failed)
            {
                throw new InvalidOperationException($"{file} exited {proc.ExitCode}: {stderr.Result}");
            }
            return stdout.Result;
        }
    }
}
