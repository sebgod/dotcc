// Exercises bitwise operators: & | ^ ~ << >>, plus compound &= |= ^= <<= >>=.
// Sets up a flag word, masks bits, then prints individual decoded fields.

#include "stdio.h"

int main() {
    // Compose a 16-bit word out of three fields:
    //   high nibble  (bits 12..15) = 0xA
    //   mid byte     (bits 4..11)  = 0x5C
    //   low nibble   (bits 0..3)   = 0x3
    int high = 0xA;
    int mid  = 0x5C;
    int low  = 0x3;

    int word = 0;
    word = word | (high << 12);
    word |= (mid << 4);
    word |= low;
    printf("packed: 0x%x\n", word);

    // Decode each field back via shift + mask.
    int dec_high = (word >> 12) & 0xF;
    int dec_mid  = (word >> 4)  & 0xFF;
    int dec_low  = word & 0xF;
    printf("decoded: high=0x%x mid=0x%x low=0x%x\n", dec_high, dec_mid, dec_low);

    // XOR-toggle a bit, ~ to invert (lower 16 only).
    int flags = 0xF0F0;
    flags ^= 0x00FF;
    printf("xor: 0x%x\n", flags);
    int inv = (~flags) & 0xFFFF;
    printf("inv: 0x%x\n", inv);

    // Compound shifts.
    int sh = 1;
    sh <<= 4;
    printf("shl: %d\n", sh);
    sh >>= 2;
    printf("shr: %d\n", sh);

    return 0;
}
