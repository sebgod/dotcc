/* End-to-end coverage for the extended <string.h> surface added beyond the
   strlen/strcmp/strcpy + mem trio (see string-h-basic). Every function here is
   ISO C so both the MSVC and gcc oracles validate it. strtok (not the POSIX
   strtok_r) is used so cl.exe compiles it; strtok_r is unit-tested directly.
   _CRT_SECURE_NO_WARNINGS silences MSVC's deprecation noise on strcpy/strcat/
   strtok — informational only, the compile still succeeds. */

#define _CRT_SECURE_NO_WARNINGS

#include <stdio.h>
#include <string.h>

int main(void)
{
    /* strncmp — bounded compare. */
    printf("strncmp eq2 = %d\n", strncmp("abcX", "abcY", 3));
    printf("strncmp ne  = %d\n", strncmp("abc", "abd", 3) < 0 ? -1 : 1);

    /* strncpy — truncate (no NUL) and pad cases. */
    char t[8];
    memset(t, '#', 8);
    strncpy(t, "hi", 5);            /* copies "hi", pads 3 NULs, leaves t[5..7]='#' */
    printf("strncpy = %s|%d%d%d\n", t, t[5], t[6], t[7]);

    /* strcat / strncat. */
    char b[32];
    strcpy(b, "foo");
    strcat(b, "bar");
    strncat(b, "bazXXXX", 3);
    printf("cat = %s\n", b);

    /* strchr / strrchr. */
    char* s = "a.b.c";
    printf("strchr = %s\n", strchr(s, '.'));
    printf("strrchr = %s\n", strrchr(s, '.'));
    printf("strchr miss = %d\n", strchr(s, 'z') == NULL);

    /* strstr. */
    printf("strstr = %s\n", strstr("hello world", "wor"));
    printf("strstr empty = %s\n", strstr("xy", ""));
    printf("strstr miss = %d\n", strstr("abc", "xyz") == NULL);

    /* strspn / strcspn / strpbrk. */
    printf("strspn = %d\n", (int)strspn("123abc", "0123456789"));
    printf("strcspn = %d\n", (int)strcspn("abc,def", ",;"));
    printf("strpbrk = %s\n", strpbrk("abc,def", ",;"));

    /* memcmp / memmove / memchr. */
    printf("memcmp = %d\n", memcmp("abc", "abc", 3));
    char m[8] = "12345";
    memmove(m + 1, m, 4);           /* overlap: shift "1234" right -> "112345"[..] */
    printf("memmove = %s\n", m);
    char* hit = (char*)memchr("hello", 'l', 5);
    printf("memchr = %s\n", hit);

    /* strtok — split on commas. */
    char csv[] = "one,two,,three";
    for (char* tok = strtok(csv, ","); tok != NULL; tok = strtok(NULL, ","))
        printf("tok=%s\n", tok);

    return 0;
}
