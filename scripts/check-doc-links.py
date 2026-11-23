#!/usr/bin/env python3
"""Check case-sensitive local Markdown links without mistaking syntax samples for links."""
from __future__ import annotations

import re
import sys
from pathlib import Path
from urllib.parse import unquote, urlsplit

FENCE = re.compile(r"^\s{0,3}(`{3,}|~{3,})")
INLINE_START = re.compile(r"!?\[[^\]]+\]\(")
REFERENCE = re.compile(r"!?\[[^\]]+\]\[([^\]]*)\]")
DEFINITION = re.compile(r"^\s{0,3}\[([^\]]+)\]:\s*(<[^>]+>|\S+)", re.MULTILINE)


def remove_code(content: str) -> str:
    lines = []
    fence = None
    for line in content.splitlines():
        mark = FENCE.match(line)
        if mark:
            token = mark.group(1)
            if fence is None:
                fence = (token[0], len(token))
            elif token[0] == fence[0] and len(token) >= fence[1]:
                fence = None
            lines.append("")
        elif fence is not None:
            lines.append("")
        else:
            lines.append(line)
    text = "\n".join(lines)
    # Mask inline code before interpreting links. A paired run may contain
    # literal Markdown, including backticks of a different length.
    output = []
    pos = 0
    while pos < len(text):
        if text[pos] != "`":
            output.append(text[pos])
            pos += 1
            continue
        end = pos
        while end < len(text) and text[end] == "`":
            end += 1
        run = text[pos:end]
        match = re.search(r"(?<!`)" + re.escape(run) + r"(?!`)", text[end:])
        if match is None:
            output.append(text[pos:end])
            pos = end
            continue
        finish = end + match.end()
        output.append(" " * (finish - pos))
        pos = finish
    return "".join(output)


def destinations(content: str):
    # Parse inline targets using depth, allowing Markdown's balanced/escaped
    # parentheses and angle-quoted filenames instead of a fragile URL regex.
    for m in INLINE_START.finditer(content):
        index = m.end()
        if index >= len(content):
            continue
        if content[index] == "<":
            close = content.find(">", index + 1)
            if close != -1:
                remainder = content[close + 1:]
                if remainder.startswith(")") or re.match(
                    r'''^\s+(?:"[^"]*"|'[^']*')\)''', remainder):
                    yield content[index + 1:close]
            continue
        begin, depth = index, 0
        while index < len(content):
            char = content[index]
            if char == "\\":
                index += 2
                continue
            if char == "(":
                depth += 1
            elif char == ")":
                if depth == 0:
                    target = content[begin:index].strip()
                    # Optional title follows whitespace after the URL.
                    if target:
                        # Optional Markdown link title is not part of the path.
                        parts = re.split(r"(?<!\\)\s+", target, maxsplit=1)
                        if len(parts) == 1 or re.fullmatch(
                            r'''(?:"[^"]*"|'[^']*'|\([^)]*\))''', parts[1]):
                            yield parts[0]
                    break
                depth -= 1
            index += 1

    definitions = {
        match.group(1).strip().lower(): match.group(2).strip("<>")
        for match in DEFINITION.finditer(content)
    }
    for match in REFERENCE.finditer(content):
        name = match.group(1).strip().lower()
        if not name:
            continue
        if name in definitions:
            yield definitions[name]
    # Validate definitions even when the reference is not used, avoiding a
    # stale-case destination hiding behind a different link spelling.
    yield from definitions.values()


def validate(repo_root: Path) -> list[str]:
    root = repo_root.resolve()
    errors = []
    files = (p for p in root.rglob("*") if p.is_file() and p.suffix.lower() == ".md")
    for file in sorted(files):
        rel = file.relative_to(root)
        if any(part in {"bin", "obj", ".git", ".venv"} for part in rel.parts):
            continue
        if (rel.parts[0] == "docs" or rel.as_posix().lower() in
            {"readme.md", "changelog.md"}) and file.name != file.name.lower():
            errors.append(f"{rel}: user documentation filename must be lowercase")
        text = remove_code(file.read_text(encoding="utf-8"))
        for url in destinations(text):
            if not url or url.startswith("#") or url.startswith("//"):
                continue
            try:
                parsed = urlsplit(url)
            except ValueError:
                errors.append(f"{rel}: malformed local URL")
                continue
            if parsed.scheme or parsed.netloc or not parsed.path:
                continue
            link_path = unquote(parsed.path).replace("\\(", "(").replace("\\)", ")")
            target = ((root / link_path.lstrip("/")) if link_path.startswith("/")
                else (file.parent / link_path)).resolve()
            if not target.is_relative_to(root):
                errors.append(f"{rel}: link outside repository: {url}")
            elif not target.exists():
                errors.append(f"{rel}: broken or case-mismatched local link: {url}")
    return errors


if __name__ == "__main__":
    failures = validate(Path(__file__).resolve().parent.parent)
    if failures:
        for item in failures:
            print("FAIL:", item, file=sys.stderr)
        sys.exit(1)
    print("PASS documentation links, references, code spans and lowercase filenames")
