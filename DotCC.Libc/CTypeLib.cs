#nullable enable

using System.Runtime.CompilerServices;

namespace DotCC.Libc;

/// <summary>
/// C99 <c>&lt;ctype.h&gt;</c> surface — character classification and
/// case conversion. Each predicate takes an <c>int</c> (C convention:
/// the argument is the original byte value or <c>EOF</c>; <c>EOF</c>
/// is treated as "no character" → all predicates return 0). Returns
/// <c>int</c> (non-zero on match, zero otherwise) per the standard.
/// </summary>
/// <remarks>
/// <para>
/// All predicates implement the "C" locale behavior — ASCII only, no
/// locale-dependent extensions. That's how most real-world C code uses
/// these (and it matches what dotcc's UTF-8-native <c>char*</c> model
/// expects: classify individual bytes, not multibyte sequences).
/// Bytes outside the 0..127 ASCII range return 0 from every predicate
/// (matching what glibc does with `LC_ALL=C`).
/// </para>
/// <para>
/// <c>toupper</c> / <c>tolower</c> map ASCII letters and pass other
/// values through unchanged (returning <c>int</c>, since the input
/// might be <c>EOF</c>).
/// </para>
/// </remarks>
public static unsafe partial class Libc
{
    // Predicates — each takes int (the byte value or EOF), returns int.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isalpha(int c) => IsAscii(c) && ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) ? 1 : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isdigit(int c) => IsAscii(c) && c >= '0' && c <= '9' ? 1 : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isalnum(int c) => isalpha(c) | isdigit(c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isspace(int c) => IsAscii(c) && (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f') ? 1 : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isupper(int c) => IsAscii(c) && c >= 'A' && c <= 'Z' ? 1 : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int islower(int c) => IsAscii(c) && c >= 'a' && c <= 'z' ? 1 : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isxdigit(int c) =>
        IsAscii(c) && ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')) ? 1 : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int iscntrl(int c) => IsAscii(c) && (c < 0x20 || c == 0x7F) ? 1 : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isprint(int c) => IsAscii(c) && c >= 0x20 && c < 0x7F ? 1 : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int isgraph(int c) => IsAscii(c) && c > 0x20 && c < 0x7F ? 1 : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ispunct(int c) =>
        // graph minus alphanumeric — punctuation chars are printable but
        // not letters or digits.
        isgraph(c) != 0 && isalnum(c) == 0 ? 1 : 0;

    // Case conversion.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int toupper(int c) => (islower(c) != 0) ? c - ('a' - 'A') : c;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int tolower(int c) => (isupper(c) != 0) ? c + ('a' - 'A') : c;

    /// <summary>True iff <paramref name="c"/> is in the ASCII range
    /// (0..127). EOF (typically -1) and bytes ≥ 128 fall outside the
    /// "C" locale's classifiable range.</summary>
    private static bool IsAscii(int c) => c >= 0 && c <= 0x7F;
}
