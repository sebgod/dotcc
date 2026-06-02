/* Phase 6 driver. Stage 1: a stub main that pulls in nothing from Lua — used to
 * shake out whether the MERGED core+lib emitted C# compiles & links at all,
 * before layering on the Lua C API and runtime semantics. Will grow into
 * `luaL_newstate -> luaL_openlibs -> luaL_dostring(...) -> lua_close`. */
int main(void) {
    return 0;
}
