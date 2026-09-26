/* A tiny host for the spam extension (spam.c): "import" it by calling
   PyInit_spam, then drive it through the same C-API a real interpreter uses —
   attribute lookup, calls with positional and keyword arguments built by
   Py_BuildValue, repr() of each result, and the raised exception's qualified
   type name + message on failure. Plain Limited-API C: built against real
   CPython 3.13 (gcc, -lpython3.13) it prints the same transcript, which is
   how expected-stdout.txt was produced. */
#define PY_SSIZE_T_CLEAN
#include <Python.h>
#include <stdio.h>

PyMODINIT_FUNC PyInit_spam(void);

static PyObject *spam;

/* Print `expr`, then `=> repr(result)` or `!! Type: message`; consumes result. */
static void
report(const char *expr, PyObject *result)
{
    if (result == NULL) {
        PyObject *exc = PyErr_GetRaisedException();
        PyObject *type = PyObject_Type(exc);
        PyObject *name = PyType_GetFullyQualifiedName((PyTypeObject *)type);
        PyObject *msg = PyObject_Str(exc);
        printf("%s\n  !! %s: %s\n", expr, PyUnicode_AsUTF8AndSize(name, NULL),
               PyUnicode_AsUTF8AndSize(msg, NULL));
        Py_DECREF(msg);
        Py_DECREF(name);
        Py_DECREF(type);
        Py_DECREF(exc);
        return;
    }
    PyObject *repr = PyObject_Repr(result);
    printf("%s\n  => %s\n", expr, PyUnicode_AsUTF8AndSize(repr, NULL));
    Py_DECREF(repr);
    Py_DECREF(result);
}

/* spam.<name>(*args, **kwargs); consumes args and kwargs (either may be NULL). */
static PyObject *
call(const char *name, PyObject *args, PyObject *kwargs)
{
    PyObject *fn = PyObject_GetAttrString(spam, name);
    PyObject *result = NULL;

    if (fn != NULL)
        result = PyObject_Call(fn, args, kwargs);
    Py_XDECREF(fn);
    Py_XDECREF(args);
    Py_XDECREF(kwargs);
    return result;
}

int
main(void)
{
    Py_Initialize();
    spam = PyInit_spam();
    if (spam == NULL) {
        PyErr_Print();
        return 1;
    }

    report("spam.__doc__", PyObject_GetAttrString(spam, "__doc__"));
    report("spam.ANSWER", PyObject_GetAttrString(spam, "ANSWER"));
    report("spam.BACKEND", PyObject_GetAttrString(spam, "BACKEND"));
    report("spam.error", PyObject_GetAttrString(spam, "error"));
    report("spam.nope", PyObject_GetAttrString(spam, "nope"));

    report("spam.add(2, 40)", call("add", Py_BuildValue("(ii)", 2, 40), NULL));
    report("spam.add(True, -8)", call("add", Py_BuildValue("(Ol)", Py_True, -8L), NULL));
    report("spam.add(1)", call("add", Py_BuildValue("(i)", 1), NULL));
    report("spam.add('x', 1)", call("add", Py_BuildValue("(si)", "x", 1), NULL));
    report("spam.add(1.5, 1)", call("add", Py_BuildValue("(di)", 1.5, 1), NULL));
    report("spam.add(1, 2, x=3)", call("add", Py_BuildValue("(ii)", 1, 2), Py_BuildValue("{s:i}", "x", 3)));

    report("spam.divide(1, 8)", call("divide", Py_BuildValue("(ii)", 1, 8), NULL));
    report("spam.divide(10, 3)", call("divide", Py_BuildValue("(ii)", 10, 3), NULL));
    report("spam.divide(1e300, 1e-10)", call("divide", Py_BuildValue("(dd)", 1e300, 1e-10), NULL));
    report("spam.divide(1, 0)", call("divide", Py_BuildValue("(ii)", 1, 0), NULL));
    report("spam.divide('a', 1)", call("divide", Py_BuildValue("(si)", "a", 1), NULL));

    report("spam.greet('dotcc')", call("greet", Py_BuildValue("(s)", "dotcc"), NULL));
    report("spam.greet('abi3', times=2, greeting='Hi')",
           call("greet", Py_BuildValue("(s)", "abi3"),
                Py_BuildValue("{s:i,s:s}", "times", 2, "greeting", "Hi")));
    report("spam.greet(\"it's\", 'Hey', 0)", call("greet", Py_BuildValue("(ssi)", "it's", "Hey", 0), NULL));
    report("spam.greet('x', times=-1)",
           call("greet", Py_BuildValue("(s)", "x"), Py_BuildValue("{s:i}", "times", -1)));
    report("spam.greet('x', colour='red')",
           call("greet", Py_BuildValue("(s)", "x"), Py_BuildValue("{s:s}", "colour", "red")));
    report("spam.greet()", call("greet", PyTuple_New(0), NULL));
    report("spam.greet(42)", call("greet", Py_BuildValue("(i)", 42), NULL));

    report("spam.stats([3, 4.5, 10])", call("stats", Py_BuildValue("([idi])", 3, 4.5, 10), NULL));
    report("spam.stats((1, 2))", call("stats", Py_BuildValue("((ii))", 1, 2), NULL));
    report("spam.stats([])", call("stats", Py_BuildValue("([])"), NULL));
    report("spam.stats(42)", call("stats", Py_BuildValue("(i)", 42), NULL));
    report("spam.stats(['a'])", call("stats", Py_BuildValue("([s])", "a"), NULL));
    report("spam.stats(1, 2)", call("stats", Py_BuildValue("(ii)", 1, 2), NULL));

    report("spam.version()", call("version", PyTuple_New(0), NULL));
    report("spam.version(1)", call("version", Py_BuildValue("(i)", 1), NULL));

    report("spam.swap('left', None)", call("swap", Py_BuildValue("(sO)", "left", Py_None), NULL));
    report("spam.swap(1)", call("swap", Py_BuildValue("(i)", 1), NULL));

    Py_DECREF(spam);
    return Py_FinalizeEx() < 0 ? 1 : 0;
}
