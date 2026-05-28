// `goto` + labels — exercises the canonical idioms:
//   - Early exit from a loop via `goto end;` (escape multiple nesting levels).
//   - Error-cleanup chain ("goto-out" pattern): allocate a, then b, then c;
//     if any step fails, jump to a cleanup label that frees what's been
//     allocated so far. Real C uses this where C++ would use RAII.
//   - Forward-only labels work — C# accepts identical syntax.
//   - Empty statement `;` after a trailing label (`done: ;`).

#include "stdio.h"
#include "stdlib.h"
#include "stddef.h"

int find_negative(int* arr, int n) {
    int i = 0;
    int idx = -1;
loop:
    if (i >= n) goto done;
    if (arr[i] < 0) {
        idx = i;
        goto done;
    }
    i = i + 1;
    goto loop;
done:
    return idx;
}

// Classic "goto-out" cleanup ladder. Each allocation may succeed or fail;
// on failure jump to the corresponding cleanup label so we free what's
// already been allocated in reverse order.
int allocate_chain(int fail_at) {
    int* a = NULL;
    int* b = NULL;
    int* c = NULL;
    int result = 0;

    a = (int*)malloc(sizeof(int));
    if (a == NULL || fail_at == 1) { result = -1; goto err_a; }
    *a = 1;

    b = (int*)malloc(sizeof(int));
    if (b == NULL || fail_at == 2) { result = -2; goto err_b; }
    *b = 2;

    c = (int*)malloc(sizeof(int));
    if (c == NULL || fail_at == 3) { result = -3; goto err_c; }
    *c = 3;

    // Success path: use all three.
    result = *a + *b + *c;

    free(c);
err_c:
    free(b);
err_b:
    free(a);
err_a:
    return result;
}

int main() {
    int arr[5];
    arr[0] = 10;
    arr[1] = 20;
    arr[2] = -7;
    arr[3] = 30;
    arr[4] = 40;
    printf("first negative idx: %d\n", find_negative(arr, 5));

    // No failure: full chain runs, result is 1+2+3 = 6.
    printf("chain ok: %d\n", allocate_chain(0));

    // Failure at each stage — different cleanup paths.
    printf("chain fail@1: %d\n", allocate_chain(1));
    printf("chain fail@2: %d\n", allocate_chain(2));
    printf("chain fail@3: %d\n", allocate_chain(3));

    return 0;
}
