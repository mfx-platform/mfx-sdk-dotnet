"""The version a release tag names, checked against every place the version is written.

    python3 tools/release_version.py v1.2.3

prints ``1.2.3`` when the tag is ``v`` and the package's version, and the version the SDK states in
its ``User-Agent`` is the same; otherwise it says which differs and exits 1. The release workflows
run it first, so a tag that does not match what would be built publishes nothing.

The package's version is ``<Version>`` in the SDK's project file. The ``User-Agent`` names
``SdkVersion.Value`` in ``MfxClient.cs``, a constant, because an assembly's informational version
carries build metadata a request header should not.

Standard library only: it runs on a bare runner.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
PROJECT = Path("src/MfxHub.Sdk/MfxHub.Sdk.csproj")
CLIENT = Path("src/MfxHub.Sdk/MfxClient.cs")

_VERSION = re.compile(r"<Version>\s*([^<\s]+)\s*</Version>")
_AGENT = re.compile(r"class SdkVersion\s*\{[^}]*?const string Value = \"([^\"]+)\"", re.S)


def versions(repo: Path = REPO) -> tuple[str | None, str | None]:
    """The package's version and the ``User-Agent``'s, each None when it cannot be found."""
    project = _VERSION.search((repo / PROJECT).read_text(encoding="utf-8-sig"))
    agent = _AGENT.search((repo / CLIENT).read_text(encoding="utf-8-sig"))
    return (project.group(1) if project else None, agent.group(1) if agent else None)


def problems(tag: str, repo: Path = REPO) -> list[str]:
    """What makes ``tag`` the wrong tag for what ``repo`` would build; empty when it is right."""
    package, agent = versions(repo)
    if package is None:
        return [f"no <Version> in {PROJECT.as_posix()}"]
    out = []
    if tag != f"v{package}":
        out.append(f"tag {tag} does not match {PROJECT.as_posix()}'s version {package}")
    if agent != package:
        out.append(f"the User-Agent names {agent} ({CLIENT.as_posix()}), the package {package}")
    return out


def main(argv: list[str] | None = None, repo: Path = REPO) -> int:
    """Print the version; 0 when the tag matches it everywhere, 1 when not, 2 on bad usage."""
    args = sys.argv[1:] if argv is None else argv
    if len(args) != 1:
        sys.stderr.write("usage: release_version.py <tag>\n")
        return 2
    found = problems(args[0], repo)
    for problem in found:
        sys.stderr.write(f"::error::{problem}\n")
    if found:
        return 1
    sys.stdout.write(args[0][1:] + "\n")
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
