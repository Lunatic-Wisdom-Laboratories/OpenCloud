#!/usr/bin/env python3
"""Rewrite the PS2's 64-bit `long` to `long long` in build-time copies of the game sources.

    long64.py FILE [FILE ...]

Files are rewritten in place, and only when the content changes. Bytes are read as latin-1 so every
encoding round-trips unchanged.
"""

import argparse
import re
from pathlib import Path
from typing import Dict, List, Optional, Tuple

_DIGITS = "0123456789"
_DEC = "0123456789'"
_HEX = "0123456789abcdefABCDEF'"
_BIN = "01'"
_RAW_PREFIXES = ("R", "LR", "uR", "UR", "u8R")
_STR_PREFIXES = ("", "L", "u", "U", "u8")
_HEADER_DIRECTIVES = ("include", "include_next", "import")
_HEADER_OPERATORS = ("__has_include", "__has_include_next")
_WS = " \t\r\n\f\v"
# Consumes "%%" first so a literal percent never begins a conversion.
# "%lo(" is the MIPS %lo() relocation operator in asm strings, not an octal conversion.
_FORMAT_LONG = re.compile(r"(%%)|(%[-+ #0]*(?:\*|\d+)?(?:\.(?:\*|\d*))?)l(?=[diouxXn])(?!o\()")


def _is_ident(ch: str) -> bool:
    return ch == "_" or (ch.isascii() and ch.isalnum()) or ord(ch) >= 0x80


def _is_alnum(ch: str) -> bool:
    return ch.isascii() and ch.isalnum()


def _line_comment_end(text: str, start: int) -> int:
    pos = start
    while True:
        nl = text.find("\n", pos)
        if nl < 0:
            return len(text)
        k = nl - 1
        if k >= 0 and text[k] == "\r":
            k -= 1
        if k >= start and text[k] == "\\":
            pos = nl + 1
            continue
        return nl


def _quoted_end(text: str, start: int, quote: str) -> int:
    pos = start + 1
    while pos < len(text):
        ch = text[pos]
        if ch == "\\":
            pos += 2
            continue
        if ch == quote:
            return pos + 1
        if ch == "\n":
            return pos
        pos += 1
    return len(text)


def _raw_end(text: str, quote: int) -> int:
    paren = text.find("(", quote + 1)
    if paren < 0:
        return quote + 1
    delim = text[quote + 1:paren]
    end = text.find(")" + delim + '"', paren + 1)
    if end < 0:
        return len(text)
    return end + len(delim) + 2


def _number_end(text: str, start: int) -> int:
    exponent = "pP" if text[start:start + 2] in ("0x", "0X") else "eE"
    pos = start
    while pos < len(text):
        ch = text[pos]
        if _is_alnum(ch) or ch in "_.":
            pos += 1
        elif ch in "+-" and pos > start and text[pos - 1] in exponent:
            pos += 1
        elif (ch == "'" and pos > start and _is_alnum(text[pos - 1])
              and pos + 1 < len(text) and _is_alnum(text[pos + 1])):
            pos += 1
        else:
            break
    return pos


def _header_end(text: str, start: int) -> Optional[int]:
    close = text.find(">", start + 1)
    nl = text.find("\n", start + 1)
    if close < 0 or (nl >= 0 and nl < close):
        return None
    return close + 1


def _opens_header_name(last_two: List[Tuple[str, str]]) -> bool:
    if len(last_two) < 2:
        return False
    (first_kind, first), (second_kind, second) = last_two
    if (first_kind, first) == ("punct", "#") and second_kind == "ident" and second in _HEADER_DIRECTIVES:
        return True
    return first_kind == "ident" and first in _HEADER_OPERATORS and (second_kind, second) == ("punct", "(")


def _tokenize(text: str) -> List[Tuple[str, str]]:
    tokens: List[Tuple[str, str]] = []
    significant: List[Tuple[str, str]] = []
    pos = 0
    while pos < len(text):
        ch = text[pos]
        kind = "punct"
        if ch in _WS:
            end = pos + 1
            while end < len(text) and text[end] in _WS:
                end += 1
            kind = "ws"
        elif text.startswith("//", pos):
            end = _line_comment_end(text, pos)
            kind = "comment"
        elif text.startswith("/*", pos):
            close = text.find("*/", pos + 2)
            end = len(text) if close < 0 else close + 2
            kind = "comment"
        elif ch in _DIGITS or (ch == "." and pos + 1 < len(text) and text[pos + 1] in _DIGITS):
            end = _number_end(text, pos)
            kind = "number"
        elif _is_ident(ch):
            end = pos + 1
            while end < len(text) and _is_ident(text[end]):
                end += 1
            word = text[pos:end]
            after = text[end] if end < len(text) else ""
            if after == '"' and word in _RAW_PREFIXES:
                end = _raw_end(text, end)
                kind = "string"
            elif after in ('"', "'") and word in _STR_PREFIXES:
                end = _quoted_end(text, end, after)
                kind = "string" if after == '"' else "char"
            else:
                kind = "ident"
        elif ch == '"':
            end = _quoted_end(text, pos, '"')
            kind = "string"
        elif ch == "'":
            end = _quoted_end(text, pos, "'")
            kind = "char"
        else:
            end = pos + 1
            header_end = _header_end(text, pos) if ch == "<" and _opens_header_name(significant[-2:]) else None
            if header_end is not None:
                end = header_end
                kind = "header"

        token = (kind, text[pos:end])
        tokens.append(token)
        if kind not in ("ws", "comment"):
            significant.append(token)
        pos = end
    return tokens


def _rewrite_format(tok: str) -> str:
    # A printf vararg of a 64-bit type is read as 32 bits under %l on LLP64, so the conversion must widen with the type.
    quote = tok.find('"')
    if quote < 0 or not tok.endswith('"') or len(tok) < quote + 2 or tok[:quote].endswith("R"):
        return tok
    body = _FORMAT_LONG.sub(lambda m: m.group(1) or m.group(2) + "ll", tok[quote + 1:-1])
    return tok[:quote + 1] + body + '"'


def _rewrite_number(tok: str) -> str:
    lower = tok.lower()
    if lower.startswith("0x"):
        if "." in tok or "p" in lower:
            return tok
        body = tok[2:]
        k = 0
        while k < len(body) and body[k] in _HEX:
            k += 1
        head, suffix = tok[:2] + body[:k], body[k:]
    elif lower.startswith("0b"):
        body = tok[2:]
        k = 0
        while k < len(body) and body[k] in _BIN:
            k += 1
        head, suffix = tok[:2] + body[:k], body[k:]
    else:
        if "." in tok or "e" in lower:
            return tok
        k = 0
        while k < len(tok) and tok[k] in _DEC:
            k += 1
        head, suffix = tok[:k], tok[k:]

    if not suffix or any(c not in "uUlL" for c in suffix):
        return tok
    ells = [i for i, c in enumerate(suffix) if c in "lL"]
    if len(ells) != 1:
        return tok
    i = ells[0]
    return head + suffix[:i] + suffix[i] * 2 + suffix[i + 1:]


def rewrite(text: str) -> str:
    tokens = _tokenize(text)
    sig = [i for i, (kind, _) in enumerate(tokens) if kind not in ("ws", "comment")]
    replaced: Dict[int, str] = {}
    for pos, idx in enumerate(sig):
        kind, tok = tokens[idx]
        if kind == "ident" and tok == "long":
            prev_tok = tokens[sig[pos - 1]] if pos > 0 else None
            next_tok = tokens[sig[pos + 1]] if pos + 1 < len(sig) else None
            if prev_tok == ("ident", "long"):
                continue
            if next_tok in (("ident", "long"), ("ident", "double")):
                continue
            replaced[idx] = "long long"
        elif kind == "number":
            new = _rewrite_number(tok)
            if new != tok:
                replaced[idx] = new
        elif kind == "string" and not _opens_header_name([tokens[i] for i in sig[max(pos - 2, 0):pos]]):
            new = _rewrite_format(tok)
            if new != tok:
                replaced[idx] = new
    return "".join(replaced.get(idx, tok) for idx, (_, tok) in enumerate(tokens))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("files", nargs="+", type=Path)
    args = parser.parse_args()
    for path in args.files:
        original = path.read_bytes().decode("latin-1")
        rewritten = rewrite(original)
        if rewritten != original:
            path.write_bytes(rewritten.encode("latin-1"))


if __name__ == "__main__":
    main()
