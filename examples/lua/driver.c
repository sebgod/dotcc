/* Phase 7 driver: narrow down Lua parser bug. */
#include "lua.h"
#include "lualib.h"
#include "lauxlib.h"
#include <stdio.h>

int luaopen_package(lua_State *L) { lua_newtable(L); return 1; }

static int run(lua_State *L, const char *script, const char *label) {
    printf("=== %s ===\n", label); fflush(stdout);
    int r = luaL_dostring(L, script);
    if (r != LUA_OK) { printf("  ERROR: %s\n", lua_tostring(L, -1)); lua_pop(L, 1); }
    fflush(stdout); return r;
}

int main(void) {
    lua_State *L = luaL_newstate();
    if (!L) { printf("FATAL\n"); return 1; }
    luaL_requiref(L, "_G", luaopen_base, 1); lua_pop(L, 1);
    luaL_requiref(L, LUA_MATHLIBNAME, luaopen_math, 1); lua_pop(L, 1);
    luaL_requiref(L, LUA_STRLIBNAME, luaopen_string, 1); lua_pop(L, 1);

    run(L, "return 42",          "return literal");
    if (lua_gettop(L) >= 1) { printf("  => %lld\n", (long long)lua_tointeger(L,-1)); lua_pop(L,1); }

    run(L, "return 2 + 3",      "return add");
    if (lua_gettop(L) >= 1) { printf("  => %lld\n", (long long)lua_tointeger(L,-1)); lua_pop(L,1); }

    run(L, "local x = 10",      "local decl");
    run(L, "x = 5",             "assignment");

    run(L, "function f() return 99 end\nreturn f()", "function def+call");
    if (lua_gettop(L) >= 1) { printf("  => %lld\n", (long long)lua_tointeger(L,-1)); lua_pop(L,1); }

    lua_close(L);
    printf("Done.\n");
    return 0;
}
