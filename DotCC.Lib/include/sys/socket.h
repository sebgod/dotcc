#ifndef _SYS_SOCKET_H
#define _SYS_SOCKET_H

/* dotcc's <sys/socket.h> — declaration-only: DotCC.Libc opens no sockets, so
   shutdown() always fails with -1 (the honest ENOTSOCK answer; callers that
   shutdown an fd "if it is a socket" — chibi's port finalizer — take the
   failure path exactly as they would for a non-socket fd on a real libc). */

#define SHUT_RD   0
#define SHUT_WR   1
#define SHUT_RDWR 2

int shutdown(int fd, int how);

#endif /* _SYS_SOCKET_H */
