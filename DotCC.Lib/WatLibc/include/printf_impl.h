#ifndef _PRINTF_IMPL_H
#define _PRINTF_IMPL_H

/* The wat backend's formatter, which the inline expansion of a printf with a literal format calls,
   under the names the libc's vsnprintf calls it by (WatBackend.FormatIntrinsics). Each takes the
   runtime function's own parameters: sign is '+', ' ' or 0 for a value that is not negative; mode
   1 left-justifies, 2 pads with zeros and 0 with spaces; min is an integer's least digit count. */

/* Aim the formatter at the buffer [dst, end), which it fills up to end - 1 and NUL-terminates;
   __builtin_dotcc_sink_end does that and returns the length of the whole output, and
   __builtin_dotcc_sink_count is the length so far. */
void __builtin_dotcc_sink(char *dst, char *end);
int __builtin_dotcc_sink_end(void);
int __builtin_dotcc_sink_count(void);

void __builtin_dotcc_pf_write(const char *s, int len);
void __builtin_dotcc_pf_int(long v, int sign, int min, int width, int mode);
void __builtin_dotcc_pf_uint(unsigned long v, long base, int alpha, int min, int width, int mode, int prefix, int force_zero);
void __builtin_dotcc_pf_str(const char *s, int max, int width, int mode);
void __builtin_dotcc_pf_char(int c, int width, int mode);
void __builtin_dotcc_pf_p(const void *p, int width, int mode);
void __builtin_dotcc_pf_f(double v, int prec, int sign, int width, int mode, int alt, int upper);
void __builtin_dotcc_pf_e(double v, int prec, int sign, int width, int mode, int alt, int upper);
void __builtin_dotcc_pf_g(double v, int prec, int sign, int width, int mode, int alt, int upper);
void __builtin_dotcc_pf_a(double v, int prec, int sign, int width, int mode, int alt, int upper);

/* The widest an integer's digits and a float's precision may be: the formatter's staging buffers
   (WatBackend.MaxNumDigits and MaxFloatPrec). A wider one is cut to these. */
#define __PF_MAX_DIGITS 30
#define __PF_MAX_PREC 60

#endif
