/* A member of an anonymous struct or union nested in another anonymous one is a
   member of the outer aggregate too (C11 6.7.2.1p13, at every level), for access
   and for designators: CPython's _Py_BackoffCounter and _PyUOpInstruction. */
#include <stdio.h>
#include <stdint.h>

typedef struct {
    union {
        struct {
            uint16_t backoff : 4;
            uint16_t value : 12;
        };
        uint16_t as_counter;
    };
} Counter;

typedef struct {
    uint16_t opcode:15;
    uint16_t format:1;
    uint16_t oparg;
    union {
        uint32_t target;
        struct {
            union {
                uint16_t exit_index;
                uint16_t jump_target;
            };
            uint16_t error_target;
        };
    };
    uint64_t operand;
} Inst;

int main(void) {
    Counter c;
    c.as_counter = 0;
    c.value = 100;
    c.backoff = 3;
    Inst i = { .opcode = 7, .oparg = 2, .jump_target = 11, .error_target = 12 };
    i.exit_index += 1;
    Counter *pc = &c;
    printf("%u %u %u %u %u %u %u\n", c.value, pc->backoff, c.as_counter, i.jump_target, i.error_target, i.target, i.opcode);
    return 0;
}
