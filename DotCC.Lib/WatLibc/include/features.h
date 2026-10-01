#ifndef _FEATURES_H
#define _FEATURES_H

/* musl's internal <features.h> (src/include/features.h) as the wat target's libc needs it.
   A wasm module is one symbol space with no dynamic linking, so visibility is moot. */
#define hidden

#endif
