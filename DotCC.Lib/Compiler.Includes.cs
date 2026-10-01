#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace DotCC;

/// <summary>Header-include resolution: the <c>#include</c> search order over the
/// user's <c>-I</c> directories and the embedded system headers. One concern of
/// <see cref="Compiler"/>; entry points live in the main file.</summary>
public static partial class Compiler
{
    /// <summary>
    /// Build the <c>#include</c> resolver for one compile over
    /// <paramref name="includeDirs"/> (the <c>-I</c> directories, in command-line
    /// order) and the embedded system headers. One resolver serves every
    /// translation unit of the compile, so a header read once is cached.
    /// </summary>
    internal static IncludeResolver BuildIncludeResolver(
        IReadOnlyList<string>? includeDirs, Func<string, string?>? libraryHeader = null)
        => new(includeDirs ?? Array.Empty<string>(), SystemHeaders, libraryHeader);

    /// <summary>One file an <c>#include</c> resolved to.</summary>
    /// <param name="Key">Identity for <c>#pragma once</c> and the header-guard
    /// cache: the full path of a disk file, a <c>&lt;builtin&gt;/</c> name for an
    /// embedded header.</param>
    /// <param name="Path">The full disk path, or null for an embedded header
    /// (which has nothing for a dependency file to list).</param>
    /// <param name="Directory">Where a quoted <c>#include</c> inside this file
    /// looks first, or null for an embedded header; <see cref="IncludeResolver.LibraryDir"/>
    /// for any file a library unit includes.</param>
    /// <param name="IsSynthetic">One of dotcc's embedded system headers, whose
    /// declarations are runtime-provided.</param>
    internal sealed record IncludeFile(string Key, string? Path, string? Directory, bool IsSynthetic);

    /// <summary>
    /// Resolves an <c>#include</c> name the way gcc and clang do. A quoted
    /// <c>"name"</c> is looked up first in the directory of the file that contains
    /// the directive (so <c>"../lexer/state.h"</c> and a header next to its
    /// includer work), then like an angle <c>&lt;name&gt;</c>: in each <c>-I</c>
    /// directory in command-line order (the first match wins), then among dotcc's
    /// embedded system headers. A user <c>-I</c> header therefore shadows an
    /// embedded one of the same name. Any file name is includable
    /// (<c>typeslots.inc</c>, <c>clinic/transmogrify.h.h</c>, a <c>.c</c> file).
    /// </summary>
    /// <remarks>
    /// Resolution probes the file system per directive and caches both the probes
    /// and the file contents; nothing is scanned or read up front. The input
    /// file's own directory is NOT a search directory for <c>&lt;name&gt;</c>, as
    /// in gcc and clang (a quoted include from it finds its neighbours through the
    /// includer rule).
    /// <para>A unit of a library compiled with the program (<see cref="Frontends.SourceLibrary"/>)
    /// sits in <see cref="LibraryDir"/>, and so does everything it includes. Its includes, quoted
    /// or angle, find the library's own headers first and then the embedded system headers, and
    /// never the user's <c>-I</c> directories, so the library builds the same in every program.</para>
    /// </remarks>
    internal sealed class IncludeResolver
    {
        /// <summary>The directory a library unit and its headers live in: not a disk path.</summary>
        internal const string LibraryDir = "<libc>";

        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private readonly string[] _dirs;
        private readonly IReadOnlyDictionary<string, string> _builtins;
        private readonly Func<string, string?>? _libraryHeader;
        private readonly Dictionary<string, bool> _exists = new(PathComparer);
        private readonly Dictionary<string, string> _content = new(PathComparer);

        /// <summary>The embedded headers this compile has included, by name: the runtime
        /// splices a header's optional C# piece (the <c>&lt;Python.h&gt;</c> shim) only
        /// when some unit includes it.</summary>
        internal HashSet<string> IncludedBuiltins { get; } = new(StringComparer.Ordinal);

        /// <summary>Resolve against <paramref name="includeDirs"/> (command-line
        /// order, duplicates dropped) and then <paramref name="builtins"/> (embedded
        /// header name to content); a library unit's includes against
        /// <paramref name="libraryHeader"/> and then <paramref name="builtins"/>.</summary>
        internal IncludeResolver(
            IReadOnlyList<string> includeDirs, IReadOnlyDictionary<string, string> builtins,
            Func<string, string?>? libraryHeader = null)
        {
            _libraryHeader = libraryHeader;
            var dirs = new List<string>();
            var seen = new HashSet<string>(PathComparer);
            foreach (var dir in includeDirs)
            {
                var full = Path.GetFullPath(dir);
                if (seen.Add(full)) { dirs.Add(full); }
            }
            _dirs = dirs.ToArray();
            _builtins = builtins;
        }

        /// <summary>Find <paramref name="name"/> as <c>#include</c> spells it:
        /// <paramref name="isAngle"/> for the <c>&lt;…&gt;</c> form;
        /// <paramref name="includerDir"/> is the directory of the file holding the
        /// directive (null for an embedded header). Null when nothing matches.</summary>
        public IncludeFile? Resolve(string name, bool isAngle, string? includerDir)
        {
            if (name.Length == 0) { return null; }
            if (includerDir == LibraryDir)
            {
                if (_libraryHeader?.Invoke(name) is not null)
                {
                    return new IncludeFile(LibraryDir + "/" + name, Path: null, LibraryDir, IsSynthetic: true);
                }
                return _builtins.ContainsKey(name)
                    ? new IncludeFile("<builtin>/" + name, Path: null, LibraryDir, IsSynthetic: true)
                    : null;
            }
            if (Path.IsPathRooted(name)) { return FileAt(name); }
            if (!isAngle && includerDir is not null && FileAt(Path.Combine(includerDir, name)) is { } local)
            {
                return local;
            }
            foreach (var dir in _dirs)
            {
                if (FileAt(Path.Combine(dir, name)) is { } found) { return found; }
            }
            return _builtins.ContainsKey(name)
                ? new IncludeFile("<builtin>/" + name, Path: null, Directory: null, IsSynthetic: true)
                : null;
        }

        /// <summary>The text of <paramref name="file"/> (<paramref name="name"/> is
        /// the spelling that found it), with line continuations spliced. False when
        /// a disk file cannot be read after all; <paramref name="error"/> says why.</summary>
        public bool TryRead(IncludeFile file, string name, out string content, out string? error)
        {
            error = null;
            if (file.Path is not { } path)
            {
                if (file.Key.StartsWith(LibraryDir + "/", StringComparison.Ordinal))
                {
                    return _libraryHeader?.Invoke(name) is { } text ? Found(text, out content) : Fail(out content);
                }
                return _builtins.TryGetValue(name, out content!) || Fail(out content);
            }
            if (_content.TryGetValue(path, out content!)) { return true; }
            try
            {
                content = SpliceLineContinuations(File.ReadAllText(path));
            }
            catch (IOException ex) { error = ex.Message; return Fail(out content); }
            catch (UnauthorizedAccessException ex) { error = ex.Message; return Fail(out content); }
            _content[path] = content;
            return true;

            static bool Fail(out string content)
            {
                content = "";
                return false;
            }

            static bool Found(string text, out string content)
            {
                content = text;
                return true;
            }
        }

        /// <summary>The file at <paramref name="candidate"/>, when one exists
        /// there (a directory does not count).</summary>
        private IncludeFile? FileAt(string candidate)
        {
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
            if (!_exists.TryGetValue(full, out var exists))
            {
                exists = File.Exists(full);
                _exists[full] = exists;
            }
            return exists
                ? new IncludeFile(full, full, Path.GetDirectoryName(full), IsSynthetic: false)
                : null;
        }
    }
}
