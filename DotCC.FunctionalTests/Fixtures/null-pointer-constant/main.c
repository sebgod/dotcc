// C's null pointer constant: an integer `0` used where a pointer is expected is
// a null pointer. C# won't implicitly convert int 0 to a pointer, so dotcc emits
// `null` at pointer returns and arguments. Oracled.
#include <stdio.h>

static int *find(int *arr, int n, int key) {
    for (int i = 0; i < n; i++) {
        if (arr[i] == key) return &arr[i];
    }
    return 0;                          // null pointer constant from a T* function
}

static int count_to_nul(const char *s) {
    int n = 0;
    while (s && *s) { n++; s++; }
    return n;
}

int main(void) {
    int a[3] = { 10, 20, 30 };
    int *p = find(a, 3, 20);           // found -> &a[1]
    int *q = find(a, 3, 99);           // not found -> null (return 0)
    printf("%d\n", p ? *p : -1);       // 20
    printf("%d\n", q ? *q : -1);       // -1 (q is null -> falsy)
    printf("%d\n", count_to_nul(0));   // 0 (null passed to a const char* param)
    return 0;
}
