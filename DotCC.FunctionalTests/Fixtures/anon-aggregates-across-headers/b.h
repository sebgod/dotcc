/* Its anonymous struct and union sit at the same lines and columns as a.h's. */
struct later { struct { double d; char tag[3]; } pair;
               union { long l; short s[4]; } u; };
