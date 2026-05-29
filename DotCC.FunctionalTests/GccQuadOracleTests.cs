#nullable enable

using System;
using System.Collections.Generic;
using DotCC.Libc;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// Opt-in differential tests for <see cref="Float128"/> against gcc's binary128
/// <c>long double</c> oracle (<see cref="GccQuadOracle"/>). Gated by the same
/// <c>DOTCC_RUN_GCC_ORACLE=1</c> flag as the fixture gcc oracle; skips cleanly
/// when WSL/gcc (with binary128 long double) isn't present.
/// </summary>
/// <remarks>
/// This is what validates the parts of <see cref="Float128"/> that the fast
/// in-process unit tests can't fully cover — here, correctly-rounded
/// binary128→double narrowing across thousands of random full-width patterns
/// and rounding boundaries, not just hand-picked cases. Arithmetic ops join as
/// they're implemented.
/// </remarks>
public sealed class GccQuadOracleTests
{
    private const string RunGccEnv = "DOTCC_RUN_GCC_ORACLE";

    private static bool RunRequested =>
        Environment.GetEnvironmentVariable(RunGccEnv) == "1";

    [Fact]
    public void Float128_narrowing_to_double_matches_gcc()
    {
        if (!RunRequested)
        {
            Assert.Skip($"gcc binary128 oracle is opt-in. Set {RunGccEnv}=1 to run it.");
        }
        if (!GccQuadOracle.IsAvailable)
        {
            Assert.Skip($"{RunGccEnv} requested but no WSL gcc with binary128 long double " +
                        "(__LDBL_MANT_DIG__ == 113) on this host.");
        }

        var inputs = BuildInputs();
        var gcc = GccQuadOracle.NarrowToDouble64(inputs);

        var mismatches = new List<string>();
        for (int i = 0; i < inputs.Count; i++)
        {
            ulong ours = BitConverter.DoubleToUInt64Bits(Float128.ToDouble(Float128.FromBits(inputs[i])));
            ulong theirs = gcc[i];
            // NaN doubles compare equal regardless of payload bits; everything
            // else must match bit-for-bit.
            if (IsNanBits(ours) && IsNanBits(theirs)) { continue; }
            if (ours != theirs)
            {
                mismatches.Add(
                    $"in=0x{(ulong)(inputs[i] >> 64):x16}{(ulong)inputs[i]:x16}  ours=0x{ours:x16}  gcc=0x{theirs:x16}");
            }
        }

        mismatches.ShouldBeEmpty(
            $"{mismatches.Count}/{inputs.Count} binary128→double narrowings diverge from gcc:\n" +
            string.Join("\n", mismatches.Count > 20 ? mismatches.GetRange(0, 20) : mismatches));
    }

    private static bool IsNanBits(ulong b)
        => (b & 0x7FF0_0000_0000_0000UL) == 0x7FF0_0000_0000_0000UL
        && (b & 0x000F_FFFF_FFFF_FFFFUL) != 0;

    /// <summary>binary128 bit pattern from sign, unbiased exponent, 112-bit fraction.</summary>
    private static UInt128 Bits(bool sign, int unbiasedExp, UInt128 fraction)
        => ((sign ? UInt128.One : UInt128.Zero) << 127)
         | (((UInt128)(uint)(unbiasedExp + 16383) & 0x7FFF) << 112)
         | (fraction & (((UInt128.One << 112)) - 1));

    private static List<UInt128> BuildInputs()
    {
        var list = new List<UInt128>();
        UInt128 mantMask = (UInt128.One << 112) - 1;

        // ── explicit edge cases ──
        list.Add(UInt128.Zero);                                   // +0
        list.Add(UInt128.One << 127);                             // -0
        list.Add((UInt128)0x7FFF << 112);                         // +inf
        list.Add((UInt128.One << 127) | ((UInt128)0x7FFF << 112));// -inf
        list.Add(((UInt128)0x7FFF << 112) | (UInt128.One << 111));// qNaN
        list.Add(((UInt128)0x7FFF << 112) | UInt128.One);         // sNaN
        list.Add(Bits(false, 0, UInt128.Zero));                   // +1
        list.Add(Bits(true, 0, UInt128.Zero));                    // -1
        list.Add(UInt128.One);                                    // smallest binary128 subnormal
        list.Add(((UInt128)0x7FFE << 112) | mantMask);            // largest finite binary128
        // Halfway / ties-to-even boundaries around 1.0.
        UInt128 one = (UInt128)0x3FFF << 112;
        list.Add(one | (UInt128.One << (112 - 53)));                       // 1 + 2^-53 (tie → 1.0)
        list.Add(one | (UInt128.One << (112 - 53)) | (UInt128.One << 8));  // just over tie → up
        list.Add(one | (UInt128.One << (112 - 52)));                       // 1 + 2^-52 (exact double)

        // ── random normal-range values (narrow to finite doubles → exercise
        //    the mantissa drop + round-to-nearest-even path) ──
        var rng = new Random(0xC0FFEE);
        for (int i = 0; i < 3000; i++)
        {
            int e = rng.Next(-1022, 1024);   // double-normal unbiased range
            list.Add(Bits(rng.Next(2) == 1, e, RandFraction(rng)));
        }
        // ── random double-subnormal range (unbiased below -1022 down past -1074) ──
        for (int i = 0; i < 1500; i++)
        {
            int e = rng.Next(-1090, -1022);
            list.Add(Bits(rng.Next(2) == 1, e, RandFraction(rng)));
        }
        // ── fully random patterns (overflow / inf / nan / extremes) ──
        for (int i = 0; i < 1500; i++)
        {
            list.Add(((UInt128)(ulong)rng.NextInt64() << 64) | (ulong)rng.NextInt64());
        }
        return list;
    }

    private static UInt128 RandFraction(Random rng)
        => (((UInt128)(ulong)rng.NextInt64() << 64) | (ulong)rng.NextInt64()) & ((UInt128.One << 112) - 1);
}
