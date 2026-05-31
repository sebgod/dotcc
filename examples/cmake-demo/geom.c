#include "geom.h"

/* file-local helper — exercises `static` linkage across the TU boundary
   (it must NOT collide with anything in main.c) */
static int iabs(int v) { return v < 0 ? -v : v; }

int manhattan(struct Point a, struct Point b) {
    return iabs(a.x - b.x) + iabs(a.y - b.y);
}

int dot(struct Point a, struct Point b) {
    return a.x * b.x + a.y * b.y;
}
