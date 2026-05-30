#ifndef _STDLIB_H
#define _STDLIB_H

/* dotcc's <stdlib.h> — memory + program-control surface. malloc/free
   route to NativeMemory.Alloc/Free under the hood. */

void* malloc(int size);
void free(void* p);

/* String -> number conversions. strtod parses a leading double and (if endptr
   is non-null) reports where parsing stopped; atof is strtod without endptr. */
double strtod(const char *nptr, char **endptr);
double atof(const char *nptr);

#endif
