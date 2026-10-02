/* A function with internal linkage, named as one in another unit is. */
static int mk(int a, int b) { return a * 10 + b; }
int a_entry(void) { return mk(1, 2); }
