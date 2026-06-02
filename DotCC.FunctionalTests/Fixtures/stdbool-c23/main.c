/* <stdbool.h> is vestigial in C23: bool/true/false are first-class KEYWORDS,
 * so the header must NOT define them as macros (it would shadow the keywords
 * and make `#ifdef bool` wrongly true). Only __bool_true_false_are_defined
 * survives. dotcc's synthetic header gates the macro bodies on
 * `__STDC_VERSION__ < 202311L`, matching gcc. Under -std=c23 this prints the
 * keyword branch; the values are identical to the pre-C23 macro path.
 * gcc oracle runs -std=c2x; MSVC has no C23 frontend (opted out). */
#include <stdbool.h>
#include <stdio.h>

int main(void) {
#ifdef bool
  printf("bool-macro\n");
#else
  printf("bool-keyword\n");
#endif
  bool b = true;
  printf("%d %d\n", (int)b, (int)false);
  printf("def=%d\n", __bool_true_false_are_defined);
  return 0;
}
