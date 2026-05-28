// __FILE__, __LINE__, __func__ — predefined identifiers expanded at the
// use site. MSVC oracle confirms each lands at the same source position
// dotcc thinks it should.

#include "stdio.h"

void log_here() {
    // __func__ expands to the enclosing function name. __FILE__ and
    // __LINE__ to the current file and line at the directive position.
    printf("fn=%s file=%s line=%d\n", __func__, __FILE__, __LINE__);
}

int main() {
    printf("fn=%s file=%s line=%d\n", __func__, __FILE__, __LINE__);
    log_here();
    return 0;
}
