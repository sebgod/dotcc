#ifndef _DOTCC_ENV_IMPL_H
#define _DOTCC_ENV_IMPL_H

/* What musl's environment functions (getenv, setenv, unsetenv) get from musl's internal headers:
   the environment, which musl names __environ and makes environ a weak alias of (dotcc has no
   weak symbols, so here it is environ itself), and the helpers they share. */

#include <stddef.h>

extern char **environ;
#define __environ environ

char *__strchrnul(const char *s, int c);
int __putenv(char *s, size_t l, char *r);
void __env_rm_add(char *old, char *new);

#endif
