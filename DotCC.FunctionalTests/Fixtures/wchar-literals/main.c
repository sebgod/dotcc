/* wchar_t + L"…"/L'x' literals and the <wchar.h> wide-string library.
   dotcc's wchar_t is the MSVC-shaped 16-bit UTF-16 type, so this matches MSVC
   (whose wchar_t is also 16-bit) but diverges from gcc/Linux's 32-bit wchar_t —
   see no-gcc-oracle.txt. All output is code-unit integers (no wide I/O), and
   wcscmp is reduced to its sign (C only guarantees the sign), so the snapshot is
   toolchain-agnostic between the two 16-bit compilers. */
#include <stdio.h>
#include <wchar.h>

static const wchar_t hello[] = L"Hello";

int main(void) {
    /* the ABI commitment: dotcc's wchar_t is 16-bit (== MSVC, != gcc's 32-bit) */
    printf("wsize=%d\n", (int)sizeof(wchar_t));

    /* L"…" string literal — pooled pointer, wcslen, indexing */
    wchar_t *p = L"abc";
    printf("len=%d a=%d b=%d c=%d\n", (int)wcslen(p), (int)p[0], (int)p[1], (int)p[2]);

    /* L'x' character constant */
    wchar_t z = L'Z';
    printf("z=%d\n", (int)z);

    /* local array init from L"…" + wcscat */
    wchar_t buf[16] = L"foo";
    wcscat(buf, L"bar");
    printf("cat=%d len=%d\n", (int)buf[3], (int)wcslen(buf));

    /* compare (sign only) */
    printf("cmp=%d ncmp=%d\n", (wcscmp(L"abc", L"abd") < 0) ? 1 : 0, wcsncmp(L"abc", L"abd", 2));

    /* search: chr / rchr / str — offsets into one buffer */
    wchar_t *h = L"a.b.c";
    printf("chr=%d rchr=%d str=%d\n",
        (int)(wcschr(h, L'.') - h),
        (int)(wcsrchr(h, L'.') - h),
        (int)(wcsstr(h, L"b.c") - h));

    /* span / pbrk */
    wchar_t *q = L"aabbc";
    printf("spn=%d cspn=%d\n", (int)wcsspn(q, L"ab"), (int)wcscspn(q, L"c"));
    wchar_t *r = L"a.b";
    printf("pbrk=%d\n", (int)(wcspbrk(r, L".") - r));

    /* wide memory */
    wchar_t m[4];
    wmemset(m, L'x', 3);
    m[3] = 0;
    wchar_t m2[4];
    wmemcpy(m2, m, 4);
    printf("mem=%d %d cmp=%d\n", (int)m[2], (int)m2[0], wmemcmp(m, L"xxx", 3));

    /* wide -> number, with endptr (transcode -> byte cores) */
    wchar_t *end;
    long v = wcstol(L"  42abc", &end, 10);
    printf("tol=%d rest=%d\n", (int)v, (int)*end);
    unsigned long u = wcstoul(L"0x1F!", &end, 0);
    printf("toul=%d rest=%d\n", (int)u, (int)*end);
    double d = wcstod(L"3.5xyz", &end);
    printf("tod=%d rest=%d\n", (int)(d * 2), (int)*end);

    /* global const array — RVA path */
    printf("hello=%d %d\n", (int)hello[0], (int)wcslen((wchar_t*)hello));
    return 0;
}
