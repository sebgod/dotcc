#ifndef _STDLIB_H
#define _STDLIB_H

/* dotcc's <stdlib.h> — memory, conversions, RNG, environment, program
   control, and generic sort/search. Implementations: malloc/free/strtod/
   atof in DotCC.Libc/Libc.cs, the rest in DotCC.Libc/StdlibLib.cs. Length
   arguments use plain `int` (dotcc's size_t stand-in). */

#ifndef NULL
#define NULL ((void *)0)
#endif

#define EXIT_SUCCESS 0
#define EXIT_FAILURE 1
#define RAND_MAX     32767

/* div/ldiv/lldiv result structs. Runtime-owned (see <time.h>'s struct tm):
   the runtime supplies Libc.div_t / ldiv_t / lldiv_t, the bodies here are for
   the IR only and must match them (RuntimeOwnedAggregateTests). */
typedef struct div_t { int quot; int rem; } div_t;
typedef struct ldiv_t { long quot; long rem; } ldiv_t;
typedef struct lldiv_t { long long quot; long long rem; } lldiv_t;

/* Memory management. */
void* malloc(int size);
void* calloc(int n, int size);
void* realloc(void* p, int size);
void free(void* p);

/* String -> number conversions. strtod parses a leading double and (if endptr
   is non-null) reports where parsing stopped; atof is strtod without endptr. */
double strtod(const char *nptr, char **endptr);
double atof(const char *nptr);
long strtol(const char *nptr, char **endptr, int base);
long strtoll(const char *nptr, char **endptr, int base);
unsigned long strtoul(const char *nptr, char **endptr, int base);
unsigned long strtoull(const char *nptr, char **endptr, int base);
int atoi(const char *nptr);
long atol(const char *nptr);
long atoll(const char *nptr);

/* Integer arithmetic. */
int abs(int n);
long labs(long n);
long llabs(long n);
div_t div(int num, int den);
ldiv_t ldiv(long num, long den);
lldiv_t lldiv(long num, long den);

/* Pseudo-random numbers. */
int rand(void);
void srand(unsigned int seed);

/* Environment + program control. */
char* getenv(const char *name);
int setenv(const char *name, const char *value, int overwrite);
int unsetenv(const char *name);
char *realpath(const char *path, char *resolved_path);
int system(const char *command);
void exit(int code);
void _Exit(int code);
void abort(void);

/* Generic sort / search — comparator is a function pointer. */
void qsort(void* base, int n, int size, int (*cmp)(const void*, const void*));
void* bsearch(const void* key, const void* base, int n, int size, int (*cmp)(const void*, const void*));

/* Multibyte string conversion (C99 7.22.8): the multibyte encoding is UTF-8 and
   wchar_t is dotcc's UTF-16 unit. (size_t)-1 reports an invalid sequence. */
unsigned long mbstowcs(wchar_t* dst, const char* src, unsigned long n);
unsigned long wcstombs(char* dst, const wchar_t* src, unsigned long n);

#endif
