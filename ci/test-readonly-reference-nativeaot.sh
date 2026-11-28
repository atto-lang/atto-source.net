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
    <StartupObject>AttoReadonlyProbe</StartupObject>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
EOF
cp "$work/guest/Guest.csproj" "$work/overflow/Guest.csproj"
cat > "$work/guest/Probe.cs" <<'EOF'
public static class AttoReadonlyProbe {
    public static int Main() {
        try { System.Console.WriteLine(AttoReadonlyGuest.Run()); return 0; }
        catch (System.OverflowException) {
            System.Console.Error.WriteLine("ATTO_CHECKED_I64_OVERFLOW"); return 73;
        }
    }
}
EOF
cp "$work/guest/Probe.cs" "$work/overflow/Probe.cs"
compile "$work/guest.atto" "$work/guest/Guest.cs"
grep -F 'long r=p.f1;' "$work/guest/Guest.cs"
grep -F 'r=checked(r+p.f1);' "$work/guest/Guest.cs"
if grep -Eq 'private static long f[0-9]+\(|f[0-9]+\(p\)' "$work/guest/Guest.cs"; then exit 1; fi
test "$(dotnet run -c Release --project "$work/guest/Guest.csproj" -p:PublishAot=false)" = 42
dotnet publish "$work/guest/Guest.csproj" -c Release -r linux-x64 -p:PublishAot=true -o "$work/native"
test "$("$work/native/Guest")" = 42
sed 's/value = 21/value = 9223372036854775807/' "$work/guest.atto" > "$work/overflow.atto"
compile "$work/overflow.atto" "$work/overflow/Guest.cs"
dotnet publish "$work/overflow/Guest.csproj" -c Release -r linux-x64 -p:PublishAot=true -o "$work/native-overflow"
status=0
"$work/native-overflow/Guest" > "$work/overflow.log" 2>&1 || status=$?
test "$status" = 73
grep -Fx 'ATTO_CHECKED_I64_OVERFLOW' "$work/overflow.log"
for mode in cycle field computed; do
  case "$mode" in
    cycle) sed 's/return get(p);/return forward(p);/' "$work/guest.atto";;
    field) sed 's/return p.value;/return p.unknown;/' "$work/guest.atto";;
    computed) sed 's/return get(p);/return get(new Parcel());/' "$work/guest.atto";;
  esac > "$work/$mode.atto"
  if compile "$work/$mode.atto" "$work/reject.cs"; then exit 1; fi
  test ! -e "$work/reject.cs"
done
cat > "$work/order.atto" <<'EOF'
// atto-ownership: borrow-default-v1
define class Parcel as pub let int padding = 99; pub let int value = 57; end
fnc h0(Parcel p): int { return p.padding; }
fnc h1(Parcel p): int { return p.value; }
fnc h2(Parcel p): int { return h0(p); }
fnc h3(Parcel p): int { return h2(p); }
fnc h4(Parcel p): int { return h3(p); }
fnc h5(Parcel p): int { return h4(p); }
fnc h6(Parcel p): int { return h5(p); }
fnc h7(Parcel p): int { return h6(p); }
pub fnc main(): int { let p = new Parcel(); return h7(p) - h1(p); }
EOF
mkdir "$work/order"
cp "$work/guest/Guest.csproj" "$work/order/Guest.csproj"
cp "$work/guest/Probe.cs" "$work/order/Probe.cs"
compile "$work/order.atto" "$work/order/Guest.cs"
grep -F 'long r=p.f0;' "$work/order/Guest.cs"
grep -F 'r=checked(r-p.f1);' "$work/order/Guest.cs"
test "$(dotnet run -c Release --project "$work/order/Guest.csproj" -p:PublishAot=false)" = 42
dotnet publish "$work/order/Guest.csproj" -c Release -r linux-x64 -p:PublishAot=true -o "$work/native-order"
test "$("$work/native-order/Guest")" = 42
echo 'CLR_AND_NATIVEAOT_READONLY_PASS result=42 overflow_and_negative_controls=pass'
