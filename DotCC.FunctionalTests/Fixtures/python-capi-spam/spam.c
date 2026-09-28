/* spam: the CPython docs' canonical extension module ("Extending Python with
   C or C++"), grown into a small tour of the Limited API: every calling
   convention the shim offers (METH_VARARGS, METH_VARARGS|METH_KEYWORDS,
   METH_NOARGS, METH_O), PyArg_ParseTuple / PyArg_ParseTupleAndKeywords /
   PyArg_UnpackTuple, Py_BuildValue, a module-specific exception and module
   constants. Plain abi3 C: the same file builds as a real CPython extension. */
#define PY_SSIZE_T_CLEAN
#include <Python.h>

static PyObject *SpamError;

/* spam.add(a, b) -> int */
static PyObject *
spam_add(PyObject *self, PyObject *args)
{
    long a, b;

    if (!PyArg_ParseTuple(args, "ll:add", &a, &b))
        return NULL;
    return PyLong_FromLong(a + b);
}

/* spam.divide(a, b) -> float */
static PyObject *
spam_divide(PyObject *self, PyObject *args)
{
    double a, b;

    if (!PyArg_ParseTuple(args, "dd:divide", &a, &b))
        return NULL;
    if (b == 0.0) {
        PyErr_SetString(PyExc_ZeroDivisionError, "division by zero");
        return NULL;
    }
    return PyFloat_FromDouble(a / b);
}

/* spam.greet(name, greeting="Hello", times=1) -> list[str] */
static PyObject *
spam_greet(PyObject *self, PyObject *args, PyObject *kwargs)
{
    static char *kwlist[] = {"name", "greeting", "times", NULL};
    const char *name;
    const char *greeting = "Hello";
    int times = 1;
    PyObject *result;
    int i;

    if (!PyArg_ParseTupleAndKeywords(args, kwargs, "s|si:greet", kwlist,
                                     &name, &greeting, &times))
        return NULL;
    if (times < 0)
        return PyErr_Format(PyExc_ValueError, "times must be >= 0, not %d", times);

    result = PyList_New(times);
    if (result == NULL)
        return NULL;
    for (i = 0; i < times; i++) {
        PyObject *line = PyUnicode_FromFormat("%s, %s! (#%d)", greeting, name, i + 1);
        if (line == NULL) {
            Py_DECREF(result);
            return NULL;
        }
        PyList_SetItem(result, i, line);   /* steals line */
    }
    return result;
}

/* spam.stats(seq) -> {'count': n, 'sum': s, 'mean': m} for a list/tuple of numbers */
static PyObject *
spam_stats(PyObject *self, PyObject *seq)
{
    Py_ssize_t n, i;
    double sum = 0.0;

    if (PyList_Check(seq))
        n = PyList_Size(seq);
    else if (PyTuple_Check(seq))
        n = PyTuple_Size(seq);
    else {
        PyObject *tname = PyType_GetName(Py_TYPE(seq));
        if (tname == NULL)
            return NULL;
        PyErr_Format(PyExc_TypeError, "expected a list or tuple, not %U", tname);
        Py_DECREF(tname);
        return NULL;
    }
    if (n == 0) {
        PyErr_SetString(SpamError, "stats() of an empty sequence");
        return NULL;
    }
    for (i = 0; i < n; i++) {
        PyObject *item = PyList_Check(seq) ? PyList_GetItem(seq, i)
                                           : PyTuple_GetItem(seq, i);   /* borrowed */
        double v = PyFloat_AsDouble(item);
        if (v == -1.0 && PyErr_Occurred())
            return NULL;
        sum += v;
    }
    return Py_BuildValue("{s:n,s:d,s:d}", "count", n, "sum", sum, "mean", sum / n);
}

/* spam.version() -> (major, minor, backend) */
static PyObject *
spam_version(PyObject *self, PyObject *Py_UNUSED(ignored))
{
    return Py_BuildValue("(iis)", 1, 0, "dotcc");
}

/* spam.swap(a, b) -> (b, a) */
static PyObject *
spam_swap(PyObject *self, PyObject *args)
{
    PyObject *a, *b;

    if (!PyArg_UnpackTuple(args, "swap", 2, 2, &a, &b))
        return NULL;
    return Py_BuildValue("(OO)", b, a);   /* O takes new references */
}

static PyMethodDef SpamMethods[] = {
    {"add", spam_add, METH_VARARGS, "Add two integers."},
    {"divide", spam_divide, METH_VARARGS, "Divide two numbers."},
    {"greet", (PyCFunction)(void (*)(void))spam_greet, METH_VARARGS | METH_KEYWORDS,
     "Greet someone, optionally more than once."},
    {"stats", spam_stats, METH_O, "Count, sum and mean of a sequence of numbers."},
    {"version", spam_version, METH_NOARGS, "The module version."},
    {"swap", spam_swap, METH_VARARGS, "Swap two objects."},
    {NULL, NULL, 0, NULL}        /* Sentinel */
};

static struct PyModuleDef spammodule = {
    PyModuleDef_HEAD_INIT,
    "spam",                                   /* name of module */
    "A small tour of the Python C-API.",      /* module documentation */
    -1,                                       /* no per-module state */
    SpamMethods
};

PyMODINIT_FUNC
PyInit_spam(void)
{
    PyObject *m;

    m = PyModule_Create(&spammodule);
    if (m == NULL)
        return NULL;

    SpamError = PyErr_NewException("spam.error", NULL, NULL);
    if (PyModule_AddObjectRef(m, "error", SpamError) < 0) {
        Py_CLEAR(SpamError);
        Py_DECREF(m);
        return NULL;
    }
    if (PyModule_AddIntConstant(m, "ANSWER", 42) < 0
        || PyModule_AddStringConstant(m, "BACKEND", "dotcc") < 0) {
        Py_DECREF(m);
        return NULL;
    }
    return m;
}
