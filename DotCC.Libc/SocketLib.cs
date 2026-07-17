#nullable enable

using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// The BSD sockets surface (<c>&lt;sys/socket.h&gt;</c>, <c>&lt;netinet/in.h&gt;</c>,
/// <c>&lt;arpa/inet.h&gt;</c>, <c>&lt;netinet/tcp.h&gt;</c>) lowered onto
/// <see cref="System.Net.Sockets.Socket"/>. A socket fd is an ordinary dotcc fd —
/// a <c>FileSlot</c> with <c>Kind == K.Socket</c> backed by a <c>Socket</c> — so
/// <c>read</c>/<c>write</c>/<c>close</c> (in <c>FileLib</c>) work on it uniformly,
/// and <c>recv</c>/<c>send</c> here share the same bulk transfer helpers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why .NET sockets, not a libc P/Invoke:</b> <c>System.Net.Sockets</c> is a
/// complete, AOT-clean, cross-platform stack, so the same emitted program speaks
/// BSD sockets faithfully on Linux AND Windows — no Winsock/POSIX split, matching
/// dotcc's "one program, picks the host behaviour at runtime" model. The constants
/// (AF_*, SOCK_*, SO_*, SHUT_*) are the Linux/glibc values (dotcc is Linux-shaped:
/// LP64, glibc errno), and the synthetic headers hand the same numbers to the C
/// program, so a call like <c>socket(AF_INET, SOCK_STREAM, 0)</c> means the same
/// thing here as it does to gcc-on-Linux.
/// </para>
/// <para>
/// <b>Scope (slice 1):</b> blocking IPv4 (<c>AF_INET</c>) TCP + UDP. <c>sockaddr_in</c>
/// is marshalled to/from <see cref="IPEndPoint"/> by byte offset (family native,
/// port + address in network order — the same byte-exact technique as <c>rusage</c>).
/// Deferred: non-blocking / <c>O_NONBLOCK</c> (degrades to blocking, like
/// <c>fcntl</c>), <c>select</c>/<c>poll</c> over mixed fd sets, IPv6, Unix-domain,
/// and <c>getaddrinfo</c> (numeric addresses only — <c>inet_pton</c>/<c>inet_addr</c>).
/// </para>
/// </remarks>
public static unsafe partial class Libc
{
    // ---- constants (Linux/glibc numeric values; mirrored in the headers) ----
    private const int AF_UNIX = 1, AF_INET = 2, AF_INET6 = 10;
    private const int SOCK_STREAM = 1, SOCK_DGRAM = 2;
    private const int SOCK_NONBLOCK = 0x800, SOCK_CLOEXEC = 0x80000;
    private const int SOL_SOCKET = 1;
    private const int SO_REUSEADDR = 2, SO_TYPE = 3, SO_ERROR = 4, SO_BROADCAST = 6;
    private const int SO_SNDBUF = 7, SO_RCVBUF = 8, SO_KEEPALIVE = 9, SO_REUSEPORT = 15;
    private const int SO_RCVTIMEO = 20, SO_SNDTIMEO = 21;
    private const int IPPROTO_TCP = 6, IPPROTO_UDP = 17;
    private const int TCP_NODELAY = 1;
    private const int MSG_OOB = 0x1, MSG_PEEK = 0x2;
    private const uint INADDR_NONE = 0xFFFFFFFFu;

    // ---- fd <-> socket plumbing --------------------------------------------

    /// <summary>Register a connected/bound <see cref="Socket"/> as a new dotcc fd
    /// (a <c>FileSlot</c> index, the same space as <c>open</c>/<c>fileno</c>).</summary>
    private static int RegisterSocketSlot(Socket sock)
    {
        var slot = new FileSlot { Kind = FileSlot.K.Socket, Socket = sock };
        lock (_filesLock)
        {
            for (int i = 3; i < _files.Count; i++)
            {
                if (_files[i] == null) { _files[i] = slot; return i; }
            }
            _files.Add(slot);
            return _files.Count - 1;
        }
    }

    /// <summary>The <see cref="Socket"/> behind an fd, or null with
    /// <paramref name="err"/> set to EBADF (no such fd) / ENOTSOCK (not a socket).</summary>
    private static Socket? SockByFd(int fd, out int err)
    {
        var s = SlotByFd(fd);
        if (s == null) { err = EBADF; return null; }
        if (s.Socket is not { } sock) { err = ENOTSOCK; return null; }
        err = 0;
        return sock;
    }

    // ---- bulk transfer (shared with FileLib's read()/write()) --------------

    /// <summary>One <c>recv</c> into <paramref name="buf"/>: returns the byte
    /// count (0 = orderly peer shutdown, the EOF that <c>read</c> reports), or -1
    /// with errno. Returns as soon as ANY data is available — never blocks for the
    /// full <paramref name="count"/>.</summary>
    private static long SocketRecvInto(FileSlot s, void* buf, ulong count, int flags)
    {
        if (s.Socket is not { } sock) { errno = ENOTSOCK; return -1; }
        int n = (int)Math.Min(count, int.MaxValue);
        var span = new Span<byte>(buf, n);
        int got = sock.Receive(span, ToSocketFlags(flags), out var serr);
        if (serr != SocketError.Success) { errno = SocketErrno(serr); return -1; }
        return got;
    }

    /// <summary>One <c>send</c> of <paramref name="buf"/>: returns the byte count
    /// actually sent, or -1 with errno.</summary>
    private static long SocketSendFrom(FileSlot s, void* buf, ulong count, int flags)
    {
        if (s.Socket is not { } sock) { errno = ENOTSOCK; return -1; }
        int n = (int)Math.Min(count, int.MaxValue);
        var span = new ReadOnlySpan<byte>(buf, n);
        int sent = sock.Send(span, ToSocketFlags(flags), out var serr);
        if (serr != SocketError.Success) { errno = SocketErrno(serr); return -1; }
        return sent;
    }

    /// <summary>One byte off a socket (the <c>fgetc</c>-on-an-fdopen'd-socket path).
    /// -1 on EOF or error (sets the slot's EOF/error flag like the stream path).</summary>
    private static int SocketReadByte(FileSlot s)
    {
        if (s.Socket is not { } sock) { errno = ENOTSOCK; s.Err = true; return -1; }
        Span<byte> one = stackalloc byte[1];
        int n = sock.Receive(one, SocketFlags.None, out var serr);
        if (serr != SocketError.Success) { errno = SocketErrno(serr); s.Err = true; return -1; }
        if (n == 0) { s.Eof = true; return -1; }
        return one[0];
    }

    /// <summary>One byte onto a socket (the <c>fputc</c>-on-an-fdopen'd-socket path).</summary>
    private static bool SocketWriteByte(FileSlot s, byte b)
    {
        if (s.Socket is not { } sock) { errno = ENOTSOCK; s.Err = true; return false; }
        Span<byte> one = stackalloc byte[1];
        one[0] = b;
        int sent = sock.Send(one, SocketFlags.None, out var serr);
        if (serr != SocketError.Success || sent != 1) { errno = SocketErrno(serr); s.Err = true; return false; }
        return true;
    }

    // ---- <sys/socket.h> calls ----------------------------------------------

    /// <summary><c>socket(domain, type, protocol)</c> — create a socket fd.
    /// <b>IPv4 only</b> (<c>AF_INET</c>); STREAM/DGRAM types; the
    /// SOCK_NONBLOCK/SOCK_CLOEXEC type flags are stripped (non-blocking is
    /// deferred — degrades to blocking, like <c>fcntl(O_NONBLOCK)</c>). Returns
    /// the fd, or -1. <c>AF_INET6</c>/<c>AF_UNIX</c> are rejected here with
    /// <c>EAFNOSUPPORT</c> rather than creating an fd that every address-taking
    /// call (bind/connect/accept/…) would then fail — the whole address layer is
    /// AF_INET-only, so the honest failure is at create time, not a dead-end fd.</summary>
    public static int socket(int domain, int type, int protocol)
    {
        // Only AF_INET is marshallable today (TryReadSockaddrIn / sockaddr_in).
        // Fail loudly at create for the families whose address paths don't exist
        // yet, instead of handing back a socket that can never be used.
        if (domain != AF_INET) { errno = EAFNOSUPPORT; return -1; }
        var af = AddressFamily.InterNetwork;

        int baseType = type & ~(SOCK_NONBLOCK | SOCK_CLOEXEC);
        var st = baseType switch
        {
            SOCK_STREAM => SocketType.Stream,
            SOCK_DGRAM => SocketType.Dgram,
            _ => SocketType.Unknown,
        };
        if (st == SocketType.Unknown) { errno = EPROTONOSUPPORT; return -1; }

        var pt = protocol switch
        {
            0 => st == SocketType.Stream ? ProtocolType.Tcp : ProtocolType.Udp,
            IPPROTO_TCP => ProtocolType.Tcp,
            IPPROTO_UDP => ProtocolType.Udp,
            _ => ProtocolType.Unspecified,
        };
        try { return RegisterSocketSlot(new Socket(af, st, pt)); }
        catch (SocketException ex) { errno = SocketErrno(ex.SocketErrorCode); return -1; }
    }

    /// <summary><c>bind(fd, addr, addrlen)</c> — bind a socket to a local address.</summary>
    public static int bind(int fd, void* addr, uint addrlen)
    {
        if (SockByFd(fd, out var err) is not { } sock) { errno = err; return -1; }
        if (!TryReadSockaddrIn(addr, addrlen, out var ep, out var aerr)) { errno = aerr; return -1; }
        try { sock.Bind(ep); return 0; }
        catch (SocketException ex) { errno = SocketErrno(ex.SocketErrorCode); return -1; }
    }

    /// <summary><c>listen(fd, backlog)</c> — mark a socket as a passive listener.</summary>
    public static int listen(int fd, int backlog)
    {
        if (SockByFd(fd, out var err) is not { } sock) { errno = err; return -1; }
        try { sock.Listen(backlog < 0 ? 0 : backlog); return 0; }
        catch (SocketException ex) { errno = SocketErrno(ex.SocketErrorCode); return -1; }
    }

    /// <summary><c>accept(fd, addr, addrlen)</c> — block for the next connection,
    /// returning a new fd for it; fills <paramref name="addr"/> with the peer when
    /// non-null (and sets <c>*addrlen</c> to the address size).</summary>
    public static int accept(int fd, void* addr, uint* addrlen)
    {
        if (SockByFd(fd, out var err) is not { } sock) { errno = err; return -1; }
        try
        {
            var conn = sock.Accept();
            if (addr != null && addrlen != null && *addrlen >= 16 && conn.RemoteEndPoint is IPEndPoint rep)
            {
                WriteSockaddrIn(addr, rep);
            }
            if (addrlen != null) { *addrlen = 16; }
            return RegisterSocketSlot(conn);
        }
        catch (SocketException ex) { errno = SocketErrno(ex.SocketErrorCode); return -1; }
    }

    /// <summary><c>connect(fd, addr, addrlen)</c> — connect to a peer (blocking).</summary>
    public static int connect(int fd, void* addr, uint addrlen)
    {
        if (SockByFd(fd, out var err) is not { } sock) { errno = err; return -1; }
        if (!TryReadSockaddrIn(addr, addrlen, out var ep, out var aerr)) { errno = aerr; return -1; }
        try { sock.Connect(ep); return 0; }
        catch (SocketException ex) { errno = SocketErrno(ex.SocketErrorCode); return -1; }
    }

    /// <summary><c>send(fd, buf, len, flags)</c> — send on a connected socket.</summary>
    public static long send(int fd, void* buf, ulong len, int flags)
    {
        var s = SlotByFd(fd);
        if (s == null) { errno = EBADF; return -1; }
        return SocketSendFrom(s, buf, len, flags);
    }

    /// <summary><c>recv(fd, buf, len, flags)</c> — receive on a connected socket.</summary>
    public static long recv(int fd, void* buf, ulong len, int flags)
    {
        var s = SlotByFd(fd);
        if (s == null) { errno = EBADF; return -1; }
        return SocketRecvInto(s, buf, len, flags);
    }

    /// <summary><c>sendto(fd, buf, len, flags, dest, destlen)</c> — datagram send;
    /// a null <paramref name="dest"/> degrades to <c>send</c> (connected socket).</summary>
    public static long sendto(int fd, void* buf, ulong len, int flags, void* dest, uint destlen)
    {
        var s = SlotByFd(fd);
        if (s == null) { errno = EBADF; return -1; }
        if (dest == null) { return SocketSendFrom(s, buf, len, flags); }
        if (s.Socket is not { } sock) { errno = ENOTSOCK; return -1; }
        if (!TryReadSockaddrIn(dest, destlen, out var ep, out var aerr)) { errno = aerr; return -1; }
        int n = (int)Math.Min(len, int.MaxValue);
        try { return sock.SendTo(new ReadOnlySpan<byte>(buf, n), ToSocketFlags(flags), ep); }
        catch (SocketException ex) { errno = SocketErrno(ex.SocketErrorCode); return -1; }
    }

    /// <summary><c>recvfrom(fd, buf, len, flags, src, srclen)</c> — datagram
    /// receive; fills <paramref name="src"/> with the sender when non-null.</summary>
    public static long recvfrom(int fd, void* buf, ulong len, int flags, void* src, uint* srclen)
    {
        var s = SlotByFd(fd);
        if (s == null) { errno = EBADF; return -1; }
        if (src == null) { return SocketRecvInto(s, buf, len, flags); }
        if (s.Socket is not { } sock) { errno = ENOTSOCK; return -1; }
        int n = (int)Math.Min(len, int.MaxValue);
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            int got = sock.ReceiveFrom(new Span<byte>(buf, n), ToSocketFlags(flags), ref remote);
            if (srclen != null && *srclen >= 16 && remote is IPEndPoint rep)
            {
                WriteSockaddrIn(src, rep);
                *srclen = 16;
            }
            return got;
        }
        catch (SocketException ex) { errno = SocketErrno(ex.SocketErrorCode); return -1; }
    }

    /// <summary><c>shutdown(fd, how)</c> — disable receive (SHUT_RD=0), send
    /// (SHUT_WR=1), or both (SHUT_RDWR=2) on a socket; ENOTSOCK for a non-socket fd
    /// (exactly what a real libc returns).</summary>
    public static int shutdown(int fd, int how)
    {
        if (SockByFd(fd, out var err) is not { } sock) { errno = err; return -1; }
        var h = how switch
        {
            0 => SocketShutdown.Receive,
            1 => SocketShutdown.Send,
            _ => SocketShutdown.Both,
        };
        try { sock.Shutdown(h); return 0; }
        catch (SocketException ex) { errno = SocketErrno(ex.SocketErrorCode); return -1; }
    }

    /// <summary><c>setsockopt(fd, level, optname, optval, optlen)</c> — the common
    /// SOL_SOCKET options + TCP_NODELAY. SO_RCVTIMEO/SO_SNDTIMEO take a
    /// <c>struct timeval</c>; the rest take an <c>int</c>. ENOPROTOOPT-style
    /// unknown options fail with EINVAL.</summary>
    public static int setsockopt(int fd, int level, int optname, void* optval, uint optlen)
    {
        if (SockByFd(fd, out var err) is not { } sock) { errno = err; return -1; }
        int ival = (optval != null && optlen >= 4) ? *(int*)optval : 0;
        try
        {
            if (level == SOL_SOCKET)
            {
                switch (optname)
                {
                    case SO_REUSEADDR: sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, ival); break;
                    // SO_REUSEPORT (per-socket load-balanced port sharing) has no .NET
                    // equivalent; a single managed process can't self-load-balance anyway,
                    // so it's mapped to ReuseAddress (the closest useful behavior — quick
                    // rebind). Documented substitution, NOT silent: getsockopt(SO_REUSEPORT)
                    // reads back the same ReuseAddress bit, so the round-trip is consistent.
                    case SO_REUSEPORT: sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, ival); break;
                    case SO_KEEPALIVE: sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, ival); break;
                    case SO_BROADCAST: sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, ival); break;
                    case SO_RCVBUF: sock.ReceiveBufferSize = ival; break;
                    case SO_SNDBUF: sock.SendBufferSize = ival; break;
                    case SO_RCVTIMEO: sock.ReceiveTimeout = TimevalToMs(optval, optlen); break;
                    case SO_SNDTIMEO: sock.SendTimeout = TimevalToMs(optval, optlen); break;
                    default: errno = EINVAL; return -1;
                }
            }
            else if (level == IPPROTO_TCP && optname == TCP_NODELAY) { sock.NoDelay = ival != 0; }
            else { errno = EINVAL; return -1; }
            return 0;
        }
        catch (SocketException ex) { errno = SocketErrno(ex.SocketErrorCode); return -1; }
    }

    /// <summary><c>getsockopt(fd, level, optname, optval, optlen)</c> — reads back
    /// the int-valued options plus SO_ERROR (0 in blocking mode — there's no
    /// pending async error) and SO_TYPE.</summary>
    public static int getsockopt(int fd, int level, int optname, void* optval, uint* optlen)
    {
        if (SockByFd(fd, out var err) is not { } sock) { errno = err; return -1; }
        if (optval == null || optlen == null || *optlen < 4) { errno = EINVAL; return -1; }
        try
        {
            int result;
            if (level == SOL_SOCKET)
            {
                result = optname switch
                {
                    SO_ERROR => 0,
                    SO_TYPE => sock.SocketType == SocketType.Dgram ? SOCK_DGRAM : SOCK_STREAM,
                    SO_REUSEADDR => (int)sock.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress)!,
                    // Symmetric with setsockopt's documented SO_REUSEPORT→ReuseAddress
                    // substitution — reads back the same bit so a set/get round-trip agrees.
                    SO_REUSEPORT => (int)sock.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress)!,
                    SO_KEEPALIVE => (int)sock.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!,
                    SO_RCVBUF => sock.ReceiveBufferSize,
                    SO_SNDBUF => sock.SendBufferSize,
                    _ => -1,
                };
            }
            else if (level == IPPROTO_TCP && optname == TCP_NODELAY) { result = sock.NoDelay ? 1 : 0; }
            else { result = -1; }
            if (result < 0) { errno = EINVAL; return -1; }
            *(int*)optval = result;
            *optlen = 4;
            return 0;
        }
        catch (SocketException ex) { errno = SocketErrno(ex.SocketErrorCode); return -1; }
    }

    /// <summary><c>getsockname(fd, addr, addrlen)</c> — the socket's own local
    /// address (the bound endpoint; how a server on an ephemeral <c>:0</c> port
    /// learns the port the OS assigned).</summary>
    public static int getsockname(int fd, void* addr, uint* addrlen)
    {
        if (SockByFd(fd, out var err) is not { } sock) { errno = err; return -1; }
        return FillEndpoint(sock.LocalEndPoint, addr, addrlen);
    }

    /// <summary><c>getpeername(fd, addr, addrlen)</c> — the connected peer's
    /// address.</summary>
    public static int getpeername(int fd, void* addr, uint* addrlen)
    {
        if (SockByFd(fd, out var err) is not { } sock) { errno = err; return -1; }
        return FillEndpoint(sock.RemoteEndPoint, addr, addrlen);
    }

    private static int FillEndpoint(EndPoint? ep, void* addr, uint* addrlen)
    {
        if (addr == null || addrlen == null || *addrlen < 16) { errno = EINVAL; return -1; }
        if (ep is IPEndPoint ip) { WriteSockaddrIn(addr, ip); *addrlen = 16; return 0; }
        errno = ENOTCONN;
        return -1;
    }

    // ---- <arpa/inet.h> byte-order + address conversion ---------------------

    /// <summary><c>htons</c> — host-to-network short (endianness-correct on any host).</summary>
    public static ushort htons(ushort x) => (ushort)IPAddress.HostToNetworkOrder((short)x);

    /// <summary><c>ntohs</c> — network-to-host short.</summary>
    public static ushort ntohs(ushort x) => (ushort)IPAddress.NetworkToHostOrder((short)x);

    /// <summary><c>htonl</c> — host-to-network long (32-bit).</summary>
    public static uint htonl(uint x) => (uint)IPAddress.HostToNetworkOrder((int)x);

    /// <summary><c>ntohl</c> — network-to-host long (32-bit).</summary>
    public static uint ntohl(uint x) => (uint)IPAddress.NetworkToHostOrder((int)x);

    /// <summary><c>inet_addr(cp)</c> — dotted-quad IPv4 string to a network-order
    /// <c>in_addr_t</c>, or INADDR_NONE (<c>0xFFFFFFFF</c>) if unparseable.</summary>
    public static uint inet_addr(byte* cp)
    {
        if (cp == null) { return INADDR_NONE; }
        if (IPAddress.TryParse(Str(cp), out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // GetAddressBytes is network order; BitConverter packs them into the
            // uint whose in-memory layout reproduces those bytes (host-agnostic).
            return BitConverter.ToUInt32(ip.GetAddressBytes());
        }
        return INADDR_NONE;
    }

    /// <summary><c>inet_pton(af, src, dst)</c> — presentation string to a packed
    /// network-order address. Returns 1 (ok), 0 (not parseable), -1 (EAFNOSUPPORT).</summary>
    public static int inet_pton(int af, byte* src, void* dst)
    {
        if (af != AF_INET) { errno = EAFNOSUPPORT; return -1; }
        if (dst == null || src == null) { return 0; }
        if (!IPAddress.TryParse(Str(src), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) { return 0; }
        ip.GetAddressBytes().AsSpan().CopyTo(new Span<byte>(dst, 4));
        return 1;
    }

    /// <summary><c>inet_ntop(af, src, dst, size)</c> — packed network-order address
    /// to a presentation string in <paramref name="dst"/>; returns <paramref
    /// name="dst"/> or null (ENOSPC / EAFNOSUPPORT).</summary>
    public static byte* inet_ntop(int af, void* src, byte* dst, uint size)
    {
        if (af != AF_INET) { errno = EAFNOSUPPORT; return null; }
        if (src == null || dst == null) { errno = EINVAL; return null; }
        var ip = new IPAddress(new ReadOnlySpan<byte>(src, 4));
        var text = Encoding.ASCII.GetBytes(ip.ToString());
        if ((uint)(text.Length + 1) > size) { errno = ENOSPC; return null; }
        text.AsSpan().CopyTo(new Span<byte>(dst, text.Length));
        dst[text.Length] = 0;
        return dst;
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Read a <c>struct sockaddr_in</c> (family native; port + 4-byte
    /// address in network order; 16 bytes total) into an <see cref="IPEndPoint"/>.</summary>
    private static bool TryReadSockaddrIn(void* addr, uint addrlen, out IPEndPoint ep, out int err)
    {
        ep = null!;
        err = 0;
        if (addr == null || addrlen < 16) { err = EINVAL; return false; }
        byte* p = (byte*)addr;
        ushort fam = Unsafe.ReadUnaligned<ushort>(p);   // sin_family is host order
        if (fam != AF_INET) { err = EAFNOSUPPORT; return false; }
        int port = (p[2] << 8) | p[3];                  // sin_port: network order -> host
        var ipBytes = new ReadOnlySpan<byte>(p + 4, 4); // sin_addr: network order
        ep = new IPEndPoint(new IPAddress(ipBytes), port);
        return true;
    }

    /// <summary>Write an <see cref="IPEndPoint"/> into a 16-byte <c>sockaddr_in</c>.</summary>
    private static void WriteSockaddrIn(void* addr, IPEndPoint ep)
    {
        byte* p = (byte*)addr;
        Unsafe.WriteUnaligned(p, (ushort)AF_INET);      // sin_family (host order)
        p[2] = (byte)(ep.Port >> 8);                    // sin_port (network order)
        p[3] = (byte)ep.Port;
        var b = ep.Address.GetAddressBytes();           // 4 bytes, network order
        p[4] = b[0]; p[5] = b[1]; p[6] = b[2]; p[7] = b[3];
        for (int i = 8; i < 16; i++) { p[i] = 0; }      // sin_zero
    }

    /// <summary><c>struct timeval*</c> (LP64: 8-byte tv_sec + 8-byte tv_usec) to
    /// milliseconds for a socket timeout. 0 (= no timeout / infinite) passes
    /// through, matching .NET's 0-means-infinite convention.</summary>
    private static int TimevalToMs(void* tv, uint len)
    {
        if (tv == null || len < 16) { return 0; }
        long sec = *(long*)tv;
        long usec = *(long*)((byte*)tv + 8);
        long ms = sec * 1000 + usec / 1000;
        return ms is <= 0 or > int.MaxValue ? 0 : (int)ms;
    }

    /// <summary>Map the C <c>MSG_*</c> recv/send flags we honour onto
    /// <see cref="SocketFlags"/> (others are ignored).</summary>
    private static SocketFlags ToSocketFlags(int flags)
    {
        var f = SocketFlags.None;
        if ((flags & MSG_OOB) != 0) { f |= SocketFlags.OutOfBand; }
        if ((flags & MSG_PEEK) != 0) { f |= SocketFlags.Peek; }
        return f;
    }

    /// <summary>Map a .NET <see cref="SocketError"/> onto the matching POSIX
    /// <c>errno</c>, so cross-platform C comparing errno behaves the same here as
    /// on a real libc.</summary>
    private static int SocketErrno(SocketError e) => e switch
    {
        SocketError.Success => 0,
        SocketError.ConnectionRefused => ECONNREFUSED,
        SocketError.AddressAlreadyInUse => EADDRINUSE,
        SocketError.AddressNotAvailable => EADDRNOTAVAIL,
        SocketError.AddressFamilyNotSupported => EAFNOSUPPORT,
        SocketError.ConnectionReset => ECONNRESET,
        SocketError.ConnectionAborted => ECONNABORTED,
        SocketError.IsConnected => EISCONN,
        SocketError.NotConnected => ENOTCONN,
        SocketError.TimedOut => ETIMEDOUT,
        SocketError.NetworkUnreachable => ENETUNREACH,
        SocketError.HostUnreachable => EHOSTUNREACH,
        SocketError.WouldBlock => EAGAIN,
        SocketError.InProgress => EINPROGRESS,
        SocketError.MessageSize => EMSGSIZE,
        SocketError.OperationNotSupported => EOPNOTSUPP,
        SocketError.ProtocolNotSupported => EPROTONOSUPPORT,
        SocketError.NotSocket => ENOTSOCK,
        SocketError.NoBufferSpaceAvailable => ENOBUFS,
        SocketError.AccessDenied => EACCES,
        SocketError.Fault => EFAULT,
        SocketError.Interrupted => EINTR,
        SocketError.Shutdown => EPIPE,
        _ => EIO,
    };
}
