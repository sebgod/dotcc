#nullable enable

using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// The conversions behind the opt-in POSIX view of Windows paths
/// (<c>-fposix-paths</c>, PathViewLib.cs, GH #254): <c>C:\Users\x</c> is
/// <c>/c/Users/x</c>, <c>\\server\share</c> is <c>//server/share</c>, and a path
/// already in the other form comes back unchanged, so both forms work as input.
/// These are pure functions. The switch itself (<c>EnablePosixPathView</c>) is
/// process-wide state, so it is exercised end to end by the <c>posix-paths</c>
/// fixture, whose program has its own copy of the runtime.
/// </summary>
public sealed class LibcPathViewTests
{
    [Theory]
    [InlineData(@"C:\Users\x", "/c/Users/x")]
    [InlineData(@"C:/Users/x", "/c/Users/x")]
    [InlineData(@"d:\a\b\", "/d/a/b/")]
    [InlineData(@"C:\", "/c")]
    [InlineData("C:", "/c")]
    [InlineData(@"\\server\share\dir\f.txt", "//server/share/dir/f.txt")]
    [InlineData(@"\\?\C:\very\long", "/c/very/long")]
    [InlineData(@"\\?\UNC\server\share\x", "//server/share/x")]
    [InlineData(@"rel\sub\f.c", "rel/sub/f.c")]
    [InlineData("rel/sub", "rel/sub")]
    [InlineData("/c/already", "/c/already")]
    [InlineData("/tmp/x", "/tmp/x")]
    [InlineData("", "")]
    public void A_windows_path_takes_the_posix_spelling(string windows, string posix)
    {
        WindowsToPosixPath(windows).ShouldBe(posix);
    }

    [Theory]
    [InlineData("/c/Users/x", @"C:\Users\x")]
    [InlineData("/c", @"C:\")]
    [InlineData("/c/", @"C:\")]
    [InlineData("/D/a/b", @"D:\a\b")]
    [InlineData("//server/share/x", @"\\server\share\x")]
    [InlineData(@"C:\Users\x", @"C:\Users\x")]
    [InlineData("C:/Users/x", "C:/Users/x")]
    [InlineData("rel/sub", "rel/sub")]
    [InlineData("/tmp/x", "/tmp/x")]
    [InlineData("/cd/x", "/cd/x")]
    [InlineData("/", "/")]
    [InlineData("///x", "///x")]
    public void A_posix_view_path_maps_back_to_windows_and_anything_else_passes_through(string posix, string windows)
    {
        PosixToWindowsPath(posix).ShouldBe(windows);
    }

    [Theory]
    [InlineData(@"C:\Users\x")]
    [InlineData(@"z:\a b\c")]
    [InlineData(@"\\server\share\x")]
    public void A_windows_path_round_trips(string windows)
    {
        PosixToWindowsPath(WindowsToPosixPath(windows)).ShouldBe(windows.Replace('/', '\\'), StringCompareShould.IgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\x", true)]
    [InlineData("C:/x", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData("C:", false)]
    [InlineData("C:x", false)]
    [InlineData("/c/x", false)]
    [InlineData("x.py", false)]
    [InlineData("-c", false)]
    [InlineData("print('C:/x')", false)]
    [InlineData(@"\\\x", false)]
    public void Only_a_whole_drive_or_unc_path_is_an_absolute_windows_path(string arg, bool expected)
    {
        IsAbsoluteWindowsPath(arg).ShouldBe(expected);
    }

    [Fact]
    public void A_path_valued_environment_variable_takes_the_posix_spelling()
    {
        PosixEnvironmentValue("PYTHONHOME", @"C:\py\home", null).ShouldBe("/c/py/home");
        PosixEnvironmentValue("TEMP", @"C:\Users\x\AppData\Local\Temp", null).ShouldBe("/c/Users/x/AppData/Local/Temp");
        // A value that is not an absolute path is left alone, however it looks.
        PosixEnvironmentValue("OS", "Windows_NT", null).ShouldBe("Windows_NT");
        PosixEnvironmentValue("GREETING", "a;b", null).ShouldBe("a;b");
    }

    [Fact]
    public void A_path_list_is_split_on_semicolons_and_joined_with_colons()
    {
        PosixEnvironmentValue("PATH", @"C:\Windows;C:\Program Files\Git\bin;;D:\tools", null)
            .ShouldBe("/c/Windows:/c/Program Files/Git/bin:/d/tools");
        PosixEnvironmentValue("PYTHONPATH", @"C:\lib", null).ShouldBe("/c/lib");
        // Already POSIX: a value the program set itself reads back as written.
        PosixEnvironmentValue("PATH", "/c/a:/c/b", null).ShouldBe("/c/a:/c/b");
        PosixEnvironmentValue("PYTHONPATH", "lib:src", null).ShouldBe("lib:src");
    }

    [Fact]
    public void An_unset_home_falls_back_to_the_user_profile()
    {
        PosixEnvironmentValue("HOME", null, @"C:\Users\x").ShouldBe("/c/Users/x");
        PosixEnvironmentValue("HOME", null, null).ShouldBeNull();
        PosixEnvironmentValue("OTHER", null, @"C:\Users\x").ShouldBeNull();
        PosixEnvironmentValue("HOME", "/home/x", @"C:\Users\x").ShouldBe("/home/x");
    }
}
