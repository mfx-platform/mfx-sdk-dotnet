"""Fail when anything internal would be published: in the public repository's tree, or in the
packages a release pushes to NuGet.

Customers read this SDK's source, tests and documents. Nothing in them may cite the team's internal
designs, decisions, work items or test cases, or name an internal repository or a private customer.

    python3 tools/check_public.py --tree <directory>      # every text file under a directory
    python3 tools/check_public.py --package <directory>   # every .nupkg and .snupkg in it

A package is also refused when it lacks what nuget.org shows of it: the README as its readme, the
Apache-2.0 licence expression, and the repository it is built from.

Two kinds of check:

* **Patterns** for internal identifiers whose *shape* is the giveaway: work-item, decision and
  architecture-record ids, test-case ids of both schemes, internal design paths, section
  references, tracker area paths, team lanes, private repository names and the name the team gives
  the SDK every other one derives from.
* **Names** of private customers, internal repositories and other products, read from
  ``tools/internal-names.txt``. A list of words a public repository must never contain cannot be
  published in it, so that file stays in the private repository and the export leaves it out; the
  public repository runs the pattern check alone, and every release is checked with both before it
  is exported. Hyphenated tokens are checked whole and part by part.

Standard library only: it runs on a bare runner.
"""

from __future__ import annotations

import re
import sys
import zipfile
from collections.abc import Iterable, Iterator
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]

#: The test-case area codes: a case id of the second scheme is ``TC-``, one of these, and four
#: digits. This closed list MUST agree with the one the test-results publisher holds, so nothing it
#: would record can be published. Longest code first, as the publisher builds its pattern.
_TC_AREAS = sorted(
    (
        "CP", "AGENT", "INFRA", "DOCS", "SDK", "ADMIN", "BILL", "SUP",
        "PORTAL", "RISK", "SEC", "COPY", "CTRD", "IDN", "TRADE", "CX",
    ),
    key=lambda code: (-len(code), code),
)  # fmt: skip

#: Identifiers that are internal by their shape.
INTERNAL = re.compile(
    # Written so that these lines do not match themselves when this file is scanned.
    r"design[/]|\bDEC-\d+|AB#\d+|\bADR-\d+|TC-S[1]-|§ ?\d"
    # A test-case id of the area scheme; the codes are joined in, so no id is spelt out here.
    r"|TC-(?:" + "|".join(_TC_AREAS) + r")-\d{4}"
    # A tracker area path is upper case - MFX, a backslash, a capital. A lower-case file path that
    # starts the same way is not one.
    r"|(?-i:MFX[\\]{1,2}[A-Z])|\bLane \d|-intern[a]l\b"
    # The team's name for the SDK the others derive from. Public text names that SDK by its
    # language, or states the behaviour itself.
    r"|\breferenc[e] SDK",
    re.IGNORECASE,
)

#: The SDK reference the docs site renders is held to more: every word in it is read as the
#: product's own, so it names no tracker or decision id even without a number, calls the trading
#: terminal "the terminal" and the platform's machines "hosts", and names no private repository.
#: Case matters for the terminal's short name only: ``mt5`` is a venue's value.
REFERENCE = "docs/sdkref.json"
REFERENCE_WORDS = re.compile(r"AB#|DEC-|(?-i:MT5)|\bcells?\b|-intern[a]l", re.IGNORECASE)

#: Where the names live: one lower-case name a line, ``#`` starts a comment. Absent in the public
#: repository, where the check runs on patterns alone.
NAMES_FILE = REPO / "tools" / "internal-names.txt"

#: Directories a tree scan never enters: git's own files, environments, caches and build output,
#: none of which is committed.
_SKIP = frozenset(
    {
        ".git", ".vs", ".idea", ".hub", ".venv", "bin", "obj", "target", "TestResults", "reports",
        "artifacts", "node_modules", "__pycache__", ".pytest_cache",
    }
)  # fmt: skip
_TOKEN = re.compile(r"[a-z0-9][a-z0-9-]*")


def load_names(path: Path = NAMES_FILE) -> frozenset[str]:
    """The names in ``path``, lower case; none when the file is absent."""
    if not path.is_file():
        return frozenset()
    lines = (line.split("#", 1)[0].strip().lower() for line in path.read_text("utf-8").splitlines())
    return frozenset(line for line in lines if line)


NAMES = load_names()


def _text(data: bytes) -> str | None:
    """``data`` as text, or None when it is binary (a NUL in its first 8 KiB)."""
    # DECISION: every file is scanned unless it is binary, rather than a list of text extensions as
    # the wheel and npm checks keep. This tree mixes C#, MSBuild, PowerShell, Python, JSON and
    # Markdown, and a file type missing from a list would be published unread.
    if b"\0" in data[:8192]:
        return None
    return data.decode("utf-8", "replace")


def _tree(root: Path) -> Iterator[tuple[str, str]]:
    for path in sorted(root.rglob("*")):
        relative = path.relative_to(root)
        if _SKIP.intersection(relative.parts[:-1]) or not path.is_file():
            continue
        text = _text(path.read_bytes())
        if text is not None:
            yield relative.as_posix(), text


def match(line: str, names: frozenset[str] = NAMES) -> str | None:
    """What in ``line`` is internal, or None. A name is reported by the token that carried it."""
    found = INTERNAL.search(line)
    if found:
        return found.group(0)
    for token in _TOKEN.findall(line.lower()):
        for word in (token, *token.split("-")):
            if word in names:
                return token
    return None


def scan(sources: Iterable[tuple[str, str]], names: frozenset[str] = NAMES) -> list[str]:
    """Every line of ``sources`` that is internal, as ``where:line: match``."""
    out: list[str] = []
    for where, text in sources:
        reference = where == REFERENCE or where.endswith("/" + REFERENCE)
        for number, line in enumerate(text.splitlines(), 1):
            hit = match(line, names)
            if hit is None and reference:
                found = REFERENCE_WORDS.search(line)
                hit = found.group(0) if found else None
            if hit:
                out.append(f"{where}:{number}: {hit}")
    return out


def tree_findings(root: Path, names: frozenset[str] = NAMES) -> list[str]:
    """Every line under ``root`` that would publish something internal."""
    return scan(_tree(root), names)


#: What nuget.org shows of a package, each read from its .nuspec: the readme on its page, the licence
#: by its SPDX expression, and the public repository it is built from. Each is (what, pattern).
NUSPEC_NEEDS = (
    ("the README as its readme", re.compile(r"<readme>README\.md</readme>")),
    ("the Apache-2.0 licence", re.compile(r'<license type="expression">Apache-2\.0</license>')),
    (
        "the public repository",
        re.compile(r'<repository type="git" url="https://github\.com/mfx-platform/mfx-sdk-dotnet"'),
    ),
)
#: The files a package carries at its root, beside the code.
PACKAGE_FILES = ("README.md", "LICENSE")


def _package(path: Path) -> tuple[list[tuple[str, str]], list[str]]:
    """The text entries of one package, as scan sources, and what it lacks."""
    sources: list[tuple[str, str]] = []
    with zipfile.ZipFile(path) as package:
        names = package.namelist()
        for name in names:
            text = _text(package.read(name))
            if text is not None:
                sources.append((f"{path.name}:{name}", text))
    if path.suffix != ".nupkg":
        return sources, []
    missing = [f"{path.name}: no {name} at its root" for name in PACKAGE_FILES if name not in names]
    nuspec = next((text for where, text in sources if where.endswith(".nuspec")), "")
    missing += [
        f"{path.name}: the .nuspec lacks {what}"
        for what, need in NUSPEC_NEEDS
        if not need.search(nuspec)
    ]
    return sources, missing


def package_findings(dist: Path, names: frozenset[str] = NAMES) -> list[str]:
    """Every line the packages in ``dist`` would ship that is internal, and everything they lack."""
    sources: list[tuple[str, str]] = []
    missing: list[str] = []
    for path in sorted(dist.iterdir()):
        if path.suffix in {".nupkg", ".snupkg"}:
            found, lacks = _package(path)
            sources.extend(found)
            missing.extend(lacks)
    return missing + scan(sources, names)


def main(argv: list[str] | None = None) -> int:
    """Run the check; 0 when clean, 1 when something internal was found, 2 on bad usage."""
    args = sys.argv[1:] if argv is None else argv
    if args[:1] not in (["--tree"], ["--package"]) or len(args) > 2:
        sys.stderr.write("usage: check_public.py --tree [<directory>] | --package <directory>\n")
        return 2
    root = Path(args[1]) if len(args) > 1 else REPO
    if not root.is_dir():
        sys.stderr.write(f"no directory at {root}\n")
        return 2
    if args[0] == "--package":
        if not any(path.suffix == ".nupkg" for path in root.iterdir()):
            sys.stderr.write(f"no .nupkg in {root}: run dotnet pack first\n")
            return 2
        found, what = package_findings(root), f"the packages in {root}"
    else:
        found, what = tree_findings(root), f"the tree at {root}"
    for line in found:
        sys.stdout.write(line + "\n")
    if found:
        sys.stderr.write(f"{len(found)} internal reference(s) or missing part(s) in {what}\n")
        return 1
    sys.stdout.write(f"nothing internal in {what}\n")
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
