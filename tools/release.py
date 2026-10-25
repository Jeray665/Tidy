"""Publishes a Tidy release on GitHub, or fixes the notes of an existing one.

    python tools/release.py 1.1.2          build, check and publish v1.1.2
    python tools/release.py --notes 1.1.0  only rewrite the notes of v1.1.0 from CHANGELOG.md

Everything text-related is read and written as UTF-8 here (never through Windows PowerShell 5.1, which
reads BOM-less UTF-8 as ANSI and mangles every accent). Before and after publishing, the notes
are checked for broken accents, and the notes GitHub stores are read back and compared byte for byte.
"""
import hashlib, json, re, shutil, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GH = r"C:\Program Files\GitHub CLI\gh.exe"
REPO = "Jeray665/Tidy"
OUT = ROOT / "dist" / "release"
FOOTER = "**Tidy-Setup.exe** instala o actualiza Tidy. **Tidy-Portable.exe** funciona sin instalar. Requiere el .NET 10 Desktop Runtime."
# what UTF-8 Spanish looks like after a wrong ANSI round trip, plus the replacement char and a stray BOM
MOJIBAKE = re.compile("[\u00c3\u00c2\ufffd\ufeff]")


def run(*args, **kw):
    print(">", " ".join(map(str, args)))
    return subprocess.run(args, check=True, cwd=ROOT, text=True, encoding="utf-8", **kw)


def clean(text, where):
    bad = MOJIBAKE.search(text)
    if bad:
        line = text[: bad.start()].count("\n") + 1
        sys.exit(f"STOP: texto roto en {where}, línea {line}: {text.splitlines()[line - 1]!r}")
    return text


def notes(version):
    log = clean((ROOT / "CHANGELOG.md").read_text(encoding="utf-8"), "CHANGELOG.md")
    m = re.search(rf"^## {re.escape(version)}\s*\n(.*?)(?=^## |\Z)", log, re.S | re.M)
    if not m or not m.group(1).strip():
        sys.exit(f"STOP: CHANGELOG.md no tiene entrada para {version}")
    return m.group(1).strip() + "\n\n" + FOOTER + "\n"


def push_notes(tag, body):
    f = OUT / "notes.md"
    f.parent.mkdir(parents=True, exist_ok=True)
    f.write_text(body, encoding="utf-8")  # no BOM
    return f


def verify_online(tag, body):
    got = run(GH, "api", f"repos/{REPO}/releases/tags/{tag}", capture_output=True).stdout
    online = json.loads(got)["body"].replace("\r\n", "\n")
    clean(online, f"las notas publicadas de {tag}")
    if online.strip() != body.strip():
        sys.exit(f"STOP: las notas de {tag} en GitHub no coinciden con CHANGELOG.md")
    print(f"OK: notas de {tag} correctas en GitHub")


def main():
    args = sys.argv[1:]
    if len(args) == 2 and args[0] == "--notes":
        tag, body = f"v{args[1]}", notes(args[1])
        run(GH, "release", "edit", tag, "--repo", REPO, "--notes-file", str(push_notes(tag, body)))
        verify_online(tag, body)
        return
    if len(args) != 1 or not re.fullmatch(r"\d+\.\d+\.\d+", args[0]):
        sys.exit(__doc__)
    version = args[0]
    tag = f"v{version}"
    csproj = (ROOT / "src" / "Tidy" / "Tidy.csproj").read_text(encoding="utf-8-sig")
    if f"<Version>{version}</Version>" not in csproj:
        sys.exit(f"STOP: Tidy.csproj no tiene <Version>{version}</Version>")
    body = notes(version)
    if run("git", "status", "--porcelain", capture_output=True).stdout.strip():
        sys.exit("STOP: hay cambios sin commit; súbelos antes de publicar")

    shutil.rmtree(OUT, ignore_errors=True)
    run("dotnet", "publish", "src/Tidy", "-c", "Release", "-p:PublishSingleFile=true", "-p:DebugType=none", "-o", str(OUT / "build"))
    exe = OUT / "build" / "Tidy.exe"
    for name in ("Tidy-Setup.exe", "Tidy-Portable.exe"):
        shutil.copyfile(exe, OUT / name)
    sha = hashlib.sha256(exe.read_bytes()).hexdigest()
    (OUT / "Tidy-Setup.exe.sha256").write_text(f"{sha}  Tidy-Setup.exe", encoding="ascii")

    run(GH, "release", "create", tag, "--repo", REPO, "--target", "main", "--title", f"Tidy {version}",
        "--notes-file", str(push_notes(tag, body)),
        str(OUT / "Tidy-Setup.exe"), str(OUT / "Tidy-Portable.exe"), str(OUT / "Tidy-Setup.exe.sha256"))
    verify_online(tag, body)
    assets = json.loads(run(GH, "api", f"repos/{REPO}/releases/tags/{tag}", capture_output=True).stdout)["assets"]
    for a in assets:
        if a["name"].endswith(".exe") and a.get("digest") != f"sha256:{sha}":
            sys.exit(f"STOP: la huella de {a['name']} en GitHub no coincide con la compilada")
    print(f"OK: {tag} publicada, huella {sha}")


if __name__ == "__main__":
    main()
