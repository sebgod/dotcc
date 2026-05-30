#nullable enable

using System.Threading;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the libc "system" surfaces that the compiler emits against
/// but which had no direct coverage: <c>&lt;ctype.h&gt;</c>,
/// <c>&lt;assert.h&gt;</c>, <c>&lt;setjmp.h&gt;</c> and the C11
/// <c>&lt;threads.h&gt;</c> subset. Each function is exercised the way
/// emitted C code would call it (the threads/setjmp handles are taken by
/// address, matching <c>thrd_t t; thrd_create(&amp;t, …)</c>).
/// </summary>
public sealed unsafe class LibcSystemTests
{
    // =================================================================
    // <ctype.h>  — classification + case conversion ("C" locale, ASCII)
    // =================================================================

    [Theory]
    [InlineData('A', 1)] [InlineData('z', 1)] [InlineData('5', 0)]
    [InlineData(' ', 0)] [InlineData(200, 0)] [InlineData(-1, 0)]   // non-ASCII / EOF → 0
    public void isalpha_classifies_letters(int c, int expected) => isalpha(c).ShouldBe(expected);

    [Theory]
    [InlineData('0', 1)] [InlineData('9', 1)] [InlineData('a', 0)] [InlineData(-1, 0)]
    public void isdigit_classifies_decimal_digits(int c, int expected) => isdigit(c).ShouldBe(expected);

    [Theory]
    [InlineData('a', 1)] [InlineData('7', 1)] [InlineData('!', 0)]
    public void isalnum_is_letter_or_digit(int c, int expected) => isalnum(c).ShouldBe(expected);

    [Theory]
    [InlineData(' ', 1)] [InlineData('\t', 1)] [InlineData('\n', 1)]
    [InlineData('\r', 1)] [InlineData('\v', 1)] [InlineData('\f', 1)] [InlineData('x', 0)]
    public void isspace_classifies_whitespace(int c, int expected) => isspace(c).ShouldBe(expected);

    [Theory]
    [InlineData('A', 1)] [InlineData('a', 0)]
    public void isupper_classifies_uppercase(int c, int expected) => isupper(c).ShouldBe(expected);

    [Theory]
    [InlineData('a', 1)] [InlineData('A', 0)]
    public void islower_classifies_lowercase(int c, int expected) => islower(c).ShouldBe(expected);

    [Theory]
    [InlineData('9', 1)] [InlineData('a', 1)] [InlineData('F', 1)] [InlineData('g', 0)]
    public void isxdigit_classifies_hex_digits(int c, int expected) => isxdigit(c).ShouldBe(expected);

    [Theory]
    [InlineData(0x01, 1)] [InlineData(0x7F, 1)] [InlineData('A', 0)]
    public void iscntrl_classifies_control_chars(int c, int expected) => iscntrl(c).ShouldBe(expected);

    [Theory]
    [InlineData(' ', 1)] [InlineData('A', 1)] [InlineData(0x7F, 0)] [InlineData(0x01, 0)]
    public void isprint_classifies_printable(int c, int expected) => isprint(c).ShouldBe(expected);

    [Theory]
    [InlineData('A', 1)] [InlineData(' ', 0)] [InlineData(0x7F, 0)]
    public void isgraph_is_printable_non_space(int c, int expected) => isgraph(c).ShouldBe(expected);

    [Theory]
    [InlineData('!', 1)] [InlineData('A', 0)] [InlineData(' ', 0)] [InlineData('5', 0)]
    public void ispunct_is_graph_minus_alnum(int c, int expected) => ispunct(c).ShouldBe(expected);

    [Theory]
    [InlineData('a', 'A')] [InlineData('A', 'A')] [InlineData('5', '5')]
    public void toupper_uppercases_letters_passes_through_rest(int c, int expected) =>
        toupper(c).ShouldBe(expected);

    [Theory]
    [InlineData('A', 'a')] [InlineData('a', 'a')] [InlineData('5', '5')]
    public void tolower_lowercases_letters_passes_through_rest(int c, int expected) =>
        tolower(c).ShouldBe(expected);

    // =================================================================
    // <assert.h>  — __dotcc_assert overloads + NDEBUG no-op
    // =================================================================

    [Fact]
    public void assert_noop_does_nothing() => __dotcc_assert_noop();   // NDEBUG branch

    [Fact]
    public void assert_int_passes_when_nonzero() => __dotcc_assert(1);

    [Fact]
    public void assert_int_throws_when_zero()
    {
        var ex = Should.Throw<AssertionFailedException>(() => __dotcc_assert(0));
        ex.ConditionText.ShouldBe("0");
        ex.Message.ShouldContain("Assertion failed: 0");
    }

    [Fact]
    public void assert_bool_captures_condition_source_text()
    {
        // CallerArgumentExpression inlines the literal source of the condition.
        var ex = Should.Throw<AssertionFailedException>(() => __dotcc_assert(1 == 2));
        ex.ConditionText.ShouldBe("1 == 2");
    }

    [Fact]
    public void assert_bool_passes_when_true() => __dotcc_assert(true);

    [Fact]
    public void assert_double_throws_on_zero()
    {
        Should.Throw<AssertionFailedException>(() => __dotcc_assert(0.0));
        __dotcc_assert(1.5);   // non-zero double: no throw
    }

    [Fact]
    public void assert_pointer_throws_on_null_passes_on_nonnull()
    {
        Should.Throw<AssertionFailedException>(() => __dotcc_assert((void*)null));
        int x = 7;
        __dotcc_assert(&x);   // non-null pointer: no throw
    }

    // =================================================================
    // <setjmp.h>  — setjmp/longjmp lowered onto exceptions
    // =================================================================

    [Fact]
    public void setjmp_returns_zero_on_direct_call() => setjmp(new LongJmpToken()).ShouldBe(0);

    [Fact]
    public void longjmp_throws_tagged_exception_carrying_value()
    {
        var env = new LongJmpToken();
        var ex = Should.Throw<LongJmpException>(() => longjmp(env, 5));
        ex.Token.ShouldBeSameAs(env);
        ex.Value.ShouldBe(5);
        ex.Message.ShouldContain("value=5");
    }

    [Fact]
    public void longjmp_normalizes_zero_value_to_one()
    {
        // longjmp(env, 0) must arrive as 1 — a setjmp can never "return" 0
        // from the jump path, so the value is bumped per C99 §7.13.2.1.
        var ex = Should.Throw<LongJmpException>(() => longjmp(new LongJmpToken(), 0));
        ex.Value.ShouldBe(1);
    }

    // =================================================================
    // <threads.h>  — thread create/join/yield + mutexes
    // =================================================================

    // C-style thread body: int f(void* arg). Reads an int through arg and
    // returns it doubled, so the result is observable via thrd_join.
    private static int Doubler(void* arg) => (*(int*)arg) * 2;

    [Fact]
    public void thrd_create_join_propagates_return_value()
    {
        int input = 21;
        thrd_t t;
        thrd_create(&t, &Doubler, &input).ShouldBe(thrd_success);
        int res = -1;
        thrd_join(t, &res).ShouldBe(thrd_success);
        res.ShouldBe(42);
    }

    [Fact]
    public void thrd_join_accepts_null_result_pointer()
    {
        int input = 5;
        thrd_t t;
        thrd_create(&t, &Doubler, &input).ShouldBe(thrd_success);
        thrd_join(t, null).ShouldBe(thrd_success);   // discard result
    }

    [Fact]
    public void thrd_create_rejects_null_handle() =>
        thrd_create(null, &Doubler, null).ShouldBe(thrd_error);

    [Fact]
    public void thrd_join_on_unknown_handle_errors()
    {
        thrd_t bogus = default;   // never created
        thrd_join(bogus, null).ShouldBe(thrd_error);
    }

    [Fact]
    public void thrd_yield_is_callable() => thrd_yield();   // no-throw scheduler hint

    [Fact]
    public void mtx_plain_lock_unlock_roundtrip()
    {
        mtx_t m;
        mtx_init(&m, mtx_plain).ShouldBe(thrd_success);
        mtx_lock(&m).ShouldBe(thrd_success);
        // Plain mutex already held by us: a non-blocking trylock must report busy.
        mtx_trylock(&m).ShouldBe(thrd_busy);
        mtx_unlock(&m).ShouldBe(thrd_success);
        // Now free — trylock succeeds.
        mtx_trylock(&m).ShouldBe(thrd_success);
        mtx_unlock(&m).ShouldBe(thrd_success);
        mtx_destroy(&m);
    }

    [Fact]
    public void mtx_recursive_allows_reentrant_lock()
    {
        mtx_t m;
        mtx_init(&m, mtx_plain | mtx_recursive).ShouldBe(thrd_success);
        mtx_lock(&m).ShouldBe(thrd_success);
        mtx_lock(&m).ShouldBe(thrd_success);     // re-entrant: depth 2
        mtx_trylock(&m).ShouldBe(thrd_success);  // depth 3, still same thread
        mtx_unlock(&m).ShouldBe(thrd_success);
        mtx_unlock(&m).ShouldBe(thrd_success);
        mtx_unlock(&m).ShouldBe(thrd_success);
        mtx_destroy(&m);
    }

    [Fact]
    public void mtx_init_rejects_null_handle() => mtx_init(null, mtx_plain).ShouldBe(thrd_error);

    [Fact]
    public void mtx_operations_serialize_across_threads()
    {
        // A plain mutex held by the main thread blocks a worker until released
        // — proves the SemaphoreSlim actually gates cross-thread.
        mtx_t m;
        mtx_init(&m, mtx_plain);
        mtx_lock(&m);

        // A lambda can't capture a pointer (closures can't hold pointer-typed
        // fields), so capture the handle's id by value and rebuild a mtx_t on
        // the worker's own stack — both refer to the same MtxState side entry.
        int id = m.Id;
        int acquired = 0;
        var worker = new Thread(() =>
        {
            mtx_t local = default; local.Id = id;
            mtx_lock(&local);
            Volatile.Write(ref acquired, 1);
            mtx_unlock(&local);
        });
        worker.Start();

        Thread.Sleep(50);
        Volatile.Read(ref acquired).ShouldBe(0);   // still blocked on our lock

        mtx_unlock(&m);
        worker.Join();
        acquired.ShouldBe(1);                       // worker proceeded after release
        mtx_destroy(&m);
    }
}
