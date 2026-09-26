#nullable enable

using System;
using System.Text;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Direct tests of the CPython Limited-API shim (PythonLib.cs) behind dotcc's synthetic
/// <c>&lt;Python.h&gt;</c>: reference counting and release, value building/parsing, the
/// repr protocol (including Python's float repr), dict key equality and the error
/// indicator. End-to-end coverage (a real extension module compiled by dotcc, diffed
/// against CPython) is the <c>python-capi-spam</c> fixture.
/// </summary>
[Collection("Runtime")]
public sealed unsafe class LibcPythonTests
{
    private static string Str(_object* o)
    {
        long n;
        byte* p = PyUnicode_AsUTF8AndSize(o, &n);
        ((nint)p).ShouldNotBe(0);
        return Encoding.UTF8.GetString(p, (int)n);
    }

    private static string Repr(_object* o)
    {
        var r = PyObject_Repr(o);
        var s = Str(r);
        Py_DecRef(r);
        return s;
    }

    /// <summary>Take the raised exception as "Type: message" and clear it.</summary>
    private static string TakeError()
    {
        var e = PyErr_GetRaisedException();
        ((nint)e).ShouldNotBe(0);
        var t = PyObject_Type(e);
        var name = PyType_GetFullyQualifiedName(t);
        var msg = PyObject_Str(e);
        var s = Str(name) + ": " + Str(msg);
        Py_DecRef(msg);
        Py_DecRef(name);
        Py_DecRef(t);
        Py_DecRef(e);
        return s;
    }

    [Theory]
    [InlineData(0.1, "0.1")]
    [InlineData(2.5, "2.5")]
    [InlineData(100.0, "100.0")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1.2345678901234568e17, "1.2345678901234568e+17")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1e-05, "1e-05")]
    [InlineData(10.0 / 3, "3.3333333333333335")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(-1.5e-7, "-1.5e-07")]
    [InlineData(double.PositiveInfinity, "inf")]
    [InlineData(double.NaN, "nan")]
    public void float_repr_matches_python(double value, string expected)
    {
        var f = PyFloat_FromDouble(value);
        Repr(f).ShouldBe(expected);
        Py_DecRef(f);
    }

    [Fact]
    public void containers_own_their_items_and_release_them()
    {
        var baseline = PyHost.LiveObjects;
        var item = PyLong_FromLong(7);
        var tuple = PyTuple_New(2);
        Py_REFCNT(item).ShouldBe(1);
        PyTuple_SetItem(tuple, 0, item).ShouldBe(0);            // steals item
        PyTuple_SetItem(tuple, 1, Py_NewRef(item)).ShouldBe(0); // second slot, new ref
        Py_REFCNT(item).ShouldBe(2);
        var list = PyList_New(0);
        PyList_Append(list, tuple).ShouldBe(0);                  // does not steal
        Py_DecRef(tuple);
        Repr(list).ShouldBe("[(7, 7)]");
        Py_DecRef(list);                                         // frees list → tuple → item
        PyHost.LiveObjects.ShouldBe(baseline);
    }

    [Fact]
    public void singletons_are_immortal()
    {
        var none = Py_GetConstantBorrowed(0);
        long before = Py_REFCNT(none);
        Py_DecRef(none);
        Py_DecRef(none);
        Py_REFCNT(none).ShouldBe(before);
        Repr(none).ShouldBe("None");
    }

    [Fact]
    public void build_value_nests_and_parse_tuple_reads_back()
    {
        _object* v;
        fixed (byte* fmt = "(i,[s,d],{s:O})\0"u8)
        fixed (byte* a = "a\0"u8)
        fixed (byte* k = "k\0"u8)
        {
            v = Py_BuildValue(fmt, 1, (void*)a, 2.5, (void*)k, (void*)Py_GetConstantBorrowed(0));
        }
        Repr(v).ShouldBe("(1, ['a', 2.5], {'k': None})");

        int i = 0;
        _object* list = null;
        _object* dict = null;
        var probe = PyList_New(0);
        var listType = Py_TYPE(probe);   // borrowed
        fixed (byte* fmt = "iO!O:f\0"u8)
        {
            PyArg_ParseTuple(v, fmt, (void*)&i, (void*)listType, (void*)&list, (void*)&dict).ShouldBe(1);
        }
        Py_DecRef(probe);
        i.ShouldBe(1);
        PyList_Size(list).ShouldBe(2);
        PyDict_Size(dict).ShouldBe(1);
        Py_DecRef(v);
    }

    [Fact]
    public void parse_tuple_reports_cpython_shaped_errors()
    {
        var args = PyTuple_New(1);
        PyTuple_SetItem(args, 0, PyFloat_FromDouble(1.5));
        long l = 0;
        fixed (byte* two = "ll:add\0"u8)
        {
            PyArg_ParseTuple(args, two, (void*)&l, (void*)&l).ShouldBe(0);
        }
        TakeError().ShouldBe("TypeError: add() takes exactly 2 arguments (1 given)");
        fixed (byte* one = "l\0"u8)
        {
            PyArg_ParseTuple(args, one, (void*)&l).ShouldBe(0);
        }
        TakeError().ShouldBe("TypeError: 'float' object cannot be interpreted as an integer");
        byte* s = null;
        fixed (byte* str = "s:greet\0"u8)
        {
            PyArg_ParseTuple(args, str, (void*)&s).ShouldBe(0);
        }
        TakeError().ShouldBe("TypeError: greet() argument 1 must be str, not float");
        ((nint)PyErr_Occurred()).ShouldBe(0);
        Py_DecRef(args);
    }

    [Fact]
    public void dict_keys_follow_python_equality()
    {
        var d = PyDict_New();
        var one = PyLong_FromLong(1);
        var oneF = PyFloat_FromDouble(1.0);
        var t = PyBool_FromLong(1);
        var a = PyUnicode_FromString(null);  // NULL → SystemError, no object
        ((nint)a).ShouldBe(0);
        TakeError().ShouldBe("SystemError: bad argument to internal function");
        PyDict_SetItem(d, one, one).ShouldBe(0);
        PyDict_SetItem(d, oneF, oneF).ShouldBe(0);  // same key as 1: replaces the value
        PyDict_SetItem(d, t, t).ShouldBe(0);        // True == 1 too
        PyDict_Size(d).ShouldBe(1);
        Repr(d).ShouldBe("{1: True}");
        var list = PyList_New(0);
        PyDict_SetItem(d, list, one).ShouldBe(-1);
        TakeError().ShouldBe("TypeError: unhashable type: 'list'");
        foreach (var o in new[] { list, t, oneF, one, d }) { Py_DecRef(o); }
    }

    [Fact]
    public void unicode_from_format_supports_the_common_conversions()
    {
        var obj = PyLong_FromLong(42);
        _object* s;
        fixed (byte* fmt = "%s|%5d|%-4i|%ld|%zd|%x|%c|%.3s|%S|%R|%%\0"u8)
        fixed (byte* hi = "hi\0"u8)
        fixed (byte* abcdef = "abcdef\0"u8)
        {
            s = PyUnicode_FromFormat(fmt, (void*)hi, 7, 3, -5L, 9L, 255, (int)'Z', (void*)abcdef, (void*)obj, (void*)obj);
        }
        Str(s).ShouldBe("hi|    7|3   |-5|9|ff|Z|abc|42|42|%");
        Py_DecRef(s);
        Py_DecRef(obj);
    }

    [Fact]
    public void a_freed_handle_is_rejected_loudly()
    {
        var o = PyLong_FromLong(123456);
        Py_DecRef(o);
        Should.Throw<InvalidOperationException>(() => Py_REFCNT(o));
    }
}
