/* strerror: the message for an error number, in glibc's wording, the same text as the C#
   runtime's (DotCC.Libc/ErrnoLib.cs). The wat target's libc (WatLibc), compiled with the program. */
#include <errno.h>
#include <string.h>

char *strerror(int errnum)
{
    switch (errnum)
    {
    case 0: return "Success";
    case EPERM: return "Operation not permitted";
    case ENOENT: return "No such file or directory";
    case ESRCH: return "No such process";
    case EINTR: return "Interrupted system call";
    case EIO: return "Input/output error";
    case ENXIO: return "No such device or address";
    case E2BIG: return "Argument list too long";
    case ENOEXEC: return "Exec format error";
    case EBADF: return "Bad file descriptor";
    case ECHILD: return "No child processes";
    case EAGAIN: return "Resource temporarily unavailable";
    case ENOMEM: return "Cannot allocate memory";
    case EACCES: return "Permission denied";
    case EFAULT: return "Bad address";
    case EBUSY: return "Device or resource busy";
    case EEXIST: return "File exists";
    case EXDEV: return "Invalid cross-device link";
    case ENODEV: return "No such device";
    case ENOTDIR: return "Not a directory";
    case EISDIR: return "Is a directory";
    case EINVAL: return "Invalid argument";
    case ENFILE: return "Too many open files in system";
    case EMFILE: return "Too many open files";
    case ENOTTY: return "Inappropriate ioctl for device";
    case EFBIG: return "File too large";
    case ENOSPC: return "No space left on device";
    case ESPIPE: return "Illegal seek";
    case EROFS: return "Read-only file system";
    case EMLINK: return "Too many links";
    case EPIPE: return "Broken pipe";
    case EDOM: return "Numerical argument out of domain";
    case ERANGE: return "Numerical result out of range";
    case EILSEQ: return "Invalid or incomplete multibyte or wide character";
    case ENOTBLK: return "Block device required";
    case ETXTBSY: return "Text file busy";
    case EDEADLK: return "Resource deadlock avoided";
    case ENAMETOOLONG: return "File name too long";
    case ENOLCK: return "No locks available";
    case ENOSYS: return "Function not implemented";
    case ENOTEMPTY: return "Directory not empty";
    case ELOOP: return "Too many levels of symbolic links";
    case EOVERFLOW: return "Value too large for defined data type";
    case ENOTSOCK: return "Socket operation on non-socket";
    case EMSGSIZE: return "Message too long";
    case EPROTONOSUPPORT: return "Protocol not supported";
    case EOPNOTSUPP: return "Operation not supported";
    case EAFNOSUPPORT: return "Address family not supported by protocol";
    case EADDRINUSE: return "Address already in use";
    case EADDRNOTAVAIL: return "Cannot assign requested address";
    case ENETDOWN: return "Network is down";
    case ENETUNREACH: return "Network is unreachable";
    case ECONNABORTED: return "Software caused connection abort";
    case ECONNRESET: return "Connection reset by peer";
    case ENOBUFS: return "No buffer space available";
    case EISCONN: return "Transport endpoint is already connected";
    case ENOTCONN: return "Transport endpoint is not connected";
    case ESHUTDOWN: return "Cannot send after transport endpoint shutdown";
    case ETIMEDOUT: return "Connection timed out";
    case ECONNREFUSED: return "Connection refused";
    case EHOSTUNREACH: return "No route to host";
    case EALREADY: return "Operation already in progress";
    case EINPROGRESS: return "Operation now in progress";
    case ECANCELED: return "Operation canceled";
    default: return "Unknown error";
    }
}
