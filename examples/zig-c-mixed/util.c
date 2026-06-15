/* A plain C helper called from main.zig. Compiled in the SAME dotcc invocation as
   the .zig unit; the two lower into one program and link by bare name. */
int square(int n) {
    return n * n;
}
