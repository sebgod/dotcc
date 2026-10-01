/* tss_delete: forget the key's destructor. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

void tss_delete(tss_t key)
{
    __dotcc_tss.dtors[*(int *)&key] = NULL;
}
