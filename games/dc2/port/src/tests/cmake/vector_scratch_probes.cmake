set(VECTOR_SCRATCH_PROBES ${CMAKE_CURRENT_BINARY_DIR}/vector_scratch_probes.hpp)
add_custom_command(OUTPUT ${VECTOR_SCRATCH_PROBES}
    COMMAND ${Python3_EXECUTABLE} ${CMAKE_SOURCE_DIR}/scripts/port/vector_scratch_probes.py
        ${CMAKE_SOURCE_DIR} ${VECTOR_SCRATCH_PROBES}
    DEPENDS ${CMAKE_SOURCE_DIR}/scripts/port/vector_scratch_probes.py
        ${CMAKE_SOURCE_DIR}/ps2/src/event_func.cpp ${CMAKE_SOURCE_DIR}/ps2/src/effscript.cpp
        ${CMAKE_SOURCE_DIR}/port/src/editexception.cpp
    VERBATIM)
target_sources(darkchronicle_tests PRIVATE ${VECTOR_SCRATCH_PROBES})
