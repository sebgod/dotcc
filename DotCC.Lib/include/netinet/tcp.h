#ifndef _NETINET_TCP_H
#define _NETINET_TCP_H

/* dotcc's <netinet/tcp.h> — TCP-level socket options (DotCC.Libc.SocketLib).
   Only TCP_NODELAY is honoured today (maps to Socket.NoDelay); other options
   are accepted-and-ignored by setsockopt or rejected with EINVAL. */

#define TCP_NODELAY 1

#endif /* _NETINET_TCP_H */
