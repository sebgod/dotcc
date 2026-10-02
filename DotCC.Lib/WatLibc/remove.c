/* remove: remove the file or empty directory path (C11 7.21.4.1, as POSIX has it).
   The wat target's libc (WatLibc), compiled with the program. */
#include <errno.h>
#include <stdio.h>
#include <unistd.h>

int remove(const char *path)
{
    if (unlink(path) == 0)
    {
        return 0;
    }
    if (errno != EISDIR && errno != EPERM)
    {
        return -1;
    }
    return rmdir(path);
}
