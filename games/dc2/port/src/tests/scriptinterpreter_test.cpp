#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>

#include "scriptinterpreter.hpp"

TEST(ScriptInterpreter, PushStackPreservesHostStringPointer) {
    CScriptInterpreter interpreter;
    SPI_STACK          stack[2]{};
    char               text[] = "string argument";
    ASSERT_GT(reinterpret_cast<std::uintptr_t>(text), UINT32_MAX);
    interpreter.SetStack(stack, 2);
    SPI_STACK argument{};
    argument.type = SPI_STACK_TYPE_STRING;
    argument.value.string = text;
    interpreter.PushStack(argument);
    ASSERT_EQ(interpreter.stack_count, 1);
    ASSERT_EQ(stack[0].value.string, text);
    EXPECT_STREQ(spiGetStackString(stack), text);
}

TEST(ScriptInterpreter, EmptyQuotedStringAndMissingArgumentKeepTheirTypes) {
    CScriptInterpreter interpreter;
    SPI_STACK          stack[4]{};
    char               strings[64]{};
    char               script[] = "\"\",,42;";
    interpreter.SetScript(script, std::strlen(script));
    interpreter.SetStack(stack, 4);
    interpreter.SetStringBuff(strings, sizeof(strings));
    ASSERT_EQ(interpreter.GetArg(), 3);
    ASSERT_EQ(interpreter.stack_count, 3);
    EXPECT_EQ(stack[0].type, SPI_STACK_TYPE_STRING);
    ASSERT_EQ(stack[0].value.string, strings);
    EXPECT_STREQ(strings, "");
    EXPECT_EQ(stack[1].type, SPI_STACK_TYPE_INVALID);
    EXPECT_EQ(stack[1].value.string, nullptr);
    EXPECT_EQ(stack[2].type, SPI_STACK_TYPE_INT);
    EXPECT_EQ(stack[2].value.integer, 42);
}

TEST(ScriptInterpreter, NumericAndEscapedTextArgumentsKeepTheirValues) {
    CScriptInterpreter interpreter;
    SPI_STACK          stack[6]{};
    char               strings[64]{};
    char               script[] = " -7, 1.5,\"a\\\"b\",bad,12 trailing,\"\x81\x5c\";";
    interpreter.SetScript(script, std::strlen(script));
    interpreter.SetStack(stack, 6);
    interpreter.SetStringBuff(strings, sizeof(strings));
    ASSERT_EQ(interpreter.GetArg(), 6);
    EXPECT_EQ(spiGetStackInt(&stack[0]), -7);
    EXPECT_FLOAT_EQ(spiGetStackFloat(&stack[1]), 1.5f);
    ASSERT_EQ(stack[2].type, SPI_STACK_TYPE_STRING);
    EXPECT_STREQ(spiGetStackString(&stack[2]), "a\"b");
    EXPECT_EQ(stack[3].type, SPI_STACK_TYPE_INVALID);
    EXPECT_EQ(stack[3].value.string, nullptr);
    EXPECT_EQ(spiGetStackInt(&stack[4]), 12);
    ASSERT_EQ(stack[5].type, SPI_STACK_TYPE_STRING);
    EXPECT_STREQ(spiGetStackString(&stack[5]), "\x81\x5c");
}
