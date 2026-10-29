#include <gtest/gtest.h>

TEST(Smoke, PortDefined) {
#ifdef PORT
    EXPECT_TRUE(true);
#else
    FAIL() << "PORT is not defined for the game-side tests";
#endif
}
