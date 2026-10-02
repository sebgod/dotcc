/* E1[E2] is (*((E1)+(E2))) (C11 6.5.2.1p2): the integer may come first, as musl's getenv's
   `l[*e]` has it. */
#include <stdio.h>
#include <string.h>

static int match(const char *name, char **e)
{
    size_t l = strlen(name);
    return !strncmp(name, *e, l) && l[*e] == '=';
}

int main(void)
{
    int a[3] = { 1, 2, 3 };
    char *env[] = { "PATH=/bin", 0 };
    printf("%d %c %d %d\n", 2[a], 1["xyz"], match("PATH", env), match("PAT", env));
    return 0;
}
