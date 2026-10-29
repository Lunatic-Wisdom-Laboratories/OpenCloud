#!/usr/bin/env python3
"""Check the records of docs/port/records.md against the sources, the PC layout and the PS2-only readers.

    records.py [--root DIR] [--build port/build/pc] [--require-clang] [--emit] [--update-lines]

A record is read in place from a disc file or a save image, so its PC layout is the disc's: the table row
for each record names its STATIC_ASSERT, its PORT_RECORD_ASSERT (`assert` rows) and its PC approach.
Checks: the rows against the text of their declaring files; the layout libclang computes for them under the
build's compile flags (size, and no pointer-wide member); the PS2-only readers against the port's overrides
and the linked binary.
"""

import argparse
import collections
import contextlib
import os
import re
import sys
from dataclasses import dataclass

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import truncations  # noqa: E402

ROOT = truncations.ROOT
RECORDS_MD = "docs/port/records.md"
PC_VALUES = ("assert", "PS2 only", "host image")
ASSERT_SCAN_DIRS = ("ps2/include", "ps2/src")
ASSERT_SCAN_SUFFIXES = (".h", ".hpp", ".c", ".cpp")
MENTION_SCAN_DIRS = ("ps2/src", "port/src")
MENTION_SCAN_SUFFIXES = (".h", ".hpp", ".c", ".cpp", ".inc", ".inl")
PS2_ONLY_READERS = {
    "DATA_HEADER": ("ps2/src/dataread.cpp", ["InitCDFile", "SearchFile", "LoadFileBG", "LoadFile2", "CDRead"]),
}
RECORD_ASSERT_RE = re.compile(r"PORT_RECORD_ASSERT\s*\(\s*sizeof\s*\(\s*(\w+)\s*\)")
DECLARED_RE = re.compile(r"`([^`]+):(\d+)`")
SIZE_PREFIX = "REC_SIZE_"


@dataclass
class Row:
    struct: str
    size: str
    declared: str
    line: int
    pc: str
    line_index: int
    declared_cell: int


def normal(text):
    return re.sub(r"\s+", "", text)


def read_lines(path):
    with open(path, encoding="latin-1", newline="") as f:
        return f.read().split("\n")


def rel(root, path):
    return os.path.relpath(path, root).replace(os.sep, "/")


def split_cells(line):
    return line.split("|")[1:-1]


def parse_table(lines):
    start = next((i for i, line in enumerate(lines) if line.strip() == "## Records"), None)
    if start is None:
        raise ValueError("records.md has no '## Records' section")
    end = next((i for i in range(start + 1, len(lines)) if lines[i].startswith("## ")), len(lines))
    header_at = next((i for i in range(start + 1, end) if lines[i].startswith("|")), None)
    if header_at is None:
        raise ValueError("'## Records' has no table")
    header = [cell.strip() for cell in split_cells(lines[header_at])]
    for name in ("Struct", "Size", "Declared", "PC"):
        if name not in header:
            raise ValueError("'## Records' table has no %r column" % name)
    struct_col, size_col, declared_col, pc_col = (header.index(n) for n in ("Struct", "Size", "Declared", "PC"))

    rows = []
    for index in range(header_at + 2, end):
        if not lines[index].startswith("|"):
            break
        cells = [cell.strip() for cell in split_cells(lines[index])]
        if len(cells) != len(header):
            raise ValueError("records.md line %d: %d cells, header has %d" % (index + 1, len(cells), len(header)))
        declared = DECLARED_RE.fullmatch(cells[declared_col])
        if declared is None:
            raise ValueError("records.md line %d: Declared %r is not `path:line`" % (index + 1, cells[declared_col]))
        pc = cells[pc_col]
        if pc not in PC_VALUES:
            raise ValueError("records.md line %d: PC %r is not one of %s" % (index + 1, pc, ", ".join(PC_VALUES)))
        rows.append(Row(struct=cells[struct_col].strip("`"), size=cells[size_col].strip("`"),
                        declared=declared.group(1), line=int(declared.group(2)), pc=pc,
                        line_index=index, declared_cell=declared_col + 1))
    return rows


def static_assert_lines(lines, row):
    target = normal("STATIC_ASSERT(sizeof(%s) == %s);" % (row.struct, row.size))
    return [i for i, line in enumerate(lines) if normal(line).startswith(target)]


def emit(rows):
    return ["%s: PORT_RECORD_ASSERT(sizeof(%s) == %s);" % (r.declared, r.struct, r.size)
            for r in rows if r.pc == "assert"]


def update_lines(root, lines, rows):
    failures = []
    for row in rows:
        hits = static_assert_lines(read_lines(os.path.join(root, row.declared)), row)
        if len(hits) != 1:
            failures.append("%s:%d %s: %d STATIC_ASSERT lines, want 1" % (row.declared, row.line, row.struct, len(hits)))
            continue
        if hits[0] + 1 == row.line:
            continue
        parts = lines[row.line_index].split("|")
        parts[row.declared_cell] = DECLARED_RE.sub(lambda m: "`%s:%d`" % (m.group(1), hits[0] + 1),
                                                   parts[row.declared_cell], count=1)
        lines[row.line_index] = "|".join(parts)
    return failures


def check_text(root, rows):
    failures, warnings = [], []
    for row in rows:
        path = os.path.join(root, row.declared)
        if not os.path.isfile(path):
            failures.append("%s:%d %s: declared file is missing" % (row.declared, row.line, row.struct))
            continue
        lines = read_lines(path)
        hits = static_assert_lines(lines, row)
        if len(hits) != 1:
            failures.append("%s:%d %s: %d STATIC_ASSERT(sizeof(%s) == %s) lines, want 1"
                            % (row.declared, row.line, row.struct, len(hits), row.struct, row.size))
            continue
        here = hits[0] + 1
        if here != row.line:
            warnings.append("%s:%d %s: Declared line is stale, STATIC_ASSERT is at line %d"
                            % (row.declared, row.line, row.struct, here))
        if row.pc != "assert":
            continue
        want = normal("PORT_RECORD_ASSERT(sizeof(%s) == %s);" % (row.struct, row.size))
        following = next((line for line in lines[hits[0] + 1:] if line.strip()), None)
        if following is None or normal(following) != want:
            failures.append("%s:%d %s: PORT_RECORD_ASSERT(sizeof(%s) == %s); must follow the STATIC_ASSERT"
                            % (row.declared, here, row.struct, row.struct, row.size))
    return failures, warnings


def scan_record_asserts(root):
    found = []
    for base in ASSERT_SCAN_DIRS:
        for dirpath, dirnames, filenames in os.walk(os.path.join(root, base)):
            dirnames.sort()
            for name in sorted(filenames):
                if not name.endswith(ASSERT_SCAN_SUFFIXES):
                    continue
                path = os.path.join(dirpath, name)
                for number, line in enumerate(read_lines(path), 1):
                    for match in RECORD_ASSERT_RE.finditer(line):
                        found.append((rel(root, path), number, match.group(1)))
    return found


def check_unlisted_asserts(root, rows):
    pc_of = {r.struct: r.pc for r in rows}
    failures = []
    for path, number, name in scan_record_asserts(root):
        if pc_of.get(name) == "assert":
            continue
        if name in pc_of:
            failures.append("%s:%d PORT_RECORD_ASSERT(sizeof(%s)) on a %s row" % (path, number, name, pc_of[name]))
        else:
            failures.append("%s:%d PORT_RECORD_ASSERT(sizeof(%s)) has no row in records.md" % (path, number, name))
    return failures


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", lambda m: "\n" * m.group(0).count("\n"), text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def check_ps2_only_mentions(root):
    failures = []
    for struct, (reader, _) in PS2_ONLY_READERS.items():
        pattern = re.compile(r"\b%s\b" % re.escape(struct))
        for base in MENTION_SCAN_DIRS:
            for dirpath, dirnames, filenames in os.walk(os.path.join(root, base)):
                dirnames.sort()
                for name in sorted(filenames):
                    path = os.path.join(dirpath, name)
                    where = rel(root, path)
                    if not name.endswith(MENTION_SCAN_SUFFIXES) or where == reader:
                        continue
                    for number, line in enumerate(strip_comments("\n".join(read_lines(path))).split("\n"), 1):
                        if pattern.search(line):
                            failures.append("%s:%d mentions %s, which only %s reads" % (where, number, struct, reader))
    return failures


def unqualified(name):
    return name.split("(", 1)[0].split("::")[-1].strip()


def check_ps2_only_functions(build):
    """Failures for the PS2-only readers' functions, or None when the build has nothing to check them against."""
    darkchronicle = os.path.join(build, "darkchronicle")
    overrides_path = os.path.join(build, "port_overrides.txt")
    if not (os.path.isfile(darkchronicle) and os.path.isfile(overrides_path)):
        return None
    replaced = {unqualified(signature) for signature in truncations.load_overrides(build)}
    try:
        linked = {unqualified(name) for name in truncations.linked_functions(darkchronicle)}
    except SystemExit as error:
        return [str(error)]
    failures = []
    for struct, (reader, functions) in PS2_ONLY_READERS.items():
        for function in functions:
            if function in replaced:
                continue
            if any(name == function or name.startswith(function + ".") for name in linked):
                failures.append("%s: %s (reader of PS2-only %s) is linked and not in port_overrides.txt"
                                % (reader, function, struct))
    return failures


@contextlib.contextmanager
def in_directory(path):
    previous = os.getcwd()
    os.chdir(path)
    try:
        yield
    finally:
        os.chdir(previous)


def size_constants(rows):
    return "".join("enum { %s%s = (%s) };\n" % (SIZE_PREFIX, r.struct, r.size) for r in rows)


def has_virtual(decl):
    K = truncations.ci.CursorKind
    return any(c.kind in (K.CXX_METHOD, K.DESTRUCTOR) and c.is_virtual_method() for c in decl.get_children())


def member_problems(type_, name, where, out):
    """Appends a line per member of type_ that holds an address: a pointer, a reference, uptr/sptr, a vtable."""
    if truncations.pointwide(type_):
        out.append("%s %s: uptr/sptr member in a record" % (where, name))
        return
    T = truncations.ci.TypeKind
    K = truncations.ci.CursorKind
    canonical = type_.get_canonical()
    if canonical.kind in (T.POINTER, T.LVALUEREFERENCE, T.RVALUEREFERENCE, T.MEMBERPOINTER):
        out.append("%s %s: pointer member in a record" % (where, name))
    elif canonical.kind in (T.CONSTANTARRAY, T.INCOMPLETEARRAY, T.VARIABLEARRAY, T.DEPENDENTSIZEDARRAY):
        member_problems(type_.get_array_element_type(), name + "[]", where, out)
    elif canonical.kind == T.RECORD:
        decl = canonical.get_declaration()
        if has_virtual(decl):
            out.append("%s %s: virtual method (vtable pointer) in a record" % (where, name))
            return
        for child in decl.get_children():
            if child.kind != K.CXX_BASE_SPECIFIER:
                continue
            if child.is_virtual_base():
                out.append("%s %s: virtual base (vtable pointer) in a record" % (where, name))
                return
            member_problems(child.type, "%s(base %s)" % (name, child.spelling), where, out)
        for field in canonical.get_fields():
            member_problems(field.type, "%s.%s" % (name, field.spelling or "<anonymous>"), where, out)


def check_tu(tu, rows):
    K = truncations.ci.CursorKind
    wanted = {r.struct for r in rows}
    decls = collections.defaultdict(list)
    sizes = {}
    for cursor in tu.cursor.walk_preorder():
        if cursor.kind == K.ENUM_CONSTANT_DECL and cursor.spelling.startswith(SIZE_PREFIX):
            sizes[cursor.spelling[len(SIZE_PREFIX):]] = cursor.enum_value
        elif cursor.spelling in wanted and cursor.location.file is not None and cursor.kind in (
                K.STRUCT_DECL, K.CLASS_DECL, K.UNION_DECL, K.TYPEDEF_DECL):
            decls[cursor.spelling].append(cursor)

    failures = []
    for row in rows:
        here = [c for c in decls[row.struct] if truncations.rel(c.location.file.name) == row.declared]
        definitions = [c for c in here if c.kind != K.TYPEDEF_DECL and c.is_definition()]
        typedefs = [c for c in here if c.kind == K.TYPEDEF_DECL]
        if not definitions and not typedefs:
            failures.append("%s:%d %s: no definition in the build's translation unit" % (row.declared, row.line, row.struct))
            continue
        decl = (definitions or typedefs)[0]
        where = "%s:%d" % (row.declared, decl.location.line)
        type_ = decl.type if definitions else decl.underlying_typedef_type
        size = type_.get_canonical().get_size()
        want = sizes.get(row.struct)
        if size != want:
            failures.append("%s %s: sizeof is %d, records.md says %s" % (where, row.struct, size, row.size))
        member_problems(type_, row.struct, where, failures)
    return failures


def parse_tu(path, args, source):
    return truncations.ci.Index.create().parse(path, args=args, unsaved_files=[(path, source)], options=0)


def diagnostics(tu):
    errors = [str(d) for d in tu.diagnostics if d.severity >= truncations.ci.Diagnostic.Error]
    return ["libclang: %s" % e for e in errors[:3]] + (["libclang: %d more errors" % (len(errors) - 3)]
                                                        if len(errors) > 3 else [])


def header_path(root, build, declared):
    mirror = os.path.join(build, "ps2_include", declared[len("ps2/include/"):])
    if declared.startswith("ps2/include/") and os.path.isfile(mirror):
        return mirror
    return os.path.join(root, declared)


def clang_checks(root, build, rows):
    entries = truncations.unit_entries(build)
    by_file = {truncations.rel(e["file"]): e for e in entries}
    failures = []

    headers = [r for r in rows if not r.declared.endswith(".cpp")]
    if headers:
        seed = next((e for e in entries if truncations.rel(e["file"]).startswith("ps2/src/")), None)
        if seed is None:
            return ["no ps2/src unit in compile_commands.json to take the PC flags from"]
        args = truncations.command_args(seed)
        declared = list(dict.fromkeys(r.declared for r in headers))
        includes = "".join('#include "%s"\n' % header_path(root, build, d).replace(os.sep, "/") for d in declared)
        name = os.path.join(seed["directory"], "records_check.cpp")
        with in_directory(seed["directory"]):
            tu = parse_tu(name, args, includes + size_constants(headers))
        failures += diagnostics(tu)
        failures += check_tu(tu, headers)

    for row in rows:
        if not row.declared.endswith(".cpp"):
            continue
        entry = by_file.get(row.declared)
        if entry is None:
            failures.append("%s:%d %s: no compile command for the unit" % (row.declared, row.line, row.struct))
            continue
        path = truncations.canon(os.path.join(root, row.declared))
        args = truncations.command_args(entry)
        with in_directory(entry["directory"]):
            tu = parse_tu(path, args, truncations.unit_source(path) + size_constants([row]))
        failures += diagnostics(tu)
        failures += check_tu(tu, [row])
    return failures


def clang_unavailable(build):
    if build is None:
        return "no --build"
    if not os.path.isfile(os.path.join(build, "compile_commands.json")):
        return "%s has no compile_commands.json" % build
    try:
        truncations.load_libclang(None)
    except ImportError as error:
        return "clang Python bindings missing (pip install libclang): %s" % error
    except Exception as error:
        return "libclang cannot be loaded: %s" % error
    return None


@contextlib.contextmanager
def tree_root(root):
    previous = truncations.ROOT
    truncations.ROOT = root
    try:
        yield
    finally:
        truncations.ROOT = previous


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--root", default=ROOT)
    parser.add_argument("--build")
    parser.add_argument("--require-clang", action="store_true")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--emit", action="store_true", help="print the PORT_RECORD_ASSERT line of every assert row")
    mode.add_argument("--update-lines", action="store_true", help="rewrite the Declared line numbers")
    options = parser.parse_args(argv)

    root = os.path.abspath(options.root)
    with tree_root(root):
        return run(options, root)


def run(options, root):
    md_path = os.path.join(root, RECORDS_MD)
    lines = read_lines(md_path)
    rows = parse_table(lines)

    if options.emit:
        print("\n".join(emit(rows)))
        return 0
    if options.update_lines:
        failures = update_lines(root, lines, rows)
        for failure in failures:
            print("records: %s" % failure, file=sys.stderr)
        if failures:
            return 1
        with open(md_path, "w", encoding="latin-1", newline="") as f:
            f.write("\n".join(lines))
        return 0

    failures, warnings = check_text(root, rows)
    failures += check_unlisted_asserts(root, rows)
    failures += check_ps2_only_mentions(root)

    notes = []
    build = os.path.abspath(options.build) if options.build is not None else None
    if build is not None:
        truncations.set_mirrors(build)
        reader_failures = check_ps2_only_functions(build)
        if reader_failures is None:
            notes.append("PS2-only reader functions skipped: %s has no darkchronicle or port_overrides.txt" % build)
        else:
            failures += reader_failures
    else:
        notes.append("PS2-only reader functions skipped: no --build")

    reason = clang_unavailable(build)
    if reason is None:
        failures += clang_checks(root, build, [r for r in rows if r.pc == "assert"])
    elif options.require_clang:
        failures.append("--require-clang: %s" % reason)
    else:
        notes.append("libclang checks skipped: %s" % reason)

    for note in notes:
        print("records: %s" % note)
    for warning in warnings:
        print("records: warning: %s" % warning, file=sys.stderr)
    for failure in failures:
        print("records: %s" % failure, file=sys.stderr)

    asserted = sum(r.pc == "assert" for r in rows)
    ps2_only = sum(r.pc == "PS2 only" for r in rows)
    host = sum(r.pc == "host image" for r in rows)
    print("records: %d records asserted, %d PS2-only, %d host images: %s"
          % (asserted, ps2_only, host, "FAIL" if failures else "OK"))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
