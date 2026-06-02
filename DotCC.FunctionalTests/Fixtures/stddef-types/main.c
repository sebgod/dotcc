/* <stddef.h> is the canonical home of size_t / ptrdiff_t (C90 7.17) — a
 * program may include ONLY <stddef.h> and use them. (dotcc also exposes them
 * via <stdint.h>, which includes <stddef.h>.) ptrdiff_t is the type of a
 * pointer difference; size_t of a sizeof / object size. Values printed as int
 * (ABI-stable: int=4 here). gcc is the oracle. */
#include <stddef.h>
#include <stdio.h>

int main(void) {
  int arr[10];
  size_t n = sizeof(arr) / sizeof(arr[0]);   /* 10 */
  ptrdiff_t d = &arr[7] - &arr[2];           /* 5  */
  size_t sz = sizeof(int);                   /* 4  */

  printf("%d\n", (int)n);
  printf("%d\n", (int)d);
  printf("%d\n", (int)sz);
  return 0;
}
