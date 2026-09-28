#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// The CPython <b>Limited API (abi3)</b> behind dotcc's synthetic <c>&lt;Python.h&gt;</c>:
/// "pretend to be CPython" so a C extension module (a <c>PyInit_&lt;name&gt;</c>, its
/// <c>PyMethodDef</c> table, <c>PyArg_ParseTuple</c> / <c>Py_BuildValue</c>, the error
/// indicator, …) compiles with dotcc and runs on .NET against a managed object model
/// instead of libpython (docs/FRONTEND-IDEAS.md #1).
/// </summary>
/// <remarks>
/// <para><b>Handles, not addresses.</b> A <c>PyObject*</c> is an opaque handle into a
/// managed table (slot index + 1, shifted left 4; non-NULL, never dereferenced). This is
/// what the Limited API buys: <c>PyObject</c> is opaque and <c>Py_INCREF</c> is a
/// function call, so no extension depends on an object layout. <see cref="_object"/> is
/// the empty runtime struct <c>struct _object</c> resolves to (the <c>struct tm</c>
/// pattern: declared here, not in the header).</para>
/// <para><b>Reference counting is real.</b> Every object carries a refcount; new vs.
/// borrowed vs. stolen references follow the CPython documentation per function, and an
/// object is released (its slot recycled, its children decref'd) when the count reaches
/// zero. Singletons and built-in type objects are immortal. There is no cycle collector,
/// so a reference cycle leaks, as does a module and its functions (each function holds
/// its module, as in CPython; extension modules are never unloaded there either).</para>
/// <para><b>Values.</b> <c>int</c> is a 64-bit <c>long</c> (no arbitrary precision);
/// <c>float</c> a <c>double</c>; <c>str</c> a .NET <c>string</c> (UTF-8 at the C boundary,
/// cached per object for <c>PyUnicode_AsUTF8*</c> and the <c>s</c> format); tuples,
/// lists and dicts own their items. Dict keys must be <c>None</c>/bool/int/float/str, a
/// tuple of those, or an identity-hashed object (module, type, function).</para>
/// <para><b>Scope.</b> Single-phase init (<c>PyModule_Create</c>), calling conventions
/// <c>METH_VARARGS</c> (± <c>METH_KEYWORDS</c>), <c>METH_NOARGS</c>, <c>METH_O</c>. No
/// <c>METH_FASTCALL</c>, multi-phase init, heap types, bytes/buffer protocol, or GIL;
/// the shim is single-threaded (the error indicator is per-thread, like CPython's).</para>
/// <para><b>Hosting.</b> <see cref="PyHost"/> is the managed side: import a module from
/// its <c>PyInit_*</c> function pointer and call into it with .NET values. This is the seam a
/// managed Python runtime (IronPython or a dotcc Python front-end) would sit on.</para>
/// <para>The header's <c>PyModuleDef</c>/<c>PyMethodDef</c> bodies are read through
/// <see cref="PyModuleDefView"/>/<see cref="PyMethodDefView"/>; keep those layouts in
/// sync with <c>DotCC.Lib/include/Python.h</c>.</para>
/// </remarks>
public static unsafe partial class Libc
{
    /// <summary>The opaque <c>struct _object</c> (<c>PyObject</c>). Never dereferenced: a
    /// <c>PyObject*</c> is a handle into the shim's object table.</summary>
#pragma warning disable CS8981
    public struct _object { }
#pragma warning restore CS8981

    // Layout mirrors of the header's PyMethodDef / PyModuleDef (LP64, 8-byte fields).
    [StructLayout(LayoutKind.Sequential)]
    private struct PyMethodDefView
    {
        public byte* ml_name;
        public void* ml_meth;
        public int ml_flags;
        public byte* ml_doc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PyModuleDefView
    {
        public long ob_refcnt;
        public void* m_init;
        public long m_index;
        public void* m_copy;
        public byte* m_name;
        public byte* m_doc;
        public long m_size;
        public PyMethodDefView* m_methods;
        public void* m_slots;
        public void* m_traverse;
        public void* m_clear;
        public void* m_free;
    }

    /// <summary>The object model: handle table, refcounts, the error indicator, the
    /// built-in type objects, and the conversions/protocols the C-API entry points share.</summary>
    private static class PyRt
    {
        internal const int METH_VARARGS = 0x1, METH_KEYWORDS = 0x2, METH_NOARGS = 0x4, METH_O = 0x8;
        internal const long Immortal = long.MaxValue / 2;

        internal sealed class Obj(object v)
        {
            public object V = v;
            public long Refs;
            public int Slot;
            public byte* Utf8;
            public long Utf8Len;
        }

        // ---- value kinds (besides bool / long / double / string) ----
        internal sealed class NoneV
        {
            public readonly string Name;
            public nint Type;
            public NoneV(string name) => Name = name;
        }

        internal sealed class TupleV
        {
            public nint[] Items;
            public TupleV(nint[] items) => Items = items;
        }

        internal sealed class ListV
        {
            public readonly List<nint> Items = new();
        }

        internal sealed class DictV
        {
            public readonly List<nint> Keys = new();
            public readonly List<nint> Vals = new();
            public readonly Dictionary<object, int> Index = new();
        }

        internal sealed class ModuleV
        {
            public string Name = "";
            public nint Dict;
            public void* State;
        }

        internal sealed class CFuncV
        {
            public string Name = "";
            public string? Doc;
            public void* Meth;
            public int Flags;
            public nint Self;
        }

        internal sealed class TypeV
        {
            public string Name = "";
            public string Module = "builtins";
            public nint Base;
            public bool IsException;
        }

        internal sealed class ExcV
        {
            public nint Type;
            public nint Args;
        }

        internal sealed class TupleKey : IEquatable<TupleKey>
        {
            private readonly object[] _items;
            public TupleKey(object[] items) => _items = items;

            public bool Equals(TupleKey? other)
            {
                if (other is null || other._items.Length != _items.Length) { return false; }
                for (int i = 0; i < _items.Length; i++)
                {
                    if (!_items[i].Equals(other._items[i])) { return false; }
                }
                return true;
            }

            public override bool Equals(object? obj) => Equals(obj as TupleKey);

            public override int GetHashCode()
            {
                var h = new HashCode();
                foreach (var item in _items) { h.Add(item); }
                return h.ToHashCode();
            }
        }

        internal static readonly List<Obj?> Heap = new();
        internal static readonly Stack<int> FreeSlots = new();
        internal static int Live;

        /// <summary>The raised exception (an owned instance handle), or 0.</summary>
        [ThreadStatic] internal static nint Err;

        internal static readonly nint ObjectType, TypeType, NoneType, BoolType, IntType, FloatType,
            StrType, TupleType, ListType, DictType, ModuleType, CFuncType, EllipsisType, NotImplType;
        internal static readonly nint None, True, False, Ellipsis, NotImplemented, Zero, One, EmptyStr, EmptyTuple;
        internal static readonly nint BaseException, Exception, ArithmeticError, LookupError, AttributeError,
            IndexError, KeyError, MemoryError, NotImplementedError, OverflowError, RuntimeError, SystemError,
            TypeError, ValueError, ZeroDivisionError;

        static PyRt()
        {
            ObjectType = NewType("object", 0);
            TypeType = NewType("type", ObjectType);
            NoneType = NewType("NoneType", ObjectType);
            IntType = NewType("int", ObjectType);
            BoolType = NewType("bool", IntType);
            FloatType = NewType("float", ObjectType);
            StrType = NewType("str", ObjectType);
            TupleType = NewType("tuple", ObjectType);
            ListType = NewType("list", ObjectType);
            DictType = NewType("dict", ObjectType);
            ModuleType = NewType("module", ObjectType);
            CFuncType = NewType("builtin_function_or_method", ObjectType);
            EllipsisType = NewType("ellipsis", ObjectType);
            NotImplType = NewType("NotImplementedType", ObjectType);

            None = NewImmortal(new NoneV("None") { Type = NoneType });
            Ellipsis = NewImmortal(new NoneV("Ellipsis") { Type = EllipsisType });
            NotImplemented = NewImmortal(new NoneV("NotImplemented") { Type = NotImplType });
            True = NewImmortal(true);
            False = NewImmortal(false);
            Zero = NewImmortal(0L);
            One = NewImmortal(1L);
            EmptyStr = NewImmortal("");
            EmptyTuple = NewImmortal(new TupleV(Array.Empty<nint>()));

            BaseException = NewType("BaseException", ObjectType, isException: true);
            Exception = NewType("Exception", BaseException, isException: true);
            ArithmeticError = NewType("ArithmeticError", Exception, isException: true);
            LookupError = NewType("LookupError", Exception, isException: true);
            AttributeError = NewType("AttributeError", Exception, isException: true);
            IndexError = NewType("IndexError", LookupError, isException: true);
            KeyError = NewType("KeyError", LookupError, isException: true);
            MemoryError = NewType("MemoryError", Exception, isException: true);
            RuntimeError = NewType("RuntimeError", Exception, isException: true);
            NotImplementedError = NewType("NotImplementedError", RuntimeError, isException: true);
            OverflowError = NewType("OverflowError", ArithmeticError, isException: true);
            SystemError = NewType("SystemError", Exception, isException: true);
            TypeError = NewType("TypeError", Exception, isException: true);
            ValueError = NewType("ValueError", Exception, isException: true);
            ZeroDivisionError = NewType("ZeroDivisionError", ArithmeticError, isException: true);
        }

        // ---- handle table ----------------------------------------------------

        internal static nint New(object v)
        {
            var o = new Obj(v) { Refs = 1 };
            int slot;
            if (FreeSlots.Count > 0)
            {
                slot = FreeSlots.Pop();
                Heap[slot] = o;
            }
            else
            {
                slot = Heap.Count;
                Heap.Add(o);
            }
            o.Slot = slot;
            Live++;
            return ((nint)slot + 1) << 4;
        }

        private static nint NewImmortal(object v)
        {
            var h = New(v);
            Get(h).Refs = Immortal;
            Live--;
            return h;
        }

        private static nint NewType(string name, nint baseType, bool isException = false)
            => NewImmortal(new TypeV { Name = name, Base = baseType, IsException = isException });

        internal static Obj Get(nint h)
        {
            long slot = ((long)h >> 4) - 1;
            if ((h & 0xF) != 0 || slot < 0 || slot >= Heap.Count || Heap[(int)slot] is not { } o)
            {
                throw new InvalidOperationException(
                    $"dotcc Python shim: 0x{(long)h:x} is not a live PyObject handle (NULL, freed, or not a PyObject*)");
            }
            return o;
        }

        internal static object V(nint h) => Get(h).V;

        internal static void IncRef(nint h)
        {
            if (h == 0) { return; }
            var o = Get(h);
            if (o.Refs < Immortal) { o.Refs++; }
        }

        internal static void DecRef(nint h)
        {
            if (h == 0) { return; }
            var o = Get(h);
            if (o.Refs >= Immortal) { return; }
            if (--o.Refs > 0) { return; }
            Heap[o.Slot] = null;
            FreeSlots.Push(o.Slot);
            Live--;
            if (o.Utf8 != null)
            {
                NativeMemory.Free(o.Utf8);
                o.Utf8 = null;
            }
            switch (o.V)
            {
                case TupleV t:
                    foreach (var item in t.Items) { DecRef(item); }
                    break;
                case ListV l:
                    foreach (var item in l.Items) { DecRef(item); }
                    break;
                case DictV d:
                    for (int i = 0; i < d.Keys.Count; i++)
                    {
                        DecRef(d.Keys[i]);
                        DecRef(d.Vals[i]);
                    }
                    break;
                case ModuleV m:
                    DecRef(m.Dict);
                    if (m.State != null) { NativeMemory.Free(m.State); }
                    break;
                case CFuncV f:
                    DecRef(f.Self);
                    break;
                case TypeV ty:
                    DecRef(ty.Base);
                    break;
                case ExcV e:
                    DecRef(e.Type);
                    DecRef(e.Args);
                    break;
            }
        }

        internal static nint NewRef(nint h)
        {
            IncRef(h);
            return h;
        }

        internal static nint NewStr(string s) => s.Length == 0 ? EmptyStr : New(s);

        internal static nint NewTuple(params nint[] items) => items.Length == 0 ? EmptyTuple : New(new TupleV(items));

        internal static nint Bool(bool b) => b ? True : False;

        // ---- errors ----------------------------------------------------------

        /// <summary>Install <paramref name="exc"/> (stolen) as the raised exception.</summary>
        internal static void SetErr(nint exc)
        {
            var old = Err;
            Err = exc;
            DecRef(old);
        }

        internal static nint NewExc(nint type, nint args)
        {
            IncRef(type);
            return New(new ExcV { Type = type, Args = args });
        }

        /// <summary>Raise <paramref name="type"/>(<paramref name="msg"/>); returns 0 (NULL)
        /// so an entry point can <c>return Raise(…)</c>.</summary>
        internal static nint Raise(nint type, string msg)
        {
            SetErr(NewExc(type, NewTuple(NewStr(msg))));
            return 0;
        }

        internal static nint BadInternalCall() => Raise(SystemError, "bad argument to internal function");

        internal static nint ErrType() => Err == 0 ? 0 : ((ExcV)V(Err)).Type;

        // ---- types -----------------------------------------------------------

        internal static nint TypeOf(nint h) => V(h) switch
        {
            NoneV n => n.Type,
            bool => BoolType,
            long => IntType,
            double => FloatType,
            string => StrType,
            TupleV => TupleType,
            ListV => ListType,
            DictV => DictType,
            ModuleV => ModuleType,
            CFuncV => CFuncType,
            TypeV => TypeType,
            ExcV e => e.Type,
            _ => ObjectType,
        };

        internal static string TypeName(nint h) => ((TypeV)V(TypeOf(h))).Name;

        internal static string QualifiedName(TypeV ty) => ty.Module == "builtins" ? ty.Name : ty.Module + "." + ty.Name;

        internal static bool IsSubtype(nint a, nint b)
        {
            while (a != 0)
            {
                if (a == b) { return true; }
                a = V(a) is TypeV ty ? ty.Base : 0;
            }
            return false;
        }

        internal static bool IsInstance(nint obj, nint type) => IsSubtype(TypeOf(obj), type);

        internal static bool IsExceptionType(nint h) => h != 0 && V(h) is TypeV { IsException: true };

        // ---- strings ---------------------------------------------------------

        internal static string FromC(byte* p)
            => p == null ? "" : Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(p));

        internal static byte* Utf8(Obj o, out long len)
        {
            if (o.Utf8 == null)
            {
                var s = (string)o.V;
                int n = Encoding.UTF8.GetByteCount(s);
                var buf = (byte*)NativeMemory.Alloc((nuint)n + 1);
                Encoding.UTF8.GetBytes(s, new Span<byte>(buf, n));
                buf[n] = 0;
                o.Utf8 = buf;
                o.Utf8Len = n;
            }
            len = o.Utf8Len;
            return o.Utf8;
        }

        internal static string Repr(nint h)
        {
            switch (V(h))
            {
                case NoneV n: return n.Name;
                case bool b: return b ? "True" : "False";
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case double d: return FloatRepr(d);
                case string s: return StrRepr(s);
                case TupleV t: return t.Items.Length == 1 ? "(" + ItemRepr(t.Items[0]) + ",)" : "(" + JoinRepr(t.Items) + ")";
                case ListV l: return "[" + JoinRepr(l.Items) + "]";
                case DictV d:
                {
                    var sb = new StringBuilder("{");
                    for (int i = 0; i < d.Keys.Count; i++)
                    {
                        if (i > 0) { sb.Append(", "); }
                        sb.Append(Repr(d.Keys[i])).Append(": ").Append(Repr(d.Vals[i]));
                    }
                    return sb.Append('}').ToString();
                }
                case ModuleV m: return $"<module '{m.Name}'>";
                case CFuncV f: return $"<built-in function {f.Name}>";
                case TypeV ty: return $"<class '{QualifiedName(ty)}'>";
                case ExcV e:
                {
                    var name = ((TypeV)V(e.Type)).Name;
                    var args = ((TupleV)V(e.Args)).Items;
                    return args.Length == 1 ? name + "(" + Repr(args[0]) + ")" : name + Repr(e.Args);
                }
                default: return "<object>";
            }
        }

        private static string ItemRepr(nint h) => h == 0 ? "<NULL>" : Repr(h);

        private static string JoinRepr(IEnumerable<nint> items)
        {
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                if (sb.Length > 0) { sb.Append(", "); }
                sb.Append(ItemRepr(item));
            }
            return sb.ToString();
        }

        internal static string ToStr(nint h)
        {
            switch (V(h))
            {
                case string s: return s;
                case ExcV e:
                {
                    var args = ((TupleV)V(e.Args)).Items;
                    if (args.Length == 0) { return ""; }
                    if (args.Length > 1) { return Repr(e.Args); }
                    // KeyError's str is the repr of its key, like CPython's.
                    return IsSubtype(e.Type, KeyError) ? Repr(args[0]) : ToStr(args[0]);
                }
                default: return Repr(h);
            }
        }

        internal static string StrRepr(string s)
        {
            char q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
            var sb = new StringBuilder(s.Length + 2);
            sb.Append(q);
            foreach (char c in s)
            {
                if (c == q || c == '\\') { sb.Append('\\').Append(c); }
                else if (c == '\n') { sb.Append("\\n"); }
                else if (c == '\r') { sb.Append("\\r"); }
                else if (c == '\t') { sb.Append("\\t"); }
                else if (c < 0x20 || c == 0x7f) { sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture)); }
                else { sb.Append(c); }
            }
            return sb.Append(q).ToString();
        }

        /// <summary>Python's <c>repr(float)</c>: the shortest round-tripping digits, fixed
        /// notation for decimal exponents in (-4, 16], scientific otherwise.</summary>
        internal static string FloatRepr(double d)
        {
            if (double.IsNaN(d)) { return "nan"; }
            if (double.IsInfinity(d)) { return d > 0 ? "inf" : "-inf"; }
            if (d == 0) { return double.IsNegative(d) ? "-0.0" : "0.0"; }
            string r = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
            int exp = 0;
            string mant = r;
            int e = r.IndexOf('E');
            if (e >= 0)
            {
                exp = int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                mant = r[..e];
            }
            int dot = mant.IndexOf('.');
            string digits = dot >= 0 ? mant.Remove(dot, 1) : mant;
            int decpt = (dot >= 0 ? dot : mant.Length) + exp;
            int lead = 0;
            while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; }
            digits = digits[lead..].TrimEnd('0');
            decpt -= lead;
            if (digits.Length == 0) { digits = "0"; }
            string body;
            if (decpt > -4 && decpt <= 16)
            {
                if (decpt <= 0) { body = "0." + new string('0', -decpt) + digits; }
                else if (decpt >= digits.Length) { body = digits + new string('0', decpt - digits.Length) + ".0"; }
                else { body = digits[..decpt] + "." + digits[decpt..]; }
            }
            else
            {
                int x = decpt - 1;
                body = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "")
                    + "e" + (x < 0 ? "-" : "+") + Math.Abs(x).ToString("00", CultureInfo.InvariantCulture);
            }
            return (d < 0 ? "-" : "") + body;
        }

        // ---- numbers / truth -------------------------------------------------

        /// <summary>The index protocol behind <c>PyLong_AsLong</c>: bool and int convert;
        /// anything else raises TypeError. False (error set) on failure.</summary>
        internal static bool AsLong(nint h, out long value)
        {
            value = -1;
            if (h == 0) { BadInternalCall(); return false; }
            switch (V(h))
            {
                case bool b: value = b ? 1 : 0; return true;
                case long l: value = l; return true;
                default:
                    Raise(TypeError, $"'{TypeName(h)}' object cannot be interpreted as an integer");
                    return false;
            }
        }

        internal static bool AsDouble(nint h, out double value)
        {
            value = -1.0;
            if (h == 0) { BadInternalCall(); return false; }
            switch (V(h))
            {
                case double d: value = d; return true;
                case long l: value = l; return true;
                case bool b: value = b ? 1 : 0; return true;
                default:
                    Raise(TypeError, $"must be real number, not {TypeName(h)}");
                    return false;
            }
        }

        internal static bool IsTrue(nint h) => V(h) switch
        {
            NoneV n => n.Name != "None",
            bool b => b,
            long l => l != 0,
            double d => d != 0,
            string s => s.Length != 0,
            TupleV t => t.Items.Length != 0,
            ListV l => l.Items.Count != 0,
            DictV d => d.Keys.Count != 0,
            _ => true,
        };

        internal static long Length(nint h)
        {
            switch (V(h))
            {
                case string s:
                {
                    long n = 0;
                    foreach (var _ in s.EnumerateRunes()) { n++; }
                    return n;
                }
                case TupleV t: return t.Items.Length;
                case ListV l: return l.Items.Count;
                case DictV d: return d.Keys.Count;
                default:
                    Raise(TypeError, $"object of type '{TypeName(h)}' has no len()");
                    return -1;
            }
        }

        // ---- dicts -----------------------------------------------------------

        /// <summary>A structural key for dict lookup (Python equality: 1 == 1.0 == True).
        /// False + TypeError set for an unhashable value.</summary>
        internal static bool TryKey(nint h, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? key)
        {
            switch (V(h))
            {
                case bool b: key = b ? 1L : 0L; return true;
                case long l: key = l; return true;
                case double d:
                    key = d == Math.Floor(d) && d >= -9.2e18 && d <= 9.2e18 ? (object)(long)d : d;
                    return true;
                case string s: key = s; return true;
                case NoneV n: key = n; return true;
                case TupleV t:
                {
                    var parts = new object[t.Items.Length];
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (!TryKey(t.Items[i], out var part)) { key = null; return false; }
                        parts[i] = part;
                    }
                    key = new TupleKey(parts);
                    return true;
                }
                case ListV or DictV:
                    Raise(TypeError, $"unhashable type: '{TypeName(h)}'");
                    key = null;
                    return false;
                default:
                    key = Get(h); // identity hash (modules, types, functions, exceptions)
                    return true;
            }
        }

        /// <summary>d[key] = val, both borrowed (the dict takes its own references).</summary>
        internal static bool DictSet(nint dict, nint key, nint val)
        {
            var d = (DictV)V(dict);
            if (!TryKey(key, out var k)) { return false; }
            IncRef(val);
            if (d.Index.TryGetValue(k, out int at))
            {
                DecRef(d.Vals[at]);
                d.Vals[at] = val;
            }
            else
            {
                IncRef(key);
                d.Index[k] = d.Keys.Count;
                d.Keys.Add(key);
                d.Vals.Add(val);
            }
            return true;
        }

        /// <summary>d[key] as a borrowed reference, or 0 when missing / unhashable (with
        /// the lookup error cleared, since PyDict_GetItem suppresses errors).</summary>
        internal static nint DictGet(nint dict, nint key)
        {
            var d = (DictV)V(dict);
            var saved = Err;
            Err = 0;
            if (!TryKey(key, out var k))
            {
                SetErr(0);
                Err = saved;
                return 0;
            }
            Err = saved;
            return d.Index.TryGetValue(k, out int at) ? d.Vals[at] : 0;
        }

        internal static nint DictGetString(nint dict, string key)
        {
            var d = (DictV)V(dict);
            return d.Index.TryGetValue(key, out int at) ? d.Vals[at] : 0;
        }

        internal static bool DictSetString(nint dict, string key, nint val)
        {
            var k = NewStr(key);
            bool ok = DictSet(dict, k, val);
            DecRef(k);
            return ok;
        }

        // ---- attributes / calls ---------------------------------------------

        /// <summary>getattr(o, name) as a new reference, or 0 + AttributeError.</summary>
        internal static nint GetAttr(nint h, string name)
        {
            switch (V(h))
            {
                case ModuleV m:
                {
                    var v = DictGetString(m.Dict, name);
                    return v != 0 ? NewRef(v) : Raise(AttributeError, $"module '{m.Name}' has no attribute '{name}'");
                }
                case TypeV ty:
                    return name switch
                    {
                        "__name__" or "__qualname__" => NewStr(ty.Name),
                        "__module__" => NewStr(ty.Module),
                        "__base__" => NewRef(ty.Base != 0 ? ty.Base : None),
                        _ => Raise(AttributeError, $"type object '{ty.Name}' has no attribute '{name}'"),
                    };
                case CFuncV f:
                    return name switch
                    {
                        "__name__" or "__qualname__" => NewStr(f.Name),
                        "__doc__" => f.Doc != null ? NewStr(f.Doc) : NewRef(None),
                        "__self__" => NewRef(f.Self != 0 ? f.Self : None),
                        _ => Raise(AttributeError, $"'builtin_function_or_method' object has no attribute '{name}'"),
                    };
                case ExcV e when name == "args":
                    return NewRef(e.Args);
                default:
                    return Raise(AttributeError, $"'{TypeName(h)}' object has no attribute '{name}'");
            }
        }

        /// <summary>callable(*args, **kwargs), with <paramref name="args"/> a tuple,
        /// <paramref name="kwargs"/> a dict or 0; both borrowed. New reference or 0.</summary>
        internal static nint Call(nint callable, nint args, nint kwargs)
        {
            if (callable == 0 || args == 0) { return BadInternalCall(); }
            if (V(args) is not TupleV argv) { return Raise(TypeError, "argument list must be a tuple"); }
            if (kwargs != 0 && V(kwargs) is not DictV) { return Raise(TypeError, "keyword list must be a dictionary"); }
            bool hasKw = kwargs != 0 && ((DictV)V(kwargs)).Keys.Count > 0;
            switch (V(callable))
            {
                case CFuncV f:
                    return CallCFunction(f, argv, args, hasKw ? kwargs : 0);
                case TypeV { IsException: true } ty:
                    if (hasKw) { return Raise(TypeError, $"{ty.Name}() takes no keyword arguments"); }
                    return NewExc(callable, NewRef(args));
                default:
                    return Raise(TypeError, $"'{TypeName(callable)}' object is not callable");
            }
        }

        /// <summary>CPython's <c>_PyObject_FunctionStr</c>: <c>module.name()</c>.</summary>
        private static string FuncStr(CFuncV f)
            => (f.Self != 0 && V(f.Self) is ModuleV m ? m.Name + "." : "") + f.Name + "()";

        private static nint CallCFunction(CFuncV f, TupleV argv, nint args, nint kwargs)
        {
            var self = (_object*)f.Self;
            _object* result;
            switch (f.Flags)
            {
                case METH_NOARGS:
                    if (kwargs != 0) { return Raise(TypeError, $"{FuncStr(f)} takes no keyword arguments"); }
                    if (argv.Items.Length != 0)
                    {
                        return Raise(TypeError, $"{FuncStr(f)} takes no arguments ({argv.Items.Length} given)");
                    }
                    result = ((delegate*<_object*, _object*, _object*>)f.Meth)(self, null);
                    break;
                case METH_O:
                    if (kwargs != 0) { return Raise(TypeError, $"{FuncStr(f)} takes no keyword arguments"); }
                    if (argv.Items.Length != 1)
                    {
                        return Raise(TypeError, $"{FuncStr(f)} takes exactly one argument ({argv.Items.Length} given)");
                    }
                    result = ((delegate*<_object*, _object*, _object*>)f.Meth)(self, (_object*)argv.Items[0]);
                    break;
                case METH_VARARGS:
                    if (kwargs != 0) { return Raise(TypeError, $"{f.Name}() takes no keyword arguments"); }
                    result = ((delegate*<_object*, _object*, _object*>)f.Meth)(self, (_object*)args);
                    break;
                case METH_VARARGS | METH_KEYWORDS:
                    result = ((delegate*<_object*, _object*, _object*, _object*>)f.Meth)(self, (_object*)args, (_object*)kwargs);
                    break;
                default:
                    return Raise(SystemError, $"{f.Name}() method: bad call flags");
            }
            var r = (nint)result;
            if (r == 0 && Err == 0)
            {
                return Raise(SystemError, $"<built-in function {f.Name}> returned NULL without setting an exception");
            }
            if (r != 0 && Err != 0)
            {
                DecRef(r);
                SetErr(0);
                return Raise(SystemError, $"<built-in function {f.Name}> returned a result with an exception set");
            }
            return r;
        }

        // ---- PyArg_Parse* ----------------------------------------------------

        private struct Unit
        {
            public char Code;
            public bool Hash;   // s# / z#
            public bool Bang;   // O!
        }

        private sealed class Format
        {
            public readonly List<Unit> Units = new();
            public int Min = -1;         // units before '|'
            public int KwOnly = -1;      // units before '$'
            public string? FName;        // after ':'
            public string? Custom;       // after ';'
            public string Fn => FName != null ? FName + "()" : "function";
            public string Prefix => FName != null ? FName + "() " : "";
        }

        private static Format? ParseFormat(string fmt)
        {
            var f = new Format();
            for (int i = 0; i < fmt.Length; i++)
            {
                char c = fmt[i];
                switch (c)
                {
                    case '|': f.Min = f.Units.Count; continue;
                    case '$': f.KwOnly = f.Units.Count; if (f.Min < 0) { f.Min = f.Units.Count; } continue;
                    case ':': f.FName = fmt[(i + 1)..]; i = fmt.Length; continue;
                    case ';': f.Custom = fmt[(i + 1)..]; i = fmt.Length; continue;
                    case ' ' or '\t': continue;
                }
                if ("ihblLnIHBkKdfpszUO".IndexOf(c) < 0)
                {
                    Raise(SystemError, $"dotcc Python shim: unsupported PyArg format unit '{c}' in \"{fmt}\"");
                    return null;
                }
                var u = new Unit { Code = c };
                if (i + 1 < fmt.Length && fmt[i + 1] == '#' && c is 's' or 'z') { u.Hash = true; i++; }
                else if (i + 1 < fmt.Length && fmt[i + 1] == '!' && c == 'O') { u.Bang = true; i++; }
                f.Units.Add(u);
            }
            if (f.Min < 0) { f.Min = f.Units.Count; }
            return f;
        }

        private static void* NextPtr(VaArg[] va, ref int vi)
            => vi < va.Length ? (void*)va[vi++] : throw new InvalidOperationException(
                "dotcc Python shim: fewer variadic arguments than the format string needs");

        private static int Slots(Unit u) => u.Hash || u.Bang ? 2 : 1;

        /// <summary>Convert one argument into the C out-pointer(s). Returns null on success;
        /// "" when a Python error is already set; otherwise the expected-type phrase for a
        /// "must be X, not Y" TypeError.</summary>
        private static string? ConvertArg(nint item, Unit u, VaArg[] va, ref int vi)
        {
            long l;
            switch (u.Code)
            {
                case 'i':
                    if (!AsLong(item, out l)) { vi++; return ""; }
                    if (l > int.MaxValue) { vi++; Raise(OverflowError, "signed integer is greater than maximum"); return ""; }
                    if (l < int.MinValue) { vi++; Raise(OverflowError, "signed integer is less than minimum"); return ""; }
                    *(int*)NextPtr(va, ref vi) = (int)l;
                    return null;
                case 'h':
                    if (!AsLong(item, out l)) { vi++; return ""; }
                    if (l > short.MaxValue) { vi++; Raise(OverflowError, "signed short integer is greater than maximum"); return ""; }
                    if (l < short.MinValue) { vi++; Raise(OverflowError, "signed short integer is less than minimum"); return ""; }
                    *(short*)NextPtr(va, ref vi) = (short)l;
                    return null;
                case 'b':
                    if (!AsLong(item, out l)) { vi++; return ""; }
                    if (l > byte.MaxValue) { vi++; Raise(OverflowError, "unsigned byte integer is greater than maximum"); return ""; }
                    if (l < 0) { vi++; Raise(OverflowError, "unsigned byte integer is less than minimum"); return ""; }
                    *(byte*)NextPtr(va, ref vi) = (byte)l;
                    return null;
                case 'B':
                    if (!AsLong(item, out l)) { vi++; return ""; }
                    *(byte*)NextPtr(va, ref vi) = unchecked((byte)l);
                    return null;
                case 'H':
                    if (!AsLong(item, out l)) { vi++; return ""; }
                    *(ushort*)NextPtr(va, ref vi) = unchecked((ushort)l);
                    return null;
                case 'I':
                    if (!AsLong(item, out l)) { vi++; return ""; }
                    *(uint*)NextPtr(va, ref vi) = unchecked((uint)l);
                    return null;
                case 'l' or 'L' or 'n':
                    if (!AsLong(item, out l)) { vi++; return ""; }
                    *(long*)NextPtr(va, ref vi) = l;
                    return null;
                case 'k' or 'K':
                    if (V(item) is not (long or bool)) { vi++; return "int"; }
                    AsLong(item, out l);
                    *(ulong*)NextPtr(va, ref vi) = unchecked((ulong)l);
                    return null;
                case 'd' or 'f':
                {
                    if (!AsDouble(item, out double d)) { vi++; return ""; }
                    if (u.Code == 'd') { *(double*)NextPtr(va, ref vi) = d; }
                    else { *(float*)NextPtr(va, ref vi) = (float)d; }
                    return null;
                }
                case 'p':
                    *(int*)NextPtr(va, ref vi) = IsTrue(item) ? 1 : 0;
                    return null;
                case 's' or 'z':
                {
                    if (u.Code == 'z' && item == None)
                    {
                        *(byte**)NextPtr(va, ref vi) = null;
                        if (u.Hash) { *(long*)NextPtr(va, ref vi) = 0; }
                        return null;
                    }
                    if (V(item) is not string s)
                    {
                        vi += Slots(u);
                        return u.Code == 'z' ? "str or None" : "str";
                    }
                    if (!u.Hash && s.Contains('\0'))
                    {
                        vi++;
                        Raise(ValueError, "embedded null character");
                        return "";
                    }
                    *(byte**)NextPtr(va, ref vi) = Utf8(Get(item), out long len);
                    if (u.Hash) { *(long*)NextPtr(va, ref vi) = len; }
                    return null;
                }
                case 'U':
                    if (V(item) is not string) { vi++; return "str"; }
                    *(_object**)NextPtr(va, ref vi) = (_object*)item;
                    return null;
                case 'O':
                    if (u.Bang)
                    {
                        var type = (nint)NextPtr(va, ref vi);
                        if (!IsInstance(item, type)) { vi++; return ((TypeV)V(type)).Name; }
                    }
                    *(_object**)NextPtr(va, ref vi) = (_object*)item;
                    return null;
                default:
                    throw new InvalidOperationException($"unreachable format unit '{u.Code}'");
            }
        }

        /// <summary>Run one conversion and turn a "must be X" answer into the TypeError.</summary>
        private static bool Convert(nint item, Unit u, VaArg[] va, ref int vi, Format f, string argLabel)
        {
            var expected = ConvertArg(item, u, va, ref vi);
            if (expected == null) { return true; }
            if (expected.Length != 0)
            {
                Raise(TypeError, f.Custom ?? $"{f.Prefix}{argLabel} must be {expected}, not {TypeName(item)}");
            }
            return false;
        }

        internal static int ParseTuple(nint args, string format, VaArg[] va)
        {
            if (args == 0) { BadInternalCall(); return 0; }
            var f = ParseFormat(format);
            if (f == null) { return 0; }
            if (V(args) is not TupleV t)
            {
                Raise(SystemError, "new style getargs format but argument is not a tuple");
                return 0;
            }
            int n = t.Items.Length, min = f.Min, max = f.Units.Count;
            if (n < min || n > max)
            {
                int bound = n < min ? min : max;
                Raise(TypeError, f.Custom ?? $"{f.Fn} takes "
                    + $"{(min == max ? "exactly" : n < min ? "at least" : "at most")} {bound} argument{(bound == 1 ? "" : "s")} ({n} given)");
                return 0;
            }
            int vi = 0;
            for (int i = 0; i < f.Units.Count; i++)
            {
                if (i >= n)
                {
                    vi += Slots(f.Units[i]);
                    continue;
                }
                if (!Convert(t.Items[i], f.Units[i], va, ref vi, f, $"argument {i + 1}")) { return 0; }
            }
            return 1;
        }

        internal static int ParseTupleAndKeywords(nint args, nint kw, string format, byte** keywords, VaArg[] va)
        {
            if (args == 0 || keywords == null) { BadInternalCall(); return 0; }
            var f = ParseFormat(format);
            if (f == null) { return 0; }
            if (V(args) is not TupleV t)
            {
                Raise(SystemError, "new style getargs format but argument is not a tuple");
                return 0;
            }
            if (kw != 0 && V(kw) is not DictV) { BadInternalCall(); return 0; }
            var names = new List<string>();
            for (byte** k = keywords; *k != null; k++) { names.Add(FromC(*k)); }
            if (names.Count != f.Units.Count)
            {
                Raise(SystemError, $"more keyword list entries ({names.Count}) than format specifiers ({f.Units.Count})");
                return 0;
            }
            var kwd = kw != 0 ? (DictV)V(kw) : null;
            int n = t.Items.Length, nkw = kwd?.Keys.Count ?? 0;
            int posMax = f.KwOnly >= 0 ? f.KwOnly : f.Units.Count;
            if (n > posMax)
            {
                Raise(TypeError, f.Custom ?? $"{f.Fn} takes at most {posMax} {(f.KwOnly >= 0 ? "positional " : "")}"
                    + $"argument{(posMax == 1 ? "" : "s")} ({n} given)");
                return 0;
            }
            if (n + nkw > f.Units.Count)
            {
                Raise(TypeError, f.Custom ?? $"{f.Fn} takes at most {f.Units.Count} argument{(f.Units.Count == 1 ? "" : "s")} ({n + nkw} given)");
                return 0;
            }
            int vi = 0;
            for (int i = 0; i < f.Units.Count; i++)
            {
                nint byName = kwd != null ? DictGetString(kw, names[i]) : 0;
                if (i < n && byName != 0)
                {
                    Raise(TypeError, $"argument for {f.Fn} given by name ('{names[i]}') and position ({i + 1})");
                    return 0;
                }
                nint item = i < n ? t.Items[i] : byName;
                if (item == 0)
                {
                    if (i < f.Min)
                    {
                        Raise(TypeError, f.Custom ?? $"{f.Fn} missing required argument '{names[i]}' (pos {i + 1})");
                        return 0;
                    }
                    vi += Slots(f.Units[i]);
                    continue;
                }
                var label = i < n ? $"argument {i + 1}" : $"argument '{names[i]}'";
                if (!Convert(item, f.Units[i], va, ref vi, f, label)) { return 0; }
            }
            if (kwd != null)
            {
                foreach (var key in kwd.Keys)
                {
                    if (V(key) is not string ks)
                    {
                        Raise(TypeError, "keywords must be strings");
                        return 0;
                    }
                    if (!names.Contains(ks))
                    {
                        Raise(TypeError, $"{f.Fn} got an unexpected keyword argument '{ks}'");
                        return 0;
                    }
                }
            }
            return 1;
        }

        // ---- Py_BuildValue ----------------------------------------------------

        internal static nint Build(string fmt, VaArg[] va)
        {
            int pos = 0, vi = 0;
            var items = new List<nint>();
            if (!BuildSeq(fmt, ref pos, '\0', va, ref vi, items))
            {
                foreach (var item in items) { DecRef(item); }
                return 0;
            }
            return items.Count switch
            {
                0 => NewRef(None),
                1 => items[0],
                _ => NewTuple(items.ToArray()),
            };
        }

        private static bool BuildSeq(string f, ref int pos, char end, VaArg[] va, ref int vi, List<nint> items)
        {
            while (pos < f.Length)
            {
                char c = f[pos];
                if (c == end) { pos++; return true; }
                if (c is ' ' or '\t' or ',' or ':') { pos++; continue; }
                var item = BuildOne(f, ref pos, va, ref vi);
                if (item == 0) { return false; }
                items.Add(item);
            }
            if (end != '\0')
            {
                Raise(SystemError, "unmatched paren in format");
                return false;
            }
            return true;
        }

        private static nint BuildOne(string f, ref int pos, VaArg[] va, ref int vi)
        {
            char c = f[pos++];
            VaArg Next(ref int i) => i < va.Length ? va[i++] : throw new InvalidOperationException(
                "dotcc Python shim: fewer variadic arguments than the Py_BuildValue format needs");
            switch (c)
            {
                case '(' or '[' or '{':
                {
                    var inner = new List<nint>();
                    char end = c == '(' ? ')' : c == '[' ? ']' : '}';
                    if (!BuildSeq(f, ref pos, end, va, ref vi, inner))
                    {
                        foreach (var item in inner) { DecRef(item); }
                        return 0;
                    }
                    if (c == '(') { return NewTuple(inner.ToArray()); }
                    if (c == '[')
                    {
                        var list = new ListV();
                        list.Items.AddRange(inner);
                        return New(list);
                    }
                    if (inner.Count % 2 != 0)
                    {
                        foreach (var item in inner) { DecRef(item); }
                        return Raise(SystemError, "Bad dict format");
                    }
                    var dict = New(new DictV());
                    bool ok = true;
                    for (int i = 0; i < inner.Count; i += 2)
                    {
                        ok = ok && DictSet(dict, inner[i], inner[i + 1]);
                    }
                    foreach (var item in inner) { DecRef(item); }
                    if (!ok) { DecRef(dict); return 0; }
                    return dict;
                }
                case 'i' or 'b' or 'h' or 'B' or 'H':
                    return New((long)(int)Next(ref vi));
                case 'I':
                    return New((long)(uint)Next(ref vi));
                case 'l' or 'L' or 'n':
                    return New((long)Next(ref vi));
                case 'k' or 'K':
                {
                    ulong u = (ulong)Next(ref vi);
                    return u > long.MaxValue
                        ? Raise(OverflowError, "dotcc Python shim: int exceeds the 64-bit signed int model")
                        : New((long)u);
                }
                case 'd' or 'f':
                    return New((double)Next(ref vi));
                case 'C':
                    return NewStr(char.ConvertFromUtf32((int)Next(ref vi)));
                case 's' or 'z' or 'U':
                {
                    var p = (byte*)(void*)Next(ref vi);
                    long len = -1;
                    if (pos < f.Length && f[pos] == '#')
                    {
                        pos++;
                        len = (long)Next(ref vi);
                    }
                    if (p == null) { return NewRef(None); }
                    return NewStr(len < 0 ? FromC(p) : Encoding.UTF8.GetString(p, (int)len));
                }
                case 'O' or 'S' or 'N':
                {
                    var h = (nint)(void*)Next(ref vi);
                    if (h == 0)
                    {
                        return Err != 0 ? 0 : Raise(SystemError, "NULL object passed to Py_BuildValue");
                    }
                    return c == 'N' ? h : NewRef(h);
                }
                default:
                    return Raise(SystemError, $"bad format char '{c}' passed to Py_BuildValue");
            }
        }

        // ---- PyUnicode_FromFormat ---------------------------------------------

        internal static string? FromFormat(string fmt, VaArg[] va)
        {
            var sb = new StringBuilder();
            int vi = 0;
            VaArg Next() => vi < va.Length ? va[vi++] : throw new InvalidOperationException(
                "dotcc Python shim: fewer variadic arguments than the format needs");
            for (int i = 0; i < fmt.Length; i++)
            {
                char c = fmt[i];
                if (c != '%') { sb.Append(c); continue; }
                if (++i >= fmt.Length) { break; }
                if (fmt[i] == '%') { sb.Append('%'); continue; }
                bool left = false, zero = false;
                for (; i < fmt.Length && fmt[i] is '-' or '0'; i++)
                {
                    if (fmt[i] == '-') { left = true; } else { zero = true; }
                }
                int width = 0, prec = -1;
                for (; i < fmt.Length && char.IsAsciiDigit(fmt[i]); i++) { width = width * 10 + (fmt[i] - '0'); }
                if (i < fmt.Length && fmt[i] == '.')
                {
                    prec = 0;
                    for (i++; i < fmt.Length && char.IsAsciiDigit(fmt[i]); i++) { prec = prec * 10 + (fmt[i] - '0'); }
                }
                bool wide = false;
                while (i < fmt.Length && fmt[i] is 'l' or 'z' or 't' or 'j') { wide = true; i++; }
                if (i >= fmt.Length) { break; }
                string piece;
                bool numeric = false;
                switch (fmt[i])
                {
                    case 'c': piece = char.ConvertFromUtf32((int)Next()); break;
                    case 'd' or 'i':
                        piece = (wide ? (long)Next() : (int)Next()).ToString(CultureInfo.InvariantCulture);
                        numeric = true;
                        break;
                    case 'u':
                        piece = (wide ? (ulong)Next() : (uint)Next()).ToString(CultureInfo.InvariantCulture);
                        numeric = true;
                        break;
                    case 'x' or 'X':
                        piece = (wide ? (ulong)Next() : (uint)Next()).ToString(fmt[i] == 'x' ? "x" : "X", CultureInfo.InvariantCulture);
                        numeric = true;
                        break;
                    case 'p': piece = "0x" + ((ulong)(nint)(void*)Next()).ToString("x", CultureInfo.InvariantCulture); break;
                    case 's': piece = FromC((byte*)(void*)Next()); break;
                    case 'U' or 'S' or 'R' or 'A' or 'V':
                    {
                        var h = (nint)(void*)Next();
                        if (fmt[i] == 'V')
                        {
                            var fallback = (byte*)(void*)Next();
                            piece = h != 0 ? ToStr(h) : FromC(fallback);
                        }
                        else if (h == 0) { BadInternalCall(); return null; }
                        else { piece = fmt[i] is 'R' or 'A' ? Repr(h) : ToStr(h); }
                        break;
                    }
                    default:
                        Raise(SystemError, $"invalid format string: unsupported conversion '%{fmt[i]}'");
                        return null;
                }
                if (!numeric && prec >= 0 && piece.Length > prec) { piece = piece[..prec]; }
                if (piece.Length < width)
                {
                    piece = left ? piece.PadRight(width)
                        : numeric && zero ? (piece.StartsWith('-') ? "-" + piece[1..].PadLeft(width - 1, '0') : piece.PadLeft(width, '0'))
                        : piece.PadLeft(width);
                }
                sb.Append(piece);
            }
            return sb.ToString();
        }

        // ---- managed <-> Python values (the PyHost seam) ------------------------

        internal static object? ToManaged(nint h)
        {
            switch (V(h))
            {
                case NoneV when h == None: return null;
                case bool b: return b;
                case long l: return l;
                case double d: return d;
                case string s: return s;
                case TupleV t:
                {
                    var arr = new object?[t.Items.Length];
                    for (int i = 0; i < arr.Length; i++) { arr[i] = ToManaged(t.Items[i]); }
                    return arr;
                }
                case ListV l:
                {
                    var list = new List<object?>(l.Items.Count);
                    foreach (var item in l.Items) { list.Add(ToManaged(item)); }
                    return list;
                }
                case DictV d:
                {
                    var dict = new Dictionary<object, object?>();
                    for (int i = 0; i < d.Keys.Count; i++)
                    {
                        dict[ToManaged(d.Keys[i]) ?? PyHost.NoneKey] = ToManaged(d.Vals[i]);
                    }
                    return dict;
                }
                default:
                    IncRef(h);
                    return new PyHost.PyRef(h);
            }
        }

        internal static nint FromManaged(object? v)
        {
            switch (v)
            {
                case null: return NewRef(None);
                case bool b: return Bool(b);
                case sbyte or byte or short or ushort or int or uint or long:
                    return New(System.Convert.ToInt64(v, CultureInfo.InvariantCulture));
                case float or double: return New(System.Convert.ToDouble(v, CultureInfo.InvariantCulture));
                case string s: return NewStr(s);
                case char ch: return NewStr(ch.ToString());
                case PyHost.PyRef r: return NewRef(r.Handle);
                case object?[] arr:
                {
                    var items = new nint[arr.Length];
                    for (int i = 0; i < arr.Length; i++) { items[i] = FromManaged(arr[i]); }
                    return NewTuple(items);
                }
                case System.Collections.IDictionary map:
                {
                    var dict = New(new DictV());
                    foreach (System.Collections.DictionaryEntry e in map)
                    {
                        var k = FromManaged(e.Key);
                        var val = FromManaged(e.Value);
                        bool ok = DictSet(dict, k, val);
                        DecRef(k);
                        DecRef(val);
                        if (!ok) { DecRef(dict); PyHost.ThrowPending(); }
                    }
                    return dict;
                }
                case System.Collections.IList seq:
                {
                    var list = new ListV();
                    foreach (var item in seq) { list.Items.Add(FromManaged(item)); }
                    return New(list);
                }
                default:
                    throw new ArgumentException($"dotcc Python shim: no Python value for a {v.GetType().Name}");
            }
        }
    }

    // =====================================================================
    // The C-API entry points (<Python.h>). New / borrowed / stolen reference
    // semantics follow the CPython documentation for each function.
    // =====================================================================

    // ---- reference counting ----

    public static void Py_IncRef(_object* o) => PyRt.IncRef((nint)o);
    public static void Py_DecRef(_object* o) => PyRt.DecRef((nint)o);
    public static _object* Py_NewRef(_object* o) => (_object*)PyRt.NewRef((nint)o);
    public static _object* Py_XNewRef(_object* o) => (_object*)PyRt.NewRef((nint)o);
    public static long Py_REFCNT(_object* o) => PyRt.Get((nint)o).Refs;

    // ---- singletons ----

    public static _object* Py_GetConstantBorrowed(uint constantId) => (_object*)(constantId switch
    {
        0 => PyRt.None,
        1 => PyRt.False,
        2 => PyRt.True,
        3 => PyRt.Ellipsis,
        4 => PyRt.NotImplemented,
        5 => PyRt.Zero,
        6 => PyRt.One,
        7 => PyRt.EmptyStr,
        9 => PyRt.EmptyTuple,
        _ => PyRt.Raise(PyRt.SystemError, $"invalid constant ID {constantId}"),
    });

    public static _object* Py_GetConstant(uint constantId) => Py_NewRef(Py_GetConstantBorrowed(constantId));

    // ---- object protocol ----

    public static _object* Py_TYPE(_object* o) => o == null ? (_object*)PyRt.BadInternalCall() : (_object*)PyRt.TypeOf((nint)o);
    public static _object* PyObject_Type(_object* o) => o == null ? (_object*)PyRt.BadInternalCall() : (_object*)PyRt.NewRef(PyRt.TypeOf((nint)o));
    public static _object* PyObject_Repr(_object* o) => (_object*)PyRt.NewStr(o == null ? "<NULL>" : PyRt.Repr((nint)o));
    public static _object* PyObject_Str(_object* o) => (_object*)PyRt.NewStr(o == null ? "<NULL>" : PyRt.ToStr((nint)o));

    public static _object* PyObject_GetAttrString(_object* o, byte* name)
        => o == null || name == null ? (_object*)PyRt.BadInternalCall() : (_object*)PyRt.GetAttr((nint)o, PyRt.FromC(name));

    public static int PyObject_SetAttrString(_object* o, byte* name, _object* v)
    {
        if (o == null || name == null) { PyRt.BadInternalCall(); return -1; }
        var attr = PyRt.FromC(name);
        if (PyRt.V((nint)o) is PyRt.ModuleV m && v != null)
        {
            return PyRt.DictSetString(m.Dict, attr, (nint)v) ? 0 : -1;
        }
        PyRt.Raise(PyRt.AttributeError, $"'{PyRt.TypeName((nint)o)}' object has no attribute '{attr}'");
        return -1;
    }

    public static int PyObject_HasAttrString(_object* o, byte* name)
    {
        var saved = PyRt.Err;
        PyRt.Err = 0;
        var r = PyObject_GetAttrString(o, name);
        PyRt.SetErr(0);
        PyRt.Err = saved;
        if (r == null) { return 0; }
        PyRt.DecRef((nint)r);
        return 1;
    }

    public static _object* PyObject_Call(_object* callable, _object* args, _object* kwargs)
        => (_object*)PyRt.Call((nint)callable, (nint)args, (nint)kwargs);

    public static _object* PyObject_CallObject(_object* callable, _object* args)
        => (_object*)PyRt.Call((nint)callable, args == null ? PyRt.EmptyTuple : (nint)args, 0);

    public static _object* PyObject_CallNoArgs(_object* callable) => (_object*)PyRt.Call((nint)callable, PyRt.EmptyTuple, 0);

    public static int PyObject_IsTrue(_object* o) => o == null ? -1 : PyRt.IsTrue((nint)o) ? 1 : 0;
    public static int PyObject_Not(_object* o) => o == null ? -1 : PyRt.IsTrue((nint)o) ? 0 : 1;
    public static long PyObject_Size(_object* o)
    {
        if (o == null) { PyRt.BadInternalCall(); return -1; }
        return PyRt.Length((nint)o);
    }

    public static long PyObject_Length(_object* o) => PyObject_Size(o);
    public static int PyCallable_Check(_object* o) => o != null && PyRt.V((nint)o) is PyRt.CFuncV or PyRt.TypeV ? 1 : 0;

    public static int PyObject_IsInstance(_object* inst, _object* cls)
    {
        if (inst == null || cls == null) { PyRt.BadInternalCall(); return -1; }
        switch (PyRt.V((nint)cls))
        {
            case PyRt.TypeV: return PyRt.IsInstance((nint)inst, (nint)cls) ? 1 : 0;
            case PyRt.TupleV t:
                foreach (var c in t.Items)
                {
                    if (PyObject_IsInstance(inst, (_object*)c) == 1) { return 1; }
                }
                return 0;
            default:
                PyRt.Raise(PyRt.TypeError, "isinstance() arg 2 must be a type, a tuple of types, or a union");
                return -1;
        }
    }

    // ---- types ----

    private static PyRt.TypeV? PyTypeArg(_object* t)
    {
        if (t != null && PyRt.V((nint)t) is PyRt.TypeV ty) { return ty; }
        PyRt.BadInternalCall();
        return null;
    }

    public static _object* PyType_GetName(_object* type)
        => PyTypeArg(type) is { } ty ? (_object*)PyRt.NewStr(ty.Name) : null;

    public static _object* PyType_GetQualName(_object* type)
        => PyTypeArg(type) is { } ty ? (_object*)PyRt.NewStr(ty.Name) : null;

    public static _object* PyType_GetFullyQualifiedName(_object* type)
        => PyTypeArg(type) is { } ty ? (_object*)PyRt.NewStr(PyRt.QualifiedName(ty)) : null;

    public static int PyType_IsSubtype(_object* a, _object* b) => PyRt.IsSubtype((nint)a, (nint)b) ? 1 : 0;

    // ---- numbers ----

    public static _object* PyLong_FromLong(long v) => (_object*)PyRt.New(v);
    public static _object* PyLong_FromLongLong(long v) => (_object*)PyRt.New(v);
    public static _object* PyLong_FromSsize_t(long v) => (_object*)PyRt.New(v);

    public static _object* PyLong_FromDouble(double v)
    {
        if (double.IsNaN(v)) { return (_object*)PyRt.Raise(PyRt.ValueError, "cannot convert float NaN to integer"); }
        if (double.IsInfinity(v)) { return (_object*)PyRt.Raise(PyRt.OverflowError, "cannot convert float infinity to integer"); }
        double t = Math.Truncate(v);
        if (t < -9.223372036854775808e18 || t >= 9.223372036854775808e18)
        {
            return (_object*)PyRt.Raise(PyRt.OverflowError, "dotcc Python shim: int exceeds the 64-bit signed int model");
        }
        return (_object*)PyRt.New((long)t);
    }

    public static long PyLong_AsLong(_object* o) => PyRt.AsLong((nint)o, out long v) ? v : -1;
    public static long PyLong_AsLongLong(_object* o) => PyLong_AsLong(o);
    public static long PyLong_AsSsize_t(_object* o) => PyLong_AsLong(o);

    public static double PyLong_AsDouble(_object* o)
    {
        if (o == null) { PyRt.BadInternalCall(); return -1.0; }
        if (PyRt.V((nint)o) is not (long or bool))
        {
            PyRt.Raise(PyRt.TypeError, "an integer is required");
            return -1.0;
        }
        PyRt.AsLong((nint)o, out long v);
        return v;
    }

    public static int PyLong_Check(_object* o) => o != null && PyRt.V((nint)o) is long or bool ? 1 : 0;

    public static _object* PyFloat_FromDouble(double v) => (_object*)PyRt.New(v);
    public static double PyFloat_AsDouble(_object* o) => PyRt.AsDouble((nint)o, out double v) ? v : -1.0;
    public static int PyFloat_Check(_object* o) => o != null && PyRt.V((nint)o) is double ? 1 : 0;

    public static _object* PyBool_FromLong(long v) => (_object*)PyRt.Bool(v != 0);
    public static int PyBool_Check(_object* o) => o != null && PyRt.V((nint)o) is bool ? 1 : 0;

    // ---- strings ----

    public static _object* PyUnicode_FromString(byte* u)
        => u == null ? (_object*)PyRt.BadInternalCall() : (_object*)PyRt.NewStr(PyRt.FromC(u));

    public static _object* PyUnicode_FromStringAndSize(byte* u, long size)
    {
        if (size < 0) { return (_object*)PyRt.Raise(PyRt.SystemError, "Negative size passed to PyUnicode_FromStringAndSize"); }
        if (u == null) { return size == 0 ? (_object*)PyRt.EmptyStr : (_object*)PyRt.BadInternalCall(); }
        return (_object*)PyRt.NewStr(Encoding.UTF8.GetString(u, (int)size));
    }

    public static _object* PyUnicode_FromFormat(byte* format, params VaArg[] va)
        => PyRt.FromFormat(PyRt.FromC(format), va) is { } s ? (_object*)PyRt.NewStr(s) : null;

    public static byte* PyUnicode_AsUTF8AndSize(_object* o, long* size)
    {
        if (o == null || PyRt.V((nint)o) is not string)
        {
            if (size != null) { *size = -1; }
            PyRt.Raise(PyRt.TypeError, "bad argument type for built-in operation");
            return null;
        }
        var p = PyRt.Utf8(PyRt.Get((nint)o), out long len);
        if (size != null) { *size = len; }
        return p;
    }

    public static long PyUnicode_GetLength(_object* o)
    {
        if (o == null || PyRt.V((nint)o) is not string)
        {
            PyRt.Raise(PyRt.TypeError, "bad argument type for built-in operation");
            return -1;
        }
        return PyRt.Length((nint)o);
    }

    public static _object* PyUnicode_Concat(_object* left, _object* right)
    {
        if (left == null || right == null) { return (_object*)PyRt.BadInternalCall(); }
        if (PyRt.V((nint)left) is not string l)
        {
            return (_object*)PyRt.Raise(PyRt.TypeError, $"must be str, not {PyRt.TypeName((nint)left)}");
        }
        if (PyRt.V((nint)right) is not string r)
        {
            return (_object*)PyRt.Raise(PyRt.TypeError, $"can only concatenate str (not \"{PyRt.TypeName((nint)right)}\") to str");
        }
        return (_object*)PyRt.NewStr(l + r);
    }

    public static int PyUnicode_CompareWithASCIIString(_object* o, byte* s)
        => Math.Sign(string.CompareOrdinal(PyRt.V((nint)o) as string ?? "", PyRt.FromC(s)));

    public static int PyUnicode_Check(_object* o) => o != null && PyRt.V((nint)o) is string ? 1 : 0;

    // ---- tuples ----

    public static _object* PyTuple_New(long size)
        => size < 0 ? (_object*)PyRt.BadInternalCall() : (_object*)PyRt.NewTuple(new nint[size]);

    public static long PyTuple_Size(_object* p)
    {
        if (p == null || PyRt.V((nint)p) is not PyRt.TupleV t) { PyRt.BadInternalCall(); return -1; }
        return t.Items.Length;
    }

    public static _object* PyTuple_GetItem(_object* p, long pos)
    {
        if (p == null || PyRt.V((nint)p) is not PyRt.TupleV t) { return (_object*)PyRt.BadInternalCall(); }
        if (pos < 0 || pos >= t.Items.Length) { return (_object*)PyRt.Raise(PyRt.IndexError, "tuple index out of range"); }
        return (_object*)t.Items[pos];
    }

    public static int PyTuple_SetItem(_object* p, long pos, _object* o)
    {
        if (p == null || PyRt.V((nint)p) is not PyRt.TupleV t)
        {
            PyRt.DecRef((nint)o);
            PyRt.BadInternalCall();
            return -1;
        }
        if (pos < 0 || pos >= t.Items.Length)
        {
            PyRt.DecRef((nint)o);
            PyRt.Raise(PyRt.IndexError, "tuple assignment index out of range");
            return -1;
        }
        var old = t.Items[pos];
        t.Items[pos] = (nint)o;
        PyRt.DecRef(old);
        return 0;
    }

    public static _object* PyTuple_Pack(long n, params VaArg[] va)
    {
        var items = new nint[n];
        for (int i = 0; i < n; i++) { items[i] = PyRt.NewRef((nint)(void*)va[i]); }
        return (_object*)PyRt.NewTuple(items);
    }

    public static int PyTuple_Check(_object* o) => o != null && PyRt.V((nint)o) is PyRt.TupleV ? 1 : 0;

    // ---- lists ----

    public static _object* PyList_New(long len)
    {
        if (len < 0) { return (_object*)PyRt.BadInternalCall(); }
        var list = new PyRt.ListV();
        for (long i = 0; i < len; i++) { list.Items.Add(0); }
        return (_object*)PyRt.New(list);
    }

    public static long PyList_Size(_object* list)
    {
        if (list == null || PyRt.V((nint)list) is not PyRt.ListV l) { PyRt.BadInternalCall(); return -1; }
        return l.Items.Count;
    }

    public static _object* PyList_GetItem(_object* list, long index)
    {
        if (list == null || PyRt.V((nint)list) is not PyRt.ListV l) { return (_object*)PyRt.BadInternalCall(); }
        if (index < 0 || index >= l.Items.Count) { return (_object*)PyRt.Raise(PyRt.IndexError, "list index out of range"); }
        return (_object*)l.Items[(int)index];
    }

    public static int PyList_SetItem(_object* list, long index, _object* item)
    {
        if (list == null || PyRt.V((nint)list) is not PyRt.ListV l)
        {
            PyRt.DecRef((nint)item);
            PyRt.BadInternalCall();
            return -1;
        }
        if (index < 0 || index >= l.Items.Count)
        {
            PyRt.DecRef((nint)item);
            PyRt.Raise(PyRt.IndexError, "list assignment index out of range");
            return -1;
        }
        var old = l.Items[(int)index];
        l.Items[(int)index] = (nint)item;
        PyRt.DecRef(old);
        return 0;
    }

    public static int PyList_Append(_object* list, _object* item)
    {
        if (list == null || item == null || PyRt.V((nint)list) is not PyRt.ListV l) { PyRt.BadInternalCall(); return -1; }
        l.Items.Add(PyRt.NewRef((nint)item));
        return 0;
    }

    public static int PyList_Check(_object* o) => o != null && PyRt.V((nint)o) is PyRt.ListV ? 1 : 0;

    // ---- dicts ----

    public static _object* PyDict_New() => (_object*)PyRt.New(new PyRt.DictV());

    public static int PyDict_SetItem(_object* p, _object* key, _object* val)
    {
        if (p == null || key == null || val == null || PyRt.V((nint)p) is not PyRt.DictV) { PyRt.BadInternalCall(); return -1; }
        return PyRt.DictSet((nint)p, (nint)key, (nint)val) ? 0 : -1;
    }

    public static int PyDict_SetItemString(_object* p, byte* key, _object* val)
    {
        if (p == null || key == null || val == null || PyRt.V((nint)p) is not PyRt.DictV) { PyRt.BadInternalCall(); return -1; }
        return PyRt.DictSetString((nint)p, PyRt.FromC(key), (nint)val) ? 0 : -1;
    }

    public static _object* PyDict_GetItem(_object* p, _object* key)
        => p == null || key == null || PyRt.V((nint)p) is not PyRt.DictV ? null : (_object*)PyRt.DictGet((nint)p, (nint)key);

    public static _object* PyDict_GetItemString(_object* p, byte* key)
        => p == null || key == null || PyRt.V((nint)p) is not PyRt.DictV ? null : (_object*)PyRt.DictGetString((nint)p, PyRt.FromC(key));

    public static int PyDict_Next(_object* p, long* ppos, _object** pkey, _object** pvalue)
    {
        if (p == null || ppos == null || PyRt.V((nint)p) is not PyRt.DictV d) { return 0; }
        long i = *ppos;
        if (i < 0 || i >= d.Keys.Count) { return 0; }
        if (pkey != null) { *pkey = (_object*)d.Keys[(int)i]; }
        if (pvalue != null) { *pvalue = (_object*)d.Vals[(int)i]; }
        *ppos = i + 1;
        return 1;
    }

    public static long PyDict_Size(_object* p)
    {
        if (p == null || PyRt.V((nint)p) is not PyRt.DictV d) { PyRt.BadInternalCall(); return -1; }
        return d.Keys.Count;
    }

    public static int PyDict_Check(_object* o) => o != null && PyRt.V((nint)o) is PyRt.DictV ? 1 : 0;

    // ---- exceptions ----

    public static _object* PyExc_BaseException => (_object*)PyRt.BaseException;
    public static _object* PyExc_Exception => (_object*)PyRt.Exception;
    public static _object* PyExc_ArithmeticError => (_object*)PyRt.ArithmeticError;
    public static _object* PyExc_LookupError => (_object*)PyRt.LookupError;
    public static _object* PyExc_AttributeError => (_object*)PyRt.AttributeError;
    public static _object* PyExc_IndexError => (_object*)PyRt.IndexError;
    public static _object* PyExc_KeyError => (_object*)PyRt.KeyError;
    public static _object* PyExc_MemoryError => (_object*)PyRt.MemoryError;
    public static _object* PyExc_NotImplementedError => (_object*)PyRt.NotImplementedError;
    public static _object* PyExc_OverflowError => (_object*)PyRt.OverflowError;
    public static _object* PyExc_RuntimeError => (_object*)PyRt.RuntimeError;
    public static _object* PyExc_SystemError => (_object*)PyRt.SystemError;
    public static _object* PyExc_TypeError => (_object*)PyRt.TypeError;
    public static _object* PyExc_ValueError => (_object*)PyRt.ValueError;
    public static _object* PyExc_ZeroDivisionError => (_object*)PyRt.ZeroDivisionError;

    public static void PyErr_SetString(_object* type, byte* message)
    {
        if (!PyRt.IsExceptionType((nint)type))
        {
            PyRt.Raise(PyRt.SystemError, "exception is not a BaseException subclass");
            return;
        }
        PyRt.Raise((nint)type, PyRt.FromC(message));
    }

    public static void PyErr_SetObject(_object* type, _object* value)
    {
        if (!PyRt.IsExceptionType((nint)type))
        {
            PyRt.Raise(PyRt.SystemError, "exception is not a BaseException subclass");
            return;
        }
        var v = (nint)value;
        if (v != 0 && PyRt.V(v) is PyRt.ExcV && PyRt.IsInstance(v, (nint)type))
        {
            PyRt.SetErr(PyRt.NewRef(v));
            return;
        }
        var args = v == 0 || v == PyRt.None ? PyRt.NewTuple()
            : PyRt.V(v) is PyRt.TupleV ? PyRt.NewRef(v)
            : PyRt.NewTuple(PyRt.NewRef(v));
        PyRt.SetErr(PyRt.NewExc((nint)type, args));
    }

    public static void PyErr_SetNone(_object* type) => PyErr_SetObject(type, null);

    public static _object* PyErr_Format(_object* exception, byte* format, params VaArg[] va)
    {
        if (PyRt.FromFormat(PyRt.FromC(format), va) is { } msg)
        {
            if (PyRt.IsExceptionType((nint)exception)) { PyRt.Raise((nint)exception, msg); }
            else { PyRt.Raise(PyRt.SystemError, "exception is not a BaseException subclass"); }
        }
        return null;
    }

    public static _object* PyErr_Occurred() => (_object*)PyRt.ErrType();
    public static void PyErr_Clear() => PyRt.SetErr(0);
    public static _object* PyErr_NoMemory() { PyRt.SetErr(PyRt.NewExc(PyRt.MemoryError, PyRt.NewTuple())); return null; }
    public static int PyErr_BadArgument() { PyRt.Raise(PyRt.TypeError, "bad argument type for built-in operation"); return 0; }

    public static _object* PyErr_NewException(byte* name, _object* @base, _object* dict)
    {
        var full = PyRt.FromC(name);
        int dot = full.LastIndexOf('.');
        if (dot < 0) { return (_object*)PyRt.Raise(PyRt.SystemError, "PyErr_NewException: name must be module.class"); }
        nint b = (nint)@base;
        if (b != 0 && PyRt.V(b) is PyRt.TupleV bases) { b = bases.Items.Length > 0 ? bases.Items[0] : 0; }
        if (b == 0) { b = PyRt.Exception; }
        if (!PyRt.IsExceptionType(b)) { return (_object*)PyRt.Raise(PyRt.TypeError, "base must be an exception type"); }
        PyRt.IncRef(b);
        return (_object*)PyRt.New(new PyRt.TypeV
        {
            Name = full[(dot + 1)..],
            Module = full[..dot],
            Base = b,
            IsException = true,
        });
    }

    public static _object* PyErr_GetRaisedException()
    {
        var e = PyRt.Err;
        PyRt.Err = 0;
        return (_object*)e;
    }

    public static void PyErr_SetRaisedException(_object* exc) => PyRt.SetErr((nint)exc);

    public static int PyErr_GivenExceptionMatches(_object* given, _object* exc)
    {
        nint g = (nint)given, e = (nint)exc;
        if (g == 0 || e == 0) { return 0; }
        if (PyRt.V(e) is PyRt.TupleV t)
        {
            foreach (var item in t.Items)
            {
                if (PyErr_GivenExceptionMatches(given, (_object*)item) == 1) { return 1; }
            }
            return 0;
        }
        if (PyRt.V(g) is PyRt.ExcV ev) { g = ev.Type; }
        return PyRt.IsSubtype(g, e) ? 1 : 0;
    }

    public static int PyErr_ExceptionMatches(_object* exc) => PyErr_GivenExceptionMatches((_object*)PyRt.ErrType(), exc);

    public static void PyErr_Print()
    {
        var e = PyRt.Err;
        if (e == 0) { return; }
        PyRt.Err = 0;
        var ev = (PyRt.ExcV)PyRt.V(e);
        var name = PyRt.QualifiedName((PyRt.TypeV)PyRt.V(ev.Type));
        var msg = PyRt.ToStr(e);
        Console.Error.WriteLine(msg.Length == 0 ? name : name + ": " + msg);
        PyRt.DecRef(e);
    }

    // ---- argument parsing / value building ----

    public static int PyArg_ParseTuple(_object* args, byte* format, params VaArg[] va)
        => PyRt.ParseTuple((nint)args, PyRt.FromC(format), va);

    public static int PyArg_ParseTupleAndKeywords(_object* args, _object* kw, byte* format, byte** keywords, params VaArg[] va)
        => PyRt.ParseTupleAndKeywords((nint)args, (nint)kw, PyRt.FromC(format), keywords, va);

    public static int PyArg_UnpackTuple(_object* args, byte* name, long min, long max, params VaArg[] va)
    {
        if (args == null || PyRt.V((nint)args) is not PyRt.TupleV t)
        {
            PyRt.Raise(PyRt.SystemError, "PyArg_UnpackTuple() argument list is not a tuple");
            return 0;
        }
        long n = t.Items.Length;
        if (n < min || n > max)
        {
            var who = name != null ? PyRt.FromC(name) : "unpacked tuple";
            long bound = n < min ? min : max;
            var how = min == max ? "" : n < min ? "at least " : "at most ";
            PyRt.Raise(PyRt.TypeError, $"{who} expected {how}{bound} argument{(bound == 1 ? "" : "s")}, got {n}");
            return 0;
        }
        for (int i = 0; i < n; i++) { *(_object**)(void*)va[i] = (_object*)t.Items[i]; }
        return 1;
    }

    public static _object* Py_BuildValue(byte* format, params VaArg[] va)
        => format == null ? (_object*)PyRt.BadInternalCall() : (_object*)PyRt.Build(PyRt.FromC(format), va);

    // ---- modules ----

    public static _object* PyModule_Create2(void* def, int apiver)
    {
        var d = (PyModuleDefView*)def;
        if (d == null || d->m_name == null) { return (_object*)PyRt.BadInternalCall(); }
        var name = PyRt.FromC(d->m_name);
        if (d->m_slots != null)
        {
            return (_object*)PyRt.Raise(PyRt.SystemError,
                $"module {name}: PyModule_Create is incompatible with m_slots (multi-phase init is not supported by the dotcc shim)");
        }
        var dict = PyRt.New(new PyRt.DictV());
        var module = PyRt.New(new PyRt.ModuleV
        {
            Name = name,
            Dict = dict,
            State = d->m_size > 0 ? NativeMemory.AllocZeroed((nuint)d->m_size) : null,
        });
        var nameStr = PyRt.NewStr(name);
        var docObj = d->m_doc != null ? PyRt.NewStr(PyRt.FromC(d->m_doc)) : PyRt.NewRef(PyRt.None);
        PyRt.DictSetString(dict, "__name__", nameStr);
        PyRt.DictSetString(dict, "__doc__", docObj);
        PyRt.DecRef(nameStr);
        PyRt.DecRef(docObj);
        for (var m = d->m_methods; m != null && m->ml_name != null; m++)
        {
            var fname = PyRt.FromC(m->ml_name);
            int flags = m->ml_flags;
            bool supported = flags == PyRt.METH_VARARGS || flags == (PyRt.METH_VARARGS | PyRt.METH_KEYWORDS)
                || flags == PyRt.METH_NOARGS || flags == PyRt.METH_O;
            if (!supported)
            {
                PyRt.DecRef(module);
                return (_object*)PyRt.Raise(PyRt.SystemError,
                    $"{name}.{fname}: unsupported ml_flags 0x{flags:x} (the dotcc shim takes METH_VARARGS[|METH_KEYWORDS], METH_NOARGS, METH_O)");
            }
            var func = PyRt.New(new PyRt.CFuncV
            {
                Name = fname,
                Doc = m->ml_doc != null ? PyRt.FromC(m->ml_doc) : null,
                Meth = m->ml_meth,
                Flags = flags,
                Self = PyRt.NewRef(module),
            });
            PyRt.DictSetString(dict, fname, func);
            PyRt.DecRef(func);
        }
        return (_object*)module;
    }

    private static PyRt.ModuleV? PyModuleArg(_object* m)
    {
        if (m != null && PyRt.V((nint)m) is PyRt.ModuleV mod) { return mod; }
        PyRt.BadInternalCall();
        return null;
    }

    public static int PyModule_AddObjectRef(_object* module, byte* name, _object* value)
    {
        if (PyModuleArg(module) is not { } mod || name == null) { return -1; }
        if (value == null)
        {
            if (PyRt.Err == 0) { PyRt.Raise(PyRt.SystemError, "PyModule_AddObjectRef() must be called with an exception raised if value is NULL"); }
            return -1;
        }
        return PyRt.DictSetString(mod.Dict, PyRt.FromC(name), (nint)value) ? 0 : -1;
    }

    public static int PyModule_Add(_object* module, byte* name, _object* value)
    {
        int r = PyModule_AddObjectRef(module, name, value);
        PyRt.DecRef((nint)value);
        return r;
    }

    public static int PyModule_AddObject(_object* module, byte* name, _object* value)
    {
        int r = PyModule_AddObjectRef(module, name, value);
        if (r == 0) { PyRt.DecRef((nint)value); }
        return r;
    }

    public static int PyModule_AddIntConstant(_object* module, byte* name, long value)
        => PyModule_Add(module, name, PyLong_FromLong(value));

    public static int PyModule_AddStringConstant(_object* module, byte* name, byte* value)
        => PyModule_Add(module, name, PyUnicode_FromString(value));

    public static _object* PyModule_GetDict(_object* module) => PyModuleArg(module) is { } mod ? (_object*)mod.Dict : null;

    public static byte* PyModule_GetName(_object* module)
    {
        if (PyModuleArg(module) is not { } mod) { return null; }
        var n = PyRt.DictGetString(mod.Dict, "__name__");
        return n != 0 && PyRt.V(n) is string ? PyRt.Utf8(PyRt.Get(n), out _) : null;
    }

    public static void* PyModule_GetState(_object* module) => PyModuleArg(module) is { } mod ? mod.State : null;

    // ---- embedding (no interpreter to start: the object model is always live) ----

    public static void Py_Initialize() { }
    public static int Py_IsInitialized() => 1;
    public static int Py_FinalizeEx() => 0;
    public static void Py_Finalize() { }

    // ---- memory ----

    public static void* PyMem_Malloc(ulong n) => NativeMemory.Alloc(n == 0 ? 1 : (nuint)n);
    public static void* PyMem_Calloc(ulong nelem, ulong elsize) => NativeMemory.AllocZeroed(nelem * elsize == 0 ? 1 : (nuint)(nelem * elsize));
    public static void* PyMem_Realloc(void* p, ulong n) => NativeMemory.Realloc(p, n == 0 ? 1 : (nuint)n);
    public static void PyMem_Free(void* p) => NativeMemory.Free(p);

    /// <summary>
    /// The managed host side of the shim: load an extension module from its
    /// <c>PyInit_&lt;name&gt;</c> function and call into it with .NET values. Values
    /// convert structurally (None ↔ null, bool, int ↔ long, float ↔ double, str ↔ string,
    /// tuple ↔ object?[], list ↔ List, dict ↔ Dictionary); anything else comes back as
    /// a <see cref="PyRef"/> owning one reference. A raised Python exception surfaces as
    /// a <see cref="PyError"/>. Handles are plain <c>nint</c> so the API is reflection-
    /// and interop-friendly.
    /// </summary>
    public static class PyHost
    {
        /// <summary>An owned reference to a Python object with no structural .NET value.</summary>
        public sealed class PyRef
        {
            public nint Handle { get; }
            public PyRef(nint handle) => Handle = handle;
            public override string ToString() => PyRt.Repr(Handle);
        }

        /// <summary>A Python exception raised across the host boundary.</summary>
        public sealed class PyError : System.Exception
        {
            public string TypeName { get; }
            public string QualifiedTypeName { get; }
            public string PyMessage { get; }

            public PyError(string typeName, string qualifiedTypeName, string pyMessage)
                : base(pyMessage.Length == 0 ? qualifiedTypeName : qualifiedTypeName + ": " + pyMessage)
            {
                TypeName = typeName;
                QualifiedTypeName = qualifiedTypeName;
                PyMessage = pyMessage;
            }
        }

        /// <summary>Stand-in dictionary key for a Python <c>None</c> key.</summary>
        public static readonly object NoneKey = new();

        /// <summary>Import: call <c>PyInit_&lt;name&gt;</c> (a <c>PyObject *(*)(void)</c>
        /// function pointer) and return the module handle (an owned reference).</summary>
        public static nint Import(nint initFunction)
        {
            var m = (nint)((delegate*<_object*>)initFunction)();
            if (m == 0 || PyRt.Err != 0)
            {
                PyRt.DecRef(m);
                ThrowPending();
            }
            return m;
        }

        /// <summary><c>getattr(obj, name)</c>, converted to a .NET value.</summary>
        public static object? GetAttr(nint obj, string name)
        {
            var v = PyRt.GetAttr(obj, name);
            if (v == 0) { ThrowPending(); }
            try { return PyRt.ToManaged(v); }
            finally { PyRt.DecRef(v); }
        }

        /// <summary><c>obj.name(*args, **kwargs)</c> with .NET values in and out.</summary>
        public static object? Call(nint obj, string name, object?[] args, IDictionary<string, object?>? kwargs = null)
        {
            var callable = PyRt.GetAttr(obj, name);
            if (callable == 0) { ThrowPending(); }
            var argv = PyRt.FromManaged(args);
            nint kw = 0;
            if (kwargs != null)
            {
                kw = PyRt.New(new PyRt.DictV());
                foreach (var (k, v) in kwargs)
                {
                    var val = PyRt.FromManaged(v);
                    PyRt.DictSetString(kw, k, val);
                    PyRt.DecRef(val);
                }
            }
            var result = PyRt.Call(callable, argv, kw);
            PyRt.DecRef(callable);
            PyRt.DecRef(argv);
            PyRt.DecRef(kw);
            if (result == 0) { ThrowPending(); }
            try { return PyRt.ToManaged(result); }
            finally { PyRt.DecRef(result); }
        }

        /// <summary><c>repr(obj)</c>.</summary>
        public static string Repr(nint obj) => PyRt.Repr(obj);

        /// <summary>Drop an owned reference (a module from <see cref="Import"/>, a
        /// <see cref="PyRef"/>'s handle).</summary>
        public static void Release(nint obj) => PyRt.DecRef(obj);

        /// <summary>The object's current reference count.</summary>
        public static long RefCount(nint obj) => PyRt.Get(obj).Refs;

        /// <summary>Live (non-immortal) objects in the table, a leak probe.</summary>
        public static int LiveObjects => PyRt.Live;

        /// <summary>Convert the pending Python exception (or a SystemError when none is set)
        /// into a <see cref="PyError"/> and clear it.</summary>
        internal static void ThrowPending()
        {
            var e = PyRt.Err;
            PyRt.Err = 0;
            if (e == 0) { throw new PyError("SystemError", "SystemError", "error return without exception set"); }
            try
            {
                var ty = (PyRt.TypeV)PyRt.V(((PyRt.ExcV)PyRt.V(e)).Type);
                throw new PyError(ty.Name, PyRt.QualifiedName(ty), PyRt.ToStr(e));
            }
            finally { PyRt.DecRef(e); }
        }
    }
}
