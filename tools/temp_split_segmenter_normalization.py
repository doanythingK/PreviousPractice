import re
from pathlib import Path

source_path = Path('PreviousPractice/Infrastructure/OcrQuestionSegmenter.cs')
target_path = Path('PreviousPractice/Infrastructure/OcrQuestionSegmenter.Normalization.cs')
architecture_path = Path('PreviousPractice/ARCHITECTURE.md')

source = source_path.read_text(encoding='utf-8')
source = source.replace(
    'public static class OcrQuestionSegmenter\n{',
    'public static partial class OcrQuestionSegmenter\n{',
    1)

method_names = {
    'TryBuildNormalizedDocument',
    'ExpandParsedLine',
    'BuildPageLayout',
    'CountHorizontalOriginClusters',
    'LooksLikeStrongLayoutHeader',
    'OrderParsedPageLines',
    'OrderColumnRegion',
    'ShouldUseRowMajorOrder',
    'ScoreHeaderReadingOrder',
    'TryReadHeaderIndexForOrdering',
    'IsFullWidthGeometry',
    'TryNormalizeGeometry',
    'GetHorizontalMidpoint',
    'ExpandSyntheticLineBreaks',
}


def method_range(text: str, name: str):
    declaration = re.compile(
        rf'(?m)^    private static [^\n]*\b{re.escape(name)}\s*\('
    ).search(text)
    if declaration is None:
        raise RuntimeError(f'method declaration not found: {name}')

    start = declaration.start()
    brace = text.find('{', declaration.end())
    if brace < 0:
        raise RuntimeError(f'opening brace not found: {name}')

    depth = 0
    i = brace
    in_string = False
    verbatim = False
    in_char = False
    escape = False
    line_comment = False
    block_comment = False
    while i < len(text):
        ch = text[i]
        nxt = text[i + 1] if i + 1 < len(text) else ''
        if line_comment:
            if ch == '\n':
                line_comment = False
            i += 1
            continue
        if block_comment:
            if ch == '*' and nxt == '/':
                block_comment = False
                i += 2
                continue
            i += 1
            continue
        if in_string:
            if verbatim:
                if ch == '"' and nxt == '"':
                    i += 2
                    continue
                if ch == '"':
                    in_string = False
                    verbatim = False
            else:
                if escape:
                    escape = False
                elif ch == '\\':
                    escape = True
                elif ch == '"':
                    in_string = False
            i += 1
            continue
        if in_char:
            if escape:
                escape = False
            elif ch == '\\':
                escape = True
            elif ch == "'":
                in_char = False
            i += 1
            continue
        if ch == '/' and nxt == '/':
            line_comment = True
            i += 2
            continue
        if ch == '/' and nxt == '*':
            block_comment = True
            i += 2
            continue
        if ch == '@' and nxt == '"':
            in_string = True
            verbatim = True
            i += 2
            continue
        if ch == '"':
            in_string = True
            i += 1
            continue
        if ch == "'":
            in_char = True
            i += 1
            continue
        if ch == '{':
            depth += 1
        elif ch == '}':
            depth -= 1
            if depth == 0:
                end = i + 1
                while end < len(text) and text[end] in '\r\n':
                    end += 1
                return start, end
        i += 1
    raise RuntimeError(f'closing brace not found: {name}')


ranges = []
for method_name in method_names:
    start, end = method_range(source, method_name)
    ranges.append((start, end, method_name, source[start:end].rstrip()))

ranges.sort(key=lambda item: item[0])
for previous, current in zip(ranges, ranges[1:]):
    if previous[1] > current[0]:
        raise RuntimeError(f'overlapping extraction: {previous[2]} / {current[2]}')

extracted = [item[3] for item in ranges]
for start, end, _, _ in reversed(ranges):
    source = source[:start] + source[end:]

while '\n\n\n' in source:
    source = source.replace('\n\n\n', '\n\n')
source_path.write_text(source, encoding='utf-8')

target = '''using PreviousPractice.Models;

namespace PreviousPractice.Infrastructure;

public static partial class OcrQuestionSegmenter
{
''' + '\n\n'.join(extracted) + '\n}\n'
target_path.write_text(target, encoding='utf-8')

architecture = architecture_path.read_text(encoding='utf-8')
old = '''### 2. OCR 문항 분할

`OcrQuestionSegmenter`는 외부 facade를 유지하면서 내부를 다음 책임으로 나눕니다.

- document normalization
- header hypothesis 수집
'''
new = '''### 2. OCR 문항 분할 — 진행 중

외부 `OcrQuestionSegmenter` facade와 판정 규칙은 유지합니다. 1차로 document normalization과 page/column layout 계산을 `Infrastructure/OcrQuestionSegmenter.Normalization.cs`로 물리 분리했습니다.

남은 내부 분리 대상은 다음과 같습니다.

- header hypothesis 수집
'''
if old not in architecture:
    raise RuntimeError('ARCHITECTURE.md target section not found')
architecture_path.write_text(architecture.replace(old, new, 1), encoding='utf-8')
