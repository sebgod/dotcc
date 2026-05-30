#nullable enable

namespace DotCC;

/// <summary>
/// Minimal synthesized type for an expression, carried up through
/// <see cref="EmitContent.Text"/> so a consuming node (today: <c>sizeof expr</c>)
/// can compute a size. This is NOT a full C type system — it captures only what
/// <c>sizeof</c> needs: enough to know an expression's byte size, with arrays
/// distinguished because dotcc lowers a C array <c>T arr[N]</c> to a C# POINTER
/// (<c>T* arr = stackalloc T[N]</c>), so C# <c>sizeof(arr)</c> would give the
/// pointer size, not <c>N * sizeof(T)</c>.
/// </summary>
/// <remarks>
/// The set of nodes that synthesize a <see cref="CType"/> is intentionally
/// partial (var / literal / subscript / deref / cast / paren / call). Anything
/// unsynthesized leaves the slot null, and <c>sizeof</c> of it raises a clear
/// CompileException rather than emitting a wrong size. The model is the seed of
/// a broader type layer (enum↔param casts, <c>char*</c>→<c>string</c>).
/// </remarks>
public abstract record CType
{
    /// <summary>
    /// A type whose C# <c>sizeof(CsType)</c> is the correct C size — every
    /// scalar, any pointer (<c>byte*</c>), an enum, and a struct VALUE (struct
    /// locals lower to C# value types, not pointers).
    /// </summary>
    public sealed record Sized(string CsType) : CType;

    /// <summary>
    /// A C array, lowered to a C# pointer. Its C <c>sizeof</c> is
    /// <c>Count * sizeof(Element)</c> — computed at emit time, since C#
    /// <c>sizeof</c> on the lowered pointer would be wrong.
    /// </summary>
    public sealed record Arr(CType Element, int Count) : CType;

    /// <summary>
    /// The element type of an indexable/dereferenceable type: an array's
    /// element, or one pointer level peeled off a <see cref="Sized"/> pointer
    /// (<c>int*</c> → <c>int</c>). Null when the type isn't indexable.
    /// </summary>
    public CType? ElementType() => this switch
    {
        Arr a => a.Element,
        Sized s when s.CsType.EndsWith("*", System.StringComparison.Ordinal)
            => new Sized(s.CsType[..^1].TrimEnd()),
        _ => null,
    };
}
