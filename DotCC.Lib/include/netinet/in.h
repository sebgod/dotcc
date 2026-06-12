#ifndef _NETINET_IN_H
#define _NETINET_IN_H

/* dotcc's <netinet/in.h> — the IPv4 address types + byte-order helpers
   (DotCC.Libc.SocketLib). Layout matches glibc/Linux so a sockaddr_in filled by
   the C program is read back byte-for-byte by the runtime. */

#include <stdint.h>       /* uint16_t, uint32_t */
#include <sys/socket.h>   /* sa_family_t, socklen_t */

typedef uint16_t in_port_t;
typedef uint32_t in_addr_t;

struct in_addr {
    in_addr_t s_addr;     /* network byte order */
};

/* 16 bytes: sin_family(2) sin_port(2) sin_addr(4) sin_zero(8). The leading
   sa_family + 16-byte size match struct sockaddr, so the cast in bind/connect
   is well-defined. */
struct sockaddr_in {
    sa_family_t    sin_family;
    in_port_t      sin_port;     /* network byte order */
    struct in_addr sin_addr;
    unsigned char  sin_zero[8];
};

/* IPv4 well-known addresses (host byte order — run through htonl for s_addr) */
#define INADDR_ANY       ((in_addr_t)0x00000000)
#define INADDR_LOOPBACK  ((in_addr_t)0x7f000001)
#define INADDR_BROADCAST ((in_addr_t)0xffffffff)
#define INADDR_NONE      ((in_addr_t)0xffffffff)

/* IP protocols */
#define IPPROTO_IP   0
#define IPPROTO_ICMP 1
#define IPPROTO_TCP  6
#define IPPROTO_UDP  17

/* Byte-order conversions (glibc declares these here) */
uint16_t htons(uint16_t hostshort);
uint16_t ntohs(uint16_t netshort);
uint32_t htonl(uint32_t hostlong);
uint32_t ntohl(uint32_t netlong);

#endif /* _NETINET_IN_H */
