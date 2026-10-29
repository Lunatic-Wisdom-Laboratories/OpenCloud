import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from long64 import rewrite  # noqa: E402


class LongKeywordTests(unittest.TestCase):

    def test_bare_long_rewritten(self):
        self.assertEqual(rewrite("long x;"), "long long x;")

    def test_long_int_rewritten(self):
        self.assertEqual(rewrite("long int i;"), "long long int i;")

    def test_unsigned_long_rewritten(self):
        self.assertEqual(rewrite("unsigned long u;"), "unsigned long long u;")

    def test_unsigned_long_int_rewritten(self):
        self.assertEqual(rewrite("unsigned long int u;"), "unsigned long long int u;")

    def test_cast_rewritten(self):
        self.assertEqual(rewrite("(long) x"), "(long long) x")

    def test_template_argument_rewritten(self):
        self.assertEqual(rewrite("f<long>();"), "f<long long>();")

    def test_long_long_untouched(self):
        self.assertEqual(rewrite("long long a; unsigned long long b;"), "long long a; unsigned long long b;")

    def test_long_double_untouched(self):
        self.assertEqual(rewrite("long double d;"), "long double d;")

    def test_signed_long_long_untouched(self):
        self.assertEqual(rewrite("signed long long s;"), "signed long long s;")

    def test_identifiers_containing_long_untouched(self):
        self.assertEqual(rewrite("long_name along longer _long"), "long_name along longer _long")

    def test_double_then_long_identifier(self):
        self.assertEqual(rewrite("double long_value;"), "double long_value;")

    def test_line_comment_untouched(self):
        self.assertEqual(rewrite("int a; // long\n"), "int a; // long\n")

    def test_block_comment_untouched(self):
        self.assertEqual(rewrite("/* long */ int a;"), "/* long */ int a;")

    def test_multiline_block_comment_untouched(self):
        self.assertEqual(rewrite("/* long\nlong */ long x;"), "/* long\nlong */ long long x;")

    def test_line_comment_continuation_untouched(self):
        self.assertEqual(rewrite("// long \\\nlong x;\nlong y;"), "// long \\\nlong x;\nlong long y;")

    def test_string_literal_untouched(self):
        self.assertEqual(rewrite('char *s = "long";'), 'char *s = "long";')

    def test_string_escapes_untouched(self):
        self.assertEqual(rewrite(r'char *s = "a\"long\"b"; long x;'), r'char *s = "a\"long\"b"; long long x;')

    def test_prefixed_strings_untouched(self):
        src = 'u8"long" u"long" U"long" L"long"'
        self.assertEqual(rewrite(src), src)

    def test_raw_string_untouched(self):
        src = 'auto r = R"x(long)x"; long y;'
        self.assertEqual(rewrite(src), 'auto r = R"x(long)x"; long long y;')

    def test_char_literal_untouched(self):
        self.assertEqual(rewrite("char c = 'l'; long x;"), "char c = 'l'; long long x;")

    def test_escaped_quote_in_char_literal(self):
        self.assertEqual(rewrite(r"char c = '\''; long x;"), r"char c = '\''; long long x;")

    def test_include_angle_path_untouched(self):
        self.assertEqual(rewrite("#include <long.h>\n"), "#include <long.h>\n")

    def test_include_with_space_untouched(self):
        self.assertEqual(rewrite("#  include <dir/long/x.h>\n"), "#  include <dir/long/x.h>\n")

    def test_include_quoted_untouched(self):
        self.assertEqual(rewrite('#include "long.h"\n'), '#include "long.h"\n')

    def test_include_next_and_import_untouched(self):
        src = "#include_next <long.h>\n#import <long.h>\n"
        self.assertEqual(rewrite(src), src)

    def test_has_include_untouched(self):
        self.assertEqual(rewrite("#if __has_include(<long.h>)\n#endif\n"), "#if __has_include(<long.h>)\n#endif\n")

    def test_preprocessor_define_rewritten(self):
        self.assertEqual(rewrite("#define W long\n"), "#define W long long\n")

    def test_angle_bracket_comparison_not_header(self):
        self.assertEqual(rewrite("if (a < b) long x;"), "if (a < b) long long x;")

    def test_signed_long_rewritten(self):
        self.assertEqual(rewrite("signed long x;"), "signed long long x;")

    def test_conversion_operator_rewritten(self):
        self.assertEqual(rewrite("operator long() const;"), "operator long long() const;")

    def test_define_continuation_rewritten(self):
        self.assertEqual(rewrite("#define A(x) \\\n  long x\n"), "#define A(x) \\\n  long long x\n")

    def test_long_at_line_end_followed_by_identifier_prefix(self):
        self.assertEqual(rewrite("unsigned long\nlong_t x;"), "unsigned long long\nlong_t x;")

    def test_long_inside_attribute_rewritten_like_keyword(self):
        self.assertEqual(rewrite("__attribute__((aligned(sizeof(long)))) long x;"),
                         "__attribute__((aligned(sizeof(long long)))) long long x;")

    def test_newlines_preserved(self):
        src = "long a;\n\n// c\r\n/* x\n */\nlong b;\n"
        out = rewrite(src)
        self.assertEqual(out.count("\n"), src.count("\n"))
        self.assertEqual(out, "long long a;\n\n// c\r\n/* x\n */\nlong long b;\n")


class LiteralTests(unittest.TestCase):

    def test_uppercase_UL(self):
        self.assertEqual(rewrite("x = 1UL;"), "x = 1ULL;")

    def test_lowercase_ul(self):
        self.assertEqual(rewrite("x = 1ul;"), "x = 1ull;")

    def test_mixed_case_uL(self):
        self.assertEqual(rewrite("x = 1uL;"), "x = 1uLL;")

    def test_mixed_case_Ul(self):
        self.assertEqual(rewrite("x = 1Ul;"), "x = 1Ull;")

    def test_lu_suffix(self):
        self.assertEqual(rewrite("x = 1lu;"), "x = 1llu;")

    def test_Lu_suffix(self):
        self.assertEqual(rewrite("x = 1Lu;"), "x = 1LLu;")

    def test_lU_suffix(self):
        self.assertEqual(rewrite("x = 1lU;"), "x = 1llU;")

    def test_LU_suffix(self):
        self.assertEqual(rewrite("x = 1LU;"), "x = 1LLU;")

    def test_single_l(self):
        self.assertEqual(rewrite("x = 5l;"), "x = 5ll;")

    def test_single_L(self):
        self.assertEqual(rewrite("x = 5L;"), "x = 5LL;")

    def test_double_suffix_untouched(self):
        self.assertEqual(rewrite("x = 5LL + 5ll + 5ULL + 5uLL;"), "x = 5LL + 5ll + 5ULL + 5uLL;")

    def test_hex_literal(self):
        self.assertEqual(rewrite("x = 0xFFFFFFFFFFFFFFDFUL;"), "x = 0xFFFFFFFFFFFFFFDFULL;")

    def test_hex_literal_with_digit_ending_in_hex_letter(self):
        self.assertEqual(rewrite("x = 0x1BL;"), "x = 0x1BLL;")

    def test_hex_without_suffix_untouched(self):
        self.assertEqual(rewrite("x = 0xABCDEF;"), "x = 0xABCDEF;")

    def test_hex_exponent_like_digits_untouched(self):
        self.assertEqual(rewrite("x = 0x1e+1;"), "x = 0x1e+1;")

    def test_binary_literal(self):
        self.assertEqual(rewrite("x = 0b101UL;"), "x = 0b101ULL;")

    def test_octal_literal(self):
        self.assertEqual(rewrite("x = 0777L;"), "x = 0777LL;")

    def test_zero_literal(self):
        self.assertEqual(rewrite("x = 0L;"), "x = 0LL;")

    def test_digit_separator_literal(self):
        self.assertEqual(rewrite("x = 1'000'000L;"), "x = 1'000'000LL;")

    def test_float_L_suffix_untouched(self):
        self.assertEqual(rewrite("x = 1.0L;"), "x = 1.0L;")

    def test_float_exponent_L_suffix_untouched(self):
        self.assertEqual(rewrite("x = 1e5L;"), "x = 1e5L;")

    def test_leading_dot_float_untouched(self):
        self.assertEqual(rewrite("x = .5L;"), "x = .5L;")

    def test_hex_float_untouched(self):
        self.assertEqual(rewrite("x = 0x1p3L;"), "x = 0x1p3L;")

    def test_float_exponent_sign_consumed(self):
        self.assertEqual(rewrite("x = 1e+5L;"), "x = 1e+5L;")

    def test_identifier_with_digits_untouched(self):
        self.assertEqual(rewrite("int x1L = 2;"), "int x1L = 2;")

    def test_literal_inside_comment_untouched(self):
        self.assertEqual(rewrite("// 1UL\nx;"), "// 1UL\nx;")

    def test_literal_inside_string_untouched(self):
        self.assertEqual(rewrite('s = "1UL";'), 's = "1UL";')

    def test_long_and_literal_together(self):
        self.assertEqual(rewrite("unsigned long v = 0xFFFFFFFFFFFFFFDFUL;"),
                         "unsigned long long v = 0xFFFFFFFFFFFFFFDFULL;")


class FormatStringTests(unittest.TestCase):

    def test_lu_conversion_widened(self):
        self.assertEqual(rewrite('printf("err %lu\\n", v);'), 'printf("err %llu\\n", v);')

    def test_all_integer_conversions_widened(self):
        self.assertEqual(rewrite('"%ld %li %lo %lu %lx %lX %ln"'), '"%lld %lli %llo %llu %llx %llX %lln"')

    def test_flags_width_precision_widened(self):
        self.assertEqual(rewrite('"%-08lx"'), '"%-08llx"')

    def test_star_width_and_precision_widened(self):
        self.assertEqual(rewrite('"%*.*ld"'), '"%*.*lld"')

    def test_precision_only_widened(self):
        self.assertEqual(rewrite('"%.lu"'), '"%.llu"')

    def test_multiple_conversions_in_one_string(self):
        self.assertEqual(rewrite('"%d %lu %s %lx"'), '"%d %llu %s %llx"')

    def test_percent_percent_before_conversion_untouched(self):
        self.assertEqual(rewrite('"%%lu"'), '"%%lu"')

    def test_percent_percent_then_conversion_widened(self):
        self.assertEqual(rewrite('"%%%lu"'), '"%%%llu"')

    def test_lld_untouched(self):
        self.assertEqual(rewrite('"%lld"'), '"%lld"')

    def test_llu_untouched(self):
        self.assertEqual(rewrite('"%llx"'), '"%llx"')

    def test_float_untouched(self):
        self.assertEqual(rewrite('"%lf %Lf"'), '"%lf %Lf"')

    def test_wide_char_and_string_untouched(self):
        self.assertEqual(rewrite('"%ls %lc"'), '"%ls %lc"')

    def test_hh_and_h_untouched(self):
        self.assertEqual(rewrite('"%hhd %hd"'), '"%hhd %hd"')

    def test_no_length_modifier_untouched(self):
        self.assertEqual(rewrite('"%d %u %x"'), '"%d %u %x"')

    def test_l_without_conversion_untouched(self):
        self.assertEqual(rewrite('"%l"'), '"%l"')

    def test_unknown_conversion_after_l_untouched(self):
        self.assertEqual(rewrite('"%lq"'), '"%lq"')

    def test_raw_string_untouched(self):
        src = 'auto r = R"(%lu)";'
        self.assertEqual(rewrite(src), src)

    def test_raw_string_with_prefix_untouched(self):
        src = 'auto r = u8R"(%lu)";'
        self.assertEqual(rewrite(src), src)

    def test_char_literal_untouched(self):
        self.assertEqual(rewrite("char c = '%';"), "char c = '%';")

    def test_comment_untouched(self):
        self.assertEqual(rewrite('// "%lu"\nint a;'), '// "%lu"\nint a;')

    def test_include_name_untouched(self):
        self.assertEqual(rewrite('#include "%lu.h"\n'), '#include "%lu.h"\n')

    def test_prefixed_ordinary_strings_widened(self):
        self.assertEqual(rewrite('L"%lu" u8"%lx"'), 'L"%llu" u8"%llx"')

    def test_escaped_quote_inside_string(self):
        self.assertEqual(rewrite(r'"\"%lu\""'), r'"\"%llu\""')

    def test_unterminated_string_untouched(self):
        self.assertEqual(rewrite('"%lu\n'), '"%lu\n')

    def test_empty_string_untouched(self):
        self.assertEqual(rewrite('""'), '""')

    def test_format_and_long_keyword_together(self):
        self.assertEqual(rewrite('long x = 1UL; printf("%lu", x);'),
                         'long long x = 1ULL; printf("%llu", x);')

    def test_wide_string_format_widened(self):
        self.assertEqual(rewrite('wprintf(L"%ld", v);'), 'wprintf(L"%lld", v);')

    def test_adjacent_concatenation_not_widened(self):
        # Known limitation: a conversion split across string tokens is not widened. No game source does this.
        src = 'printf("%" "lu", x);'
        self.assertEqual(rewrite(src), src)

    def test_mips_lo_relocation_in_asm_untouched(self):
        src = 'asm("addiu $t0, $t0, %lo(sym)");'
        self.assertEqual(rewrite(src), src)


if __name__ == "__main__":
    unittest.main()
