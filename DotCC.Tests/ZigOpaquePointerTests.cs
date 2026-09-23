#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for pointers to a container that could not be lowered (road-to-zig-std G3), in the shape of
/// <c>std.Io.Writer</c>'s <c>VTable.sendFile</c>: a fn-pointer field taking <c>*File.Reader</c>, where the
/// file-as-struct <c>File</c> sits on the platform floor (here a <c>comptime_float</c> field stands in for
/// <c>handle: std.posix.fd_t</c>). The pointer lowers as an opaque <c>void*</c>, the nested
/// <c>File.Reader</c> that embeds <c>File</c> by value is withdrawn rather than emitted with a dangling
/// field type, and a use that needs the layout still fails loudly. Also pins a call through a fn-pointer
/// FIELD (<c>t.send(…)</c>, the Writer's <c>w.vtable.drain(…)</c> dispatch).
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigOpaquePointerTests
{
    private static string Emit(string main)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "File.zig"), """
                const File = @This();
                handle: comptime_float,
                pub const Reader = struct {
                    file: File,
                };
                """);
            File.WriteAllText(Path.Combine(dir, "Writer.zig"), """
                const File = @import("File.zig");
                pub const VTable = struct {
                    send: *const fn (r: *File.Reader, n: u8) u8,
                };
                """);
            var path = Path.Combine(dir, "main.zig");
            File.WriteAllText(path, main);
            return Compiler.EmitCSharp(new[] { path });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_pointer_to_a_failed_container_is_opaque_and_its_dependents_are_withdrawn()
    {
        var cs = Emit("""
            const Writer = @import("Writer.zig");
            const File = @import("File.zig");
            fn sendNone(r: *File.Reader, n: u8) u8 {
                _ = r;
                return n;
            }
            pub fn main() u8 {
                const t = Writer.VTable{ .send = sendNone };
                return t.send(undefined, 42);
            }
            """);
        cs.ShouldContain("public delegate*<void*, byte, byte> send;");
        cs.ShouldContain("static unsafe byte sendNone(void* r, byte n)");
        cs.ShouldContain("return t.send(default(void*), 42);");
        cs.ShouldNotContain("File__File");     // the failed file-as-struct
        cs.ShouldNotContain("File__Reader");   // embeds it by value, so it cannot be emitted either
    }

    [Fact]
    public void A_use_that_needs_the_failed_containers_layout_is_still_loud()
    {
        var ex = Should.Throw<CompileException>(() => Emit("""
            const File = @import("File.zig");
            fn peek(r: *File.Reader) u8 {
                _ = r.file;
                return 0;
            }
            pub fn main() u8 {
                return peek(undefined);
            }
            """));
        ex.Message.ShouldContain("no field 'file' on type void*");

        var byValue = Should.Throw<CompileException>(() => Emit("""
            const File = @import("File.zig");
            fn g(r: File.Reader) u8 {
                _ = r;
                return 0;
            }
            pub fn main() u8 {
                return g(undefined);
            }
            """));
        byValue.Message.ShouldContain("zig container `File__File` could not be lowered");
    }
}
