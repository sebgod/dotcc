#ifndef _STDLIB_H
#define _STDLIB_H

/* dotcc's <stdlib.h> — memory + program-control surface. malloc/free
   route to NativeMemory.Alloc/Free under the hood. */

void* malloc(int size);
void free(void* p);

#endif
