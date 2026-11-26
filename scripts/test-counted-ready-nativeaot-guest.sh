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
test "$(git hash-object tests/fixtures/counted-ready-v1/counted-alias.atto)" = 324837078af3e7eeb9b619d0fb07142f0a0d6291
test "$(git hash-object tests/fixtures/counted-ready-v1/counted-dynamic.atto)" = 7c9ea9270d766612de0d502147e0df31b0dc3454
test "$(git hash-object tests/fixtures/counted-ready-v1/counted-dynamic-zero.atto)" = 4707b13d3daebe05ced967418ba120cf801e2d8c
test "$(git hash-object tests/fixtures/counted-ready-v1/counted-distinct-alias-projection.atto)" = 5baca9bb9eb4b98500cab770888a45fb60bbf10d
for name in counted-distinct-alias-projection counted-mixed-future-alias counted-multi-channel-fair counted-mixed-zero counted-three counted-zero counted-one counted-alias counted-dynamic counted-dynamic-zero; do
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
# Dynamic invalid count and exceptional exit must terminate with the right error.
for case in invalid-count overflow-before-await empty-read; do
  case "$case" in
    invalid-count) body='val int count = 17; val Array[int] b = await select<int>(count) { when readch a: int n -> { return n; } }; return b[0];'; expected='ArgumentOutOfRangeException';;
    overflow-before-await) body='writech a, 42; val Future<Array<int>> pending = select<int>(1) { when readch a: int n -> { return n; } }; val int x = 9223372036854775807 + 1; val Array[int] b = await pending; return b[0];'; expected='OverflowException';;
    empty-read) body='val Array[int] b = await select<int>(0) { when readch a: int n -> { return n; } }; return readch a;'; expected='InvalidOperationException';;
  esac
  printf 'suspends fnc main(): int { val Channel<int> a = Channel.new<int>(1); %s }\n' "$body" > "$work/$case.atto"
  class="$(dotnet "$work/compiler/Atto.Cil.Compiler.dll" --counted-ready-v1 "$work/$case.atto" "$work/guest/$case.cs")"
  echo "try { $class.Run(); throw new Exception(\"$case unexpectedly succeeded\"); } catch ($expected) { Console.WriteLine(\"NEGATIVE_RUNTIME_PASS $case\"); }" >> "$work/guest/Program.cs"
done
dotnet publish "$work/guest/Guest.csproj" -c Release -r linux-x64 -o "$work/native" -warnaserror
file "$work/native/Guest" | grep -F ELF
"$work/native/Guest" | tee "$work/results"
test "$(grep -c '^GUEST_PASS ' "$work/results")" = 10
echo COUNTED_READY_NATIVEAOT_GUEST_PASS
