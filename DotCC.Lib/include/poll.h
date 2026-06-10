#ifndef _POLL_H
#define _POLL_H

/* dotcc's <poll.h> — declared so poll-style readiness probes COMPILE.
   poll() reports every polled fd as ready (return = nfds): dotcc's fds are
   file-backed slots, for which "ready" is always true — the read that follows
   completes without blocking. A real socket/pipe fd that ISN'T ready would
   diverge (the follow-up read blocks where C would have returned), but no
   such fd exists in DotCC.Libc. */

#define POLLIN  0x001
#define POLLPRI 0x002
#define POLLOUT 0x004
#define POLLERR 0x008
#define POLLHUP 0x010

typedef unsigned long nfds_t;

struct pollfd {
    int   fd;
    short events;
    short revents;
};

int poll(struct pollfd *fds, nfds_t nfds, int timeout);

#endif /* _POLL_H */
