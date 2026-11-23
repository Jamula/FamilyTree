#!/usr/bin/env python3
"""Validate GitHub Markdown local links and lowercase human-facing doc paths."""
from __future__ import annotations

import re
import sys
from pathlib import Path
from urllib.parse import unquote, urlsplit

LINK = re.compile(r'!?\[[^\]]*\]\((?:<([^>]+)>|([^\s)]+))(?:\s+[^)]*)?\)')
FENCE = re.compile(r'^\s*(`{3,}|~{3,})')


def markdown_without_fences(content: str) -> str:
    output = []
    fence = None
    for line in content.splitlines():
        match = FENCE.match(line)
        if match:
            token = match.group(1)
            if fence is None:
                fence = (token[0], len(token))
            elif token[0] == fence[0] and len(token) >= fence[1]:
                fence = None
            continue
        if fence is None:
            output.append(line)
    return '\n'.join(output)


def validate(repo_root: Path) -> list[str]:
    root = repo_root.resolve()
    errors = []
    for file in sorted(root.rglob('*.md')):
        rel = file.relative_to(root)
        if any(part in {'bin', 'obj', '.git', '.venv'} for part in rel.parts):
            continue
        if (rel.parts[0] == 'docs' or rel.as_posix().lower() in
            {'readme.md', 'changelog.md'}) and file.name != file.name.lower():
            errors.append(f'{rel}: user documentation filename must be lowercase')
        content = markdown_without_fences(file.read_text(encoding='utf-8'))
        for match in LINK.finditer(content):
            url = (match.group(1) or match.group(2)).strip()
            if not url or url.startswith('#') or url.startswith('//'):
                continue
            parsed = urlsplit(url)
            if parsed.scheme or parsed.netloc:
                continue
            link_path = unquote(parsed.path)
            if not link_path:
                continue
            target = ((root / link_path.lstrip('/')) if link_path.startswith('/')
                else (file.parent / link_path)).resolve()
            if not target.is_relative_to(root):
                errors.append(f'{rel}: link outside repository: {url}')
            elif not target.exists():
                errors.append(f'{rel}: broken or case-mismatched local link: {url}')
    return errors


if __name__ == '__main__':
    origin = Path(__file__).resolve().parent.parent
    failures = validate(origin)
    if failures:
        for item in failures:
            print('FAIL:', item, file=sys.stderr)
        sys.exit(1)
    print('PASS documentation local links, images and lowercase filenames')
