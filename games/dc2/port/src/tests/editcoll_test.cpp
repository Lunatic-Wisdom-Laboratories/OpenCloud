#include <gtest/gtest.h>

#include "editcoll.hpp"

TEST(EditColl, ClipBoxXZReportsOverlappingBoxes) {
    float max_a[4] = {2.0f, 0.0f, 2.0f, 1.0f};
    float min_a[4] = {0.0f, 0.0f, 0.0f, 1.0f};
    float max_b[4] = {4.0f, 0.0f, 4.0f, 1.0f};
    float min_b[4] = {1.0f, 0.0f, 1.0f, 1.0f};

    EXPECT_EQ(ClipBoxXZ(max_a, min_a, max_b, min_b), 1);
}

TEST(EditColl, ClipBoxXZCountsTouchingBoxesAsOverlapping) {
    float max_a[4] = {2.0f, 0.0f, 2.0f, 1.0f};
    float min_a[4] = {0.0f, 0.0f, 0.0f, 1.0f};
    float max_b[4] = {4.0f, 0.0f, 4.0f, 1.0f};
    float min_b[4] = {2.0f, 0.0f, 2.0f, 1.0f};

    EXPECT_EQ(ClipBoxXZ(max_a, min_a, max_b, min_b), 1);
}

TEST(EditColl, ClipBoxXZRejectsBoxesSeparatedOnX) {
    float max_a[4] = {2.0f, 0.0f, 2.0f, 1.0f};
    float min_a[4] = {0.0f, 0.0f, 0.0f, 1.0f};
    float max_b[4] = {-1.0f, 0.0f, 2.0f, 1.0f};
    float min_b[4] = {-3.0f, 0.0f, 0.0f, 1.0f};

    EXPECT_EQ(ClipBoxXZ(max_a, min_a, max_b, min_b), 0);
}

TEST(EditColl, ClipBoxXZRejectsBoxesSeparatedOnZ) {
    float max_a[4] = {2.0f, 0.0f, 2.0f, 1.0f};
    float min_a[4] = {0.0f, 0.0f, 0.0f, 1.0f};
    float max_b[4] = {1.0f, 0.0f, -1.0f, 1.0f};
    float min_b[4] = {0.0f, 0.0f, -3.0f, 1.0f};

    EXPECT_EQ(ClipBoxXZ(max_a, min_a, max_b, min_b), 0);
}

TEST(EditColl, ClipBoxXZIgnoresHeight) {
    float max_a[4] = {2.0f, -100.0f, 2.0f, 1.0f};
    float min_a[4] = {0.0f, -100.0f, 0.0f, 1.0f};
    float max_b[4] = {4.0f, 100.0f, 4.0f, 1.0f};
    float min_b[4] = {1.0f, 100.0f, 1.0f, 1.0f};

    EXPECT_EQ(ClipBoxXZ(max_a, min_a, max_b, min_b), 1);
}

TEST(EditColl, ClipBoxXZSeparationInFirstPairAloneRejects) {
    float max_a[4] = {2.0f, 0.0f, 2.0f, 1.0f};
    float min_a[4] = {0.0f, 0.0f, 0.0f, 1.0f};
    float max_b[4] = {4.0f, 0.0f, 4.0f, 1.0f};
    float min_b[4] = {3.0f, 0.0f, 1.0f, 1.0f};

    EXPECT_EQ(ClipBoxXZ(max_a, min_a, max_b, min_b), 0);
}

TEST(EditColl, ClipBoxXZNegativeZeroSeparationRejects) {
    float max_a[4] = {-0.0f, 0.0f, 2.0f, 1.0f};
    float min_a[4] = {-5.0f, 0.0f, 0.0f, 1.0f};
    float max_b[4] = {4.0f, 0.0f, 4.0f, 1.0f};
    float min_b[4] = {0.0f, 0.0f, 1.0f, 1.0f};

    EXPECT_EQ(ClipBoxXZ(max_a, min_a, max_b, min_b), 0);
}

TEST(EditColl, ClipBoxXZZeroSizeBoxesAtSamePointOverlap) {
    float max_a[4] = {1.0f, 0.0f, 1.0f, 1.0f};
    float min_a[4] = {1.0f, 0.0f, 1.0f, 1.0f};
    float max_b[4] = {1.0f, 0.0f, 1.0f, 1.0f};
    float min_b[4] = {1.0f, 0.0f, 1.0f, 1.0f};

    EXPECT_EQ(ClipBoxXZ(max_a, min_a, max_b, min_b), 1);
}
