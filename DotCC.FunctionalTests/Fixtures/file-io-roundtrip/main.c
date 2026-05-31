/* <stdio.h> FILE* round-trip: write formatted + raw text to a temp file,
   then rewind and read it back line by line. Exercises the FILE* lowering
   end-to-end — `FILE *fp` decl, `fp == NULL` check, fprintf/fputs/fgets/
   ftell/rewind/feof — through the emitter (FILE* stays a real pointer).

   Uses tmpfile() so the test is hermetic (anonymous, auto-deleted) and needs
   no writable working directory or cleanup. */
#include <stdio.h>

int main(void) {
    FILE *fp = tmpfile();
    if (fp == NULL) {
        printf("tmpfile failed\n");
        return 1;
    }

    fprintf(fp, "%d %s\n", 42, "hello");
    fputs("second line\n", fp);
    long size = ftell(fp);

    rewind(fp);
    char buf[64];
    while (fgets(buf, sizeof(buf), fp)) {
        printf("read: %s", buf);
    }

    printf("size=%ld eof=%d\n", size, feof(fp));
    fclose(fp);
    return 0;
}
