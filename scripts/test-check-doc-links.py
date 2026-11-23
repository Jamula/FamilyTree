#!/usr/bin/env python3
"""Negative tests for the case-sensitive documentation link checker."""
import importlib.util
import tempfile
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    'check_doc_links', Path(__file__).with_name('check-doc-links.py'))
assert spec and spec.loader
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

with tempfile.TemporaryDirectory() as workspace:
    root = Path(workspace)
    (root / 'docs').mkdir()
    (root / 'docs' / 'architecture.md').write_text('# Diagram', encoding='utf-8')
    (root / 'docs' / 'assets').mkdir()
    (root / 'docs' / 'assets' / 'wireframe.svg').write_text('<svg/>', encoding='utf-8')
    index = root / 'readme.md'
    index.write_text('[Architecture](docs/architecture.md)\n'
                     '![Preview](docs/assets/wireframe.svg)\n'
                     '[External](https://example.org/x)\n'
                     '```markdown\n[Old](does-not-exist.md)\n```\n',
                     encoding='utf-8')
    assert module.validate(root) == []
    index.write_text('[Broken](docs/ARCHITECTURE.md)\n', encoding='utf-8')
    assert any('case-mismatched' in x for x in module.validate(root))
    index.write_text('![Broken image](docs/assets/missing.svg)\n', encoding='utf-8')
    assert any('broken' in x for x in module.validate(root))
    index.write_text('[Traversal](../../outside.md)\n', encoding='utf-8')
    assert any('outside repository' in x for x in module.validate(root))
    (root / 'docs' / 'UPPER.md').write_text('No links', encoding='utf-8')
    assert any('filename must be lowercase' in x for x in module.validate(root))
    for root_name in ('README.md', 'CHANGELOG.md'):
        root_file = root / root_name
        root_file.write_text('Uppercase filename', encoding='utf-8')
        assert any(root_name in x and 'filename must be lowercase' in x
                   for x in module.validate(root))
        root_file.unlink()
print('PASS docs link checker broken URL, case, traversal, image and code-fence tests')
