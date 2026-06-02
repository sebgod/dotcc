/* Alignment union via compile-time offsetof — Lua's ltable.c trick.
 *
 * `offsetof(Limbox_aux, follows_pNode)` is the byte offset where a `Node`
 * (alignment 8) lands after a `Node*` — i.e. the padding needed so a Node that
 * follows a `Node*` is properly aligned. The `Limbox` union is sized to hold
 * BOTH a `lastfree` pointer AND that padding, so one Limbox placed right before
 * an array of Nodes (same malloc'd block) keeps the Nodes aligned.
 *
 * C# needs the array-member bound `char padding[offsetof(...)]` as a literal, so
 * dotcc folds offsetof to a compile-time constant from a struct-layout model
 * (C-ABI rules, matching .NET blittable layout). gcc is the oracle for the
 * resulting offsets/sizes. All values are ABI-stable (char=1, int=4,
 * double/long long/pointer=8), printed as int. */
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>

typedef unsigned char lu_byte;
typedef long long lua_Integer;
typedef double lua_Number;
typedef int (*lua_CFunction)(void *L);

typedef union Value {
  void *p;
  lua_CFunction f;     /* a function pointer is still pointer-sized */
  lua_Integer i;
  lua_Number n;
  lu_byte ub;
} Value;

#define TValuefields Value value_; lu_byte tt_

typedef struct TValue { TValuefields; } TValue;

typedef union Node {
  struct NodeKey {
    TValuefields;
    lu_byte key_tt;
    int next;
    Value key_val;
  } u;
  TValue i_val;
} Node;

typedef struct { Node *dummy; Node follows_pNode; } Limbox_aux;

typedef union {
  Node *lastfree;
  char padding[offsetof(Limbox_aux, follows_pNode)];
} Limbox;

#define getlimbox(nodes)  ((Limbox*)(nodes) - 1)   /* the (p - 1)->m idiom */

int main(void) {
  int nnodes = 4;
  char *block;
  Node *nodes;
  Limbox *lim;
  int k;

  printf("offsetof = %d\n", (int)offsetof(Limbox_aux, follows_pNode));
  printf("sizeof Limbox = %d\n", (int)sizeof(Limbox));
  printf("sizeof Node = %d\n", (int)sizeof(Node));

  /* one Limbox immediately before the node array, in the same block */
  block = (char*)malloc(sizeof(Limbox) + nnodes * sizeof(Node));
  nodes = (Node*)(block + sizeof(Limbox));

  lim = getlimbox(nodes);
  lim->lastfree = &nodes[nnodes - 1];

  for (k = 0; k < nnodes; k++)
    nodes[k].i_val.value_.i = (lua_Integer)(k * 10);

  /* read lastfree back through the Limbox sitting before the array */
  printf("lastfree value = %d\n", (int)getlimbox(nodes)->lastfree->i_val.value_.i);
  printf("node[2] value = %d\n", (int)nodes[2].i_val.value_.i);

  free(block);
  return 0;
}
