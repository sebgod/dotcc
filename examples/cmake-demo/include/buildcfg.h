#ifndef BUILDCFG_H
#define BUILDCFG_H

/* This header lives in include/, NOT next to main.c — so the preprocessor's
   source-dir search misses it. It is found ONLY when CMake's
   target_include_directories() is threaded through dotcc's compile rule as
   -I (see dotcc-toolchain.cmake). If -I weren't forwarded, the build fails
   right here with "header not found" — which is exactly what makes this a
   real test of the plumbing. */
#define BUILD_TAG "cfg-ok"

#endif
