/* A C `switch` is int-semantic: the controlling expression is integer-promoted
 * and case labels are converted to that type. So a switch on a plain `int` may
 * legally use enumerator case labels (Lua's lexer: `switch (ls->t.token) { case
 * TK_NAME: … }`, where `token` is an `int` field, cases are `enum RESERVED`
 * members). dotcc lowers enums to real C# enums, and C# rejects both
 * `switch(int){ case Enum.X }` and `switch(Enum){ case (int)… }` — so it decays
 * the subject AND the enumerator case labels to `(int)` (uniform int = pure C
 * semantics). A switch ON an enum value keeps working through the same decay. */
#include <stdio.h>

enum Tok { TK_NAME = 257, TK_INT, TK_FLOAT };

/* int subject, enumerator case labels (the Lua-lexer shape) */
static const char *kind(int token) {
    switch (token) {
        case TK_NAME:  return "name";
        case TK_INT:   return "int";
        case TK_FLOAT: return "float";
        default:       return "?";
    }
}

/* enum subject, enumerator case labels */
static int weight(enum Tok t) {
    switch (t) {
        case TK_NAME:  return 1;
        case TK_INT:   return 2;
        case TK_FLOAT: return 3;
        default:       return 0;
    }
}

int main(void) {
    /* enum->int at decl init is already handled; route through int locals so the
     * int-param call (a separate enum->param-cast gap) isn't exercised here. */
    int a = TK_NAME, b = TK_INT, c = TK_FLOAT;
    printf("%s %s %s %s\n", kind(a), kind(b), kind(c), kind(42));
    printf("%d\n", weight(TK_NAME) + weight(TK_INT) + weight(TK_FLOAT));
    return 0;
}
