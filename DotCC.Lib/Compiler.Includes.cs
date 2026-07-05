#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>Header-include resolution: the resilient include scan and the lazy
/// IncludeMap (user -I dirs over embedded system headers). One concern of
/// <see cref="Compiler"/> — entry points live in the main file.</summary>
public static partial class Compiler
{
    internal static IncludeMap BuildIncludeMap(
        IReadOnlyList<string> inputPaths,
        IReadOnlyList<string>? includeDirs)
        => BuildIncludeMaps(inputPaths, includeDirs).Content;

    /// <summary>
    /// Single-level enumeration options for the hand-rolled recursive walk below.
    /// <see cref="EnumerationOptions.RecurseSubdirectories"/> is deliberately FALSE —
    /// <see cref="EnumerateIncludablesResilient"/> recurses one directory at a time so
    /// a single failing subtree can be skipped instead of tearing down the whole walk.
    /// </summary>
    private static readonly EnumerationOptions OneLevelScan = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,        // skip chmod-700 / ACL'd dirs (UnauthorizedAccess)
        ReturnSpecialDirectories = false, // no "." / ".."
        AttributesToSkip = 0,
        BufferSize = 65536,
    };

    /// <summary>One directory entry, projected straight from the OS enumeration
    /// buffer — just the fields the header scan needs. <see cref="FileSystemEnumerable{T}"/>
    /// hands the transform a <c>ref FileSystemEntry</c>, so reading <c>Name</c> +
    /// attributes here allocates only the name string (vs <c>Directory.EnumerateFiles</c>,
    /// which materializes a full path string for EVERY entry whether wanted or not).</summary>
    private readonly record struct DirEntry(string Name, bool IsDirectory, bool IsReparsePoint);

    private static IEnumerable<DirEntry> EnumerateOneLevel(string dir) =>
        new System.IO.Enumeration.FileSystemEnumerable<DirEntry>(
            dir,
            static (ref System.IO.Enumeration.FileSystemEntry e) => new DirEntry(
                e.FileName.ToString(),
                (e.Attributes & FileAttributes.Directory) != 0,
                (e.Attributes & FileAttributes.ReparsePoint) != 0),
            OneLevelScan);

    /// <summary>
    /// Recursively enumerate includable files (<c>.h</c> / <c>.c</c>) under
    /// <paramref name="root"/>, yielding each match's full path and its <c>/</c>-separated
    /// path relative to <paramref name="root"/> (built incrementally as we descend, so no
    /// per-file <see cref="Path.GetRelativePath"/> re-parse).
    ///
    /// A hand-rolled walk — NOT <c>Directory.EnumerateFiles(..., AllDirectories)</c> —
    /// because dotcc auto-adds each input file's OWN directory as a search dir, and that
    /// is frequently a big shared dir (the system TEMP root, where the test suite drops
    /// scratch <c>.c</c> files among thousands of unrelated installer subtrees). The
    /// framework's recursive overload tears down the ENTIRE enumeration on the first
    /// directory that throws — <see cref="EnumerationOptions.IgnoreInaccessible"/> only
    /// swallows <see cref="UnauthorizedAccessException"/>, NOT a transient
    /// <see cref="IOException"/> ("insufficient system resources") that an enormous tree
    /// provokes. Walking one level at a time under a per-directory guard skips the bad
    /// subtree while the headers we actually need still resolve. Reparse points
    /// (junctions / symlinks) are skipped to avoid cycles. Technique mirrors
    /// <c>../filetreewalker</c>'s <c>Walker</c> (FileSystemEnumerable per level + manual
    /// recursion + per-level try/catch), trimmed to the header-scan need.
    /// </summary>
    private static IEnumerable<(string FullPath, string Rel)> EnumerateIncludablesResilient(string root)
    {
        // Explicit stack of (directory, its rel prefix) — "" at the root.
        var pending = new Stack<(string Dir, string RelPrefix)>();
        pending.Push((root, ""));
        while (pending.Count > 0)
        {
            var (dir, prefix) = pending.Pop();
            List<DirEntry> entries;
            try
            {
                // Drain this ONE level under the guard: FileSystemEnumerable can throw
                // mid-enumeration (MoveNext), so a bad directory is contained here.
                entries = new List<DirEntry>(EnumerateOneLevel(dir));
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if (e.IsReparsePoint) { continue; } // don't follow junctions/symlinks
                var rel = prefix.Length == 0 ? e.Name : prefix + "/" + e.Name;
                if (e.IsDirectory)
                {
                    pending.Push((Path.Combine(dir, e.Name), rel));
                }
                else if (e.Name.EndsWith(".h", StringComparison.OrdinalIgnoreCase)
                      || e.Name.EndsWith(".c", StringComparison.OrdinalIgnoreCase))
                {
                    yield return (Path.Combine(dir, e.Name), rel);
                }
            }
        }
    }

    /// <summary>
    /// Resolve headers: scan every <c>-I</c> directory + every <c>.h</c>
    /// alongside each <c>.c</c> + the synthetic system headers. Returns both
    /// the <c>name → content</c> map the preprocessor reads AND a
    /// <c>name → on-disk path</c> map used to render dependency files
    /// (<c>-MD</c>/<c>-MMD</c>). Last-wins (in the same dir order) so a user
    /// <c>-I</c> overrides a system header, and the two maps stay consistent.
    /// The synthetic system headers are embedded resources with no disk path,
    /// so they appear only in <c>Content</c> — never in <c>Paths</c>; that is
    /// exactly what keeps them out of the dependency file (nothing for
    /// make/ninja to stat).
    /// </summary>
    private static (IncludeMap Content, Dictionary<string, string> Paths) BuildIncludeMaps(
        IReadOnlyList<string> inputPaths,
        IReadOnlyList<string>? includeDirs)
    {
        var eager = new Dictionary<string, string>(SystemHeaders, StringComparer.Ordinal);
        var lazy = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var dirs = (includeDirs ?? Array.Empty<string>())
            .Concat(inputPaths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p)) ?? "."))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) { continue; }
            // One resilient walk collects BOTH headers and includable `.c` files.
            // A file directly in the dir registers under its bare name
            // (`#include "lstate.h"`). One in a SUBDIRECTORY registers under its
            // dir-relative path with `/` separators, so the subdirectory-qualified
            // include form resolves too: `-I include` + `#include "chibi/sexp.h"` →
            // key `chibi/sexp.h` (chibi-scheme's layout; ditto <sys/types.h> trees).
            // Depth-0 names keep the exact pre-recursion semantics — a nested header
            // is deliberately NOT registered bare, matching gcc.
            foreach (var (full, rel) in EnumerateIncludablesResilient(dir))
            {
                if (rel.EndsWith(".h", StringComparison.OrdinalIgnoreCase))
                {
                    // Header content is read eagerly. A header that vanishes/locks
                    // between scan and read is skipped, not fatal — the same
                    // race-tolerance the lazy `.c` path already has below.
                    string content;
                    try { content = SpliceLineContinuations(File.ReadAllText(full)); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    eager[rel] = content;
                    paths[rel] = full;
                }
                else
                {
                    // `.c` registers too: `#include "opt/fcall.c"` (chibi vm.c) is the
                    // single-translation-unit composition idiom — a quoted include is
                    // a textual splice regardless of extension. Registered by PATH and
                    // read only on actual inclusion: an input's directory can hold many
                    // unrelated `.c` files (a temp dir, a whole source tree), and
                    // reading them all eagerly is wasted I/O — and a race against other
                    // processes' transient files.
                    lazy[rel] = full;
                    paths[rel] = full;
                }
            }
        }
        return (new IncludeMap(eager, lazy), paths);
    }

    /// <summary>
    /// The preprocessor's <c>#include</c> resolution map. Header (<c>.h</c>)
    /// content is loaded eagerly at map-build time (the historical behavior);
    /// includable <c>.c</c> files are registered by path and read on first
    /// actual inclusion, with the content cached. An unreadable lazy entry
    /// (deleted/locked between scan and use) reports as unresolvable rather
    /// than failing the compile.
    /// </summary>
    internal sealed class IncludeMap
    {
        private readonly Dictionary<string, string> _eager;
        private readonly Dictionary<string, string> _lazyPaths;

        internal IncludeMap(Dictionary<string, string> eager, Dictionary<string, string> lazyPaths)
        {
            _eager = eager;
            _lazyPaths = lazyPaths;
        }

        /// <summary>All-eager map (no lazy entries) — test convenience.</summary>
        internal IncludeMap(Dictionary<string, string> eager)
            : this(eager, new Dictionary<string, string>(StringComparer.Ordinal)) { }

        public bool ContainsKey(string name) => _eager.ContainsKey(name) || _lazyPaths.ContainsKey(name);

        public bool TryGetValue(string name, out string content)
        {
            if (_eager.TryGetValue(name, out content!)) { return true; }
            if (_lazyPaths.TryGetValue(name, out var path))
            {
                try
                {
                    content = SpliceLineContinuations(File.ReadAllText(path));
                }
                catch (IOException) { content = ""; return false; }
                catch (UnauthorizedAccessException) { content = ""; return false; }
                _eager[name] = content; // cache for the next TU in this compile
                return true;
            }
            content = "";
            return false;
        }
    }

}
