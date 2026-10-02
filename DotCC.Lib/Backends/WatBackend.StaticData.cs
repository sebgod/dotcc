#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DotCC.Backends;

using DotCC.Ir;

/// <summary>
/// Static initializers as data. C requires an object with static storage to be initialized by
/// constant expressions (arithmetic constants, the addresses of static objects and functions,
/// offsets from them), and the module knows every such value when it is built: a global's
/// address is fixed, and so is a function's table slot. So an initializer becomes the bytes it
/// stores, in a data segment, instead of stores the module runs at start (CPython's run to
/// 8 MB of code in one function, past what a browser compiles). Each value is lowered as the
/// stores would lower it, and the instructions that leaves are folded at compile time
/// (<see cref="FoldConstant"/>); a value that needs anything but constants (a load, a call)
/// leaves its object to the start function's stores, as before.
/// </summary>
internal sealed partial class WatBackend
{
    /// <summary>The storage of the static compound literals (see <see cref="ReserveStatic"/>),
    /// as [start, end) address ranges, in the order reserved.</summary>
    private readonly List<(int Start, int End)> _staticLiterals = new();

    /// <summary>While an image is built: the bytes its values store into the storage of the
    /// compound literals they reserve, and which addresses those are.</summary>
    private Dictionary<int, byte>? _imageMemory;
    private Func<int, bool>? _imageWritable;

    /// <summary>The bytes the initializer <paramref name="init"/> of <paramref name="g"/>
    /// stores into its <paramref name="size"/> bytes, laid out at compile time, with those of
    /// each static compound literal it reserves (at the literal's address), or null when some
    /// value is not one the module can compute without running code.</summary>
    private (byte[] Image, List<(int Addr, byte[] Bytes)> Literals)? StaticImage(GlobalVar g, CExpr init, int size)
    {
        var image = new byte[size];
        var literals = new Dictionary<int, byte>();
        var first = _staticLiterals.Count;
        bool Writable(int addr)
        {
            for (var k = first; k < _staticLiterals.Count; k++)
            {
                if (addr >= _staticLiterals[k].Start && addr < _staticLiterals[k].End) { return true; }
            }
            return false;
        }
        _imageMemory = literals;
        _imageWritable = Writable;
        try
        {
            var ok = init switch
            {
                PinnedArray { Elems: { } elems } pa => ImageElements(image, 0, pa.Element, elems),
                PinnedArray => true,
                StructInit si when IsAggregate(g.Sym.Type) => ImageMembers(image, 0, g.Sym.Type, si),
                _ => ImageValue(image, 0, g.Sym.Type, init),
            };
            if (ok && g.Flexible is { } tail)
            {
                ok = ImageElements(image, FlexibleOffset(g.Sym.Type, tail), tail.Element, tail.Elems);
            }
            if (!ok) { return null; }
            // Each literal's storage on its own: between two of them lie other objects (the
            // strings their members point at), whose bytes are not theirs to write.
            var bytes = new List<(int Addr, byte[] Bytes)>();
            for (var k = first; k < _staticLiterals.Count; k++)
            {
                var (start, end) = _staticLiterals[k];
                var run = new byte[end - start];
                for (var a = start; a < end; a++) { run[a - start] = literals.TryGetValue(a, out var b) ? b : (byte)0; }
                bytes.Add((start, run));
            }
            return (image, bytes);
        }
        finally
        {
            _imageMemory = null;
            _imageWritable = null;
        }
    }

    /// <summary>Lay out array elements of type <paramref name="element"/> from <paramref name="at"/>.</summary>
    private bool ImageElements(byte[] image, int at, CType element, IReadOnlyList<CExpr> elems)
    {
        var step = WasmSizeOf(element);
        for (var i = 0; i < elems.Count; i++)
        {
            if (!ImageValue(image, at + i * step, element, elems[i])) { return false; }
        }
        return true;
    }

    /// <summary>Lay out the members <paramref name="si"/> gives of the aggregate of type
    /// <paramref name="type"/> at <paramref name="offset"/>, as
    /// <see cref="StoreAggregateMembers"/> stores them: a bit-field ored into its unit.</summary>
    private bool ImageMembers(byte[] image, int offset, CType type, StructInit si)
    {
        var name = ((CType.Named)type.Unqualified).Name;
        foreach (var m in si.Members)
        {
            if (m.FieldType.Unqualified is CType.VoidType) { continue; }
            if (Unit.FieldPlaceOf(name, m.Name) is { Field.IsBitField: true } place)
            {
                if (place.Field.BitWidth is not { } width || width == 0 || m.Value is DefaultLit) { continue; }
                var unit = BitUnitType(place);
                var code = Capture(() =>
                {
                    EmitExpr(m.Value);
                    EmitConvert(m.Value.Type, place.Field.Type);
                    EmitConvert(place.Field.Type, unit);
                });
                if (FoldConstant(code, _imageMemory, _imageWritable) is not { } bits) { return false; }
                var mask = width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
                var at = offset + place.Offset;
                var bytes = WasmSizeOf(unit);
                var word = Read(image, at, bytes);
                word = (word & ~(mask << place.BitOffset)) | ((bits & mask) << place.BitOffset);
                Write(image, at, bytes, word);
                continue;
            }
            var memberAt = offset + (Unit.OffsetOfConst(name, m.Name)
                ?? throw new IrUnsupportedException($"the wat target cannot place member '{m.Name}' of {name}"));
            if (!ImageValue(image, memberAt, m.FieldType, m.Value)) { return false; }
        }
        return true;
    }

    /// <summary>Lay out one value of type <paramref name="type"/> at <paramref name="at"/>, as
    /// <see cref="StoreInitValue"/> stores it.</summary>
    private bool ImageValue(byte[] image, int at, CType type, CExpr value)
    {
        switch (value)
        {
            case StructInit nested when IsAggregate(type):
                return ImageMembers(image, at, type, nested);
            case ArrayValue av when type.Unqualified is CType.Array:
                return ImageElements(image, at, av.Element, av.Elems);
            case LitInt { Value: 0 }:
            case NullPtr:
            case DefaultLit:
                return true;
        }
        if (IsAggregate(type)) { return false; }
        // The store's own instructions before the store itself (a pointer's widening to the
        // eight bytes it takes in memory), then the store's width.
        var store = StoreInstr(type).Split(' ');
        var code = Capture(() => { EmitExpr(value); EmitConvert(value.Type, type); });
        if (FoldConstant(code + string.Join("\n", store[..^1]), _imageMemory, _imageWritable) is not { } bits) { return false; }
        var width = store[^1] switch
        {
            "i32.store8" => 1,
            "i32.store16" => 2,
            "i32.store" or "f32.store" => 4,
            _ => 8,
        };
        Write(image, at, width, bits);
        return true;
    }

    /// <summary>What <paramref name="emit"/> writes, emitted aside.</summary>
    private string Capture(Action emit)
    {
        var prev = _out;
        var indent = _indent;
        var buf = new StringBuilder();
        _out = buf;
        try { emit(); }
        finally
        {
            _out = prev;
            _indent = indent;
        }
        return buf.ToString();
    }

    private static ulong Read(byte[] image, int at, int bytes)
    {
        ulong v = 0;
        for (var i = 0; i < bytes; i++) { v |= (ulong)image[at + i] << (8 * i); }
        return v;
    }

    private static void Write(byte[] image, int at, int bytes, ulong v)
    {
        for (var i = 0; i < bytes; i++) { image[at + i] = (byte)(v >> (8 * i)); }
    }

    /// <summary>Data segments for the image of an object at <paramref name="addr"/>: its runs
    /// of bytes that are not zero (memory starts zeroed), a short run of the object's own zeros
    /// kept inside a segment rather than starting another.</summary>
    private void AddImageSegments(int addr, byte[] image)
    {
        const int Gap = 32;
        var i = 0;
        while (i < image.Length)
        {
            if (image[i] == 0) { i++; continue; }
            var start = i;
            var end = i + 1;
            var zeros = 0;
            for (var j = end; j < image.Length && zeros <= Gap; j++)
            {
                if (image[j] == 0) { zeros++; }
                else { zeros = 0; end = j + 1; }
            }
            var hex = new StringBuilder((end - start) * 3);
            for (var k = start; k < end; k++) { hex.Append('\\').Append(image[k].ToString("x2", CultureInfo.InvariantCulture)); }
            _strData.Add((addr + start, hex.ToString()));
            i = end;
        }
    }

    /// <summary>The value the wat <paramref name="code"/> leaves, when it computes only on
    /// constants, as its bits: the constants, integer and floating arithmetic, comparisons and
    /// the conversions, <c>select</c>, locals, and an <c>if</c> whose condition is one (C's
    /// <c>?:</c> on constants). With <paramref name="memory"/>, it may also store to and load
    /// from the addresses <paramref name="writable"/> allows (the storage of a static compound
    /// literal, which starts zeroed), which the stores record there. Null when it does anything
    /// else (another load or store, a call, a branch), would trap (a division by zero), or does
    /// not leave one value. A float is folded as wasm computes it; a NaN result is the
    /// canonical one.</summary>
    internal static ulong? FoldConstant(string code, Dictionary<int, byte>? memory = null, Func<int, bool>? writable = null)
    {
        // A value's type: 'i' i32, 'I' i64, 'f' f32, 'F' f64. An i32 is kept zero-extended.
        var stack = new Stack<(char T, ulong V)>();
        var locals = new Dictionary<string, (char T, ulong V)>(StringComparer.Ordinal);
        // The ifs being run: true for one in its then arm (whose else, if any, is skipped).
        var arms = new Stack<bool>();
        var tokens = new List<string>();
        foreach (var raw in code.Split('\n'))
        {
            var line = raw;
            var comment = line.IndexOf(";;", StringComparison.Ordinal);
            if (comment >= 0) { line = line[..comment]; }
            tokens.AddRange(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        static float F32(ulong v) => BitConverter.Int32BitsToSingle(unchecked((int)(uint)v));
        static double F64(ulong v) => BitConverter.Int64BitsToDouble(unchecked((long)v));
        static ulong B32(float f) => float.IsNaN(f) ? 0x7fc00000UL : (uint)BitConverter.SingleToInt32Bits(f);
        static ulong B64(double d) => double.IsNaN(d) ? 0x7ff8000000000000UL : unchecked((ulong)BitConverter.DoubleToInt64Bits(d));
        // From token i (an if's or an else's), the index of its arm's end: the matching else
        // (stopAtElse) or end, at the same depth.
        int SkipArm(int i, bool stopAtElse)
        {
            var depth = 0;
            for (var j = i + 1; j < tokens.Count; j++)
            {
                switch (tokens[j])
                {
                    case "if" or "block" or "loop": depth++; break;
                    case "else" when depth == 0 && stopAtElse: return j;
                    case "end" when depth == 0: return j;
                    case "end": depth--; break;
                }
            }
            return -1;
        }
        for (var i = 0; i < tokens.Count; i++)
        {
            var op = tokens[i];
            switch (op)
            {
                case "nop":
                    continue;
                case "if":
                {
                    // `if (result T)`: the result type is the arms' business.
                    if (i + 1 < tokens.Count && tokens[i + 1].StartsWith("(result", StringComparison.Ordinal))
                    {
                        i++;
                        while (i < tokens.Count && !tokens[i].EndsWith(')')) { i++; }
                    }
                    if (stack.Count < 1) { return null; }
                    if ((stack.Pop().V & 0xFFFFFFFFUL) != 0)
                    {
                        arms.Push(true);
                        continue;
                    }
                    var next = SkipArm(i, stopAtElse: true);
                    if (next < 0) { return null; }
                    if (tokens[next] == "else") { arms.Push(false); }
                    i = next;
                    continue;
                }
                case "else":
                {
                    if (arms.Count == 0 || !arms.Pop()) { return null; }
                    var end = SkipArm(i, stopAtElse: false);
                    if (end < 0) { return null; }
                    i = end;
                    continue;
                }
                case "end":
                    if (arms.Count == 0) { return null; }
                    arms.Pop();
                    continue;
                case "select":
                {
                    if (stack.Count < 3) { return null; }
                    var c = stack.Pop();
                    var b = stack.Pop();
                    var a = stack.Pop();
                    stack.Push((c.V & 0xFFFFFFFFUL) != 0 ? a : b);
                    continue;
                }
                case "local.get":
                    if (++i >= tokens.Count || !locals.TryGetValue(tokens[i], out var got)) { return null; }
                    stack.Push(got);
                    continue;
                case "local.set" or "local.tee":
                    if (++i >= tokens.Count || stack.Count < 1) { return null; }
                    locals[tokens[i]] = op == "local.set" ? stack.Pop() : stack.Peek();
                    continue;
            }
            var dot = op.IndexOf('.');
            if (dot < 0) { return null; }
            var t = op[..dot] switch { "i32" => 'i', "i64" => 'I', "f32" => 'f', "f64" => 'F', _ => '?' };
            var name = op[(dot + 1)..];
            if (t == '?') { return null; }
            if (name.StartsWith("store", StringComparison.Ordinal) || name.StartsWith("load", StringComparison.Ordinal))
            {
                // A memory access: its width from the name, an offset= immediate after it.
                var offset = 0;
                while (i + 1 < tokens.Count && (tokens[i + 1].StartsWith("offset=", StringComparison.Ordinal) || tokens[i + 1].StartsWith("align=", StringComparison.Ordinal)))
                {
                    i++;
                    if (tokens[i].StartsWith("offset=", StringComparison.Ordinal)) { offset = int.Parse(tokens[i][7..], CultureInfo.InvariantCulture); }
                }
                var digits = new string(name.SkipWhile(ch => !char.IsDigit(ch)).TakeWhile(char.IsDigit).ToArray());
                var width = digits.Length > 0 ? int.Parse(digits, CultureInfo.InvariantCulture) / 8 : t is 'i' or 'f' ? 4 : 8;
                if (memory is null || writable is null) { return null; }
                if (name.StartsWith("store", StringComparison.Ordinal))
                {
                    if (stack.Count < 2) { return null; }
                    var (_, value) = stack.Pop();
                    var at = (int)(uint)stack.Pop().V + offset;
                    for (var k = 0; k < width; k++)
                    {
                        if (!writable(at + k)) { return null; }
                        memory[at + k] = (byte)(value >> (8 * k));
                    }
                }
                else
                {
                    if (stack.Count < 1) { return null; }
                    var at = (int)(uint)stack.Pop().V + offset;
                    ulong value = 0;
                    for (var k = 0; k < width; k++)
                    {
                        if (!writable(at + k)) { return null; }
                        value |= (ulong)(memory.TryGetValue(at + k, out var by) ? by : 0) << (8 * k);
                    }
                    // A narrow load extends, signed (_s) or not, to its type.
                    if (width < 8 && name.EndsWith("_s", StringComparison.Ordinal))
                    {
                        var shift = 64 - 8 * width;
                        value = unchecked((ulong)((long)(value << shift) >> shift));
                    }
                    stack.Push((t, t is 'i' or 'f' ? value & 0xFFFFFFFFUL : value));
                }
                continue;
            }
            if (name == "const")
            {
                if (++i >= tokens.Count || ParseConst(t, tokens[i]) is not { } k) { return null; }
                stack.Push((t, k));
                continue;
            }
            var unary = name is "eqz" or "clz" or "ctz" or "popcnt" or "extend8_s" or "extend16_s" or "extend32_s"
                or "neg" or "abs" or "sqrt" or "ceil" or "floor" or "trunc" or "nearest"
                // The conversions name their operand's type: wrap_i64, extend_i32_s, convert_i32_u, ...
                || name.Contains("_i32") || name.Contains("_i64") || name.Contains("_f32") || name.Contains("_f64");
            if (unary)
            {
                if (stack.Count < 1) { return null; }
                var (at, av) = stack.Pop();
                if (Unary(t, name, at, av) is not { } r) { return null; }
                stack.Push(r);
                continue;
            }
            if (stack.Count < 2) { return null; }
            var (bt, bv) = stack.Pop();
            var (lt, lv) = stack.Pop();
            if (lt != bt || Binary(t, name, lv, bv) is not { } result) { return null; }
            stack.Push(result);
        }
        return stack.Count == 1 && arms.Count == 0 ? stack.Pop().V : null;

        // A wat integer: decimal or 0x hex, signed or not, '_' between digits; its low 64 bits.
        static ulong? ParseInt(string s)
        {
            s = s.Replace("_", "");
            var neg = s.StartsWith('-');
            if (neg || s.StartsWith('+')) { s = s[1..]; }
            ulong v;
            var ok = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.TryParse(s[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v)
                : ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
            return ok ? (neg ? unchecked(0UL - v) : v) : null;
        }

        static ulong? ParseConst(char t, string s)
        {
            var inv = CultureInfo.InvariantCulture;
            switch (t)
            {
                case 'i':
                    return ParseInt(s) is { } i32 ? i32 & 0xFFFFFFFFUL : null;
                case 'I':
                    return ParseInt(s);
                default:
                    double d;
                    if (s is "inf" or "+inf") { d = double.PositiveInfinity; }
                    else if (s == "-inf") { d = double.NegativeInfinity; }
                    else if (s is "nan" or "+nan" or "-nan") { d = double.NaN; }
                    else if (!double.TryParse(s, NumberStyles.Float, inv, out d)) { return null; }
                    return t == 'f' ? B32((float)d) : B64(d);
            }
        }

        static (char, ulong)? Unary(char t, string name, char at, ulong v)
        {
            uint u32 = (uint)v;
            switch (t, name)
            {
                case ('i', "eqz"): return at == 'i' ? ('i', u32 == 0 ? 1UL : 0) : null;
                case ('I', "eqz"): return at == 'I' ? ('i', v == 0 ? 1UL : 0) : null;
                case ('i', "extend8_s"): return ('i', (uint)(int)(sbyte)u32);
                case ('i', "extend16_s"): return ('i', (uint)(int)(short)u32);
                case ('I', "extend8_s"): return ('I', unchecked((ulong)(long)(sbyte)v));
                case ('I', "extend16_s"): return ('I', unchecked((ulong)(long)(short)v));
                case ('I', "extend32_s"): return ('I', unchecked((ulong)(long)(int)u32));
                case ('i', "wrap_i64"): return at == 'I' ? ('i', v & 0xFFFFFFFFUL) : null;
                case ('I', "extend_i32_s"): return at == 'i' ? ('I', unchecked((ulong)(long)(int)u32)) : null;
                case ('I', "extend_i32_u"): return at == 'i' ? ('I', u32) : null;
                case ('f', "neg"): return ('f', B32(-F32(v)));
                case ('F', "neg"): return ('F', B64(-F64(v)));
                case ('f', "abs"): return ('f', B32(MathF.Abs(F32(v))));
                case ('F', "abs"): return ('F', B64(Math.Abs(F64(v))));
                case ('f', "demote_f64"): return at == 'F' ? ('f', B32((float)F64(v))) : null;
                case ('F', "promote_f32"): return at == 'f' ? ('F', B64(F32(v))) : null;
                case ('f', "convert_i32_s"): return at == 'i' ? ('f', B32((int)u32)) : null;
                case ('f', "convert_i32_u"): return at == 'i' ? ('f', B32(u32)) : null;
                case ('f', "convert_i64_s"): return at == 'I' ? ('f', B32(unchecked((long)v))) : null;
                case ('f', "convert_i64_u"): return at == 'I' ? ('f', B32(v)) : null;
                case ('F', "convert_i32_s"): return at == 'i' ? ('F', B64((int)u32)) : null;
                case ('F', "convert_i32_u"): return at == 'i' ? ('F', B64(u32)) : null;
                case ('F', "convert_i64_s"): return at == 'I' ? ('F', B64(unchecked((long)v))) : null;
                case ('F', "convert_i64_u"): return at == 'I' ? ('F', B64(v)) : null;
                case ('i', "reinterpret_f32"): return at == 'f' ? ('i', v) : null;
                case ('I', "reinterpret_f64"): return at == 'F' ? ('I', v) : null;
                case ('f', "reinterpret_i32"): return at == 'i' ? ('f', v) : null;
                case ('F', "reinterpret_i64"): return at == 'I' ? ('F', v) : null;
            }
            // A float to an integer: in range only (a trapping trunc out of range traps; a
            // saturating one is left to run).
            if (name.StartsWith("trunc_f", StringComparison.Ordinal) && !name.StartsWith("trunc_sat", StringComparison.Ordinal))
            {
                var d = at == 'f' ? F32(v) : at == 'F' ? F64(v) : double.NaN;
                if (double.IsNaN(d)) { return null; }
                var tr = Math.Truncate(d);
                var signed = name.EndsWith("_s", StringComparison.Ordinal);
                if (t == 'i')
                {
                    if (signed) { return tr >= int.MinValue && tr <= int.MaxValue ? ('i', (uint)(int)tr) : null; }
                    return tr >= 0 && tr <= uint.MaxValue ? ('i', (uint)tr) : null;
                }
                if (signed) { return tr >= -9.2233720368547758e18 && tr < 9.2233720368547758e18 ? ('I', unchecked((ulong)(long)tr)) : null; }
                return tr >= 0 && tr < 1.8446744073709552e19 ? ('I', (ulong)tr) : null;
            }
            return null;
        }

        static (char, ulong)? Binary(char t, string name, ulong a, ulong b)
        {
            if (t is 'i' or 'I')
            {
                var bits = t == 'i' ? 32 : 64;
                var mask = t == 'i' ? 0xFFFFFFFFUL : ulong.MaxValue;
                long sa = t == 'i' ? (int)(uint)a : unchecked((long)a);
                long sb = t == 'i' ? (int)(uint)b : unchecked((long)b);
                ulong ua = a & mask, ub = b & mask;
                var sh = (int)(ub & (ulong)(bits - 1));
                ulong? r = name switch
                {
                    "add" => ua + ub,
                    "sub" => ua - ub,
                    "mul" => ua * ub,
                    "and" => ua & ub,
                    "or" => ua | ub,
                    "xor" => ua ^ ub,
                    "shl" => ua << sh,
                    "shr_u" => ua >> sh,
                    "shr_s" => unchecked((ulong)(sa >> sh)),
                    "div_u" => ub == 0 ? null : ua / ub,
                    "rem_u" => ub == 0 ? null : ua % ub,
                    "div_s" => sb == 0 || (sb == -1 && sa == (t == 'i' ? int.MinValue : long.MinValue)) ? null : unchecked((ulong)(sa / sb)),
                    "rem_s" => sb == 0 ? null : sb == -1 ? 0UL : unchecked((ulong)(sa % sb)),
                    _ => null,
                };
                if (r is { } v) { return (t, v & mask); }
                bool? cmp = name switch
                {
                    "eq" => ua == ub,
                    "ne" => ua != ub,
                    "lt_s" => sa < sb,
                    "lt_u" => ua < ub,
                    "gt_s" => sa > sb,
                    "gt_u" => ua > ub,
                    "le_s" => sa <= sb,
                    "le_u" => ua <= ub,
                    "ge_s" => sa >= sb,
                    "ge_u" => ua >= ub,
                    _ => null,
                };
                return cmp is { } c ? ('i', c ? 1UL : 0) : null;
            }
            double x = t == 'f' ? F32(a) : F64(a);
            double y = t == 'f' ? F32(b) : F64(b);
            double? fr = name switch
            {
                "add" => x + y,
                "sub" => x - y,
                "mul" => x * y,
                "div" => x / y,
                _ => null,
            };
            if (fr is { } f)
            {
                // An f32 operation rounds to single precision: compute in float.
                if (t == 'f')
                {
                    float fx = (float)x, fy = (float)y;
                    float r32 = name switch { "add" => fx + fy, "sub" => fx - fy, "mul" => fx * fy, _ => fx / fy };
                    return ('f', B32(r32));
                }
                return ('F', B64(f));
            }
            bool? fc = name switch
            {
                "eq" => x == y,
                "ne" => x != y,
                "lt" => x < y,
                "gt" => x > y,
                "le" => x <= y,
                "ge" => x >= y,
                _ => null,
            };
            return fc is { } yes ? ('i', yes ? 1UL : 0) : null;
        }
    }
}
