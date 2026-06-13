/* C23 #embed — bake a file's bytes into a char array at compile time.
 * dotcc reads blob.bin's raw bytes (here the ASCII "embed works") and emits a
 * single carrier token that the IR expands into the array's byte initializers
 * (NOT a multi-million-token comma list). Also exercises the limit() parameter.
 * gcc 15+ is the oracle (#embed is C23); MSVC's cl.exe has no #embed (opts out). */
#include <stdio.h>

static const unsigned char data[] = {
    #embed "blob.bin"
};

int main(void) {
    /* __has_embed (C23): the resource-detection operator, usable in #if. */
#if __has_embed("blob.bin")
    printf("has-embed\n");
#endif
#if !__has_embed("definitely-absent.bin")
    printf("absent-ok\n");
#endif
    printf("size=%d\n", (int)sizeof(data));
    for (int i = 0; i < (int)sizeof(data); i++) putchar(data[i]);
    putchar('\n');

    /* limit(N): embed only the first N bytes. */
    unsigned char head[] = {
        #embed "blob.bin" limit(5)
    };
    printf("head=%d:", (int)sizeof(head));
    for (int i = 0; i < (int)sizeof(head); i++) putchar(head[i]);
    putchar('\n');
    return 0;
}
