#nullable enable

using System;
using System.Threading.Tasks;
using DotCC.Libc;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Direct runtime tests for <see cref="Atomic"/> (AtomicLib.cs) at the narrow widths.
/// A 1- or 2-byte atomic must touch only its own bytes: CPython's <c>_PyOnceFlag</c>
/// is a <c>uint8_t</c> whose compare-exchange, done 4 bytes wide, compared the
/// neighbours too and never succeeded (a hang at interpreter exit), and on success
/// would have overwritten them. The emit side is <see cref="AtomicTests"/>; the
/// end-to-end C path is the <c>atomic-narrow</c> fixture.
/// </summary>
public sealed class AtomicRuntimeTests
{
    [Fact]
    public void byte_compare_exchange_touches_only_its_byte()
    {
        var buf = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        byte expected = 0x22;
        Atomic.CompareExchange(ref buf[1], ref expected, (byte)0x99).ShouldBeTrue();
        buf.ShouldBe(new byte[] { 0x11, 0x99, 0x33, 0x44 });

        expected = 0x22;   // stale: the CAS fails and reports what is there
        Atomic.CompareExchange(ref buf[1], ref expected, (byte)0x55).ShouldBeFalse();
        expected.ShouldBe((byte)0x99);
        buf.ShouldBe(new byte[] { 0x11, 0x99, 0x33, 0x44 });
    }

    [Fact]
    public void ushort_compare_exchange_touches_only_its_bytes()
    {
        var buf = new ushort[] { 0x1111, 0x2222, 0x3333 };
        ushort expected = 0x2222;
        Atomic.CompareExchange(ref buf[1], ref expected, (ushort)0xBEEF).ShouldBeTrue();
        buf.ShouldBe(new ushort[] { 0x1111, 0xBEEF, 0x3333 });
    }

    [Fact]
    public void narrow_exchange_load_and_store()
    {
        var buf = new byte[] { 1, 2, 3, 4 };
        Atomic.Exchange(ref buf[2], (byte)9).ShouldBe((byte)3);
        Atomic.Load(ref buf[2]).ShouldBe((byte)9);
        Atomic.Store(ref buf[0], (byte)7).ShouldBe((byte)7);
        buf.ShouldBe(new byte[] { 7, 2, 9, 4 });

        var shorts = new short[] { -1, 5, -1 };
        Atomic.Exchange(ref shorts[1], (short)-300).ShouldBe((short)5);
        shorts.ShouldBe(new short[] { -1, -300, -1 });
    }

    [Fact]
    public void byte_fetch_add_wraps_in_its_own_width()
    {
        var buf = new byte[] { 0xFF, 0x00 };
        Atomic.FetchAdd(ref buf[0], (byte)1).ShouldBe((byte)0xFF);
        buf.ShouldBe(new byte[] { 0x00, 0x00 });   // no carry into the neighbour
        Atomic.OrFetch(ref buf[1], (byte)0x81).ShouldBe((byte)0x81);
    }

    [Fact]
    public void cbool_compare_exchange()
    {
        var flags = new CBool[] { false, false, true };
        var expected = (CBool)false;
        Atomic.CompareExchange(ref flags[1], ref expected, (CBool)true).ShouldBeTrue();
        ((int)flags[0]).ShouldBe(0);
        ((int)flags[1]).ShouldBe(1);
        ((int)flags[2]).ShouldBe(1);
    }

    [Fact]
    public async Task ushort_fetch_add_is_atomic_under_contention()
    {
        // Four writers on one 2-byte counter beside a sentinel: every increment lands
        // (4 * 10000 = 40000 fits in 16 bits) and the sentinel is never touched.
        var buf = new ushort[] { 0, 0xABCD };
        var writers = new Task[4];
        for (var w = 0; w < writers.Length; w++)
        {
            writers[w] = Task.Run(() =>
            {
                for (var i = 0; i < 10000; i++) { Atomic.FetchAdd(ref buf[0], (ushort)1); }
            }, TestContext.Current.CancellationToken);
        }
        await Task.WhenAll(writers);
        buf[0].ShouldBe((ushort)40000);
        buf[1].ShouldBe((ushort)0xABCD);
    }

    [Fact]
    public void an_atomic_of_another_size_fails_loudly()
    {
        var g = Guid.NewGuid();   // 16 bytes: no Interlocked width matches
        Should.Throw<NotSupportedException>(() => Atomic.Load(ref g)).Message.ShouldContain("16-byte atomic");
    }
}
