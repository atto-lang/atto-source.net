"""Generate test-owned trust pins and transport fixtures; never used by compiler."""
import hashlib
import pathlib
import sys
import zipfile

root = pathlib.Path(sys.argv[1])
root.mkdir(parents=True, exist_ok=True)
name = "@org/project/package"
path = "src/network.atto"
manifest = ('[package]\nname = "' + name + '"\nversion = "1.0.0"\n'
            '[namespaces]\nnetwork = "' + path + '"\n').encode()
source = b"use namespace network; pub fnc answer(int x):int { return x + 2; }"
sha = lambda data: hashlib.sha256(data).hexdigest()
lock = ('[lock]\nmanifest_sha256 = "' + sha(manifest) + '"\n[files]\n"' +
        path + '" = "' + sha(source) + '"\n').encode()
(root / "pin").write_text(sha(lock), encoding="ascii")
(root / "main.atto").write_text("import pkg '" + name +
    "' as p; pub fnc main()->int{return p.network.answer(40);}", encoding="utf-8")
(root / "missing.atto").write_text("import pkg '" + name +
    "' as p; pub fnc main()->int{return p.network.missing(40);}", encoding="utf-8")

def archive(filename, provider=source, extra=None, include_lock=True):
    with zipfile.ZipFile(root / filename, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr(".atto.pkg.toml", manifest)
        if include_lock:
            z.writestr(".atto.pkg.lock", lock)
        z.writestr(path, provider)
        if extra:
            z.writestr(*extra)

archive("good.zip")
archive("tampered.zip", source.replace(b"+ 2", b"+ 3"))
archive("traversal.zip", extra=("../evil.atto", b"bad"))
archive("extra.zip", extra=("extra.atto", b"bad"))
archive("duplicate.zip", extra=(path, source))
archive("no-lock.zip", include_lock=False)
archive("oversized.zip", extra=("big.atto", b"x" * 1048577))
