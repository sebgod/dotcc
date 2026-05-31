cmake_minimum_required(VERSION 3.20)

# dotcc is the C compiler here — see dotcc-toolchain.cmake. Because it's wired
# as CMAKE_C_COMPILER, this is an ordinary C project: `project(... C)` and a
# plain add_executable(). CMake's own compile→link graph invokes dotcc per file
# (each .c → a .cs object) and links the objects into a runnable program.
#
#   cmake -S . -B build --toolchain dotcc-toolchain.cmake -DDOTCC_DLL=/abs/dotcc.dll
#   cmake --build build
#   ctest --test-dir build --output-on-failure
project(dotcc_cmake_demo C)

add_executable(geom_app main.c geom.c)

enable_testing()
add_test(NAME geom COMMAND geom_app)
set_tests_properties(geom PROPERTIES PASS_REGULAR_EXPRESSION "manhattan=7 dot=16")
