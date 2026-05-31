/* <stdlib.h> system(): the command-processor probe.

   We deliberately test only system(NULL) here, not an actual command. The
   functional harness runs the emitted program in-process with Console.Out
   redirected to capture stdout; a child process spawned by system("...")
   inherits the real OS stdout instead, so its output wouldn't be captured
   (and would interleave nondeterministically). system(NULL) spawns nothing —
   it just reports whether a command interpreter exists — so it's the part of
   the contract we can assert portably. Real command execution is covered by
   the unit tests (LibcStdlibTests.system_returns_the_child_exit_code). */
#include <stdio.h>
#include <stdlib.h>

int main(void) {
    if (system(NULL)) {
        printf("command processor available\n");
    } else {
        printf("no command processor\n");
    }
    return 0;
}
