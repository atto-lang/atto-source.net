#!/usr/bin/env bash
set -euo pipefail
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
compiler=compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj
dotnet build -c Release "$compiler" -p:PublishAot=false -p:SelfContained=false
compile() {
  dotnet run --no-build -c Release --project "$compiler" -p:PublishAot=false -p:SelfContained=false -- emit-readonly-reference-csharp "$1" --out "$2"
}
cat > "$work/guest.atto" <<'EOF'
// atto-ownership: borrow-default-v1
define class Parcel as pub let int padding = 99; pub let int value = 21; end
fnc get(Parcel p): int { return p.value; }
fnc forward(Parcel p): int { return get(p); }
pub fnc main(): int { let p = new Parcel(); return forward(p) + get(p); }
EOF
mkdir "$work/guest" "$work/overflow"
cat > "$work/guest/Guest.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>Guest</AssemblyName>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
EOF
cp "$work/guest/Guest.csproj" "$work/overflow/Guest.csproj"
compile "$work/guest.atto" "$work/guest/Guest.cs"
grep -F 'private static long f1(Obj p){return f0(p);}' "$work/guest/Guest.cs"
grep -F 'private static long f0(Obj p){return p.f1;}' "$work/guest/Guest.cs"
test "$(dotnet run -c Release --project "$work/guest/Guest.csproj" -p:PublishAot=false)" = 42
dotnet publish "$work/guest/Guest.csproj" -c Release -r linux-x64 -p:PublishAot=true -o "$work/native"
test "$("$work/native/Guest")" = 42
sed 's/value = 21/value = 9223372036854775807/' "$work/guest.atto" > "$work/overflow.atto"
compile "$work/overflow.atto" "$work/overflow/Guest.cs"
dotnet publish "$work/overflow/Guest.csproj" -c Release -r linux-x64 -p:PublishAot=true -o "$work/native-overflow"
if (ulimit -c 0; "$work/native-overflow") > "$work/overflow.log" 2>&1; then exit 1; fi
grep -F 'OverflowException' "$work/overflow.log"
for mode in cycle field computed; do
  case "$mode" in
    cycle) sed 's/return get(p);/return forward(p);/' "$work/guest.atto";;
    field) sed 's/return p.value;/return p.unknown;/' "$work/guest.atto";;
    computed) sed 's/return get(p);/return get(new Parcel());/' "$work/guest.atto";;
  esac > "$work/$mode.atto"
  if compile "$work/$mode.atto" "$work/reject.cs"; then exit 1; fi
  test ! -e "$work/reject.cs"
done
echo 'CLR_AND_NATIVEAOT_READONLY_PASS result=42 overflow_and_negative_controls=pass'
