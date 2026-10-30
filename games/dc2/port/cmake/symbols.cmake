# Debug information for crash reports (docs/SYMBOLS.md). Every configuration carries at least DWARF line
# tables in opencloud, so the in-process crash handler (cpptrace, through libdwarf) prints file:line from a
# shipped binary: Debug and RelWithDebInfo have -g already, Release and MinSizeRel gain -gline-tables-only.
# The link writes a build id, which the report header names and tools/crash/symbolize.py matches symbols by.
#
# CT_SYMBOLS:
#   embedded  debug information in the binary only (macOS: opencloud.dSYM, see below).
#   split     also writes developer symbol files beside the binary: opencloud.pdb on Windows (CodeView for
#             WinDbg, Visual Studio and dbghelp; the exe keeps its DWARF for cpptrace), opencloud.debug on
#             Linux and opencloud.dSYM on macOS even in Debug.
#   off       no added flags or steps.
set(CT_SYMBOLS embedded CACHE STRING "Debug information for crash reports: embedded, split or off")
set_property(CACHE CT_SYMBOLS PROPERTY STRINGS embedded split off)
if(NOT CT_SYMBOLS MATCHES "^(embedded|split|off)$")
    message(FATAL_ERROR "CT_SYMBOLS must be embedded, split or off, not '${CT_SYMBOLS}'")
endif()

if(NOT CT_SYMBOLS STREQUAL "off")
    set(ct_debug_options "")
    if(WIN32 AND CT_SYMBOLS STREQUAL "split")
        # clang emits CodeView instead of DWARF for -gcodeview alone; -gdwarf keeps both. -gdwarf also asks
        # for full debug information, so it must come before -gline-tables-only for that to stay the level.
        list(APPEND ct_debug_options -gdwarf -gcodeview)
        target_link_options(opencloud PRIVATE -Wl,--pdb=)
    endif()
    list(APPEND ct_debug_options $<$<CONFIG:Release,MinSizeRel>:-gline-tables-only>)
    target_compile_options(ct_options INTERFACE ${ct_debug_options})
    target_compile_options(ct_host PRIVATE ${ct_debug_options})

    if(WIN32)
        # Without a PDB, lld records the build id in a CodeView debug directory entry of its own.
        target_link_options(opencloud PRIVATE -Wl,--build-id)
    elseif(NOT APPLE)
        target_link_options(opencloud PRIVATE -Wl,--build-id=sha1)
    endif()
    # Mach-O links have an LC_UUID by default.

    # A copy of the debug sections in opencloud.debug, for gdb and symbolize.py; the binary keeps its own
    # line tables for the crash handler, and its debuglink names the copy.
    if(CT_SYMBOLS STREQUAL "split" AND NOT WIN32 AND NOT APPLE)
        if(NOT CMAKE_OBJCOPY)
            message(FATAL_ERROR "CT_SYMBOLS=split needs objcopy or llvm-objcopy")
        endif()
        add_custom_command(TARGET opencloud POST_BUILD
            COMMAND ${CMAKE_OBJCOPY} --only-keep-debug $<TARGET_FILE:opencloud> $<TARGET_FILE:opencloud>.debug
            COMMAND ${CMAKE_OBJCOPY} --add-gnu-debuglink=$<TARGET_FILE:opencloud>.debug $<TARGET_FILE:opencloud>
            VERBATIM)
    endif()

    # A Mach-O link leaves the DWARF in the object files, so a binary moved off the build machine has no line
    # tables until dsymutil collects them into opencloud.dSYM, where cpptrace looks for them.
    if(APPLE)
        get_filename_component(ct_compiler_dir ${CMAKE_CXX_COMPILER} DIRECTORY)
        find_program(CT_DSYMUTIL dsymutil HINTS ${ct_compiler_dir} REQUIRED)
        if(CT_SYMBOLS STREQUAL "split")
            set(ct_dsym_configs Debug,RelWithDebInfo,Release,MinSizeRel)
        else()
            set(ct_dsym_configs RelWithDebInfo,Release,MinSizeRel)
        endif()
        add_custom_command(TARGET opencloud POST_BUILD
            COMMAND "$<IF:$<CONFIG:${ct_dsym_configs}>,${CT_DSYMUTIL};$<TARGET_FILE:opencloud>;-o;$<TARGET_FILE:opencloud>.dSYM,${CMAKE_COMMAND};-E;true>"
            COMMAND_EXPAND_LISTS
            VERBATIM)
    endif()
endif()
