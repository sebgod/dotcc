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

# Compile one TU → a `.cs` object fragment.
#   <DEFINES>  → -DNAME=VAL …  (target_compile_definitions / add_definitions)
#   <INCLUDES> → -I/abs/dir …  (target_include_directories / include_directories)
# dotcc is clang-shaped, so CMake's default glued spellings (`-I/abs/path`,
# `-DNAME=VAL`) parse as-is — no CMAKE_INCLUDE_FLAG_SEP_C tweak needed.
set(CMAKE_C_COMPILE_OBJECT
    "<CMAKE_C_COMPILER> ${DOTCC_DLL} --emit=obj <DEFINES> <INCLUDES> <SOURCE> -o <OBJECT>")

# Link the `.cs` objects → a runnable target (helper builds the assembly and
# writes a `dotnet`-launcher at <TARGET>, so `ctest`/`./<target>` just works).
set(CMAKE_C_LINK_EXECUTABLE
    "bash ${CMAKE_CURRENT_LIST_DIR}/dotcc-link.sh ${DOTCC_DLL} <TARGET> <OBJECTS>")
