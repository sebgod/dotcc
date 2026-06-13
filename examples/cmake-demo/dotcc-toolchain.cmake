# dotcc as CMAKE_C_COMPILER.  Use with:  cmake --toolchain dotcc-toolchain.cmake -DDOTCC_DLL=/path/to/dotcc.dll
#
# dotcc transpiles C to .NET, LTO-style: the per-TU "compile" emits a `.cs`
# object fragment (the intermediate); "link" merges the objects, builds, and
# drops a launcher.  So CMake's own compile→link graph drives dotcc — you write
# plain add_executable(); this file wires the rules.

set(CMAKE_SYSTEM_NAME Generic)            # no host-platform ABI assumptions

# dotcc runs as `dotnet <dotcc.dll>`. Pass -DDOTCC_DLL=… or set $DOTCC_DLL.
if(NOT DOTCC_DLL)
    set(DOTCC_DLL "$ENV{DOTCC_DLL}")
endif()
if(NOT DOTCC_DLL)
    message(FATAL_ERROR "dotcc toolchain: set -DDOTCC_DLL=/path/to/dotcc.dll")
endif()

set(CMAKE_C_COMPILER dotnet)              # the program CMake invokes
set(CMAKE_C_COMPILER_ID "dotcc")          # skip vendor-ID sniffing
set(CMAKE_C_COMPILER_VERSION "0.1")
set(CMAKE_C_COMPILER_FORCED TRUE)         # dotcc has no native ABI — skip the test-compile/ABI probe
set(CMAKE_C_OUTPUT_EXTENSION ".cs")       # objects are `.cs` fragments (the MSVC `.obj` / gcc `.o` slot)

# Header-dependency tracking. dotcc emits a gcc/Make-format `.d` file (clang's
# `-MD`) listing each TU + every header it #includes, so CMake's compiler-driven
# dependency scanning recompiles a TU when a header it pulls in changes. This is
# how Ninja gets correct incremental rebuilds (Ninja relies solely on the
# compiler's depfile — `deps = gcc`); Makefiles use it too once
# CMAKE_C_DEPENDS_USE_COMPILER is on. The depfile flags go inline in the compile
# rule below via the <DEP_FILE> (the `.d` path) and <DEP_TARGET> (the object)
# placeholders, which CMake substitutes once CMAKE_C_DEPFILE_FORMAT is set.
set(CMAKE_C_DEPFILE_FORMAT gcc)
set(CMAKE_C_DEPENDS_USE_COMPILER TRUE)

# Compile one TU → a `.cs` object fragment.
#   <DEFINES>    → -DNAME=VAL …  (target_compile_definitions / add_definitions)
#   <INCLUDES>   → -I/abs/dir …  (target_include_directories / include_directories)
#   -MD -MT <DEP_TARGET> -MF <DEP_FILE> → emit the header-dependency `.d` file
# dotcc is clang-shaped, so CMake's default glued spellings (`-I/abs/path`,
# `-DNAME=VAL`) parse as-is — no CMAKE_INCLUDE_FLAG_SEP_C tweak needed.
set(CMAKE_C_COMPILE_OBJECT
    "<CMAKE_C_COMPILER> ${DOTCC_DLL} --emit=obj <DEFINES> <INCLUDES> -MD -MT <DEP_TARGET> -MF <DEP_FILE> <SOURCE> -o <OBJECT>")

# Link the `.cs` objects → a runnable target (helper builds the assembly and
# writes a `dotnet`-launcher at <TARGET>, so `ctest`/`./<target>` just works).
# <LINK_LIBRARIES> carries target_link_libraries(...) entries — dotcc's import
# mode binds an undefined prototype to a `-l`-named native library at link
# (glued `-lfoo` parses as-is); the link helper forwards them via "$@".
set(CMAKE_C_LINK_EXECUTABLE
    "bash ${CMAKE_CURRENT_LIST_DIR}/dotcc-link.sh ${DOTCC_DLL} <TARGET> <OBJECTS> <LINK_LIBRARIES>")
