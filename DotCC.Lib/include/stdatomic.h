#ifndef _STDATOMIC_H
#define _STDATOMIC_H

/* dotcc's <stdatomic.h> (C11). The generic functions (atomic_load,
   atomic_store, atomic_exchange, atomic_compare_exchange_*, atomic_fetch_*,
   atomic_flag_*, atomic_thread_fence, …) are NOT declared here — they're
   type-generic, and dotcc intercepts them by name in the emitter and lowers
   them onto seq-cst Interlocked-backed `Atomic.*` helpers (see
   DotCC.Libc/AtomicLib.cs). This header supplies the TYPES, the
   `memory_order` enum, the lock-free macros, and `atomic_flag`.

   Memory orders are accepted but treated uniformly as seq-cst (a full barrier
   on .NET) — the conservative over-approximation, never weaker than C requires.
   The atomic typedefs lower to `_Atomic <T>`; an eligible 4-/8-byte scalar
   (int/uint/long/ulong/size_t/…/float/double) gets lock-free atomic access, a
   narrow / _Bool atomic falls back to a plain access (documented in C-SUPPORT). */

/* memory_order (C11 7.17.1). Values per the standard's typical encoding. */
typedef enum memory_order {
    memory_order_relaxed = 0,
    memory_order_consume = 1,
    memory_order_acquire = 2,
    memory_order_release = 3,
    memory_order_acq_rel = 4,
    memory_order_seq_cst = 5
} memory_order;

/* The atomic_<T> typedefs (C11 7.17.6). Mapped straight to the underlying
   primitive dotcc uses (size_t/ptrdiff_t/intptr_t are 64-bit here, matching
   <stdint.h>); a use of the alias is treated exactly like `_Atomic <T>`. */
typedef _Atomic _Bool              atomic_bool;
typedef _Atomic char               atomic_char;
typedef _Atomic signed char        atomic_schar;
typedef _Atomic unsigned char      atomic_uchar;
typedef _Atomic short              atomic_short;
typedef _Atomic unsigned short     atomic_ushort;
typedef _Atomic int                atomic_int;
typedef _Atomic unsigned int       atomic_uint;
typedef _Atomic long               atomic_long;
typedef _Atomic unsigned long      atomic_ulong;
typedef _Atomic long long          atomic_llong;
typedef _Atomic unsigned long long atomic_ullong;
typedef _Atomic unsigned long      atomic_size_t;     /* size_t      → unsigned long */
typedef _Atomic long               atomic_ptrdiff_t;  /* ptrdiff_t   → long */
typedef _Atomic long               atomic_intptr_t;   /* intptr_t    → long */
typedef _Atomic unsigned long      atomic_uintptr_t;  /* uintptr_t   → unsigned long */
typedef _Atomic long               atomic_intmax_t;
typedef _Atomic unsigned long      atomic_uintmax_t;

/* Lock-free macros (C11 7.17.1). dotcc's eligible scalar atomics are lock-free,
   so the always-lock-free value 2 is reported for every category. */
#define ATOMIC_BOOL_LOCK_FREE     2
#define ATOMIC_CHAR_LOCK_FREE     2
#define ATOMIC_CHAR16_T_LOCK_FREE 2
#define ATOMIC_CHAR32_T_LOCK_FREE 2
#define ATOMIC_WCHAR_T_LOCK_FREE  2
#define ATOMIC_SHORT_LOCK_FREE    2
#define ATOMIC_INT_LOCK_FREE      2
#define ATOMIC_LONG_LOCK_FREE     2
#define ATOMIC_LLONG_LOCK_FREE    2
#define ATOMIC_POINTER_LOCK_FREE  2

/* Initialization helpers. ATOMIC_VAR_INIT was deprecated in C17 but is still
   accepted. atomic_init() is intercepted by the emitter (a plain store). */
#define ATOMIC_VAR_INIT(value) (value)
#define kill_dependency(y) (y)

/* atomic_flag (C11 7.17.8). dotcc models the opaque flag as an int (0 = clear);
   atomic_flag_test_and_set / atomic_flag_clear are intercepted by the emitter. */
typedef int atomic_flag;
#define ATOMIC_FLAG_INIT 0

#endif
