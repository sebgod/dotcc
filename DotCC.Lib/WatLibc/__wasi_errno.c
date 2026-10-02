/* __wasi_errno: the C errno for a WASI errno. WASI numbers its errors its own way (WASI preview1's
   errno enumeration), and <errno.h> has Linux's numbers; an error <errno.h> has no name for is
   the nearest one it has (EIO for the rest), and WASI's ENOTCAPABLE, a path outside every
   directory the program was given, is EPERM. The wat target's libc (WatLibc), compiled with the program. */
#include <errno.h>
#include <wasi.h>

int __wasi_errno(int err)
{
    switch (err)
    {
    case 1: return E2BIG;
    case 2: return EACCES;
    case 3: return EADDRINUSE;
    case 4: return EADDRNOTAVAIL;
    case 5: return EAFNOSUPPORT;
    case 6: return EAGAIN;
    case 7: return EALREADY;
    case 8: return EBADF;
    case 10: return EBUSY;
    case 11: return ECANCELED;
    case 12: return ECHILD;
    case 13: return ECONNABORTED;
    case 14: return ECONNREFUSED;
    case 15: return ECONNRESET;
    case 16: return EDEADLK;
    case 18: return EDOM;
    case 19: return ENOSPC;
    case 20: return EEXIST;
    case 21: return EFAULT;
    case 22: return EFBIG;
    case 23: return EHOSTUNREACH;
    case 25: return EILSEQ;
    case 26: return EINPROGRESS;
    case 27: return EINTR;
    case 28: return EINVAL;
    case 29: return EIO;
    case 30: return EISCONN;
    case 31: return EISDIR;
    case 32: return ELOOP;
    case 33: return EMFILE;
    case 34: return EMLINK;
    case 35: return EMSGSIZE;
    case 37: return ENAMETOOLONG;
    case 38: return ENETDOWN;
    case 39: return ENETDOWN;
    case 40: return ENETUNREACH;
    case 41: return ENFILE;
    case 42: return ENOBUFS;
    case 43: return ENODEV;
    case 44: return ENOENT;
    case 45: return ENOEXEC;
    case 46: return ENOLCK;
    case 48: return ENOMEM;
    case 51: return ENOSPC;
    case 52: return ENOSYS;
    case 53: return ENOTCONN;
    case 54: return ENOTDIR;
    case 55: return ENOTEMPTY;
    case 57: return ENOTSOCK;
    case 58: return ENOTSUP;
    case 59: return ENOTTY;
    case 60: return ENXIO;
    case 61: return EOVERFLOW;
    case 63: return EPERM;
    case 64: return EPIPE;
    case 66: return EPROTONOSUPPORT;
    case 68: return ERANGE;
    case 69: return EROFS;
    case 70: return ESPIPE;
    case 71: return ESRCH;
    case 73: return ETIMEDOUT;
    case 74: return ETXTBSY;
    case 75: return EXDEV;
    case 76: return EPERM;
    default: return EIO;
    }
}
