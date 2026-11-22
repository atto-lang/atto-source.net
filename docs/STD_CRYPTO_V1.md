# std.crypto — portable encryption and cryptographic services, v1

## Status

This repository owns its own `stdlib/std/crypto.atto` source. The `.atto` functions in this first slice are **pure admission policy**,
not a complete guest-callable encryption implementation.
A module path of `"std.crypto"` is the intended core import (never
`pkg "std.crypto"`), but **the import resolver and host crypto bindings still
need implementation and an executed compiler-backed test**. Do not promote
the fixture or this PR as proof of functioning encryption.

## .NET trusted-host AEAD prototype in this branch

`runtime/Ores.Net.Runtime/CryptoAeadHostV1.cs` now provides managed,
full-AOT-compatible **host-only** AES-256-GCM and IETF ChaCha20-Poly1305
using `System.Security.Cryptography`. It uses fresh 32-byte CSPRNG keys,
per-key random 4-byte prefix plus 64-bit nonce counter (max **2^32**
encryptions/key), 96-bit nonces, full 128-bit tags, defensive ciphertext
clones, and deterministic `Dispose()` key zeroization. Decrypt reports
`false` with no plaintext on malformed input or failed authentication.
An unsupported algorithm fails closed via `IsSupported`; there is no
silent fallback to a weaker or custom cipher.

`tests/CryptoAeadHostV1` is a source-owned executable test project
with host round trips, nonces, tampering, wrong key/AAD/nonce, empty
messages and key disposal checks. It explicitly exits nonzero if either
algorithm is unavailable; a skipped algorithm is **not** parity.

**Status:** .NET host primitive is not yet guest-callable. Its caller must
authenticate context and actor generation, enforce a `CRYPTO` capability,
preserve move-only `AeadKey` and zeroizing `SecretBytes` types, and
enforce per-tenant/actor memory limits. Unlike the Java branch, there is
not yet an actor-owned key registry or `Result<T,CryptoError>` guest
binding here. `dotnet` is not present in the current isolated test
environment; **no .NET compilation, test execution, NativeAOT, or
cross-backend guest interoperability was verified.** Keep this PR Draft.

To test on a trustworthy admitted .NET 10 runner:

```sh
dotnet run --project tests/CryptoAeadHostV1/CryptoAeadHostV1.csproj -c Release
dotnet publish tests/CryptoAeadHostV1/CryptoAeadHostV1.csproj -c Release -r linux-x64
```

The second command builds an artifact; it is not by itself evidence that
a published native executable passed tests. Execute the resulting binary
and record exact-head logs separately.

## Public API to implement

- `hash.sha256(bytes) -> Bytes` and `mac.hmac_sha256(secret, bytes) -> Bytes`.
- `kdf.hkdf_sha256(ikm, salt, info, output_bytes) -> SecretBytes`.
- `kdf.pbkdf2_hmac_sha256(password_bytes, salt, iterations, output_bytes)`
  for bounded protocol compatibility only; Argon2id is a later audited
  password-storage service, not a substitute for HKDF.
- `random.secure_bytes(n) -> SecretBytes` and `random.key_256() -> SecretKey`.
- `aead.aes_256_gcm.seal(key, aad, plaintext) -> Sealed{nonce,ciphertext}` and
  `aead.aes_256_gcm.open(key, nonce, aad, ciphertext) -> Result<Bytes, CryptoError>`.
  Sealing reserves a nonce through the opaque key handle; callers do not
  choose a nonce for managed encryption.
- `aead.chacha20_poly1305.seal/open(...)` with equivalent authenticated
  semantics where vetted support exists.
- `constant_time.equals(left, right) -> bool` implemented by backend code,
  not a guest-language early-return byte loop.

These are **target API signatures**, not runnable exports in this first PR.
The current usable pure functions are the named predicates in `crypto.atto`.

## Wire and security contract

Algorithm ids are 1 = AES-256-GCM and 2 = IETF ChaCha20-Poly1305.
Both require 32-byte keys, **unique 12-byte nonces for each key**, and
**full 16-byte authentication tags**. AAD is authenticated but not encrypted.
Ciphertext size in this proposal is plaintext length + 16 tag bytes
(without any nonce or envelope prefix); any future envelope uses an explicit
version, algorithm, nonce, and lengths, without silently altering this shape.
Do not expose an unauthenticated cipher as an encryption convenience API.

Use an internal nonce manager with per-key counters and crash-safe
reservation, or another reviewed nonce-generation strategy. A random nonce
by itself is not an unlimited uniqueness guarantee. Never auto-generate a
nonce with an untrusted/noncryptographic RNG or let untrusted code seed the
CSPRNG. Keys are opaque, move-only or sealed handles with no accidental
serialization/logging and explicit zeroization on retirement where possible.
No secret-dependent branching or timing guarantees are made by policy
predicates; trusted backends must supply those guarantees.

Open authenticates **before** returning any plaintext. Tampering, unknown
key, bad tag and wrong AAD produce the same externally observable
authentication failure. All error paths drop buffers and wipe secret
temporary memory where possible. Enforce permission/effect and memory
quotas again in the host; validation functions are advisory and cannot
authorize a resource operation.

Per-call budgets: 16 MiB message, 64 KiB AAD, 1 MiB HMAC key/IKM,
HKDF-SHA-256 output ≤ 8,160 bytes (RFC 5869), bounded PBKDF2 work
≤ 100,000 rounds **for protocol compatibility**, entropy requests ≤ 4,096
bytes. Higher-cost password storage must use a separate async/memory-hard
and explicitly budgeted API.

## Implementation ownership and rollout

1. Keep this compiler's `.atto` code local. Separate compilers may use
   different algorithms for policy, memory layout and host library bindings
   while preserving public protocol behavior and test vectors.
2. Bind import `"std.crypto"` and guest types to the compiler; map
   trusted primitives to platform vetted implementations. Java can reuse
   its existing SHA/HMAC/PBKDF2 draft PR #1367 (where applicable);
   other backends need independently authenticated implementations.
3. Add AES-256-GCM and ChaCha20-Poly1305 behind capabilities and
   ownership-safe key/nonce types. Do not implement AES/GCM/Poly1305
   manually in a new guest loop as a stopgap for vetted primitives.
4. Execute `tests/fixtures/crypto_policy_v1.atto` with this compiler
   after importer integration, then run NIST/RFC known-answer vectors,
   tamper/wrong-key/wrong-AAD tests, random quality integration checks,
   arena scrub/lifetime tests, cancellation and zero-GC/AOT admission.
   Record the exact head SHA and steps; source-only checks and
   zero-step Actions are NOT_EXECUTED for guest conformance.

References: RFC 8439 (ChaCha20-Poly1305), RFC 5869 (HKDF),
NIST SP 800-38D (AES-GCM). NIST's current 38D revision work does
not justify weakening nonce-uniqueness or tag requirements.
