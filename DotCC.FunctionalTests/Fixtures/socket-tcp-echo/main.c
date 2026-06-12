// Self-contained BSD-sockets TCP echo over the loopback interface, exercising
// dotcc's sockets end-to-end: socket/setsockopt/bind/listen/getsockname/accept/
// connect/send/recv/shutdown/close + htons/htonl + the struct sockaddr_in layout
// and the (struct sockaddr *) cast. Both endpoints live in one process — a C11
// <threads.h> server thread (dotcc has no fork), client in main.
//
// The OS assigns an ephemeral port (bind to :0); the server publishes it through
// a mutex-guarded global (dotcc's <threads.h> defers cnd_*, so main spins on it
// with thrd_yield — the mutex provides the memory barrier). Deterministic: the
// server listen()s before publishing the port, so the client's connect lands in
// the backlog even before accept() runs.
//
// Sockets are POSIX, so _POSIX_C_SOURCE makes real glibc expose the prototypes
// under a strict -std= (dotcc ignores it — its headers declare unconditionally).
// MSVC has no <sys/socket.h>/<unistd.h>/Winsock-without-WSAStartup, so the MSVC
// oracle opts out; gcc on Linux (x64 + arm64) differential-tests it.
#define _POSIX_C_SOURCE 200809L
#include <stdio.h>
#include <string.h>
#include <threads.h>
#include <unistd.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>

static int g_port = 0;   // published by the server under the shared mutex

int server_thread(void *arg) {
    mtx_t *m = (mtx_t *)arg;
    int srv = socket(AF_INET, SOCK_STREAM, 0);
    int one = 1;
    setsockopt(srv, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(0);                       // ephemeral
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);  // 127.0.0.1
    bind(srv, (struct sockaddr *)&addr, sizeof(addr));

    struct sockaddr_in local;
    socklen_t llen = sizeof(local);
    getsockname(srv, (struct sockaddr *)&local, &llen);
    listen(srv, 1);

    mtx_lock(m);
    g_port = ntohs(local.sin_port);
    mtx_unlock(m);

    int conn = accept(srv, NULL, NULL);
    char buf[256];
    int got = 0, r;
    while (got < 256 && (r = recv(conn, buf + got, 256 - got, 0)) > 0) { got += r; }
    int sent = 0;
    while (sent < got) { int w = send(conn, buf + sent, got - sent, 0); if (w <= 0) break; sent += w; }
    close(conn);
    close(srv);
    return 0;
}

int main(void) {
    mtx_t mux;
    mtx_init(&mux, mtx_plain);
    thrd_t t;
    thrd_create(&t, &server_thread, &mux);

    int port = 0;
    while (port == 0) {
        mtx_lock(&mux);
        port = g_port;
        mtx_unlock(&mux);
        thrd_yield();
    }

    int cli = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in peer;
    memset(&peer, 0, sizeof(peer));
    peer.sin_family = AF_INET;
    peer.sin_port = htons(port);
    peer.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    connect(cli, (struct sockaddr *)&peer, sizeof(peer));

    const char *msg = "hello dotcc sockets";
    int mlen = (int)strlen(msg);
    int off = 0;
    while (off < mlen) { int w = send(cli, msg + off, mlen - off, 0); if (w <= 0) break; off += w; }
    shutdown(cli, SHUT_WR);                          // half-close: EOF to the server

    char rbuf[256];
    int total = 0, g;
    while (total < 256 && (g = recv(cli, rbuf + total, 256 - total, 0)) > 0) { total += g; }
    rbuf[total] = '\0';
    printf("echo: %s\n", rbuf);

    close(cli);
    thrd_join(t, NULL);
    mtx_destroy(&mux);
    return 0;
}
