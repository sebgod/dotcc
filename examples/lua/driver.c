/* Phase 7 driver: isolate table overflow bug. */
#include "lua.h"
#include "lauxlib.h"
#include <stdio.h>

int luaopen_package(lua_State *L) {
    lua_newtable(L);
    return 1;
}

int main(void) {
    lua_State *L = luaL_newstate();
    if (!L) { printf("FATAL: newstate\n"); return 1; }

    /* Create a table and insert string keys until we hit the rehash */
    lua_newtable(L);
    int i;
    for (i = 0; i < 20; i++) {
        char key[16];
        sprintf(key, "k%d", i);
        lua_pushinteger(L, i);
        lua_setfield(L, -2, key);
    }
    printf("Inserted %d keys OK\n", i);

    /* Read some back */
    lua_getfield(L, -1, "k5");
    printf("k5 = %lld\n", (long long)lua_tointeger(L, -1));
    lua_pop(L, 1);

    lua_getfield(L, -1, "k19");
    printf("k19 = %lld\n", (long long)lua_tointeger(L, -1));
    lua_pop(L, 1);

    lua_close(L);
    printf("OK\n");
    return 0;
}
