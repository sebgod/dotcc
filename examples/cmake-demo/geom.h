#ifndef GEOM_H
#define GEOM_H

/* A shared header used by two translation units (geom.c defines these,
   main.c calls them) — the multi-TU + shared-struct case a build system
   has to get right. */

struct Point { int x; int y; };

int manhattan(struct Point a, struct Point b);
int dot(struct Point a, struct Point b);

#endif
