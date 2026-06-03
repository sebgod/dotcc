# Known bugs

This file tracks known correctness bugs that are understood but not yet fixed.
See [`C-SUPPORT.md`](C-SUPPORT.md) for the feature coverage matrix.

## Parser: binary operator dropped after `sizeof(struct|union|enum)`

The `SizeofFolder` (a `RewritingTokenStream` between the TypeNameRewriter and the
parser) folds `sizeof(T)` to a numeric constant so the parser doesn't hit a
conflict that drops binary operators after the `sizeof`. For keyword types
(`int`, `long`, etc.) and simple typedefs it works. For `struct`/`union`/`enum`
types the folder can't compute the size (it would need the emitter's layout
model), so it returns `null` and the raw `sizeof(struct Foo)` reaches the parser
— which then drops any following `*`, `+`, `<<`, etc.

**Repro:**
```c
struct S { int a; };
int x = (int)(sizeof(struct S) * 8);  // * 8 is silently dropped
```

**Workaround:** Use a typedef:
```c
struct S { int a; };
typedef struct S S_t;
int x = (int)(sizeof(S_t) * 8);  // typedef size unknown → same bug (for now)
```

This doesn't affect the Lua port (only integer typedefs are used with
`l_numbits`), but it would affect a C program that does `sizeof(struct Foo) *
CHAR_BIT` on a struct whose size the emitter knows.

**Fix direction:** Either (a) teach `SizeofFolder` to look up struct/union/enum
sizes from the same layout model the emitter uses, or (b) resolve the underlying
parser conflict so the raw `sizeof` doesn't need to be folded at all.
