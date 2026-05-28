/* Escaped quotes inside string literals — the canonical C pattern that
   the previous `"[^"]*"` lexer regex couldn't handle. Now resolved
   upstream in LALR.CC's IRxParser (alternation support), so the
   `"(\\["\\nrtbf0...]|[^"\\])*"` regex in c.lalr.yaml matches the full
   C string-literal shape including embedded `\"`. */

#include <stdio.h>

int main(void)
{
    printf("with \"embedded\" quotes\n");
    printf("escape combos: \\ \" /\n");
    printf("trailing backslash safe: x\\\n");
    return 0;
}
