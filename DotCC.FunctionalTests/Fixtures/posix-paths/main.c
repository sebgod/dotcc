/* Built with -fposix-paths (posix-paths.txt): on Windows the runtime shows
   the program POSIX paths (/c/Users/... for C:\Users\...), and every function
   taking a path accepts that form. Elsewhere the flag changes nothing, so the
   same checks hold on every host: paths the libc hands out are absolute POSIX
   paths, and handing them back works. (GH #254) */
/* The gcc oracle builds with -std=, where glibc declares realpath only for an
   XSI program (_XOPEN_SOURCE; _POSIX_C_SOURCE alone is not enough), so ask for
   that surface (dotcc's synthetic headers declare it unconditionally). */
#define _XOPEN_SOURCE 700
#include <dirent.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/stat.h>
#include <unistd.h>

/* Whether s holds c (dotcc's strchr takes a non-const char *, GH #243). */
static int has_char(const char *s, char c)
{
    for (; *s != 0; s++) { if (*s == c) { return 1; } }
    return 0;
}

static int posix_absolute(const char *p)
{
    return p != NULL && p[0] == '/' && !has_char(p, '\\');
}

int main(void)
{
    char cwd[4096];
    if (getcwd(cwd, sizeof cwd) == NULL) { puts("getcwd failed"); return 1; }
    printf("getcwd is a POSIX path: %d\n", posix_absolute(cwd));

    /* The cwd's own spelling goes back in: stat, opendir. */
    struct stat st;
    printf("stat(getcwd()) works: %d\n", stat(cwd, &st) == 0 && S_ISDIR(st.st_mode));
    DIR *d = opendir(cwd);
    printf("opendir(getcwd()) works: %d\n", d != NULL);
    if (d != NULL) { closedir(d); }

    /* A temp file named by tmpnam: create, find, resolve, remove. */
    char name[L_tmpnam];
    if (tmpnam(name) == NULL) { puts("tmpnam failed"); return 1; }
    printf("tmpnam is a POSIX path: %d\n", posix_absolute(name));
    FILE *f = fopen(name, "w");
    printf("fopen(tmpnam) works: %d\n", f != NULL);
    if (f != NULL) { fputs("x", f); fclose(f); }
    printf("access(tmpnam) works: %d\n", access(name, F_OK) == 0);
    char *real = realpath(name, NULL);
    printf("realpath is a POSIX path: %d\n", posix_absolute(real));
    printf("realpath goes back in: %d\n", real != NULL && access(real, F_OK) == 0);
    free(real);
    printf("remove(tmpnam) works: %d\n", remove(name) == 0);

    /* Path-valued environment: no Windows separators. */
    const char *path = getenv("PATH");
    printf("PATH has no ';': %d\n", path != NULL && !has_char(path, ';'));
    const char *home = getenv("HOME");
    printf("HOME is unset or a POSIX path: %d\n", home == NULL || posix_absolute(home));
    return 0;
}
