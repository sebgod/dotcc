#nullable enable

using System;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// The opt-in POSIX view of Windows paths (<c>dotcc -fposix-paths</c>, GH #254).
/// <para>
/// By default a dotcc program sees paths the way .NET does: on Windows
/// <c>getcwd</c> returns <c>C:\Users\x</c> and a path argument goes to .NET
/// unchanged, so <c>C:\x</c>, <c>C:/x</c> and relative paths all work. POSIX-shaped
/// C code that does its own path arithmetic cannot reason about that form: to
/// CPython's <c>posixpath</c> a drive-letter path is relative and a backslash is an
/// ordinary character. A program built with <c>-fposix-paths</c> calls
/// <see cref="EnablePosixPathView"/> before <c>main</c>, and on Windows the libc
/// then presents one POSIX namespace, spelled as MSYS2 and Git for Windows do:
/// <c>C:\Users\x</c> is <c>/c/Users/x</c> and <c>\\server\share\x</c> is
/// <c>//server/share/x</c>.
/// </para>
/// <para>
/// Paths going OUT to the program (<c>getcwd</c>, <c>realpath</c>, <c>readlink</c>,
/// <c>tmpnam</c>, <c>argv</c>, and path-valued environment variables) take the
/// POSIX form. Paths coming IN (every function that takes a path) accept both
/// forms: <c>/c/x</c> maps back to <c>C:\x</c>, and anything else passes through as
/// before. On other hosts, or without the flag, every conversion is the identity.
/// </para>
/// </summary>
public static unsafe partial class Libc
{
    private static bool _posixPathView;

    /// <summary>Turn the POSIX path view on for this program (a no-op off Windows).
    /// Called by the program shell before <c>main</c> when the program is built with
    /// <c>-fposix-paths</c>.</summary>
    public static void EnablePosixPathView() => _posixPathView = OperatingSystem.IsWindows();

    /// <summary>A path the program passed in, decoded for .NET: under the POSIX view
    /// <c>/c/x</c> becomes <c>C:\x</c>; otherwise the UTF-8 string as is.</summary>
    internal static string HostPath(byte* path)
    {
        var s = path == null ? string.Empty : Encoding.UTF8.GetString(path, strlen(path));
        return _posixPathView ? PosixToWindowsPath(s) : s;
    }

    /// <summary>A path the libc hands to the program: the POSIX form under the view
    /// (<c>C:\x</c> becomes <c>/c/x</c>), else unchanged.</summary>
    internal static string ViewPath(string hostPath) => _posixPathView ? WindowsToPosixPath(hostPath) : hostPath;

    /// <summary>A program argument as the program should see it. Under the POSIX view
    /// an argument that IS an absolute Windows path (<c>X:\...</c>, <c>X:/...</c>,
    /// <c>\\server\...</c>) takes the POSIX form, so <c>python C:\work\x.py</c> names
    /// the script by a path <c>posixpath</c> sees as absolute; any other argument is
    /// left alone. The program shell routes every <c>argv</c> entry through this when
    /// the program is built with <c>-fposix-paths</c>.</summary>
    public static string ViewArgument(string arg) =>
        _posixPathView && IsAbsoluteWindowsPath(arg) ? WindowsToPosixPath(arg) : arg;

    /// <summary>An environment value as the program should see it. Under the POSIX
    /// view: a value that is an absolute Windows path takes the POSIX form; a
    /// <c>PATH</c>-style list (<c>PATH</c>, or a name ending in <c>PATH</c>) holding a
    /// Windows path or a <c>;</c> is split on <c>;</c>, each entry converted, and joined
    /// with <c>:</c>; and an unset <c>HOME</c> falls back to <c>USERPROFILE</c>, as in
    /// MSYS2. Values already in POSIX form pass through unchanged, so a value the
    /// program set itself reads back as written.</summary>
    internal static string? ViewEnvironmentValue(string name, string? value) =>
        _posixPathView ? PosixEnvironmentValue(name, value, Environment.GetEnvironmentVariable("USERPROFILE")) : value;

    /// <summary>The rule behind <see cref="ViewEnvironmentValue"/>, with the host's
    /// <c>USERPROFILE</c> passed in (so it is a pure function of its arguments).</summary>
    public static string? PosixEnvironmentValue(string name, string? value, string? userProfile)
    {
        if (value is null)
        {
            return name.Equals("HOME", StringComparison.OrdinalIgnoreCase) && userProfile is { Length: > 0 }
                ? WindowsToPosixPath(userProfile)
                : null;
        }
        if (name.EndsWith("PATH", StringComparison.OrdinalIgnoreCase) && LooksLikeWindowsPathList(value))
        {
            var parts = value.Split(';');
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                if (part.Length == 0) { continue; }
                if (sb.Length > 0) { sb.Append(':'); }
                sb.Append(WindowsToPosixPath(part));
            }
            return sb.ToString();
        }
        return IsAbsoluteWindowsPath(value) ? WindowsToPosixPath(value) : value;
    }

    /// <summary>True for a list value in Windows form: it holds a <c>;</c>, a
    /// backslash, or a drive-letter path.</summary>
    private static bool LooksLikeWindowsPathList(string value)
    {
        if (value.IndexOf(';') >= 0 || value.IndexOf('\\') >= 0) { return true; }
        for (var i = 0; i + 2 < value.Length; i++)
        {
            if (IsAsciiLetter(value[i]) && value[i + 1] == ':' && (value[i + 2] == '/' || value[i + 2] == '\\')
                && (i == 0 || value[i - 1] == ';')) { return true; }
        }
        return false;
    }

    /// <summary>True when <paramref name="path"/> is an absolute Windows path: a drive
    /// letter followed by a separator, or a UNC / device path starting <c>\\</c>.</summary>
    public static bool IsAbsoluteWindowsPath(string path) =>
        path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')
        || path.Length >= 3 && path[0] == '\\' && path[1] == '\\' && path[2] != '\\';

    /// <summary>The POSIX spelling of a Windows path: <c>C:\a\b</c> is <c>/c/a/b</c>,
    /// <c>C:</c> alone is <c>/c</c>, <c>\\server\share\x</c> is <c>//server/share/x</c>, the
    /// extended-length prefix <c>\\?\</c> (and <c>\\?\UNC\</c>) is dropped, and in anything
    /// else a backslash becomes a slash (on Windows a backslash is always a
    /// separator). A path already in POSIX form comes back unchanged.</summary>
    public static string WindowsToPosixPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) { path = @"\\" + path[8..]; }
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) { path = path[4..]; }
        if (path.Length >= 2 && IsAsciiLetter(path[0]) && path[1] == ':'
            && (path.Length == 2 || path[2] == '\\' || path[2] == '/'))
        {
            var rest = path.Length > 3 ? path[3..].Replace('\\', '/') : string.Empty;
            var drive = char.ToLowerInvariant(path[0]);
            return rest.Length == 0 ? "/" + drive : "/" + drive + "/" + rest;
        }
        return path.Replace('\\', '/');
    }

    /// <summary>The Windows spelling of a POSIX-view path: <c>/c</c> and <c>/c/a/b</c>
    /// are <c>C:\</c> and <c>C:\a\b</c>, and <c>//server/share/x</c> is
    /// <c>\\server\share\x</c>. Anything else (a relative path, a Windows path, a
    /// rooted path without a drive such as <c>/tmp</c>) comes back unchanged, so both
    /// forms work as input.</summary>
    public static string PosixToWindowsPath(string path)
    {
        if (path.Length >= 2 && path[0] == '/' && IsAsciiLetter(path[1]) && (path.Length == 2 || path[2] == '/'))
        {
            var rest = path.Length > 3 ? path[3..].Replace('/', '\\') : string.Empty;
            return char.ToUpperInvariant(path[1]) + @":\" + rest;
        }
        if (path.Length >= 3 && path[0] == '/' && path[1] == '/' && path[2] != '/')
        {
            return @"\\" + path[2..].Replace('/', '\\');
        }
        return path;
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
