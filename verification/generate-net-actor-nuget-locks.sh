#!/usr/bin/env bash
# Run on a trusted developer or source CI runner with .NET 10.
# Generates real NuGet lockfiles, DOES NOT manufacture package hashes or
# commit anything. Review and commit both actual files before private mirroring.
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$repo_root"

managed="verification/Ores.Net.Tests/Ores.Net.Tests.csproj"
native="verification/Ores.Net.ActorMemoryProbe/Ores.Net.ActorMemoryProbe.csproj"
managed_lock="verification/Ores.Net.Tests/packages.lock.json"
native_lock="verification/Ores.Net.ActorMemoryProbe/packages.lock.json"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "NET_ACTOR_NUGET_LOCK_DENIED: dotnet 10 SDK unavailable" >&2
  exit 1
fi
dotnet --version
# Do not overwrite existing lockfile changes from another author. Only one
# reviewed source HEAD/tree may be used as the basis of a dependency receipt.
if test -n "$(git status --porcelain -- "$managed_lock" "$native_lock")"; then
  echo "NET_ACTOR_NUGET_LOCK_DENIED: existing lockfile changes require review" >&2
  exit 1
fi
# One online dependency resolution phase on a trusted SOURCE authoring host.
# This does not run in the cross-organization private-source carrier.
for project in "$managed" "$native"; do
  dotnet restore "$project" --use-lock-file --force-evaluate \
    --disable-parallel --source https://api.nuget.org/v3/index.json
done
for lock in "$managed_lock" "$native_lock"; do
  test -s "$lock" || {
    echo "NET_ACTOR_NUGET_LOCK_DENIED: missing generated $lock" >&2
    exit 1
  }
done

# The following are distinct LOCKED restores, not force-evaluation commands.
# They must fail if package versions drift relative to the generated files.
for project in "$managed" "$native"; do
  dotnet restore "$project" --locked-mode --disable-parallel \
    --source https://api.nuget.org/v3/index.json
done

python3 - "$managed_lock" "$native_lock" <<'PY'
import json
import pathlib
import sys

for path in sys.argv[1:]:
    raw = pathlib.Path(path).read_bytes()
    data = json.loads(raw.decode("utf-8"))
    if data.get("version") != 1 or not isinstance(data.get("dependencies"), dict):
        raise SystemExit("NET_ACTOR_NUGET_LOCK_DENIED: invalid generated NuGet schema")
    print("NET_ACTOR_NUGET_LOCK_GENERATED", path,
          "target_frameworks=" + ",".join(sorted(data["dependencies"])))
PY

# Git blob identities below must be copied from the actual generated source
# files and bound in the independent carrier review; do not reuse older SHA.
printf 'SOURCE_PARENT_COMMIT=%s\n' "$(git rev-parse HEAD)"
printf 'SOURCE_PARENT_TREE=%s\n' "$(git rev-parse 'HEAD^{tree}')"
for lock in "$managed_lock" "$native_lock"; do
  printf 'LOCK_GIT_BLOB=%s path=%s\n' "$(git hash-object "$lock")" "$lock"
  sha256sum "$lock"
done
echo "NET_ACTOR_NUGET_LOCK_BOOTSTRAP_PASS: review/commit BOTH generated locks; NO private-source CI proof"
