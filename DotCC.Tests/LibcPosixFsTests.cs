#nullable enable

using System;
using System.IO;
using System.Text;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the POSIX filesystem surface (<see cref="DotCC.Libc"/>'s
/// PosixFsLib): directory iteration (<c>opendir</c>/<c>readdir</c>/<c>closedir</c>)
/// and the path operations (<c>mkdir</c>/<c>rmdir</c>/<c>unlink</c>/<c>getcwd</c>/
/// <c>access</c>) over <see cref="System.IO"/>. These are what chibi-scheme's
/// <c>(chibi filesystem)</c> binds to.
/// </summary>
public sealed unsafe class LibcPosixFsTests
{
    private static byte* C(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s + "\0");
        var p = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)bytes.Length);
        bytes.AsSpan().CopyTo(new Span<byte>(p, bytes.Length));
        return p;
    }

    [Fact]
    public void mkdir_opendir_readdir_unlink_rmdir_roundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotcc-fs-" + Guid.NewGuid().ToString("N"));
        mkdir(C(dir), 0x1FF).ShouldBe(0);          // 0777
        Directory.Exists(dir).ShouldBeTrue();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hi");

        // opendir + readdir enumerates "." ".." and the real entry.
        var d = opendir(C(dir));
        ((nint)d).ShouldNotBe((nint)0);
        var seen = new System.Collections.Generic.List<string>();
        for (var e = readdir(d); e != null; e = readdir(d))
        {
            // d_name is at offset 0 of struct dirent (see include/dirent.h).
            seen.Add(Encoding.UTF8.GetString((byte*)e, strlen((byte*)e)));
        }
        closedir(d).ShouldBe(0);
        seen.ShouldContain(".");
        seen.ShouldContain("..");
        seen.ShouldContain("a.txt");

        unlink(C(Path.Combine(dir, "a.txt"))).ShouldBe(0);
        File.Exists(Path.Combine(dir, "a.txt")).ShouldBeFalse();
        rmdir(C(dir)).ShouldBe(0);
        Directory.Exists(dir).ShouldBeFalse();
    }

    [Fact]
    public void getcwd_reports_a_path_and_chdir_changes_it()
    {
        var saved = Directory.GetCurrentDirectory();
        byte* buf = stackalloc byte[1024];
        ((nint)getcwd(buf, 1024)).ShouldNotBe((nint)0);
        Encoding.UTF8.GetString(buf, strlen(buf)).ShouldNotBeNullOrEmpty();

        var tmp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            chdir(C(tmp)).ShouldBe(0);
            // The new cwd should resolve to the temp dir (normalize for symlinks).
            Path.GetFullPath(Directory.GetCurrentDirectory()).ShouldBe(Path.GetFullPath(tmp));
        }
        finally { Directory.SetCurrentDirectory(saved); }
    }

    [Fact]
    public void access_reports_existence()
    {
        var f = Path.GetTempFileName();
        try
        {
            access(C(f), 0).ShouldBe(0);                 // F_OK on an existing file
            access(C(f + ".nope"), 0).ShouldBe(-1);      // missing -> -1
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void unlink_missing_file_fails()
    {
        var missing = Path.Combine(Path.GetTempPath(), "dotcc-fs-missing-" + Guid.NewGuid().ToString("N"));
        unlink(C(missing)).ShouldBe(-1);
    }
}
