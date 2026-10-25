"""Collects every user-visible Spanish text in Tidy and keeps src/Tidy/Assets/i18n/en.json in step.

  python tools/i18n.py          adds new texts (empty translation), drops ones no longer used, reports what is missing
  python tools/i18n.py --check  only checks; exit code 1 if any text has no English translation (release.py runs this)

Interpolated C# strings become templates: $"{n} cambios" -> "{0} cambios". At runtime Tidy matches them with the
holes as wildcards, and translates the text inside each hole on its own (so ternaries like {(on ? "sí" : "no")} work).
"""
import io, json, os, re, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
SRC = os.path.join(ROOT, 'src', 'Tidy')
OUT = os.path.join(SRC, 'Assets', 'i18n', 'en.json')

ESC = {'n': '\n', 't': '\t', 'r': '\r', '"': '"', "'": "'", '\\': '\\', '0': '\0'}


def cs_literals(code):
    """Yields (text, line, nested) for each string literal; interpolated ones as templates. Literals inside holes come
    too, marked nested: those are the pieces (like "activado") that get translated on their own at runtime."""
    i, n = 0, len(code)
    while i < n:
        c = code[i]
        if code.startswith('//', i):
            i = code.find('\n', i); i = n if i < 0 else i
        elif code.startswith('/*', i):
            i = code.find('*/', i + 2) + 2
        elif c == "'":  # char literal
            j = i + 1
            while j < n and code[j] != "'":
                j += 2 if code[j] == '\\' else 1
            i = j + 1
        elif c in '$@"':
            m = re.match(r'(\$@|@\$|\$|@)?("""|")', code[i:])
            if not m or (c != '"' and not m.group(1)):
                i += 1; continue
            prefix, quote = m.group(1) or '', m.group(2)
            start = i + m.end()
            line = code.count('\n', 0, i) + 1
            if quote == '"""':  # raw string: XML/JSON templates, never UI text
                i = code.find('"""', start) + 3
                continue
            text, holes, j = [], 0, start
            verbatim, interp = '@' in prefix, '$' in prefix
            while j < n:
                ch = code[j]
                if ch == '"':
                    if verbatim and code.startswith('""', j):
                        text.append('"'); j += 2; continue
                    j += 1; break
                if ch == '\\' and not verbatim:
                    text.append(ESC.get(code[j + 1], code[j + 1])); j += 2; continue
                if interp and ch == '{':
                    if code.startswith('{{', j):
                        text.append('{'); j += 2; continue
                    depth, k = 1, j + 1
                    while k < n and depth:  # find the matching brace, skipping nested strings
                        if code[k] == '"':
                            q = k + 1
                            while q < n and code[q] != '"':
                                q += 2 if code[q] == '\\' else 1
                            k = q + 1; continue
                        depth += {'{': 1, '}': -1}.get(code[k], 0); k += 1
                    expr = code[j + 1:k - 1]
                    yield from ((t, line, True) for t, _, _ in cs_literals(expr))
                    text.append('{%d}' % holes); holes += 1; j = k; continue
                if interp and code.startswith('}}', j):
                    text.append('}'); j += 2; continue
                text.append(ch); j += 1
            yield ''.join(text), line, False
            i = j
        else:
            i += 1


SPANISH = re.compile(r"[áéíóúñ¿¡ÁÉÍÓÚÑ]|\b(de|la|el|los|las|que|y|en|para|con|sin|se|un|una|por|más|tu|tus|no|al|del|lo|es|ya|o|su|sus|qué|cómo|hay|si)\b", re.I)
WORD = re.compile(r"^[A-ZÁÉÍÓÚÑ][a-záéíóúñü]+$")
CODE = re.compile(r"[\\/_]|^[\w.\-]+\.(exe|dll|json|txt|lnk|log|ico|png|xml|ps1|msc|cpl)$|^https?:|^\{\d\}$|^pack:|^[A-Z]+:|=|^\W*$")


def human(s, line_text='', nested=False):
    t = s.strip()
    if nested and re.fullmatch(r"[a-záéíóúñü ]{3,}", t): return True  # ternary pieces: "activado", "cosas"…
    if len(t) < 2 or not re.search(r'[A-Za-zÁÉÍÓÚÑáéíóúñ]', t): return False
    if 'Log(' in line_text or 'DllImport' in line_text: return False
    if CODE.search(t) and not SPANISH.search(t): return False
    if re.fullmatch(r'[A-Za-z0-9]+', t) and re.search(r'\d', t): return False  # symbol names like Broom24
    if re.fullmatch(r'\d+ [a-záéíóúñü]{3,}', t): return True  # singulars like "1 archivo"
    words = re.findall(r'[A-Za-zÁÉÍÓÚÑáéíóúñü]{2,}', t)
    if re.search(r'\{\d\}', t) and re.search(r'[A-Za-zÁÉÍÓÚÑáéíóúñü]{3}', re.sub(r'\{\d\}', '', t)): return True  # "Disco {0}", "{0} hilos"
    return bool(SPANISH.search(t)) or bool(WORD.match(t)) or (len(words) >= 2 and ' ' in t)


def collect():
    found = {}
    for base, dirs, files in os.walk(SRC):
        dirs[:] = [d for d in dirs if d not in ('bin', 'obj')]
        for f in sorted(files):
            p = os.path.join(base, f)
            if f.endswith('.cs'):
                code = io.open(p, encoding='utf-8-sig').read()
                lines = code.split('\n')
                for s, line, nested in cs_literals(code):
                    if human(s, lines[line - 1], nested):
                        found.setdefault(s, os.path.relpath(p, ROOT))
            elif f.endswith('.xaml'):
                x = io.open(p, encoding='utf-8-sig').read()
                for m in re.finditer(r'\b(Text|Content|Header|ToolTip|Title|Description|PlaceholderText|Hint)="([^"{][^"]*)"', x):
                    s = (m.group(2).replace('&quot;', '"').replace('&amp;', '&').replace('&lt;', '<').replace('&gt;', '>')
                         .replace('&#10;', '\n').replace('&#xA;', '\n'))
                    # in XAML these attributes are always shown, so lowercase pieces like Run "  ·  publicado " count too
                    if human(s) or re.search(r'[a-záéíóúñ]{3}', s): found.setdefault(s, os.path.relpath(p, ROOT))
    for line in io.open(os.path.join(ROOT, 'CHANGELOG.md'), encoding='utf-8'):  # "Novedades" dialog
        if line.startswith('- '): found.setdefault(line[2:].strip(), 'CHANGELOG.md')
    return found


def main():
    found = collect()
    old = json.load(io.open(OUT, encoding='utf-8')) if os.path.exists(OUT) else {}
    merged = {k: old.get(k, '') for k in found}
    missing = [k for k, v in merged.items() if not v.strip()]
    if '--check' in sys.argv:
        for k in missing[:20]: print('  sin traducir:', repr(k), '(' + found[k] + ')')
        print(f'{len(merged)} textos, {len(missing)} sin traducir')
        sys.exit(1 if missing else 0)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with io.open(OUT, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(merged, f, ensure_ascii=False, indent=1)
        f.write('\n')
    dropped = len([k for k in old if k not in merged])
    print(f'{len(merged)} textos, {len(missing)} sin traducir, {dropped} ya no se usan (quitados)')


if __name__ == '__main__':
    main()
