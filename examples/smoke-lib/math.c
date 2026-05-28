// dotcc -shared demo: a tiny C library with two exported entries and one
// internal helper. After `dotcc -shared math.c -o build/`, run
// `dotnet publish -c Release` in build/ to get a real .dll/.so/.dylib.

int add(int a, int b) {
    return a + b;
}

double scale(double v, double k) {
    return v * k;
}

// `static` = internal linkage. Visible to add()/scale() but not exported
// to consumers of the resulting shared library.
static int helper(int x) {
    return x * 2;
}

int double_it(int x) {
    return helper(x);
}
