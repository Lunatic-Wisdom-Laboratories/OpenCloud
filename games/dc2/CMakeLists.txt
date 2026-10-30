cmake_minimum_required(VERSION 3.22)
project(opencloud NONE)

# Dark Cloud 2's native port (port/), built from the decompiled game in ps2/src with the shared engine in ../../engine.
# Here rather than in port so ctest finds the tests from the top of the build tree.
enable_testing()
add_subdirectory(port)
