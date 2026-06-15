// dotcc Zig front-end — vertical-slice demo.
//
// A real Zig program compiled through dotcc: parsed by the generated `DotCC.Zig`
// LALR(1) grammar, lowered to the neutral IR by ZigLowering, then emitted as C# and
// run — exactly the path a `.c` file takes, but behind the second `IFrontend`.
//
//   dotnet run --project DotCC -c Release -- examples/zig-hello/main.zig --emit=file -o out.cs
//   dotnet run out.cs ; echo $?      # -> 42
//
// No I/O yet (that waits on `@cImport` of C stdio), so the result is observed as the
// process exit code. Slice surface: `fn`, typed `const`, `return`, arithmetic.
pub fn main() u8 {
    const x: u8 = 40;
    return x + 2;
}
