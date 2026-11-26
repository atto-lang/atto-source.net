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

# Mutate ZIP structure without modifying the independently pinned source bytes.
valid = (root / "good.zip").read_bytes()
eocd = len(valid) - 22
assert valid[eocd:eocd+4] == b"PK\x05\x06"
central = int.from_bytes(valid[eocd+16:eocd+20], "little")
assert valid[central:central+4] == b"PK\x01\x02"
(root / "truncated.zip").write_bytes(valid[:central])
(root / "trailing.zip").write_bytes(valid + b"\x00")
for label, offset in (("central-name", central+46), ("central-offset", central+42),
                      ("central-crc", central+16), ("central-count", eocd+10)):
    changed = bytearray(valid)
    changed[offset] ^= 1
    (root / (label + ".zip")).write_bytes(changed)


# Independently re-pin each forbidden provider. These are valid authenticated
# packages, not broken hashes: denial must be at the compiler capability gate.
for label, provider in {
    "guard-provider": b"use namespace network; pub fnc answer(int x):int { guard x eq 40 { return 0; } return x + 2; }",
    "match-provider": b"use namespace network; pub fnc answer(int x):int { return match x over on _ -> 42; end; }",
    "sync-provider": b"use namespace network; pub fnc answer(int x):int { return sync task<int> { return x + 2; }; }",
    "suspends-provider": b"use namespace network; pub suspends fnc answer(int x):int { return x + 2; }",
    "trap-provider": b"use namespace network; pub fnc answer(int x):int { val int value = trap x; return value + 2; }",
}.items():
    provider_lock = ('[lock]\nmanifest_sha256 = "' + sha(manifest) + '"\n[files]\n"' +
                     path + '" = "' + sha(provider) + '"\n').encode()
    (root / (label + ".pin")).write_text(sha(provider_lock), encoding="ascii")
    with zipfile.ZipFile(root / (label + ".zip"), "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr(".atto.pkg.toml", manifest)
        z.writestr(".atto.pkg.lock", provider_lock)
        z.writestr(path, provider)
