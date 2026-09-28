#ifndef Py_PYTHON_H
#define Py_PYTHON_H

/* dotcc's <Python.h>: a "pretend to be CPython" header for building C
   extension modules against the stable ABI (the Limited API / abi3). Only the
   Limited API is offered: PyObject is OPAQUE and every refcount operation is a
   function call, so an extension never depends on an object layout. The
   runtime half is DotCC.Libc/PythonLib.cs, which implements this surface over
   a managed object model (a handle table: a PyObject* is a handle, never a
   real address; see docs/FRONTEND-IDEAS.md #1).

   Model notes (where this header deliberately differs from CPython's):
   - Py_LIMITED_API is defined (3.13) when the extension did not choose a
     version, steering `#ifdef Py_LIMITED_API` code onto its abi3 path.
   - `struct _object` has no body here: it is the runtime's opaque
     Libc._object (the same pattern as <time.h>'s struct tm), so
     `sizeof(PyObject)` and `o->ob_refcnt` do not compile, as abi3 intends.
   - PyTypeObject is an alias of PyObject (types are objects in the handle
     table); PyType_* and Py_TYPE take / return the same handle type.
   - Single-phase init only: PyModule_Create. A PyModuleDef with m_slots
     (multi-phase init, PyModuleDef_Init) raises SystemError.
   - Integers are 64-bit (long); there is no arbitrary-precision int.
   - METH_FASTCALL is not offered: METH_VARARGS, METH_KEYWORDS, METH_NOARGS
     and METH_O are.

   PyModuleDef / PyMethodDef are declared HERE with their LP64 layout (all
   8-byte fields); the runtime reads them through layout-mirror structs, so
   keep the two in sync (DotCC.Libc/PythonLib.cs, PyMethodDefView /
   PyModuleDefView). */

#include <stddef.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <assert.h>

/* ---- Version ---------------------------------------------------------- */
#define PY_MAJOR_VERSION 3
#define PY_MINOR_VERSION 13
#define PY_MICRO_VERSION 0
#define PY_RELEASE_LEVEL_FINAL 0xF
#define PY_RELEASE_LEVEL PY_RELEASE_LEVEL_FINAL
#define PY_RELEASE_SERIAL 0
#define PY_VERSION "3.13.0"
#define PY_VERSION_HEX 0x030D00F0
#define PYTHON_API_VERSION 1013
#define PYTHON_ABI_VERSION 3

#ifndef Py_LIMITED_API
#define Py_LIMITED_API 0x030D0000
#endif

/* dotcc marker, so portable code can tell it is building against the shim. */
#define DOTCC_PYTHON_SHIM 1

/* ---- Export / linkage macros ----------------------------------------- */
#define PyAPI_FUNC(RTYPE) RTYPE
#define PyAPI_DATA(RTYPE) extern RTYPE
#define PyMODINIT_FUNC PyObject *
#define Py_UNUSED(name) _unused_ ## name
#define Py_ARRAY_LENGTH(array) (sizeof(array) / sizeof((array)[0]))

/* ---- Core types ------------------------------------------------------- */
typedef long Py_ssize_t;
typedef long Py_hash_t;

#define PY_SSIZE_T_MAX LONG_MAX
#define PY_SSIZE_T_MIN LONG_MIN

typedef struct _object PyObject;
typedef PyObject PyTypeObject;

typedef PyObject *(*PyCFunction)(PyObject *, PyObject *);
typedef PyObject *(*PyCFunctionWithKeywords)(PyObject *, PyObject *, PyObject *);

/* PyMethodDef.ml_flags */
#define METH_VARARGS  0x0001
#define METH_KEYWORDS 0x0002
#define METH_NOARGS   0x0004
#define METH_O        0x0008

typedef struct PyMethodDef {
    const char *ml_name;
    PyCFunction ml_meth;
    int ml_flags;
    const char *ml_doc;
} PyMethodDef;

typedef struct PyModuleDef_Base {
    Py_ssize_t ob_refcnt;
    void *m_init;
    Py_ssize_t m_index;
    PyObject *m_copy;
} PyModuleDef_Base;

#define PyModuleDef_HEAD_INIT { 1, NULL, 0, NULL }

typedef struct PyModuleDef {
    PyModuleDef_Base m_base;
    const char *m_name;
    const char *m_doc;
    Py_ssize_t m_size;
    PyMethodDef *m_methods;
    void *m_slots;
    void *m_traverse;
    void *m_clear;
    void *m_free;
} PyModuleDef;

/* ---- Reference counting ---------------------------------------------- */
void Py_IncRef(PyObject *o);
void Py_DecRef(PyObject *o);
PyObject *Py_NewRef(PyObject *o);
PyObject *Py_XNewRef(PyObject *o);
Py_ssize_t Py_REFCNT(PyObject *o);

/* Py_IncRef / Py_DecRef are NULL-safe (as in CPython), so the X forms share them. */
#define Py_INCREF(op) Py_IncRef((PyObject *)(op))
#define Py_DECREF(op) Py_DecRef((PyObject *)(op))
#define Py_XINCREF(op) Py_IncRef((PyObject *)(op))
#define Py_XDECREF(op) Py_DecRef((PyObject *)(op))
#define Py_CLEAR(op) \
    do { \
        PyObject *_py_tmp = (PyObject *)(op); \
        if (_py_tmp != NULL) { \
            (op) = NULL; \
            Py_DecRef(_py_tmp); \
        } \
    } while (0)

/* ---- Singletons (the 3.13 Limited API spelling) ---------------------- */
#define Py_CONSTANT_NONE 0
#define Py_CONSTANT_FALSE 1
#define Py_CONSTANT_TRUE 2
#define Py_CONSTANT_ELLIPSIS 3
#define Py_CONSTANT_NOT_IMPLEMENTED 4
#define Py_CONSTANT_ZERO 5
#define Py_CONSTANT_ONE 6
#define Py_CONSTANT_EMPTY_STR 7
#define Py_CONSTANT_EMPTY_BYTES 8
#define Py_CONSTANT_EMPTY_TUPLE 9

PyObject *Py_GetConstant(unsigned int constant_id);
PyObject *Py_GetConstantBorrowed(unsigned int constant_id);

#define Py_None Py_GetConstantBorrowed(Py_CONSTANT_NONE)
#define Py_False Py_GetConstantBorrowed(Py_CONSTANT_FALSE)
#define Py_True Py_GetConstantBorrowed(Py_CONSTANT_TRUE)
#define Py_RETURN_NONE return Py_NewRef(Py_None)
#define Py_RETURN_TRUE return Py_NewRef(Py_True)
#define Py_RETURN_FALSE return Py_NewRef(Py_False)

#define Py_Is(x, y) ((x) == (y))
#define Py_IsNone(x) Py_Is((x), Py_None)
#define Py_IsTrue(x) Py_Is((x), Py_True)
#define Py_IsFalse(x) Py_Is((x), Py_False)

/* ---- Object protocol ------------------------------------------------- */
PyTypeObject *Py_TYPE(PyObject *o);
PyObject *PyObject_Type(PyObject *o);
PyObject *PyObject_Repr(PyObject *o);
PyObject *PyObject_Str(PyObject *o);
PyObject *PyObject_GetAttrString(PyObject *o, const char *name);
int PyObject_SetAttrString(PyObject *o, const char *name, PyObject *v);
int PyObject_HasAttrString(PyObject *o, const char *name);
PyObject *PyObject_Call(PyObject *callable, PyObject *args, PyObject *kwargs);
PyObject *PyObject_CallObject(PyObject *callable, PyObject *args);
PyObject *PyObject_CallNoArgs(PyObject *callable);
int PyObject_IsTrue(PyObject *o);
int PyObject_Not(PyObject *o);
Py_ssize_t PyObject_Size(PyObject *o);
Py_ssize_t PyObject_Length(PyObject *o);
int PyCallable_Check(PyObject *o);
int PyObject_IsInstance(PyObject *inst, PyObject *cls);

/* ---- Types ------------------------------------------------------------ */
PyObject *PyType_GetName(PyTypeObject *type);
PyObject *PyType_GetQualName(PyTypeObject *type);
PyObject *PyType_GetFullyQualifiedName(PyTypeObject *type);
int PyType_IsSubtype(PyTypeObject *a, PyTypeObject *b);

/* ---- Numbers ---------------------------------------------------------- */
PyObject *PyLong_FromLong(long v);
PyObject *PyLong_FromLongLong(long long v);
PyObject *PyLong_FromSsize_t(Py_ssize_t v);
PyObject *PyLong_FromDouble(double v);
long PyLong_AsLong(PyObject *o);
long long PyLong_AsLongLong(PyObject *o);
Py_ssize_t PyLong_AsSsize_t(PyObject *o);
double PyLong_AsDouble(PyObject *o);
int PyLong_Check(PyObject *o);

PyObject *PyFloat_FromDouble(double v);
double PyFloat_AsDouble(PyObject *o);
int PyFloat_Check(PyObject *o);

PyObject *PyBool_FromLong(long v);
int PyBool_Check(PyObject *o);

/* ---- Strings ---------------------------------------------------------- */
PyObject *PyUnicode_FromString(const char *u);
PyObject *PyUnicode_FromStringAndSize(const char *u, Py_ssize_t size);
PyObject *PyUnicode_FromFormat(const char *format, ...);
const char *PyUnicode_AsUTF8AndSize(PyObject *o, Py_ssize_t *size);
Py_ssize_t PyUnicode_GetLength(PyObject *o);
PyObject *PyUnicode_Concat(PyObject *left, PyObject *right);
int PyUnicode_CompareWithASCIIString(PyObject *o, const char *s);
int PyUnicode_Check(PyObject *o);

/* ---- Tuples / lists / dicts ------------------------------------------ */
PyObject *PyTuple_New(Py_ssize_t size);
Py_ssize_t PyTuple_Size(PyObject *p);
PyObject *PyTuple_GetItem(PyObject *p, Py_ssize_t pos);
int PyTuple_SetItem(PyObject *p, Py_ssize_t pos, PyObject *o);
PyObject *PyTuple_Pack(Py_ssize_t n, ...);
int PyTuple_Check(PyObject *o);

PyObject *PyList_New(Py_ssize_t len);
Py_ssize_t PyList_Size(PyObject *list);
PyObject *PyList_GetItem(PyObject *list, Py_ssize_t index);
int PyList_SetItem(PyObject *list, Py_ssize_t index, PyObject *item);
int PyList_Append(PyObject *list, PyObject *item);
int PyList_Check(PyObject *o);

PyObject *PyDict_New(void);
int PyDict_SetItem(PyObject *p, PyObject *key, PyObject *val);
int PyDict_SetItemString(PyObject *p, const char *key, PyObject *val);
PyObject *PyDict_GetItem(PyObject *p, PyObject *key);
PyObject *PyDict_GetItemString(PyObject *p, const char *key);
int PyDict_Next(PyObject *p, Py_ssize_t *ppos, PyObject **pkey, PyObject **pvalue);
Py_ssize_t PyDict_Size(PyObject *p);
int PyDict_Check(PyObject *o);

/* ---- Exceptions ------------------------------------------------------ */
PyAPI_DATA(PyObject *) PyExc_BaseException;
PyAPI_DATA(PyObject *) PyExc_Exception;
PyAPI_DATA(PyObject *) PyExc_ArithmeticError;
PyAPI_DATA(PyObject *) PyExc_LookupError;
PyAPI_DATA(PyObject *) PyExc_AttributeError;
PyAPI_DATA(PyObject *) PyExc_IndexError;
PyAPI_DATA(PyObject *) PyExc_KeyError;
PyAPI_DATA(PyObject *) PyExc_MemoryError;
PyAPI_DATA(PyObject *) PyExc_NotImplementedError;
PyAPI_DATA(PyObject *) PyExc_OverflowError;
PyAPI_DATA(PyObject *) PyExc_RuntimeError;
PyAPI_DATA(PyObject *) PyExc_SystemError;
PyAPI_DATA(PyObject *) PyExc_TypeError;
PyAPI_DATA(PyObject *) PyExc_ValueError;
PyAPI_DATA(PyObject *) PyExc_ZeroDivisionError;

void PyErr_SetString(PyObject *type, const char *message);
void PyErr_SetObject(PyObject *type, PyObject *value);
void PyErr_SetNone(PyObject *type);
PyObject *PyErr_Format(PyObject *exception, const char *format, ...);
PyObject *PyErr_Occurred(void);
void PyErr_Clear(void);
PyObject *PyErr_NoMemory(void);
int PyErr_BadArgument(void);
PyObject *PyErr_NewException(const char *name, PyObject *base, PyObject *dict);
PyObject *PyErr_GetRaisedException(void);
void PyErr_SetRaisedException(PyObject *exc);
int PyErr_ExceptionMatches(PyObject *exc);
int PyErr_GivenExceptionMatches(PyObject *given, PyObject *exc);
void PyErr_Print(void);

/* ---- Argument parsing / value building -------------------------------- */
int PyArg_ParseTuple(PyObject *args, const char *format, ...);
int PyArg_ParseTupleAndKeywords(PyObject *args, PyObject *kw, const char *format, char **keywords, ...);
int PyArg_UnpackTuple(PyObject *args, const char *name, Py_ssize_t min, Py_ssize_t max, ...);
PyObject *Py_BuildValue(const char *format, ...);

/* ---- Modules ---------------------------------------------------------- */
PyObject *PyModule_Create2(PyModuleDef *def, int apiver);
#define PyModule_Create(module) PyModule_Create2((module), PYTHON_API_VERSION)
int PyModule_AddObjectRef(PyObject *module, const char *name, PyObject *value);
int PyModule_Add(PyObject *module, const char *name, PyObject *value);
int PyModule_AddObject(PyObject *module, const char *name, PyObject *value);
int PyModule_AddIntConstant(PyObject *module, const char *name, long value);
int PyModule_AddStringConstant(PyObject *module, const char *name, const char *value);
PyObject *PyModule_GetDict(PyObject *module);
const char *PyModule_GetName(PyObject *module);
void *PyModule_GetState(PyObject *module);

#define PyModule_AddIntMacro(m, c) PyModule_AddIntConstant((m), #c, (c))
#define PyModule_AddStringMacro(m, c) PyModule_AddStringConstant((m), #c, (c))

/* ---- Embedding (a host program's lifecycle calls; no-ops in the shim) ---- */
void Py_Initialize(void);
int Py_IsInitialized(void);
int Py_FinalizeEx(void);
void Py_Finalize(void);

/* ---- Memory ----------------------------------------------------------- */
void *PyMem_Malloc(size_t n);
void *PyMem_Calloc(size_t nelem, size_t elsize);
void *PyMem_Realloc(void *p, size_t n);
void PyMem_Free(void *p);

#endif /* Py_PYTHON_H */
