#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
dotnet build compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj -c Release -o "$work/compiler" -warnaserror
mkdir "$work/guest"
cat > "$work/guest/Guest.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><PublishAot>true</PublishAot><SelfContained>true</SelfContained><InvariantGlobalization>true</InvariantGlobalization><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
<ItemGroup><ProjectReference Include="$root/runtime/Ores.Net.Runtime/Ores.Net.Runtime.csproj" /></ItemGroup>
<Import Project="$root/build/StrictNativeAot.targets" />
</Project>
EOF
echo 'using System;' > "$work/guest/Program.cs"
test "$(git hash-object tests/fixtures/counted-ready-v1/counted-mixed-future-alias.atto)" = 8f57de03c9fb44eb5594c99a7095f34b702a266a
test "$(git hash-object tests/fixtures/counted-ready-v1/counted-multi-channel-fair.atto)" = 4b9b79b9b8670f5b282d33e63b7a4b5691c2979a
test "$(git hash-object tests/fixtures/counted-ready-v1/counted-mixed-zero.atto)" = 458323db6019da434b97e5b966ee646411310acf
test "$(git hash-object tests/fixtures/counted-ready-v1/counted-three.atto)" = 6531efa0689f2a5728b40a6412651e8143e45865
test "$(git hash-object tests/fixtures/counted-ready-v1/counted-zero.atto)" = 42e49fc1eea64b31e86ec722e1457c1ab706a7e1
test "$(git hash-object tests/fixtures/counted-ready-v1/counted-one.atto)" = 910062ebf96613d15a16ddb080abd89375d7709c
test "$(git hash-object tests/fixtures/counted-ready-v1/reject-counted-negative.atto)" = e2ff6fdb406b8807b74fa15beaee10a7f9438e22
test "$(git hash-object tests/fixtures/counted-ready-v1/reject-counted-bool.atto)" = 4a52c22442bc8a9fe55eb95b23722595db32c97e
test "$(git hash-object tests/fixtures/counted-ready-v1/reject-counted-scalar.atto)" = a2a2dd184237f17b5e8d455f498719ef1ed01d97
test "$(git hash-object tests/fixtures/counted-ready-v1/reject-counted-default.atto)" = 0c67c1fc8eaf55df6abb2b0fe7d2ce80f169bc69
test "$(git hash-object tests/fixtures/counted-ready-v1/reject-counted-write.atto)" = 63fa3c9add56f3793e6d755f006cd3568acf1f04
test "$(git hash-object tests/fixtures/counted-ready-v1/reject-counted-dynamic-bool.atto)" = 36cd372a5385e34f2e3bd8dd936e4e0491052633
test "$(git hash-object tests/fixtures/counted-ready-v1/reject-counted-dynamic-unknown.atto)" = 0e61da7a98987ed5a182148538b35b8a890abf26
for name in counted-mixed-future-alias counted-multi-channel-fair counted-mixed-zero counted-three counted-zero counted-one; do
  class="$(dotnet "$work/compiler/Atto.Cil.Compiler.dll" --counted-ready-v1 "tests/fixtures/counted-ready-v1/$name.atto" "$work/guest/$name.cs")"
  [[ "$class" =~ ^Guest_[a-f0-9]{64}$ ]]
  echo "if ($class.Run() != 42L) throw new Exception(\"$name failed\"); Console.WriteLine(\"GUEST_PASS $name\");" >> "$work/guest/Program.cs"
done
for name in reject-counted-negative reject-counted-bool reject-counted-scalar reject-counted-default reject-counted-write reject-counted-dynamic-bool reject-counted-dynamic-unknown; do
  if dotnet "$work/compiler/Atto.Cil.Compiler.dll" --counted-ready-v1 "tests/fixtures/counted-ready-v1/$name.atto" "$work/guest/bad.cs" > "$work/out" 2> "$work/err"; then
    echo "unexpected acceptance: $name"; exit 1
  fi
  test ! -e "$work/guest/bad.cs"
  grep -F E_COUNTED_READY_PROFILE "$work/err"
done
dotnet publish "$work/guest/Guest.csproj" -c Release -r linux-x64 -o "$work/native" -warnaserror
file "$work/native/Guest" | grep -F ELF
"$work/native/Guest" | tee "$work/results"
test "$(grep -c '^GUEST_PASS ' "$work/results")" = 6
echo COUNTED_READY_NATIVEAOT_GUEST_PASS
