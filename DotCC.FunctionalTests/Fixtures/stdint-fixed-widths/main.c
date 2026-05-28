/* Fixed-width integer types from <stdint.h>. Casting all printf args
   to int to keep formatting portable across dotcc + MSVC; the test is
   really about the typedefs parsing + the values surviving the round
   trip. */

#include <stdio.h>
#include <stdint.h>

int main(void)
{
    int8_t   s8  = -42;
    uint8_t  u8  = 200;
    int16_t  s16 = -1000;
    uint16_t u16 = 50000;
    int32_t  s32 = -123456;
    uint32_t u32 = 4000000000u;
    int64_t  s64 = -1234567890123L;
    uint64_t u64 = 9876543210000uL;

    size_t   sz = 4096;
    intptr_t ip = 0;

    printf("s8=%d u8=%d\n",  (int)s8, (int)u8);
    printf("s16=%d u16=%d\n", (int)s16, (int)u16);
    printf("s32=%d u32=%d\n", (int)s32, (int)u32);
    printf("s64=%d u64=%d\n", (int)s64, (int)u64);
    printf("sz=%d ip=%d\n",   (int)sz,  (int)ip);

    /* Limit macros. */
    printf("INT8_MAX=%d UINT8_MAX=%d\n",  INT8_MAX,  UINT8_MAX);
    printf("INT16_MAX=%d UINT16_MAX=%d\n", INT16_MAX, UINT16_MAX);
    printf("INT32_MAX=%d\n", INT32_MAX);

    return 0;
}
