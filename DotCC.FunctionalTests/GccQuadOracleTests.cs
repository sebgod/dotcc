#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
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

    [Fact]
    public void Float128_addition_matches_gcc()
        => AssertOpMatchesGcc(GccQuadOracle.Add, BuildPairs(),
            c => Float128.Add(Float128.FromBits(c[0]), Float128.FromBits(c[1])).Bits);

    [Fact]
    public void Float128_multiplication_matches_gcc()
        => AssertOpMatchesGcc(GccQuadOracle.Multiply, BuildPairs(),
            c => Float128.Multiply(Float128.FromBits(c[0]), Float128.FromBits(c[1])).Bits);

    [Fact]
    public void Float128_division_matches_gcc()
        => AssertOpMatchesGcc(GccQuadOracle.Divide, BuildPairs(),
            c => Float128.Divide(Float128.FromBits(c[0]), Float128.FromBits(c[1])).Bits);

    [Fact]
    public void Float128_sqrt_matches_gcc()
        => AssertOpMatchesGcc(GccQuadOracle.Sqrt, BuildUnary(),
            c => Float128.Sqrt(Float128.FromBits(c[0])).Bits);

    [Fact]
    public void Float128_fma_matches_gcc()
        => AssertOpMatchesGcc(GccQuadOracle.Fma, BuildTriples(),
            c => Float128.FusedMultiplyAdd(
                Float128.FromBits(c[0]), Float128.FromBits(c[1]), Float128.FromBits(c[2])).Bits);

    // Exact integral / algebraic functions — bit-exact vs gcc.
    [Fact]
    public void Float128_floor_matches_gcc()
        => AssertOpMatchesGcc(GccQuadOracle.Floor, BuildUnary(), c => Float128.Floor(Float128.FromBits(c[0])).Bits);

    [Fact]
    public void Float128_ceiling_matches_gcc()
        => AssertOpMatchesGcc(GccQuadOracle.Ceiling, BuildUnary(), c => Float128.Ceiling(Float128.FromBits(c[0])).Bits);

    [Fact]
    public void Float128_truncate_matches_gcc()
        => AssertOpMatchesGcc(GccQuadOracle.Truncate, BuildUnary(), c => Float128.Truncate(Float128.FromBits(c[0])).Bits);

    [Fact]
    public void Float128_round_matches_gcc()
        => AssertOpMatchesGcc(GccQuadOracle.Round, BuildUnary(), c => Float128.Round(Float128.FromBits(c[0])).Bits);

    [Fact]
    public void Float128_copysign_matches_gcc()
        => AssertOpMatchesGcc(GccQuadOracle.CopySign, BuildPairs(),
            c => Float128.CopySign(Float128.FromBits(c[0]), Float128.FromBits(c[1])).Bits);

    [Fact]
    public void Float128_fmod_matches_gcc()
        => AssertOpMatchesGcc(GccQuadOracle.Fmod, BuildPairs(),
            c => Float128.Fmod(Float128.FromBits(c[0]), Float128.FromBits(c[1])).Bits);

    // cbrt/hypot: we round correctly, but glibc's cbrtl/hypotl aren't
    // guaranteed correctly-rounded — so compare within a small ULP tolerance.
    [Fact]
    public void Float128_cbrt_close_to_gcc()
        => AssertOpCloseToGcc(GccQuadOracle.Cbrt, BuildUnary(),
            c => Float128.Cbrt(Float128.FromBits(c[0])).Bits, maxUlp: 2);

    [Fact]
    public void Float128_hypot_close_to_gcc()
    {
        // Exclude hypot(±inf, NaN): C99 Annex F.10.4.3 says this is +inf "even
        // if y is NaN" (which dotcc returns), but this glibc returns NaN — an
        // implementation divergence on a corner the standard pins to +inf.
        var cases = BuildPairs().FindAll(c =>
            !((IsInf(c[0]) && IsQuadNan(c[1])) || (IsQuadNan(c[0]) && IsInf(c[1]))));
        AssertOpCloseToGcc(GccQuadOracle.Hypot, cases,
            c => Float128.Hypot(Float128.FromBits(c[0]), Float128.FromBits(c[1])).Bits, maxUlp: 2);
    }

    private static bool IsInf(UInt128 b)
        => ((b >> 112) & 0x7FFF) == 0x7FFF && (b & ((UInt128.One << 112) - 1)) == 0;

    [Fact]
    public void Float128_exp_close_to_gcc()
        => AssertOpCloseToGcc(GccQuadOracle.Exp, BuildExpInputs(),
            c => Float128.Exp(Float128.FromBits(c[0])).Bits, maxUlp: 2);

    [Fact]
    public void Float128_log_close_to_gcc()
        => AssertOpCloseToGcc(GccQuadOracle.Log, BuildLogInputs(),
            c => Float128.Log(Float128.FromBits(c[0])).Bits, maxUlp: 2);

    [Fact]
    public void Float128_exp2_close_to_gcc()
        => AssertOpCloseToGcc(GccQuadOracle.Exp2, BuildExpInputs(),
            c => Float128.Exp2(Float128.FromBits(c[0])).Bits, maxUlp: 2);

    [Fact]
    public void Float128_exp10_close_to_gcc()
        => AssertOpCloseToGcc(GccQuadOracle.Exp10, BuildExpInputs(),
            c => Float128.Exp10(Float128.FromBits(c[0])).Bits, maxUlp: 2);

    [Fact]
    public void Float128_expm1_close_to_gcc()
        => AssertOpCloseToGcc(GccQuadOracle.Expm1, BuildExpInputs(),
            c => Float128.Expm1(Float128.FromBits(c[0])).Bits, maxUlp: 2);

    [Fact]
    public void Float128_log2_close_to_gcc()
        => AssertOpCloseToGcc(GccQuadOracle.Log2, BuildLogInputs(),
            c => Float128.Log2(Float128.FromBits(c[0])).Bits, maxUlp: 2);

    [Fact]
    public void Float128_log10_close_to_gcc()
        => AssertOpCloseToGcc(GccQuadOracle.Log10, BuildLogInputs(),
            c => Float128.Log10(Float128.FromBits(c[0])).Bits, maxUlp: 2);

    [Fact]
    public void Float128_log1p_close_to_gcc()
        => AssertOpCloseToGcc(GccQuadOracle.Log1p, BuildLog1pInputs(),
            c => Float128.Log1p(Float128.FromBits(c[0])).Bits, maxUlp: 2);

    [Fact]
    public void Float128_pow_close_to_gcc()
        => AssertOpCloseToGcc(GccQuadOracle.Pow, BuildPowInputs(),
            c => Float128.Pow(Float128.FromBits(c[0]), Float128.FromBits(c[1])).Bits, maxUlp: 2);

    // log1p over x > -1, spanning small |x| (the atanh path) and larger.
    private static List<UInt128[]> BuildLog1pInputs()
    {
        var rng = new Random(0x1B1);
        var list = new List<UInt128[]>();
        for (int i = 0; i < 2500; i++) // small |x|: exponents down to -60
        {
            list.Add(new[] { Bits(rng.Next(2) == 1, rng.Next(-60, 0), RandFraction(rng)) });
        }
        for (int i = 0; i < 1500; i++) // larger positive x
        {
            list.Add(new[] { Bits(false, rng.Next(0, 200), RandFraction(rng)) });
        }
        return list;
    }

    // pow(x, y): positive bases over moderate exponents so the result stays finite.
    private static List<UInt128[]> BuildPowInputs()
    {
        var rng = new Random(0x70E);
        var list = new List<UInt128[]>();
        for (int i = 0; i < 4000; i++)
        {
            list.Add(new[]
            {
                Bits(false, rng.Next(-30, 31), RandFraction(rng)),   // base > 0
                Bits(rng.Next(2) == 1, rng.Next(-6, 7), RandFraction(rng)), // small exponent
            });
        }
        // Integer exponents on negative bases (sign handling).
        for (int i = 0; i < 500; i++)
        {
            list.Add(new[]
            {
                Bits(true, rng.Next(-10, 11), RandFraction(rng)),
                FromInt(rng.Next(-5, 6)),
            });
        }
        return list;
    }

    private static UInt128 FromInt(int n) => Float128.FromInt64(n).Bits;

    // exp over a non-overflowing range (|x| ≲ 700): exponents -40..9, both signs.
    private static List<UInt128[]> BuildExpInputs()
    {
        var rng = new Random(0xE7B);
        var list = new List<UInt128[]> { new[] { UInt128.Zero }, new[] { Bits(false, 0, UInt128.Zero) } };
        for (int i = 0; i < 4000; i++)
        {
            list.Add(new[] { Bits(rng.Next(2) == 1, rng.Next(-40, 10), RandFraction(rng)) });
        }
        return list;
    }

    // log over positive values across the full exponent range (sign bit clear).
    private static List<UInt128[]> BuildLogInputs()
    {
        var rng = new Random(0x106);
        var list = new List<UInt128[]> { new[] { Bits(false, 0, UInt128.Zero) } }; // 1.0 → 0
        for (int i = 0; i < 4000; i++)
        {
            list.Add(new[] { Bits(false, rng.Next(-1000, 1001), RandFraction(rng)) });
        }
        return list;
    }

    /// <summary>
    /// Run a binary128-result op over <paramref name="cases"/> (each carrying
    /// the op's operand bit patterns) and assert our result equals gcc's
    /// bit-for-bit (NaN payloads excepted).
    /// </summary>
    private static void AssertOpMatchesGcc(
        GccQuadOracle.Op op, IReadOnlyList<UInt128[]> cases, Func<UInt128[], UInt128> ours)
    {
        if (!RunRequested)
        {
            Assert.Skip($"gcc binary128 oracle is opt-in. Set {RunGccEnv}=1 to run it.");
        }
        if (!GccQuadOracle.IsAvailable)
        {
            Assert.Skip($"{RunGccEnv} requested but no WSL gcc with binary128 long double on this host.");
        }

        var gcc = GccQuadOracle.ComputeBinary128(op, cases);

        var mismatches = new List<string>();
        for (int i = 0; i < cases.Count; i++)
        {
            UInt128 mine = ours(cases[i]);
            UInt128 theirs = gcc[i];
            if (IsQuadNan(mine) && IsQuadNan(theirs)) { continue; } // NaN payloads may differ
            if (mine != theirs)
            {
                string ops = string.Join(" ", Array.ConvertAll(cases[i], x => "0x" + Hex(x)));
                mismatches.Add($"in=[{ops}]  ours=0x{Hex(mine)}  gcc=0x{Hex(theirs)}");
            }
        }

        mismatches.ShouldBeEmpty(
            $"{mismatches.Count}/{cases.Count} results diverge from gcc:\n" +
            string.Join("\n", mismatches.Count > 20 ? mismatches.GetRange(0, 20) : mismatches));
    }

    /// <summary>Like <see cref="AssertOpMatchesGcc"/> but allows up to
    /// <paramref name="maxUlp"/> representable steps of difference — for
    /// functions where our result is correctly rounded but gcc's may not be.</summary>
    private static void AssertOpCloseToGcc(
        GccQuadOracle.Op op, IReadOnlyList<UInt128[]> cases, Func<UInt128[], UInt128> ours, int maxUlp)
    {
        if (!RunRequested)
        {
            Assert.Skip($"gcc binary128 oracle is opt-in. Set {RunGccEnv}=1 to run it.");
        }
        if (!GccQuadOracle.IsAvailable)
        {
            Assert.Skip($"{RunGccEnv} requested but no WSL gcc with binary128 long double on this host.");
        }

        var gcc = GccQuadOracle.ComputeBinary128(op, cases);
        var mismatches = new List<string>();
        for (int i = 0; i < cases.Count; i++)
        {
            UInt128 mine = ours(cases[i]);
            UInt128 theirs = gcc[i];
            bool mineNan = IsQuadNan(mine), theirsNan = IsQuadNan(theirs);
            if (mineNan && theirsNan) { continue; }
            if (mineNan != theirsNan)
            {
                mismatches.Add($"NaN mismatch: ours=0x{Hex(mine)} gcc=0x{Hex(theirs)}");
                continue;
            }
            BigInteger ulp = BigInteger.Abs(TotalOrder(mine) - TotalOrder(theirs));
            if (ulp > maxUlp)
            {
                string ops = string.Join(" ", Array.ConvertAll(cases[i], x => "0x" + Hex(x)));
                mismatches.Add($"in=[{ops}]  ours=0x{Hex(mine)}  gcc=0x{Hex(theirs)}  ulp={ulp}");
            }
        }
        mismatches.ShouldBeEmpty(
            $"{mismatches.Count}/{cases.Count} results exceed {maxUlp} ULP from gcc:\n" +
            string.Join("\n", mismatches.Count > 20 ? mismatches.GetRange(0, 20) : mismatches));
    }

    /// <summary>Signed total-order index of a binary128 bit pattern (sign-
    /// magnitude → monotonic integer), so |Δindex| counts representable steps.</summary>
    private static BigInteger TotalOrder(UInt128 b)
    {
        UInt128 mag = b & ((UInt128.One << 127) - 1);
        BigInteger m = ((BigInteger)(ulong)(mag >> 64) << 64) | (ulong)mag;
        return (b >> 127) != 0 ? -m : m;
    }

    private static List<UInt128[]> BuildUnary()
    {
        var list = new List<UInt128[]>();
        foreach (var x in BuildInputs()) { list.Add(new[] { x }); }
        return list;
    }

    private static List<UInt128[]> BuildTriples()
    {
        var rng = new Random(0xF3A);
        var triples = new List<UInt128[]>();
        // Normal-range triples → exercise the exact-product + exact-add path.
        for (int i = 0; i < 3000; i++)
        {
            triples.Add(new[]
            {
                Bits(rng.Next(2) == 1, rng.Next(-500, 501), RandFraction(rng)),
                Bits(rng.Next(2) == 1, rng.Next(-500, 501), RandFraction(rng)),
                Bits(rng.Next(2) == 1, rng.Next(-500, 501), RandFraction(rng)),
            });
        }
        // fma's whole point: a·b and c nearly cancel, so the un-rounded product
        // matters. c ≈ -(a·b).
        for (int i = 0; i < 1500; i++)
        {
            int ea = rng.Next(-200, 201), eb = rng.Next(-200, 201);
            UInt128 fa = RandFraction(rng), fb = RandFraction(rng);
            bool sa = rng.Next(2) == 1, sb = rng.Next(2) == 1;
            // Approximate product exponent for an opposing c.
            triples.Add(new[]
            {
                Bits(sa, ea, fa),
                Bits(sb, eb, fb),
                Bits(!(sa ^ sb), ea + eb, RandFraction(rng)),
            });
        }
        // Fully random (inf/nan/zero/subnormal mixes).
        for (int i = 0; i < 1000; i++)
        {
            triples.Add(new[] { RandBits(rng), RandBits(rng), RandBits(rng) });
        }
        return triples;
    }

    private static string Hex(UInt128 x) => $"{(ulong)(x >> 64):x16}{(ulong)x:x16}";

    private static bool IsQuadNan(UInt128 b)
        => ((b >> 112) & 0x7FFF) == 0x7FFF && (b & ((UInt128.One << 112) - 1)) != 0;

    private static List<UInt128[]> BuildPairs()
    {
        var rng = new Random(0xADD);
        var pool = BuildInputs();
        var pairs = new List<UInt128[]>();

        // Every edge value against a few others (inf±inf, x+0, 0+0, 1-1, …).
        for (int i = 0; i < 13; i++)            // the explicit edges from BuildInputs
        {
            for (int j = 0; j < 13; j++) { pairs.Add(new[] { pool[i], pool[j] }); }
        }

        // Random normal-range pairs → rounding on the aligned sum.
        for (int i = 0; i < 3000; i++)
        {
            pairs.Add(new[]
            {
                Bits(rng.Next(2) == 1, rng.Next(-1000, 1001), RandFraction(rng)),
                Bits(rng.Next(2) == 1, rng.Next(-1000, 1001), RandFraction(rng)),
            });
        }
        // Near-cancellation: b ≈ -a with a perturbed low mantissa → catastrophic
        // cancellation + renormalisation.
        for (int i = 0; i < 1500; i++)
        {
            int e = rng.Next(-1000, 1001);
            bool s = rng.Next(2) == 1;
            UInt128 fa = RandFraction(rng);
            UInt128 fb = fa ^ ((UInt128)(ulong)rng.NextInt64() & 0xFFFF); // tweak low bits
            pairs.Add(new[] { Bits(s, e, fa), Bits(!s, e, fb) });
        }
        // Widely separated exponents → smaller operand collapses to sticky.
        for (int i = 0; i < 1500; i++)
        {
            pairs.Add(new[]
            {
                Bits(rng.Next(2) == 1, rng.Next(900, 1001), RandFraction(rng)),
                Bits(rng.Next(2) == 1, rng.Next(-1001, -900), RandFraction(rng)),
            });
        }
        // Fully random patterns (subnormals, inf, NaN, extremes).
        for (int i = 0; i < 1000; i++)
        {
            pairs.Add(new[] { RandBits(rng), RandBits(rng) });
        }
        return pairs;
    }

    private static UInt128 RandBits(Random rng)
        => ((UInt128)(ulong)rng.NextInt64() << 64) | (ulong)rng.NextInt64();

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
