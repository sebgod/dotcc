#include <stdio.h>

/* `typedef enum { … } Name;` (anonymous) and `typedef enum Tag { … } Name;`
 * (tagged) — the enum counterparts of typedef-struct/union. Lua's metamethod
 * table `typedef enum { TM_INDEX, … } TMS;` (ltm.h) is the anonymous form. */

typedef enum { TM_INDEX, TM_NEWINDEX, TM_ADD, TM_SUB } TMS;   /* anonymous */
typedef enum Color { RED, GREEN = 5, BLUE } Color;            /* tagged + explicit value */

static const char *name(TMS t) {
    switch (t) {
        case TM_INDEX:    return "index";
        case TM_NEWINDEX: return "newindex";
        case TM_ADD:      return "add";
        default:          return "?";
    }
}

int main(void) {
    TMS t = TM_ADD;
    Color c = BLUE;            /* GREEN=5, so BLUE=6 */
    printf("t=%d name=%s c=%d\n", (int)t, name(t), (int)c);
    return 0;
}
