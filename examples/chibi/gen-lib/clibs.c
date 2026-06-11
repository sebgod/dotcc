#include "chibi/eval.h"

#define sexp_init_library sexp_init_lib_chibi_ast
#include "lib/chibi/ast.c"
#undef sexp_init_library

#define sexp_init_library sexp_init_lib_scheme_time
#include "lib/scheme/time.c"
#undef sexp_init_library

#define sexp_init_library sexp_init_lib_srfi_98
#include "lib/srfi/98/env.c"
#undef sexp_init_library

#define sexp_init_library sexp_init_lib_srfi_69
#include "lib/srfi/69/hash.c"
#undef sexp_init_library


struct sexp_library_entry_t sexp_static_libraries_array[] = {
  { "lib/chibi/ast", sexp_init_lib_chibi_ast },
  { "lib/scheme/time", sexp_init_lib_scheme_time },
  { "lib/srfi/98/env", sexp_init_lib_srfi_98 },
  { "lib/srfi/69/hash", sexp_init_lib_srfi_69 },
  { NULL, NULL }
};

struct sexp_library_entry_t* sexp_static_libraries = sexp_static_libraries_array;
