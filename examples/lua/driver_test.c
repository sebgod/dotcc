#include "lua.h"
#include "lualib.h"
#include "lauxlib.h"
#include <stdio.h>

int luaopen_package(lua_State *L) {
    lua_newtable(L);
    return 1;
}

int main(void) {
    lua_State *L = luaL_newstate();
    if (!L) { return 1; }
    luaL_openlibs(L);
    
    /* Simple arithmetic */
    if (luaL_dostring(L, "return 2 + 3") == 0) {
        printf("2+3 = %lld\n", (long long)lua_tointeger(L, -1));
    }
    
    /* Call a Lua function */
    if (luaL_dostring(L, "function fib(n) if n<2 then return n else return fib(n-1)+fib(n-2) end end return fib(10)") == 0) {
        printf("fib(10) = %lld\n", (long long)lua_tointeger(L, -1));
    }
    
    lua_close(L);
    printf("Lua OK!\n");
    return 0;
}
