// Exercises three struct features in one go:
//   - Forward declaration `struct Node;` (no body; type referenced before
//     the definition lands).
//   - Self-referential structs — `struct Node` contains `struct Node*`.
//   - Designated initializers — `{ .val = 7, .next = NULL }`.
//   - Struct return-by-value — `make_pair` returns a struct.

#include "stdio.h"
#include "stdlib.h"
#include "stddef.h"

// Forward declaration. The pointer-to-Node use below resolves to this.
struct Node;

// Helper struct, returned by value.
struct Pair {
    int first;
    int second;
};

// Self-referential definition.
struct Node {
    int val;
    struct Node* next;
};

// Return-by-value of a struct. C lets the caller take the result as a
// value or assign field-by-field; we emit a C# struct return, which is
// a by-value copy at the call site.
struct Pair make_pair(int a, int b) {
    struct Pair p = { .first = a, .second = b };
    return p;
}

// Linked-list ops using malloc + the self-ref pointer.
struct Node* push(struct Node* head, int v) {
    struct Node* n = (struct Node*)malloc(sizeof(struct Node));
    n->val = v;
    n->next = head;
    return n;
}

void print_list(struct Node* head) {
    struct Node* cur = head;
    while (cur != NULL) {
        printf("%d ", cur->val);
        cur = cur->next;
    }
    printf("\n");
}

void free_list(struct Node* head) {
    struct Node* cur = head;
    while (cur != NULL) {
        struct Node* next = cur->next;
        free(cur);
        cur = next;
    }
}

int main() {
    // Designated initializer with one designated field omitted —
    // C99 says it zero-fills, C# struct-init does the same.
    struct Pair p = { .second = 99 };
    printf("pair: (%d, %d)\n", p.first, p.second);

    // Return-by-value: caller gets a copy.
    struct Pair q = make_pair(3, 4);
    printf("made: (%d, %d)\n", q.first, q.second);

    // Linked list of three nodes, push-front order so iteration prints 3,2,1.
    struct Node* head = NULL;
    head = push(head, 1);
    head = push(head, 2);
    head = push(head, 3);
    print_list(head);
    free_list(head);

    return 0;
}
