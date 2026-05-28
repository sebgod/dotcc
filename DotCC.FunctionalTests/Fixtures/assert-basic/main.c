/* <assert.h> happy-path. All asserts pass; the program reaches the
   final printf. The failure path (where assert throws on a falsy
   condition) is covered by a unit test in DotCC.Tests — it can't be
   a fixture because the resulting non-zero exit code can't easily be
   asserted in the FixtureRunner pipeline that captures stdout. */

#include <stdio.h>
#include <assert.h>

int sum(int a, int b) {
    assert(a >= 0);
    assert(b >= 0);
    return a + b;
}

int main(void) {
    int total = sum(3, 4);
    assert(total == 7);
    printf("sum=%d\n", total);
    return 0;
}
