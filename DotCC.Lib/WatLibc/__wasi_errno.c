/* __wasi_errno: the C errno for a WASI errno. WASI numbers its errors its own way (WASI preview1's
   errno enumeration), and <errno.h> has Linux's numbers. The wat target's libc (WatLibc), compiled with the program. */
#include <errno.h>
#include <wasi.h>

int __wasi_errno(int err)
{
    switch (err)
    {
    case 2: return EACCES;
    case 6: return EAGAIN;
    case 8: return EBADF;
    case 20: return EEXIST;
    case 27: return EINTR;
    case 28: return EINVAL;
    case 29: return EIO;
    case 31: return EISDIR;
    case 41: return EMFILE;
    case 44: return ENOENT;
    case 48: return ENOMEM;
    case 51: return ENOSPC;
    case 52: return ENOSYS;
    case 54: return ENOTDIR;
    case 55: return ENOTEMPTY;
    case 61: return EOVERFLOW;
    case 63: return EPERM;
    case 64: return EPIPE;
    case 70: return ESPIPE;
    default: return EIO;
    }
}
