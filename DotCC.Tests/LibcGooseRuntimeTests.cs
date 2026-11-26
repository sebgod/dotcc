#nullable enable

using System;
using System.Threading;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Direct tests of the runtime surface Goose's POSIX path brought in: <c>&lt;sys/mman.h&gt;</c>
/// (MmanLib), <c>&lt;pthread.h&gt;</c> (PthreadLib), <c>sigaltstack</c>, the stream locks,
/// <c>nanosleep</c>, and <c>snprintf</c> with a size of 0.
/// </summary>
[Collection("Runtime")]
public sealed unsafe class LibcGooseRuntimeTests
{
    private const int PROT_READ = 1, PROT_WRITE = 2, PROT_NONE = 0;
    private const int MAP_PRIVATE = 0x02, MAP_ANONYMOUS = 0x20, MAP_NORESERVE = 0x4000;
    private const int MADV_DONTNEED = 4;

    [Fact]
    public void snprintf_with_size_zero_stores_nothing_and_measures()
    {
        // A null destination is C's way to measure first (C11 7.21.6.5p2).
        snprintf(null, 0, L("%s:%d\0"u8)).Arg(L("abc\0"u8)).Arg(1234).Done().ShouldBe(8);
        var dst = stackalloc byte[4];
        dst[0] = 0xEE;
        snprintf(dst, 0, L("x\0"u8)).Done().ShouldBe(1);
        dst[0].ShouldBe((byte)0xEE);
    }

    [Fact]
    public void mmap_gives_zeroed_pages_that_madvise_dontneed_zeroes_again()
    {
        var page = (ulong)Environment.SystemPageSize;
        var len = 8 * page;
        var p = (byte*)mmap(null, len, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
        ((nint)p).ShouldNotBe((nint)(-1));
        try
        {
            p[0].ShouldBe((byte)0);
            p[len - 1].ShouldBe((byte)0);
            new Span<byte>(p, (int)len).Fill(0x5A);
            madvise(p, 2 * page, MADV_DONTNEED).ShouldBe(0);
            p[0].ShouldBe((byte)0);
            p[2 * page].ShouldBe((byte)0x5A);
            // A guard page, then accessible again with its contents.
            mprotect(p + 4 * page, page, PROT_NONE).ShouldBe(0);
            mprotect(p + 4 * page, page, PROT_READ | PROT_WRITE).ShouldBe(0);
            p[4 * page].ShouldBe((byte)0x5A);
        }
        finally
        {
            munmap(p, len).ShouldBe(0);
        }
    }

    [Fact]
    public void mmap_of_a_reserved_only_range_commits_on_mprotect()
    {
        var page = (ulong)Environment.SystemPageSize;
        var p = (byte*)mmap(null, 4 * page, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        ((nint)p).ShouldNotBe((nint)(-1));
        mprotect(p, page, PROT_READ | PROT_WRITE).ShouldBe(0);
        p[0] = 7;
        p[0].ShouldBe((byte)7);
        munmap(p, 4 * page).ShouldBe(0);
    }

    [Fact]
    public void mmap_of_a_descriptor_dotcc_has_not_opened_fails_EBADF()
    {
        // 99 is no dotcc descriptor, whatever host descriptor 99 the test process has open: the
        // number must never reach the host's mmap as it is (CI's Linux host had one open).
        var p = mmap(null, 4096, PROT_READ, MAP_PRIVATE, 99, 0);
        ((nint)p).ShouldBe((nint)(-1));
        errno.ShouldBe(EBADF);
    }

    [Fact]
    public void mmap_of_a_file_maps_its_bytes_where_the_host_can()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dotcc-mmap-{Guid.NewGuid():N}.bin");
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* cpath = pathBytes)
        {
            var f = fopen(cpath, L("w+b\0"u8));
            try
            {
                fputs(L("mapped!\0"u8), f);
                var p = (byte*)mmap(null, 7, PROT_READ, MAP_PRIVATE, fileno(f), 0);
                if (OperatingSystem.IsWindows())
                {
                    ((nint)p).ShouldBe((nint)(-1));
                    errno.ShouldBe(ENODEV);
                }
                else
                {
                    ((nint)p).ShouldNotBe((nint)(-1));
                    new ReadOnlySpan<byte>(p, 7).SequenceEqual("mapped!"u8).ShouldBeTrue();
                    munmap(p, 7).ShouldBe(0);
                }
            }
            finally
            {
                fclose(f);
                System.IO.File.Delete(path);
            }
        }
    }

    [Fact]
    public void sigaltstack_keeps_and_hands_back_the_thread_stack()
    {
        // stack_t: ss_sp at 0, ss_flags at 8, ss_size at 16.
        var run = new Thread(() =>
        {
            var old = stackalloc byte[24];
            sigaltstack(null, old).ShouldBe(0);
            (*(int*)(old + 8) & 2).ShouldBe(2);   // SS_DISABLE: none yet
            var ss = stackalloc byte[24];
            *(nint*)ss = 0x1000;
            *(int*)(ss + 8) = 0;
            *(ulong*)(ss + 16) = 8192;
            sigaltstack(ss, null).ShouldBe(0);
            sigaltstack(null, old).ShouldBe(0);
            (*(nint*)old).ShouldBe((nint)0x1000);
            (*(ulong*)(old + 16)).ShouldBe(8192UL);
            *(ulong*)(ss + 16) = 16;
            sigaltstack(ss, null).ShouldBe(-1);   // below MINSIGSTKSZ
        });
        run.Start();
        run.Join();
    }

    [Fact]
    public void pthread_create_join_returns_the_result_and_mutexes_exclude()
    {
        var mutex = stackalloc int[2];   // PTHREAD_MUTEX_INITIALIZER
        var total = 0L;
        var threads = stackalloc ulong[4];
        Holder.Mutex = mutex;
        Holder.Total = &total;
        for (var i = 0; i < 4; i++)
        {
            pthread_create(&threads[i], null, &Holder.Work, (void*)1000).ShouldBe(0);
        }
        for (var i = 0; i < 4; i++)
        {
            void* r;
            pthread_join(threads[i], &r).ShouldBe(0);
            ((nint)r).ShouldBe((nint)1000);
        }
        total.ShouldBe(4 * 500500L);
        pthread_mutex_destroy(mutex).ShouldBe(0);
    }

    private static class Holder
    {
        public static int* Mutex;
        public static long* Total;

        public static void* Work(void* arg)
        {
            for (long i = 1; i <= (long)arg; i++)
            {
                pthread_mutex_lock(Mutex);
                *Total += i;
                pthread_mutex_unlock(Mutex);
            }
            return arg;
        }
    }

    [Fact]
    public void An_error_checking_mutex_reports_relock_and_foreign_unlock()
    {
        var attr = stackalloc int[1];
        pthread_mutexattr_init(attr);
        pthread_mutexattr_settype(attr, 2);   // PTHREAD_MUTEX_ERRORCHECK
        var m = stackalloc int[2];
        pthread_mutex_init(m, attr);
        pthread_mutex_lock(m).ShouldBe(0);
        pthread_mutex_lock(m).ShouldBe(EDEADLK);
        pthread_mutex_unlock(m).ShouldBe(0);
        pthread_mutex_unlock(m).ShouldBe(EPERM);
    }

    [Fact]
    public void pthread_cond_timedwait_times_out_on_the_monotonic_clock()
    {
        var cattr = stackalloc int[1];
        pthread_condattr_init(cattr);
        pthread_condattr_setclock(cattr, 1).ShouldBe(0);   // CLOCK_MONOTONIC
        var c = stackalloc int[2];
        pthread_cond_init(c, cattr);
        var m = stackalloc int[2];
        pthread_mutex_lock(m);
        timespec at;
        clock_gettime(1, &at);
        at.tv_nsec += 10_000_000;
        if (at.tv_nsec >= 1_000_000_000) { at.tv_sec++; at.tv_nsec -= 1_000_000_000; }
        pthread_cond_timedwait(c, m, &at).ShouldBe(ETIMEDOUT);
        pthread_mutex_unlock(m).ShouldBe(0);   // held again after the wait
    }

    [Fact]
    public void flockfile_is_recursive_and_ftrylockfile_takes_it()
    {
        flockfile(stdout);
        ftrylockfile(stdout).ShouldBe(0);
        funlockfile(stdout);
        funlockfile(stdout);
        var other = new Thread(() => { ftrylockfile(stdout).ShouldBe(0); funlockfile(stdout); });
        other.Start();
        other.Join();
    }

    [Fact]
    public void nanosleep_rejects_an_out_of_range_nanosecond_count()
    {
        timespec t = default;
        t.tv_nsec = 1_000_000_000;
        nanosleep(&t, null).ShouldBe(-1);
        errno.ShouldBe(EINVAL);
        t.tv_nsec = 1_000;
        timespec rem;
        nanosleep(&t, &rem).ShouldBe(0);
        rem.tv_sec.ShouldBe(0);
    }
}
