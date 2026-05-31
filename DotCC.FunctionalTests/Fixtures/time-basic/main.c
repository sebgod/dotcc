/* <time.h> scalar surface. Comparisons stay inside if() (dotcc's printf
   builder has no bool arg overload), so the output is deterministic and
   both oracles validate. */

#include <stdio.h>
#include <time.h>

int main(void)
{
    printf("difftime = %.1f\n", difftime(1000, 400));   /* 600.0 */

    time_t t1 = time(NULL);
    time_t t2 = time(NULL);
    if (t2 >= t1)                 printf("time monotonic\n");
    if (time(NULL) > 1000000000)  printf("time after 2001\n");  /* we're well past */

    clock_t c = clock();
    if (c >= 0)                   printf("clock nonneg\n");

    return 0;
}
