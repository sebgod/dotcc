#ifndef _SYS_UN_H
#define _SYS_UN_H

/* dotcc's <sys/un.h> — the Unix-domain (AF_UNIX / AF_LOCAL) address family,
   lowered onto .NET's UnixDomainSocketEndPoint (DotCC.Libc.SocketLib). A
   pathname socket: sun_family = AF_UNIX, sun_path a NUL-terminated filesystem
   path. Works on Linux and Windows 10 1803+ alike. dotcc models pathname
   sockets; abstract/unnamed sockets (a leading-NUL sun_path) are not modeled. */

#include <sys/socket.h>   /* sa_family_t */

/* glibc's sun_path capacity; the widely-relied-on 108-byte size. */
#define UNIX_PATH_MAX 108

struct sockaddr_un {
    sa_family_t sun_family;              /* AF_UNIX */
    char        sun_path[UNIX_PATH_MAX]; /* NUL-terminated pathname */
};

#endif /* _SYS_UN_H */
