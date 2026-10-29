#include <gtest/gtest.h>

#include <cstdlib>
#include <memory>

#include "dng_event.hpp"
#include "dng_hud.hpp"
#include "scenesnd.hpp"

namespace {

struct ScenePtrDeleter {
    void operator()(CScene *scene) const {
        std::free(scene);
    }
};

// A zeroed scene holds no characters, so every slot reads as empty.
using SceneHandle = std::unique_ptr<CScene, ScenePtrDeleter>;

SceneHandle EmptyScene() {
    return SceneHandle(static_cast<CScene *>(std::calloc(1, sizeof(CScene))));
}

} // namespace

TEST(DngDraw, RedMarkDrawsOnlyWhenRequested) {
    CRedMarkModel model{};
    model.frame = nullptr;
    model.angle = 0.5f;

    float start[4] = {1.0f, 2.0f, 3.0f, 0.0f};
    model.SetPosition(start);

    model.draw_request = 0;
    EXPECT_EQ(model.Draw(), 0);

    float unrequested[4];
    model.GetPosition(unrequested);
    EXPECT_FLOAT_EQ(unrequested[0], 1.0f);
    EXPECT_FLOAT_EQ(unrequested[1], 2.0f);
    EXPECT_FLOAT_EQ(unrequested[2], 3.0f);

    model.draw_request = 1;
    EXPECT_EQ(model.Draw(), 0);
    EXPECT_EQ(model.draw_request, 0);

    float restored[4];
    model.GetPosition(restored);
    EXPECT_FLOAT_EQ(restored[0], 1.0f);
    EXPECT_FLOAT_EQ(restored[1], 2.0f);
    EXPECT_FLOAT_EQ(restored[2], 3.0f);
}

TEST(DngDraw, LockOnWithoutPlayerDrawsNothing) {
    SceneHandle scene = EmptyScene();
    ASSERT_TRUE(scene);

    CLockOnModel model{};
    model.scene = scene.get();

    char marker[] = "marker";
    model.name = marker;

    EXPECT_EQ(model.Draw(), 0);
    EXPECT_EQ(model.name, marker);
}
