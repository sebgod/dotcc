/* tss_create: a new key with the destructor a finishing thread created by thrd_create runs on its value (as the C# runtime's, not the main thread's). The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

static void run_dtors(void)
{
    for (int round = 0; round < TSS_DTOR_ITERATIONS; round++)
    {
        int any = 0;
        for (int k = 0; k < __dotcc_tss.next && k < DOTCC_TSS_KEYS; k++)
        {
            void *v = __dotcc_tss_values.v[k];
            if (v != NULL && __dotcc_tss.dtors[k] != NULL)
            {
                __dotcc_tss_values.v[k] = NULL;
                __dotcc_tss.dtors[k](v);
                any = 1;
            }
        }
        if (!any) { return; }
    }
}

int tss_create(tss_t *key, tss_dtor_t dtor)
{
    int k = atomic_fetch_add((atomic_int *)&__dotcc_tss.next, 1);
    if (k >= DOTCC_TSS_KEYS) { return thrd_error; }
    __dotcc_tss.dtors[k] = dtor;
    __dotcc_tss.run_dtors = run_dtors;
    *(int *)key = k;
    return thrd_success;
}
