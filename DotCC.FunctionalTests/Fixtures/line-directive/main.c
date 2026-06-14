/* `#line N` / `#line N "file"` (C89 §6.10.4) renumbers __LINE__ and overrides
 * the presumed __FILE__ for the lines that FOLLOW the directive — used by code
 * generators (yacc/lex, chibi's stub emitter, ...) so __LINE__/__FILE__ point at
 * the original source. dotcc honours both macros; compiler diagnostics still use
 * physical lines (documented in C-SUPPORT.md). gcc and MSVC are oracles — the
 * output is fully deterministic because #line fixes the values. */
#include <stdio.h>

int main(void) {
#line 100 "virtual.c"
    printf("line=%d\n", __LINE__);    /* the line after #line 100 is line 100 */
    printf("file=%s\n", __FILE__);    /* overridden presumed name */
#line 1
    printf("reset=%d\n", __LINE__);   /* renumbered again: this is line 1 */
    return 0;
}
