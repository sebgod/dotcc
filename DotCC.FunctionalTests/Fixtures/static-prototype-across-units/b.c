/* Declared static before its use and defined after it (CPython's unionobject.c declares
   `static PyObject *make_union(PyObject *);` while typevarobject.c defines its own): the calls
   before the definition are this unit's mk, not a.c's. */
static int mk(int);
int b_entry(void) { return mk(5); }
static int mk(int a) { return a + 100; }
