/* Wide I/O: formatted output (wprintf/fwprintf/swprintf), formatted input
   (swscanf), and character/line output (putwchar/fputws). dotcc's wchar_t is the
   MSVC-shaped 16-bit type, so this matches the MSVC oracle (also 16-bit) but
   diverges from gcc's 32-bit wchar_t — see no-gcc-oracle.txt. All output is ASCII
   (the wide→narrow conversion is identical across the two 16-bit compilers), and
   only wide stdio is used (no mixing with narrow printf, which MSVC's stream
   orientation would forbid). */
#include <stdio.h>
#include <wchar.h>

int main(void) {
    /* wprintf: %d, wide %ls, %f */
    wprintf(L"d=%d ls=%ls f=%.1f\n", 42, L"hi", 3.5);

    /* fwprintf to a stream */
    fwprintf(stdout, L"fw=%d\n", 7);

    /* wide character / line output */
    putwchar(L'A');
    putwchar(L'\n');
    fputws(L"line\n", stdout);

    /* swprintf into a wide buffer (returns the wide-char count) */
    wchar_t buf[32];
    int n = swprintf(buf, 32, L"%d-%ls", 99, L"x");
    wprintf(L"swn=%d buf0=%d len=%d\n", n, (int)buf[0], (int)wcslen(buf));

    /* swscanf from a wide string into an int + a wide token */
    int a;
    wchar_t word[16];
    int got = swscanf(L"123 abc", L"%d %ls", &a, word);
    wprintf(L"got=%d a=%d w0=%d\n", got, a, (int)word[0]);

    return 0;
}
