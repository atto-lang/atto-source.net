#!/usr/bin/env python3
"""Verify a clean exact-head checkout without invoking Git clean/smudge filters.

Source-local evidence preflight. This is NOT compiler/test execution evidence.
Only read tracked worktree bytes; use Git's staged tree and index metadata.
"""
import hashlib
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile


class IntegrityError(RuntimeError):
    pass


def git(repo: Path, *args: str) -> bytes:
    return subprocess.check_output(
        ["git", "-c", "core.fsmonitor=false", *args],
        cwd=repo, stderr=subprocess.PIPE)


def verify(repo: Path) -> int:
    repo = repo.resolve(strict=True)
    if git(repo, "rev-parse", "--show-toplevel").strip() != os.fsencode(str(repo)):
        raise IntegrityError("verification root must be the Git top-level checkout")
    expected_tree = git(repo, "rev-parse", "--verify", "HEAD^{tree}").strip()
    actual_tree = git(repo, "write-tree").strip()
    if expected_tree != actual_tree:
        raise IntegrityError("index tree differs from the exact HEAD tree")
    if git(repo, "ls-files", "--others", "-z"):
        raise IntegrityError("untracked worktree files (including ignored paths)")
    fmt = git(repo, "rev-parse", "--show-object-format").strip().decode("ascii")
    if fmt not in ("sha1", "sha256"):
        raise IntegrityError("unrecognized Git object format")
    tracked = git(repo, "ls-files", "--stage", "-z").split(b"\x00")
    count = 0
    seen = set()
    for entry in tracked:
        if not entry:
            continue
        header, delim, raw_path = entry.partition(b"\t")
        if not delim:
            raise IntegrityError("malformed index entry")
        fields = header.split()
        if len(fields) != 3 or fields[2] != b"0":
            raise IntegrityError("unmerged index entry")
        mode, expected, _stage = fields
        relative = Path(os.fsdecode(raw_path))
        if relative.is_absolute() or ".." in relative.parts or not relative.parts:
            raise IntegrityError("invalid tracked path")
        if raw_path in seen:
            raise IntegrityError("duplicate tracked path")
        seen.add(raw_path)
        current = repo
        for part in relative.parts[:-1]:
            current = current / part
            if current.is_symlink():
                raise IntegrityError("symlinked parent of tracked file")
        target = repo / relative
        try:
            metadata = target.lstat()
            if mode in (b"100644", b"100755"):
                if not stat.S_ISREG(metadata.st_mode):
                    raise IntegrityError("tracked regular file is not regular")
                # Windows checkout filesystems do not reliably expose Unix
                # executable permission bits through Python stat(). HEAD/index
                # mode identity is checked above via Git's exact tree; on POSIX
                # independently require the on-disk executable bit as well.
                if os.name != "nt" and (
                    bool(metadata.st_mode & stat.S_IXUSR) != (mode == b"100755")
                ):
                    raise IntegrityError("tracked executable mode differs from index")
                content = target.read_bytes()
            elif mode == b"120000":
                if not stat.S_ISLNK(metadata.st_mode):
                    raise IntegrityError("tracked symlink is not a symlink")
                content = os.fsencode(os.readlink(target))
            else:
                # Submodules and exotic modes need an independently specified checker.
                raise IntegrityError("unsupported tracked mode (including submodule)")
        except OSError as exc:
            raise IntegrityError("missing or unreadable tracked file") from exc
        hash_value = hashlib.new(fmt)
        hash_value.update(b"blob " + str(len(content)).encode("ascii") + b"\x00" + content)
        if hash_value.hexdigest().encode("ascii") != expected:
            raise IntegrityError("raw worktree blob differs from HEAD index")
        count += 1
    if not count:
        raise IntegrityError("empty tracked tree cannot certify source")
    return count


def self_test() -> None:
    with tempfile.TemporaryDirectory(prefix="raw-worktree-self-test-") as tmp:
        root = Path(tmp)
        git(root, "init", "-q")
        (root / ".gitattributes").write_text("example.txt filter=attack\n")
        (root / ".gitignore").write_text("generated/*.java\n")
        (root / "example.txt").write_text("safe\n")
        git(root, "add", ".gitattributes", ".gitignore", "example.txt")
        git(root, "-c", "user.name=Fixture", "-c",
            "user.email=fixture@example.invalid", "commit", "-qm", "initial")
        git(root, "config", "filter.attack.clean", "sh -c 'touch filter-executed; cat'")
        if verify(root) != 3 or (root / "filter-executed").exists():
            raise IntegrityError("clean control or filter nonexecution failed")
        (root / "example.txt").write_text("mutated\n")
        try:
            verify(root)
        except IntegrityError:
            pass
        else:
            raise IntegrityError("modified tracked file was accepted")
        (root / "example.txt").write_text("safe\n")
        (root / "untracked.txt").write_text("unknown")
        try:
            verify(root)
        except IntegrityError:
            pass
        else:
            raise IntegrityError("untracked file was accepted")
        (root / "untracked.txt").unlink()
        # An ignored source file must not pass as a clean exact-head checkout.
        ignored = root / "generated" / "Injected.java"
        ignored.parent.mkdir()
        ignored.write_text("class Injected {}\n")
        git(root, "check-ignore", "-q", "generated/Injected.java")
        try:
            verify(root)
        except IntegrityError:
            pass
        else:
            raise IntegrityError("ignored untracked source file was accepted")
        ignored.unlink()
        ignored.parent.rmdir()
        # Git's index executable bit must match the raw on-disk mode.
        (root / "example.txt").chmod(0o755)
        try:
            verify(root)
        except IntegrityError:
            pass
        else:
            raise IntegrityError("changed executable bit was accepted")
        (root / "example.txt").chmod(0o644)
        # Reset the test before the symlink case; no source is copied.
        (root / "example.txt").unlink()
        (root / "example.txt").symlink_to(".gitattributes")
        try:
            verify(root)
        except IntegrityError:
            pass
        else:
            raise IntegrityError("replaced tracked regular file was accepted")
        # An index-only staged addition changes HEAD^{tree} without changing
        # any existing tracked worktree contents. Fail before hashing blobs.
        # Deliberately do not run 'git reset' during cleanup: a configured
        # clean filter could run while Git refreshes unrelated index entries.
        staged = root / "staged.txt"
        staged.write_text("staged\n")
        git(root, "add", "staged.txt")
        try:
            verify(root)
        except IntegrityError:
            pass
        else:
            raise IntegrityError("staged index divergence was accepted")
        if (root / "filter-executed").exists():
            raise IntegrityError("Git clean filter executed during verification")
    print("RAW_WORKTREE_SELF_TEST_PASS clean=1 negative=6 filter_executed=0")


if __name__ == "__main__":
    try:
        if len(sys.argv) == 2 and sys.argv[1] == "--self-test":
            self_test()
        elif len(sys.argv) == 2:
            count = verify(Path(sys.argv[1]))
            print(f"RAW_WORKTREE_HEAD_MATCH tracked={count} filters=none")
        else:
            raise IntegrityError("usage: verify-raw-worktree.py <repo-root>|--self-test")
    except (IntegrityError, subprocess.CalledProcessError) as error:
        print(f"RAW_WORKTREE_NOT_VERIFIED: {error}", file=sys.stderr)
        sys.exit(2)
