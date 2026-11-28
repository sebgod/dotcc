#include <math.h>
#include <stdio.h>

/* Under plain <math.h> an unsuffixed function computes in double whatever the
   argument: an int or a float converts to double. */
int main(void) {
    int n = 2;
    float f = 2.0f;
    printf("sqrt(int)   %.17g\n", sqrt(n));
    printf("sqrt(float) %.17g\n", sqrt(f));
    printf("sin(float)  %.12g\n", sin(f));
    printf("exp(float)  %.12g\n", exp(f));
    printf("log(int)    %.12g\n", log(n));
    printf("pow(i, f)   %.12g\n", pow(n, f / 3.0f));
    printf("sizeof      %d\n", (int)sizeof(sqrt(f)));
    return 0;
}
