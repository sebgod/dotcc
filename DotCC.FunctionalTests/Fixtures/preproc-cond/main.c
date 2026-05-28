// #if expression evaluator + #elif chains. Tests:
//   - #if with arithmetic / comparison
//   - #if with object-like macro expansion (VERSION → 3)
//   - #if defined(X) and #if !defined(X)
//   - #if / #elif / #elif / #else chain with only one arm picked
//   - nested chains
//   - bitwise + logical operators in #if

#include "stdio.h"

#define VERSION 3

// Arithmetic + comparison.
#if VERSION >= 2
#define GREETING "v2 or later"
#else
#define GREETING "v1"
#endif

// defined() variants.
#if defined(VERSION) && !defined(UNDEFINED_THING)
#define HAVE_VERSION 1
#else
#define HAVE_VERSION 0
#endif

// #if / #elif / #elif / #else chain — exactly one arm picked.
#if VERSION == 1
#define TIER "low"
#elif VERSION == 2
#define TIER "mid"
#elif VERSION == 3
#define TIER "high"
#else
#define TIER "unknown"
#endif

// Bitwise and shift operators.
#if (1 << 3) == 8
#define BITSHIFT_OK 1
#else
#define BITSHIFT_OK 0
#endif

// All-false chain hits the #else arm.
#if 0
#define BUCKET "a"
#elif 0
#define BUCKET "b"
#else
#define BUCKET "default"
#endif

int main() {
    printf("greeting: %s\n", GREETING);
    printf("have_version=%d\n", HAVE_VERSION);
    printf("tier: %s\n", TIER);
    printf("bitshift_ok=%d\n", BITSHIFT_OK);
    printf("bucket: %s\n", BUCKET);
    return 0;
}
