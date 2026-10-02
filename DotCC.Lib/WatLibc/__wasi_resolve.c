/* __wasi_resolve: a path as WASI takes it, a preopened directory and a path relative to it (see
   posix_impl.h). The current directory is the layer's own, "/" at the start: getcwd and chdir,
   here too, read and set it. The preopens are read from the host (fd_prestat_get from fd 3 on)
   on first use; one whose name is relative (a host's "--dir ." gives ".") is under "/".
   The wat target's libc (WatLibc), compiled with the program.
   dotcc-libc: also defines getcwd chdir */
#include <errno.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <posix_impl.h>

#define MAX_PREOPENS 32

static struct
{
    int fd;
    char *name;
    unsigned long len;
} preopens[MAX_PREOPENS];
static int npreopens = -1;
static char cwd[__DOTCC_PATH_MAX] = "/";

/* Into out (size bytes, at least 2) the absolute path for path, relative to base (absolute)
   when it is not absolute itself, with ".", ".." and repeated '/' taken out: it starts with '/'
   and ends without one, unless it is "/". 0, or -1 when it does not fit. */
static int normalize(const char *base, const char *path, char *out, unsigned long size)
{
    unsigned long n = 0;
    out[n++] = '/';
    for (int pass = path[0] == '/' ? 1 : 0; pass < 2; pass++)
    {
        const char *p = pass == 0 ? base : path;
        while (*p)
        {
            const char *seg;
            unsigned long len;
            while (*p == '/')
            {
                p++;
            }
            if (!*p)
            {
                break;
            }
            seg = p;
            while (*p && *p != '/')
            {
                p++;
            }
            len = (unsigned long)(p - seg);
            if (len == 1 && seg[0] == '.')
            {
                continue;
            }
            if (len == 2 && seg[0] == '.' && seg[1] == '.')
            {
                while (n > 1 && out[n - 1] != '/')
                {
                    n--;
                }
                if (n > 1)
                {
                    n--;
                }
                continue;
            }
            if (n > 1)
            {
                if (n + 1 >= size)
                {
                    return -1;
                }
                out[n++] = '/';
            }
            if (n + len >= size)
            {
                return -1;
            }
            memcpy(out + n, seg, len);
            n += len;
        }
    }
    out[n] = 0;
    return 0;
}

static void load_preopens(void)
{
    npreopens = 0;
    for (int fd = 3; npreopens < MAX_PREOPENS; fd++)
    {
        struct __wasi_prestat ps;
        char *raw;
        char *name;
        if (__wasi_fd_prestat_get(fd, &ps))
        {
            break;
        }
        if (ps.tag != 0)
        {
            continue;
        }
        raw = malloc(ps.pr_name_len + 1);
        name = malloc(ps.pr_name_len + 2);
        if (!raw || !name || __wasi_fd_prestat_dir_name(fd, raw, ps.pr_name_len))
        {
            free(raw);
            free(name);
            continue;
        }
        raw[ps.pr_name_len] = 0;
        normalize("/", raw, name, ps.pr_name_len + 2);
        free(raw);
        preopens[npreopens].fd = fd;
        preopens[npreopens].name = name;
        preopens[npreopens].len = strlen(name);
        npreopens++;
    }
}

int __wasi_resolve(const char *path, char *rel, unsigned long size)
{
    char abs[__DOTCC_PATH_MAX];
    const char *tail;
    int best = -1;
    unsigned long best_len = 0;
    if (!*path)
    {
        errno = ENOENT;
        return -1;
    }
    if (normalize(cwd, path, abs, sizeof abs))
    {
        errno = ENAMETOOLONG;
        return -1;
    }
    if (npreopens < 0)
    {
        load_preopens();
    }
    for (int i = 0; i < npreopens; i++)
    {
        unsigned long len = preopens[i].len;
        int under = len == 1
            || (strncmp(abs, preopens[i].name, len) == 0 && (abs[len] == '/' || abs[len] == 0));
        if (under && len > best_len)
        {
            best = i;
            best_len = len;
        }
    }
    if (best < 0)
    {
        errno = ENOENT;
        return -1;
    }
    tail = abs + best_len;
    if (*tail == '/')
    {
        tail++;
    }
    if (!*tail)
    {
        tail = ".";
    }
    if (strlen(tail) + 1 > size)
    {
        errno = ENAMETOOLONG;
        return -1;
    }
    strcpy(rel, tail);
    return preopens[best].fd;
}

char *getcwd(char *buf, unsigned long size)
{
    unsigned long len = strlen(cwd) + 1;
    if (!buf)
    {
        /* glibc's extension, which CPython relies on: a null buf asks for one from malloc, of
           size bytes or, with size 0, as many as it takes. */
        if (size && size < len)
        {
            errno = ERANGE;
            return 0;
        }
        buf = malloc(size ? size : len);
        if (!buf)
        {
            errno = ENOMEM;
            return 0;
        }
    }
    else if (size < len)
    {
        errno = size ? ERANGE : EINVAL;
        return 0;
    }
    memcpy(buf, cwd, len);
    return buf;
}

int chdir(const char *path)
{
    char rel[__DOTCC_PATH_MAX];
    char abs[__DOTCC_PATH_MAX];
    struct __wasi_filestat fs;
    int err;
    int dirfd = __wasi_resolve(path, rel, sizeof rel);
    if (dirfd < 0)
    {
        return -1;
    }
    err = __wasi_path_filestat_get(dirfd, __WASI_LOOKUP_SYMLINK_FOLLOW, rel, strlen(rel), &fs);
    if (err)
    {
        return __wasi_fail(err);
    }
    if (fs.filetype != __WASI_FILETYPE_DIRECTORY)
    {
        errno = ENOTDIR;
        return -1;
    }
    normalize(cwd, path, abs, sizeof abs);
    strcpy(cwd, abs);
    return 0;
}
