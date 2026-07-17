#ifndef _SYS_SOCKET_H
#define _SYS_SOCKET_H

/* dotcc's <sys/socket.h> — the BSD sockets surface, lowered onto .NET's
   System.Net.Sockets (DotCC.Libc.SocketLib). Faithful blocking IPv4 TCP+UDP on
   every host (Linux/Windows alike — no Winsock split). The constants are the
   Linux/glibc numeric values (dotcc is LP64/Linux-shaped), so the same C program
   means the same thing here as it does to gcc-on-Linux. Unix-domain sockets
   (AF_UNIX, pathname form) work too — see <sys/un.h>. Deferred: non-blocking
   (O_NONBLOCK degrades to blocking), select/poll over mixed fd sets, IPv6,
   getaddrinfo — see C-SUPPORT.md. */

#include <sys/types.h>   /* ssize_t, size_t */

typedef unsigned int   socklen_t;
typedef unsigned short sa_family_t;

/* Address / protocol families */
#define AF_UNSPEC 0
#define AF_UNIX   1
#define AF_LOCAL  1
#define AF_INET   2
#define AF_INET6  10
#define PF_UNSPEC AF_UNSPEC
#define PF_UNIX   AF_UNIX
#define PF_LOCAL  AF_LOCAL
#define PF_INET   AF_INET
#define PF_INET6  AF_INET6

/* Socket types */
#define SOCK_STREAM 1
#define SOCK_DGRAM  2
#define SOCK_RAW    3

/* setsockopt / getsockopt levels and option names */
#define SOL_SOCKET   1
#define SO_REUSEADDR 2
#define SO_TYPE      3
#define SO_ERROR     4
#define SO_BROADCAST 6
#define SO_SNDBUF    7
#define SO_RCVBUF    8
#define SO_KEEPALIVE 9
#define SO_REUSEPORT 15
#define SO_RCVTIMEO  20
#define SO_SNDTIMEO  21

/* send / recv flags */
#define MSG_OOB       0x01
#define MSG_PEEK      0x02
#define MSG_DONTROUTE 0x04
#define MSG_WAITALL   0x100

/* shutdown(how) */
#define SHUT_RD   0
#define SHUT_WR   1
#define SHUT_RDWR 2

/* The generic socket address. Concrete families (struct sockaddr_in in
   <netinet/in.h>) share its 16-byte size and leading sa_family, so the
   (struct sockaddr *) cast in bind/connect/accept is well-defined. */
struct sockaddr {
    sa_family_t sa_family;
    char        sa_data[14];
};

int     socket(int domain, int type, int protocol);
int     bind(int fd, const struct sockaddr *addr, socklen_t addrlen);
int     listen(int fd, int backlog);
int     accept(int fd, struct sockaddr *addr, socklen_t *addrlen);
int     connect(int fd, const struct sockaddr *addr, socklen_t addrlen);
ssize_t send(int fd, const void *buf, size_t len, int flags);
ssize_t recv(int fd, void *buf, size_t len, int flags);
ssize_t sendto(int fd, const void *buf, size_t len, int flags,
               const struct sockaddr *dest_addr, socklen_t addrlen);
ssize_t recvfrom(int fd, void *buf, size_t len, int flags,
                 struct sockaddr *src_addr, socklen_t *addrlen);
int     setsockopt(int fd, int level, int optname, const void *optval, socklen_t optlen);
int     getsockopt(int fd, int level, int optname, void *optval, socklen_t *optlen);
int     getsockname(int fd, struct sockaddr *addr, socklen_t *addrlen);
int     getpeername(int fd, struct sockaddr *addr, socklen_t *addrlen);
int     shutdown(int fd, int how);

#endif /* _SYS_SOCKET_H */
