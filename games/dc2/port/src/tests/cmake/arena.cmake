if(WIN32 AND TARGET opencloud_tests)
    # Linked as opencloud is, so a test sees the same above-4-GiB layout the game runs with.
    target_link_options(opencloud_tests PRIVATE -Wl,--dynamicbase -Wl,--high-entropy-va)
endif()
