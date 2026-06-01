// A block comment whose close line is a run of stars before the slash (star,
// star, slash) — Lua's doc-comment style, used pervasively (e.g. lfunc.c's
// 'newupval'). The lexer must treat a star RUN before the slash as part of the
// comment terminator, not its body; the earlier regex ran such a comment on to
// the NEXT close, silently swallowing the code between. Below we close doc
// comments that way, then define code that MUST survive. gcc/MSVC agree.
// (This header is // line comments precisely so it can describe the token
// without writing it literally and closing itself early.)
#include <stdio.h>

/*
** Doc comment whose close line is `**` then `/` — with 'quotes' and a * star
** inside, to mirror Lua's prose. Everything below this MUST still compile.
**/
static int after_doc(int x) { return x + 1; }

/** one-liner doc with leading double-star **/
static int one_liner(int x) { return x * 2; }

/***/    /* an all-stars comment, then a normal one */
int main(void) {
    int a = after_doc(40);      /* 41 */
    int b = one_liner(a);       /* 82 */
    printf("%d %d\n", a, b);    /* 41 82 */
    return 0;
}
