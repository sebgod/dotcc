#ifndef _STDLIB_H
#define _STDLIB_H

/* dotcc's <stdlib.h> — memory, conversions, RNG, environment, program
   control, and generic sort/search. Implementations: malloc/free/strtod/
   atof in DotCC.Libc/Libc.cs, the rest in DotCC.Libc/StdlibLib.cs. Length
   arguments use plain `int` (dotcc's size_t stand-in). */

#ifndef NULL
#define NULL null
#endif

#define EXIT_SUCCESS 0
#define EXIT_FAILURE 1
#define RAND_MAX     32767

/* div/ldiv/lldiv result structs. These names are pre-registered in
   dotcc's TypeNameRewriter (Compiler.PredefinedTypeNames) and resolve to
   the Libc.div_t / ldiv_t / lldiv_t value structs — no typedef needed. */

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
int system(const char *command);
void exit(int code);
void _Exit(int code);
void abort(void);

/* Generic sort / search — comparator is a function pointer. */
void qsort(void* base, int n, int size, int (*cmp)(const void*, const void*));
void* bsearch(const void* key, const void* base, int n, int size, int (*cmp)(const void*, const void*));

#endif
