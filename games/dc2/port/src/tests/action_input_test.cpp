#include <gtest/gtest.h>

#include <cstring>
#include <vector>

#include "actscript.hpp"
#include "gamepad.hpp"
#include "mainloop.hpp"
#include "mg_memory.hpp"
#include "padcontrol.hpp"
#include "scenesnd.hpp"

TEST(ActionInput, LogicalButtonsWriteThroughTheOutputReference) {
    std::vector<u_long128> storage(1024);
    mgCMemory              memory;
    memory.stSetBuffer(storage.data(), storage.size());
    RS_PROG_HEADER program = {};
    std::memcpy(program.magic, "SB2", 3);
    CRunScript script;
    SetActionExtendTable();
    ASSERT_TRUE(SetActionScript(&script, reinterpret_cast<char *>(&program), &memory));

    PadCtrl.Initialize();
    PadCtrl.RegisterBtn(PAD_BTN_ACTION_CONFIRM, PAD_CROSS, PAD_CTRL_TRIGGER_DOWN);
    PadCtrl.RegisterBtn(PAD_BTN_L1_HELD_2, PAD_L1, PAD_CTRL_TRIGGER_ON);
    PadCtrl.RegisterBtn(PAD_BTN_R1_HELD_2, PAD_R1, PAD_CTRL_TRIGGER_ON);
    GamePad__2.key_lock = 0;
    GamePad__2.previous_pad[0].button = 0;
    GamePad__2.pad[0].button = PAD_CROSS | PAD_L1;
    PadCtrl.Update(&GamePad__2);

    for (int button : {PAD_BTN_ACTION_CONFIRM, PAD_BTN_L1_HELD_2, PAD_BTN_R1_HELD_2}) {
        RS_STACKDATA output = {};
        output.type = RS_INT;
        output.val.i = -123;
        RS_STACKDATA args[2] = {};
        args[0].type = RS_INT;
        args[0].val.i = button;
        args[1].type = RS_PTR;
        args[1].val.p = &output;
        script.ext_func_table[ACTION_EXT_GET_BTN](args, 2);
        EXPECT_EQ(output.val.i, PadCtrl.Btn(button)) << button;
        EXPECT_EQ(args[0].val.i, button);
        EXPECT_EQ(args[1].val.p, &output);
    }
}

TEST(ActionInput, PauseMaskWritesThroughTheFollowingOutputReference) {
    std::vector<u_long128> storage(1024);
    mgCMemory              memory;
    memory.stSetBuffer(storage.data(), storage.size());
    RS_PROG_HEADER program = {};
    std::memcpy(program.magic, "SB2", 3);
    CRunScript script;
    SetActionExtendTable();
    ASSERT_TRUE(SetActionScript(&script, reinterpret_cast<char *>(&program), &memory));
    CScene scene;
    nowScene__2 = &scene;
    scene.battle_area.pause_flag = 0x5;

    for (int mask : {0x1, 0x2, 0x4, 0x7}) {
        RS_STACKDATA output = {};
        output.type = RS_INT;
        output.val.i = -123;
        RS_STACKDATA args[2] = {};
        args[0].type = RS_INT;
        args[0].val.i = mask;
        args[1].type = RS_PTR;
        args[1].val.p = &output;
        ASSERT_EQ(script.ext_func_table[ACTION_EXT_CHECK_PAUSE](args, 2), 1);
        EXPECT_EQ(output.val.i, 0x5 & mask);
        EXPECT_EQ(args[0].val.i, mask);
        EXPECT_EQ(args[1].val.p, &output);
    }
    nowScene__2 = nullptr;
}
