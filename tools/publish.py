#!/usr/bin/env python3
"""Sync the package source out of a Unity project and publish a VPM release.

The Unity project is the place the package is actually edited:

    E:\\unity的东西\\工程\\纸柠接头\\Packages\\io.github.sgzincanya.zincat

This tool compares that folder with the repository copy, copies only real
content changes (line ending / BOM noise is ignored), and can then bump the
version, rebuild the listing, commit, tag and push so the GitHub workflow
publishes the release.

Usage
    python tools/publish.py                 # dry run: what changed?
    python tools/publish.py --apply         # copy changes into the repo
    python tools/publish.py --version 2.11.4 --notes "..."   # full release
    python tools/publish.py --project "<other package dir>"

Run from anywhere; paths are resolved from this file.
"""
from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
PACKAGE_REL = Path("Packages/io.github.sgzincanya.zincat")
PACKAGE = REPO / PACKAGE_REL
DEFAULT_PROJECT = Path(r"E:\unity的东西\工程\纸柠接头\Packages\io.github.sgzincanya.zincat")
RELEASE_URL = "https://github.com/SgZincanya/Zincat/releases/download/v{v}/{n}-{v}.zip"

# Only these are normalised by tools/build_release.py, so only these are
# normalised when comparing / copying. Everything else is byte-compared.
LF_EXTS = {".cs", ".json", ".yml", ".yaml"}
SKIP_DIRS = {"__pycache__"}


def norm_bytes(path: Path) -> bytes:
    data = path.read_bytes()
    if path.suffix.lower() in LF_EXTS:
        if data.startswith(b"\xef\xbb\xbf"):
            data = data[3:]
        data = data.replace(b"\r\n", b"\n")
    return data


def scan(root: Path) -> dict[str, Path]:
    out: dict[str, Path] = {}
    for p in root.rglob("*"):
        if not p.is_file():
            continue
        rel = p.relative_to(root).as_posix()
        if any(part in SKIP_DIRS for part in rel.split("/")):
            continue
        out[rel] = p
    return out


def diff(project: Path) -> tuple[list[str], list[str], list[str], dict[str, Path]]:
    src = scan(project)
    dst = scan(PACKAGE)
    added = sorted(set(src) - set(dst))
    removed = sorted(set(dst) - set(src))
    changed = sorted(
        rel for rel in set(src) & set(dst) if norm_bytes(src[rel]) != norm_bytes(dst[rel])
    )
    return added, changed, removed, src


def run(cmd: list[str], cwd: Path = REPO, check: bool = True) -> subprocess.CompletedProcess:
    print("$ " + " ".join(cmd), flush=True)
    return subprocess.run(cmd, cwd=cwd, check=check, text=True, encoding="utf-8", errors="replace")


def git(*args: str, check: bool = True) -> subprocess.CompletedProcess:
    return run(["git", *args], check=check)


def working_tree_clean() -> bool:
    res = subprocess.run(
        ["git", "status", "--porcelain"], cwd=REPO, text=True, capture_output=True, encoding="utf-8"
    )
    return res.returncode == 0 and res.stdout.strip() == ""


def apply_changes(added: list[str], changed: list[str], removed: list[str], src: dict[str, Path]) -> None:
    for rel in added + changed:
        target = PACKAGE / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        data = norm_bytes(src[rel])
        target.write_bytes(data)
        print(f"  wrote {rel}")
    for rel in removed:
        (PACKAGE / rel).unlink()
        print(f"  removed {rel}")


def load_manifest() -> dict:
    return json.loads((PACKAGE / "package.json").read_text(encoding="utf-8-sig"))


def main() -> int:
    ap = argparse.ArgumentParser(description="Sync Unity project package source and publish a VPM release.")
    ap.add_argument("--project", type=Path, default=DEFAULT_PROJECT, help="package folder inside the Unity project")
    ap.add_argument("--apply", action="store_true", help="copy detected changes into the repository")
    ap.add_argument("--allow-remove", action="store_true", help="also delete repo files missing from the project")
    ap.add_argument("--version", help="new version, e.g. 2.11.4 (implies --apply and full publish)")
    ap.add_argument("--notes", default="", help="release notes / commit suffix")
    ap.add_argument("--no-push", action="store_true", help="commit and tag locally, but do not push")
    args = ap.parse_args()

    project: Path = args.project
    if not (project / "package.json").is_file():
        print(f"error: no package.json under {project}", file=sys.stderr)
        return 2

    current = load_manifest()["version"]
    print(f"repo package : {PACKAGE}\n  version    : {current}")
    print(f"project      : {project}\n  version    : {json.loads((project / 'package.json').read_text(encoding='utf-8-sig'))['version']}")

    added, changed, removed, src = diff(project)
    print(f"\nsync status: {len(added)} new, {len(changed)} modified, {len(removed)} missing-in-project")
    for rel in added:
        print(f"  + {rel}")
    for rel in changed:
        print(f"  M {rel}")
    for rel in removed:
        print(f"  - {rel}")

    if args.version:
        args.apply = True

    if not args.apply:
        if not (added or changed or removed):
            print("\nnothing to publish: project matches the repository.")
        else:
            print("\ndry run only. Re-run with --apply (or --version X.Y.Z) to write these changes.")
        return 0

    if args.version:
        try:
            if tuple(int(x) for x in args.version.split(".")) <= tuple(int(x) for x in current.split(".")):
                print(f"error: {args.version} is not newer than {current}", file=sys.stderr)
                return 2
        except ValueError:
            print(f"error: bad version {args.version!r}", file=sys.stderr)
            return 2
        if not working_tree_clean():
            print("error: repository has uncommitted changes; commit or discard them first", file=sys.stderr)
            return 2

    if removed and not args.allow_remove:
        print("\nrefusing to delete files that exist only in the repository; pass --allow-remove if intended", file=sys.stderr)
        return 1

    print("\ncopying:")
    apply_changes(added, changed, removed, src)

    if not args.version:
        print("\ndone. Review with `git -C <repo> status`, then bump the version and build.")
        return 0

    manifest = load_manifest()
    manifest["version"] = args.version
    manifest["url"] = RELEASE_URL.format(v=args.version, n=manifest["name"])
    (PACKAGE / "package.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n"
    )
    print(f"\nversion -> {args.version}")

    print("\nbuilding release zip + listing:")
    run([sys.executable, "tools/build_release.py"])

    tag = f"v{args.version}"
    message = f"Release Zincat {args.version}" + (f": {args.notes}" if args.notes else "")
    git("add", "-A")
    git("commit", "-m", message)
    git("tag", tag)
    if args.no_push:
        print(f"\ncommitted and tagged {tag} locally (not pushed).")
        return 0
    git("push", "origin", "main")
    git("push", "origin", tag)
    print(f"\npushed. watch the release at https://github.com/SgZincanya/Zincat/actions")
    return 0


if __name__ == "__main__":
    sys.exit(main())
