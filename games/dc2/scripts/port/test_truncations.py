import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import truncations  # noqa: E402

# LLP64 hosts make unsigned long 4 bytes; the pointer-wide integer is what uptr names on every host.
PRELUDE = "typedef __UINTPTR_TYPE__ uptr;\ntypedef __INTPTR_TYPE__ sptr;\n"
# The build's flags for ps2 units: its pointer-to-int casts are warnings, not errors. C++20 stands in for the
# build's C++26, which the test source does not need.
PARSE_ARGS = ["-x", "c++", "-std=c++20", "-fms-extensions", "-Wno-pointer-to-int-cast", "-Wno-int-to-pointer-cast",
              "-Wno-void-pointer-to-int-cast"]


def setUpModule():
    try:
        truncations.load_libclang(None)
    except ImportError as error:
        raise unittest.SkipTest("clang Python bindings are missing: %s" % error)
    except Exception as error:
        raise unittest.SkipTest("libclang cannot be loaded: %s" % error)


def analyse(source):
    with tempfile.TemporaryDirectory() as root:
        path = os.path.join(root, "ps2", "src", "t.cpp")
        os.makedirs(os.path.dirname(path))
        with open(path, "w") as f:
            f.write(PRELUDE + source)
        with mock.patch.object(truncations, "ROOT", root):
            return truncations.walk(path, PARSE_ARGS)


class NarrowingTests(unittest.TestCase):

    def narrowing(self, source):
        result = analyse(source)
        self.assertEqual(result["errors"], [])
        return result["narrowing"]

    def test_pointer_cast_in_local_initialiser(self):
        sites = self.narrowing("void f(char *p) { int x = (int) p; (void) x; }")
        self.assertEqual(len(sites), 1)
        self.assertEqual(sites[0]["kind"], "pointer cast")
        self.assertEqual(sites[0]["destination"], "local x")
        self.assertEqual(sites[0]["function"], "f")
        self.assertEqual(sites[0]["expression"], "(int)p")

    def test_uptr_and_sptr_casts_are_not_sites(self):
        result = analyse("void f(char *p) { uptr a = (uptr) p; sptr b = (sptr) p; (void) a; (void) b; }")
        self.assertEqual(result["errors"], [])
        self.assertEqual(result["narrowing"], [])
        self.assertEqual(result["casts"], [])

    def test_uptr_narrowed_into_local(self):
        sites = self.narrowing("void f(char *p) { uptr a = (uptr) p; int b = a; (void) b; }")
        self.assertEqual(len(sites), 1)
        self.assertEqual(sites[0]["kind"], "uptr narrowed")
        self.assertEqual(sites[0]["destination"], "local b")
        self.assertEqual(sites[0]["expression"], "a")

    def test_uptr_narrowed_through_typedef(self):
        sites = self.narrowing("typedef uptr myptr;\nvoid f(char *p) { myptr a = (myptr) p; int b = a; (void) b; }")
        self.assertEqual(len(sites), 1)
        self.assertEqual(sites[0]["kind"], "uptr narrowed")
        self.assertEqual(sites[0]["destination"], "local b")

    def test_field_destination(self):
        sites = self.narrowing("struct S { int field; };\nvoid f(char *p, S *s) { s->field = (int) p; }")
        self.assertEqual(len(sites), 1)
        self.assertRegex(sites[0]["destination"], r"^field S::field \(ps2/src/t\.cpp:\d+\)$")

    def test_return_destination(self):
        sites = self.narrowing("int f(char *p) { return (int) p; }")
        self.assertEqual(len(sites), 1)
        self.assertEqual(sites[0]["destination"], "return f (int)")

    def test_argument_destination(self):
        sites = self.narrowing("void g(int v) { (void) v; }\nvoid f(char *p) { g((int) p); }")
        self.assertEqual(len(sites), 1)
        self.assertEqual(sites[0]["destination"], "arg g#0 (int)")

    def test_uptr_narrowed_as_argument(self):
        sites = self.narrowing("void g(int v) { (void) v; }\nvoid f(uptr a) { g(a); }")
        self.assertEqual(len(sites), 1)
        self.assertEqual(sites[0]["kind"], "uptr narrowed")
        self.assertEqual(sites[0]["destination"], "arg g#0 (int)")

    def test_uptr_narrowed_as_return(self):
        sites = self.narrowing("int f(uptr a) { return a; }")
        self.assertEqual(len(sites), 1)
        self.assertEqual(sites[0]["destination"], "return f (int)")

    def test_uptr_comparison_is_not_narrowed(self):
        self.assertEqual(self.narrowing("int f(uptr a) { return a == 0; }"), [])

    def test_round_trip_classification_unchanged(self):
        result = analyse("struct T { int v; };\nvoid f(char *p) { T *t = (T *) ((int) p + 4); (void) t; }")
        self.assertEqual(result["errors"], [])
        site = next(c for c in result["casts"] if c["type"] == "int")
        self.assertEqual(site["context"], "cast back")
        self.assertEqual(truncations.classify(site, set(), set()), "round-trip")

    def test_benign_low_bits_mask(self):
        sites = self.narrowing("void f(char *p) { int x = (uptr) p & 0x3F; int y = 0x3F & (uptr) p; "
                               "(void) x; (void) y; }")
        self.assertEqual([s["class"] for s in sites], ["benign:low-bits", "benign:low-bits"])

    def test_benign_residue(self):
        sites = self.narrowing("void f(char *p) { int x = (sptr) p % 64; (void) x; }")
        self.assertEqual([s["class"] for s in sites], ["benign:residue"])

    def test_benign_difference(self):
        sites = self.narrowing("void f(char *a, char *b) { int x = ((sptr) a - (sptr) b) / 16; (void) x; }")
        self.assertEqual([s["class"] for s in sites], ["benign:difference"])

    def test_pointer_offset_is_not_benign(self):
        sites = self.narrowing("void f(char *p) { int x = (uptr) p + 4; (void) x; }")
        self.assertEqual([s["class"] for s in sites], ["counted"])

    def test_wide_or_negative_mask_is_not_benign(self):
        sites = self.narrowing("enum E { NEG = -1 };\nvoid f(char *p) { int x = (uptr) p & NEG; "
                               "int y = (uptr) p & 0x100000000; (void) x; (void) y; }")
        self.assertEqual([s["class"] for s in sites], ["counted", "counted"])

    def test_plain_pointer_cast_is_counted(self):
        sites = self.narrowing("void f(char *p) { int x = (int) p; (void) x; }")
        self.assertEqual([s["class"] for s in sites], ["counted"])

    def test_worklist_is_sorted_tsv(self):
        sites = [
            {"file": "ps2/src/b.cpp", "line": 3, "column": 1, "function": "g", "state": "open",
             "kind": "pointer cast", "destination": "local x", "class": "counted", "expression": "(int)p"},
            {"file": "ps2/src/a.cpp", "line": 9, "column": 5, "function": "f", "state": "replaced",
             "kind": "uptr narrowed", "destination": "return f (int)", "class": "benign:residue",
             "expression": "a\tb"},
        ]
        with tempfile.TemporaryDirectory() as scratch:
            path = os.path.join(scratch, "worklist.tsv")
            truncations.write_worklist(sites, path)
            with open(path, encoding="utf-8") as f:
                lines = f.read().splitlines()
        self.assertEqual(lines, [
            truncations.WORKLIST_HEADER,
            "ps2/src/a.cpp\t9\t5\tf\treplaced\tuptr narrowed\treturn f (int)\tbenign:residue\ta b",
            "ps2/src/b.cpp\t3\t1\tg\topen\tpointer cast\tlocal x\tcounted\t(int)p",
        ])


def site(file, function, state, class_):
    return {"file": file, "line": 1, "column": 1, "function": function, "state": state, "class": class_,
            "destination": "local x", "expression": "p", "kind": "pointer cast"}


class DiagnosticTests(unittest.TestCase):

    def test_error_count_is_not_capped_with_the_stored_messages(self):
        source = "void f() {\n" + "".join("    int v%d = undeclared%d;\n" % (i, i) for i in range(7)) + "}\n"
        result = analyse(source)
        self.assertEqual(result["error_count"], 7)
        self.assertEqual(len(result["errors"]), 5)


class OwnerAndSummaryTests(unittest.TestCase):

    def test_owner_lookup_prefers_the_function_then_the_unit(self):
        owners = {("ps2/src/t.cpp", None): "unit owner", ("ps2/src/t.cpp", "f"): "function owner"}
        with mock.patch.dict(truncations.NARROWING_OWNER, owners, clear=True):
            self.assertEqual(truncations.owner_of(site("ps2/src/t.cpp", "f", "open", "counted")), "function owner")
            self.assertEqual(truncations.owner_of(site("ps2/src/t.cpp", "g", "open", "counted")), "unit owner")
            self.assertIsNone(truncations.owner_of(site("ps2/src/u.cpp", "g", "open", "counted")))

    def test_summary_partitions_benign_and_replaced(self):
        sites = [
            site("ps2/src/t.cpp", "f", "open", "counted"),
            site("ps2/src/t.cpp", "g", "open", "counted"),
            site("ps2/src/t.cpp", "h", "open", "benign:low-bits"),
            site("ps2/src/t.cpp", "i", "replaced", "counted"),
        ]
        with mock.patch.dict(truncations.NARROWING_OWNER, {("ps2/src/t.cpp", "f"): "owned"}, clear=True):
            self.assertEqual(truncations.narrowing_summary(sites),
                             "2 narrowing sites in compiled ps2/ code (1 owned, 1 unowned), 1 benign, "
                             "1 in replaced functions")


if __name__ == "__main__":
    unittest.main()
