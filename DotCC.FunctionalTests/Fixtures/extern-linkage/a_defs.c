/* Definitions — the storage for `g_count` and the body of `bump` live here.
 * The other translation unit reaches them by `extern` declaration. */
int g_count = 0;

void bump(int by) {
    g_count += by;
}
