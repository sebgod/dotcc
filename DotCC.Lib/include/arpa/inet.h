#ifndef _ARPA_INET_H
#define _ARPA_INET_H

/* dotcc's <arpa/inet.h> — IPv4 presentation <-> binary conversion
   (DotCC.Libc.SocketLib). Pulls in <netinet/in.h> for in_addr_t / struct in_addr
   and the htons family, exactly as glibc's arpa/inet.h does. */

#include <netinet/in.h>

in_addr_t   inet_addr(const char *cp);
int         inet_pton(int af, const char *src, void *dst);
const char *inet_ntop(int af, const void *src, char *dst, socklen_t size);

#endif /* _ARPA_INET_H */
