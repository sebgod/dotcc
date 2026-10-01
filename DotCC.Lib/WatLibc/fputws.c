/* fputws: a wide string to a stream, as UTF-8, without a newline; 0, or -1. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>
#include <stdlib.h>

int fputws(const wchar_t *s, FILE *f)
{
    char small[256];
    size_t n = __wcs_to_utf8(NULL, s, (size_t)-1);
    char *bytes = n <= sizeof small ? small : malloc(n);
    if (!bytes) return -1;
    __wcs_to_utf8(bytes, s, n);
    size_t written = fwrite(bytes, 1, n, f);
    if (bytes != small) free(bytes);
    return written == n ? 0 : -1;
}
