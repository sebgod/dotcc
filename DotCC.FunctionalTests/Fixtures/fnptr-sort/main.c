// Function pointers via typedef. Mini bubble-sort that takes a comparator
// callback — classic qsort-style use case.

#include "stdio.h"

typedef int (*Comparator)(int a, int b);

int ascending(int a, int b) { return a - b; }
int descending(int a, int b) { return b - a; }

void bubble_sort(int* arr, int n, Comparator cmp) {
    for (int i = 0; i < n; i = i + 1) {
        for (int j = 0; j < n - 1 - i; j = j + 1) {
            if (cmp(arr[j], arr[j + 1]) > 0) {
                int t = arr[j];
                arr[j] = arr[j + 1];
                arr[j + 1] = t;
            }
        }
    }
}

void print_arr(int* arr, int n, char* label) {
    printf("%s:", label);
    for (int i = 0; i < n; i = i + 1) {
        printf(" %d", arr[i]);
    }
    printf("\n");
}

int main() {
    int data[] = {3, 1, 4, 1, 5, 9, 2, 6};
    int n = 8;

    Comparator asc = &ascending;
    bubble_sort(data, n, asc);
    print_arr(data, n, "asc");

    bubble_sort(data, n, &descending);
    print_arr(data, n, "desc");

    return 0;
}
