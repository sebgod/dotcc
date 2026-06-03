/* Lua-on-dotcc regression suite. Exercises the specific failure modes we
 * hit and fixed during the Phase 6–8 bring-up. Add new cases here when you
 * find a runtime bug — a failure in this driver means a regression.
 *
 * Build: see link.sh
 * Oracle: compile with gcc and compare stdout; dotcc must match.
 */
#include "lua.h"
#include "lualib.h"
#include "lauxlib.h"
#include <stdio.h>

int luaopen_package(lua_State *L) { lua_newtable(L); return 1; }

/* Run a single script via luaL_dostring, print the result if any. */
static void test(lua_State *L, const char *label, const char *script) {
    printf("=== %s ===\n", label); fflush(stdout);
    int r = luaL_dostring(L, script);
    if (r != LUA_OK) {
        printf("  ERROR: %s\n", lua_tostring(L, -1));
        lua_pop(L, 1);
    } else if (lua_gettop(L) >= 1) {
        /* prefer integer, fall back to float for non-integer numbers */
        if (lua_type(L, -1) == LUA_TNUMBER) {
            printf("  => %lld\n", (long long)lua_tointeger(L, -1));
        } else {
            printf("  => (type %d)\n", lua_type(L, -1));
        }
        lua_pop(L, 1);
    }
    fflush(stdout);
}

int main(void) {
    lua_State *L = luaL_newstate();
    if (!L) { printf("FATAL: no state\n"); return 1; }
    luaL_requiref(L, "_G", luaopen_base, 1);           lua_pop(L, 1);
    luaL_requiref(L, LUA_MATHLIBNAME, luaopen_math, 1); lua_pop(L, 1);
    luaL_requiref(L, LUA_STRLIBNAME, luaopen_string, 1); lua_pop(L, 1);

    /* ── basic expressions ─────────────────────────────────────────── */
    test(L, "return literal",    "return 42");
    test(L, "return add",        "return 2 + 3");
    test(L, "return nothing",    "return");
    test(L, "empty chunk",       "");

    /* ── locals and assignment ─────────────────────────────────────── */
    test(L, "local decl",        "local x = 10");
    test(L, "assignment",        "x = 5");
    test(L, "assign + return",   "x = 5  return x");

    /* ── multi-statement chunks ────────────────────────────────────── */
    test(L, "local + return",    "local a = 1  return a + 2");

    /* ── function definitions ──────────────────────────────────────── */
    test(L, "def only",          "function f() return 99 end");
    test(L, "def + return n",    "function f() return 99 end  return 42");
    test(L, "def + call (TC)",   "function f() return 99 end\nreturn f()");
    test(L, "def + call multi",  "function g() return 1,2 end\nreturn g()");

    /* ── C function calls ──────────────────────────────────────────── */
    test(L, "C call",            "return math.abs(-42)");

    lua_close(L);
    printf("Done.\n");
    return 0;
}
