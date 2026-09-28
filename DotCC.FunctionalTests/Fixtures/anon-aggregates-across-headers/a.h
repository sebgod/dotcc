/* Its anonymous struct and union sit at the same lines and columns as b.h's. */
struct first { struct { int x; int y; } pair;
               union { int i; char c; } u; };
