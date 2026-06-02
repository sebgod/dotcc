/* Phase 6 driver. Stage 1: a stub main that pulls in nothing from Lua — used to
 * shake out whether the MERGED core+lib emitted C# compiles & links at all,
 * before layering on the Lua C API and runtime semantics. Will grow into
 * `luaL_newstate -> luaL_openlibs -> luaL_dostring(...) -> lua_close`. */

#include "lua.h"

/* The package/loadlib library (dynamic C-module loading) is intentionally
 * EXCLUDED from this link — it maps onto dlopen/LoadLibrary, which dotcc's .NET
 * target doesn't model. linit.c's loadedlibs[] still references
 * luaopen_package, so provide a harmless stub to satisfy the reference. When the
 * driver eventually calls luaL_openlibs and this runs, it registers an empty
 * `package` table (no `require`, no dynamic loading) and returns it. */
int luaopen_package(lua_State *L) {
    lua_newtable(L);
    return 1;
}

int main(void) {
    return 0;
}
