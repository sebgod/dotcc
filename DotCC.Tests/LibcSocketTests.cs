#nullable enable

using System;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the BSD sockets surface (<see cref="DotCC.Libc"/>'s SocketLib,
/// lowered onto <c>System.Net.Sockets</c>). Everything runs over the loopback
/// interface on an OS-assigned ephemeral port (<c>bind</c> to <c>:0</c> +
/// <c>getsockname</c>) so the tests are self-contained and non-flaky — no fixed
/// port to collide with, no external network. The unsafe pointer work lives in
/// sync helpers so the concurrent test can stay <c>async</c> (pointer locals and
/// <c>async</c> don't mix).
/// </summary>
[Collection("Runtime")]
public sealed class LibcSocketTests
{
    // Linux/glibc numeric values — exactly what the synthetic headers hand the C
    // program (and what SocketLib interprets internally).
    private const int AF_UNIX = 1, AF_INET = 2, SOCK_STREAM = 1, SOCK_DGRAM = 2;
    private const int SOL_SOCKET = 1, SO_REUSEADDR = 2, SHUT_WR = 1;

    /// <summary>Fill a 16-byte <c>sockaddr_in</c> for 127.0.0.1:<paramref name="port"/>
    /// (family host order; port + address network order), as C code would.</summary>
    private static unsafe void MakeLoopback(byte* sa, int port)
    {
        for (int i = 0; i < 16; i++) { sa[i] = 0; }
        *(ushort*)sa = AF_INET;                          // sin_family (host order)
        sa[2] = (byte)(port >> 8); sa[3] = (byte)port;   // sin_port (network order)
        sa[4] = 127; sa[5] = 0; sa[6] = 0; sa[7] = 1;    // sin_addr 127.0.0.1 (network order)
    }

    private static unsafe int PortOf(byte* sa) => (sa[2] << 8) | sa[3];

    [Fact]
    public async Task tcp_loopback_echo_roundtrips()
    {
        int srv = OpenLoopbackListener(out int port);
        srv.ShouldBeGreaterThanOrEqualTo(0);
        port.ShouldBeGreaterThan(0);

        // Server runs concurrently: accept one connection, drain to EOF, echo back.
        var server = Task.Run(() => EchoOnce(srv), TestContext.Current.CancellationToken);

        EchoClientRoundtrips(port).ShouldBeTrue();

        close(srv);
        await server.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>socket → SO_REUSEADDR → bind 127.0.0.1:0 → listen → getsockname.
    /// Returns the listening fd and the OS-assigned <paramref name="port"/>.</summary>
    private static unsafe int OpenLoopbackListener(out int port)
    {
        port = 0;
        int srv = socket(AF_INET, SOCK_STREAM, 0);
        if (srv < 0) { return srv; }
        int one = 1;
        setsockopt(srv, SOL_SOCKET, SO_REUSEADDR, &one, 4);
        byte* sa = stackalloc byte[16];
        MakeLoopback(sa, 0);
        if (bind(srv, sa, 16) != 0 || listen(srv, 1) != 0) { close(srv); return -1; }
        byte* local = stackalloc byte[16];
        uint llen = 16;
        if (getsockname(srv, local, &llen) != 0) { close(srv); return -1; }
        port = PortOf(local);
        return srv;
    }

    /// <summary>Accept one connection, read until the peer half-closes, echo the
    /// whole message back, close.</summary>
    private static unsafe void EchoOnce(int srvFd)
    {
        int conn = accept(srvFd, null, null);
        if (conn < 0) { return; }
        byte* rb = stackalloc byte[256];
        int got = 0;
        long r;
        while (got < 256 && (r = recv(conn, rb + got, (ulong)(256 - got), 0)) > 0) { got += (int)r; }
        int sent = 0;
        while (sent < got) { long w = send(conn, rb + sent, (ulong)(got - sent), 0); if (w <= 0) { break; } sent += (int)w; }
        close(conn);
    }

    /// <summary>Connect, send a message, half-close (SHUT_WR signals EOF to the
    /// server), read the echo to EOF, compare. True iff the round-trip matched.</summary>
    private static unsafe bool EchoClientRoundtrips(int port)
    {
        int cli = socket(AF_INET, SOCK_STREAM, 0);
        if (cli < 0) { return false; }
        byte* peer = stackalloc byte[16];
        MakeLoopback(peer, port);
        if (connect(cli, peer, 16) != 0) { close(cli); return false; }

        byte* msg = stackalloc byte[64];
        int mlen = Encoding.ASCII.GetBytes("hello dotcc sockets", new Span<byte>(msg, 64));
        int off = 0;
        while (off < mlen) { long w = send(cli, msg + off, (ulong)(mlen - off), 0); if (w <= 0) { close(cli); return false; } off += (int)w; }
        shutdown(cli, SHUT_WR);

        byte* rbuf = stackalloc byte[256];
        int total = 0;
        long g;
        while (total < 256 && (g = recv(cli, rbuf + total, (ulong)(256 - total), 0)) > 0) { total += (int)g; }
        bool ok = total == mlen && new ReadOnlySpan<byte>(rbuf, total).SequenceEqual(new ReadOnlySpan<byte>(msg, mlen));
        close(cli);
        return ok;
    }

    [Fact]
    public unsafe void udp_loopback_datagram_roundtrips()
    {
        // Datagrams need no connection/accept, so both ends live on this thread:
        // sendto buffers the datagram, the following recvfrom returns it.
        int rx = socket(AF_INET, SOCK_DGRAM, 0);
        rx.ShouldBeGreaterThanOrEqualTo(0);
        byte* sa = stackalloc byte[16];
        MakeLoopback(sa, 0);
        bind(rx, sa, 16).ShouldBe(0);
        byte* local = stackalloc byte[16];
        uint llen = 16;
        getsockname(rx, local, &llen).ShouldBe(0);
        int port = PortOf(local);

        int tx = socket(AF_INET, SOCK_DGRAM, 0);
        tx.ShouldBeGreaterThanOrEqualTo(0);
        byte* dest = stackalloc byte[16];
        MakeLoopback(dest, port);
        byte* msg = stackalloc byte[16];
        int mlen = Encoding.ASCII.GetBytes("datagram", new Span<byte>(msg, 16));
        sendto(tx, msg, (ulong)mlen, 0, dest, 16).ShouldBe(mlen);

        byte* rbuf = stackalloc byte[64];
        byte* from = stackalloc byte[16];
        uint fromlen = 16;
        long got = recvfrom(rx, rbuf, 64, 0, from, &fromlen);
        got.ShouldBe(mlen);
        new ReadOnlySpan<byte>(rbuf, (int)got).SequenceEqual(new ReadOnlySpan<byte>(msg, mlen)).ShouldBeTrue();
        from[4].ShouldBe((byte)127);     // recvfrom filled the sender's address (loopback)

        close(tx);
        close(rx);
    }

    [Fact]
    public unsafe void byte_order_and_inet_conversions_are_correct()
    {
        // Round-trips are host-endianness-agnostic.
        ntohs(htons(8080)).ShouldBe((ushort)8080);
        ntohl(htonl(0xDEADBEEF)).ShouldBe(0xDEADBEEFu);

        // inet_pton writes the 4 address bytes in network order.
        byte* dst = stackalloc byte[4];
        fixed (byte* ip = "127.0.0.1\0"u8) { inet_pton(AF_INET, ip, dst).ShouldBe(1); }
        dst[0].ShouldBe((byte)127);
        dst[1].ShouldBe((byte)0);
        dst[2].ShouldBe((byte)0);
        dst[3].ShouldBe((byte)1);

        // inet_addr packs the same 4 bytes into in_addr_t (network order in memory).
        fixed (byte* ip = "127.0.0.1\0"u8) { inet_addr(ip).ShouldBe(*(uint*)dst); }

        // inet_ntop reverses it.
        byte* text = stackalloc byte[16];
        (inet_ntop(AF_INET, dst, text, 16) != null).ShouldBeTrue();
        Encoding.ASCII.GetString(text, (int)strlen(text)).ShouldBe("127.0.0.1");
    }

    [Fact]
    public unsafe void socket_calls_on_a_non_socket_fd_report_enotsock()
    {
        // fd 1 is stdout — a real fd, but not a socket. The socket calls must
        // report ENOTSOCK (exactly as a real libc does), and a bogus fd EBADF.
        errno = 0;
        listen(1, 1).ShouldBe(-1);
        errno.ShouldBe(ENOTSOCK);

        byte* sa = stackalloc byte[16];
        MakeLoopback(sa, 12345);
        errno = 0;
        bind(99999, sa, 16).ShouldBe(-1);
        errno.ShouldBe(EBADF);
    }

    [Fact]
    public unsafe void socket_rejects_af_inet6_at_create()
    {
        // AF_INET6's sockaddr_in6 marshalling isn't modeled yet, so socket() must
        // fail loudly with EAFNOSUPPORT — not hand back a dead-end fd that every
        // subsequent bind/connect would fail on. (AF_UNIX IS supported — below.)
        const int AF_INET6 = 10;
        errno = 0;
        socket(AF_INET6, SOCK_STREAM, 0).ShouldBe(-1);
        errno.ShouldBe(EAFNOSUPPORT);
    }

    [Fact]
    public async Task unix_domain_stream_echo_roundtrips()
    {
        string path = UniqueSocketPath();
        int srv = OpenUnixListener(path);
        srv.ShouldBeGreaterThanOrEqualTo(0);
        try
        {
            // Reuse EchoOnce (fd-generic): accept one connection, drain, echo back.
            var server = Task.Run(() => EchoOnce(srv), TestContext.Current.CancellationToken);
            UnixEchoClientRoundtrips(path).ShouldBeTrue();
            close(srv);
            await server.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            close(srv);
            try { System.IO.File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public unsafe void unix_getsockname_reads_back_bound_path()
    {
        // Exercises the sockaddr_un write path: bind to a pathname, then read it
        // back — proves UnixDomainSocketEndPoint round-trips through WriteSockaddr.
        string path = UniqueSocketPath();
        int srv = socket(AF_UNIX, SOCK_STREAM, 0);
        srv.ShouldBeGreaterThanOrEqualTo(0);
        try
        {
            byte* sa = stackalloc byte[128];
            uint len = MakeUnix(sa, path);
            bind(srv, sa, len).ShouldBe(0);

            byte* got = stackalloc byte[128];
            uint glen = 128;
            getsockname(srv, got, &glen).ShouldBe(0);
            ((ushort*)got)[0].ShouldBe((ushort)AF_UNIX);        // sun_family
            Encoding.UTF8.GetString(got + 2, (int)strlen(got + 2)).ShouldBe(path);
        }
        finally
        {
            close(srv);
            try { System.IO.File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    private static string UniqueSocketPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "dotcc-uds-" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + ".sock");

    /// <summary>Fill a <c>sockaddr_un</c> for <paramref name="path"/> (family host
    /// order; NUL-terminated <c>sun_path</c>); returns the used length.</summary>
    private static unsafe uint MakeUnix(byte* sa, string path)
    {
        *(ushort*)sa = AF_UNIX;
        var bytes = Encoding.UTF8.GetBytes(path);
        for (int i = 0; i < bytes.Length; i++) { sa[2 + i] = bytes[i]; }
        sa[2 + bytes.Length] = 0;
        return (uint)(2 + bytes.Length + 1);
    }

    /// <summary>socket(AF_UNIX) → bind(path) → listen; returns the listening fd.</summary>
    private static unsafe int OpenUnixListener(string path)
    {
        int srv = socket(AF_UNIX, SOCK_STREAM, 0);
        if (srv < 0) { return srv; }
        byte* sa = stackalloc byte[128];
        uint len = MakeUnix(sa, path);
        if (bind(srv, sa, len) != 0 || listen(srv, 1) != 0) { close(srv); return -1; }
        return srv;
    }

    /// <summary>Connect over AF_UNIX, send a message, half-close, read the echo,
    /// compare. True iff the round-trip matched.</summary>
    private static unsafe bool UnixEchoClientRoundtrips(string path)
    {
        int cli = socket(AF_UNIX, SOCK_STREAM, 0);
        if (cli < 0) { return false; }
        byte* peer = stackalloc byte[128];
        uint len = MakeUnix(peer, path);
        if (connect(cli, peer, len) != 0) { close(cli); return false; }

        byte* msg = stackalloc byte[64];
        int mlen = Encoding.ASCII.GetBytes("hello unix socket", new Span<byte>(msg, 64));
        int off = 0;
        while (off < mlen) { long w = send(cli, msg + off, (ulong)(mlen - off), 0); if (w <= 0) { close(cli); return false; } off += (int)w; }
        shutdown(cli, SHUT_WR);

        byte* rbuf = stackalloc byte[256];
        int total = 0;
        long g;
        while (total < 256 && (g = recv(cli, rbuf + total, (ulong)(256 - total), 0)) > 0) { total += (int)g; }
        bool ok = total == mlen && new ReadOnlySpan<byte>(rbuf, total).SequenceEqual(new ReadOnlySpan<byte>(msg, mlen));
        close(cli);
        return ok;
    }

    [Fact]
    public unsafe void so_reuseport_set_get_roundtrips()
    {
        // SO_REUSEPORT maps to ReuseAddress (documented substitution); the fix made
        // the round-trip consistent — getsockopt reads back what setsockopt stored.
        const int SO_REUSEPORT = 15;
        int fd = socket(AF_INET, SOCK_STREAM, 0);
        fd.ShouldBeGreaterThanOrEqualTo(0);
        try
        {
            int one = 1;
            setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &one, 4).ShouldBe(0);
            int got = 0;
            uint len = 4;
            getsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &got, &len).ShouldBe(0);
            got.ShouldNotBe(0);
        }
        finally { close(fd); }
    }
}
