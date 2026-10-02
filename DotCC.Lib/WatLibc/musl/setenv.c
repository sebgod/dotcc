#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <env_impl.h>

int setenv(const char *var, const char *value, int overwrite)
{
	char *s;
	size_t l1, l2;

	if (!var || !(l1 = __strchrnul(var, '=') - var) || var[l1]) {
		errno = EINVAL;
		return -1;
	}
	if (!overwrite && getenv(var)) return 0;

	l2 = strlen(value);
	s = malloc(l1+l2+2);
	if (!s) return -1;
	memcpy(s, var, l1);
	s[l1] = '=';
	memcpy(s+l1+1, value, l2+1);
	return __putenv(s, l1, s);
}
