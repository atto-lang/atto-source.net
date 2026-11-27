#!/usr/bin/env bash
# Compile real Atto to CIL, then execute the same guest on CoreCLR and NativeAOT.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
dotnet build "$compiler" -c Release
cat > "$tmp/Host.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors><IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>
  <ItemGroup><Reference Include="AttoGuest"><HintPath>AttoGuest.dll</HintPath><Private>true</Private></Reference></ItemGroup>
</Project>
EOF
cat > "$tmp/Program.cs" <<'EOF'
using System.Globalization;
try
{
    Console.WriteLine(AttoGenerated.Functions.main().ToString(CultureInfo.InvariantCulture));
    return 0;
}
catch (OverflowException) { Console.Error.WriteLine("E_OVERFLOW"); return 70; }
catch (InvalidOperationException e) when (e.Message == "E_STACK")
{ Console.Error.WriteLine("E_STACK"); return 75; }
EOF
fib() { printf 'pub fnc main(): int {return %s |> fnc fib(int v): int {^ match v over on 0 -> 0, on 1 -> 1, on _ -> fib(v-2)+fib(v-1) end };}\n' "$1"; }
count() { printf 'pub fnc main(): int {return %s |> fnc count(int v): int {^ match v over on 0 -> 0, on _ -> count(v-1)+1 end;};}\n' "$1"; }
compile() {
    rm -f "$tmp/AttoGuest.dll"
    dotnet run --no-build -c Release --project "$compiler" -- "$tmp/input.atto" "$tmp/AttoGuest.dll"
    test -s "$tmp/AttoGuest.dll"
}
expect() {
    local label="$1" want_status="$2" want_out="$3" want_err="$4"; shift 4
    local rc=0
    timeout 30s "$@" >"$tmp/out" 2>"$tmp/err" || rc=$?
    test "$rc" -eq "$want_status" || { echo "bad status $label: $rc" >&2; cat "$tmp/err" >&2; exit 1; }
    if [[ -n "$want_out" ]]; then printf '%s\n' "$want_out" >"$tmp/want"; else : >"$tmp/want"; fi
    cmp "$tmp/want" "$tmp/out"
    if [[ -n "$want_err" ]]; then printf '%s\n' "$want_err" >"$tmp/want"; else : >"$tmp/want"; fi
    cmp "$tmp/want" "$tmp/err"
    echo "PASS $label"
}
for spec in 'fib0:0:0:' 'fib1:0:1:' 'fib10:0:55:' 'fib20:0:6765:' 'count1024:0:1024:' 'count1025:75::E_STACK' 'overflow:70::E_OVERFLOW'; do
    IFS=: read -r id status value err <<<"$spec"
    case "$id" in
        fib*) fib "${id#fib}" >"$tmp/input.atto" ;;
        count*) count "${id#count}" >"$tmp/input.atto" ;;
        overflow) echo 'pub fnc main(): int {return 9223372036854775807 |> fnc f(int v): int {return v+1;};}' >"$tmp/input.atto" ;;
    esac
    compile
    dotnet build "$tmp/Host.csproj" -c Release -t:Rebuild
    expect "$id-CoreCLR" "$status" "$value" "$err" dotnet "$tmp/bin/Release/net8.0/Host.dll"
    # Publish each distinct guest; no stale binary may stand in for it.
    dotnet publish "$tmp/Host.csproj" -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o "$tmp/aot-$id"
    expect "$id-NativeAOT" "$status" "$value" "$err" "$tmp/aot-$id/Host"
done
cat >"$tmp/input.atto" <<'EOF'
pub fnc main(): int {
    return 55 |> fnc fib(int v): int {
        ^ match v over
            on 0 -> 0,
            on 1 -> 1,
            on _ -> fib(v - 2) + fib(v - 1)
        end
    };
}
EOF
test "$(git hash-object "$tmp/input.atto")" = ab60c8c4ad9726a526a4b0aa9ba76c0acca50b9d
compile
echo 'COMPILE_ONLY exact fib55'
while IFS='|' read -r code source; do
    printf '%s\n' "$source" >"$tmp/input.atto"
    rm -f "$tmp/AttoGuest.dll"
    rc=0
    dotnet run --no-build -c Release --project "$compiler" -- "$tmp/input.atto" "$tmp/AttoGuest.dll" >"$tmp/out" 2>"$tmp/err" || rc=$?
    test "$rc" -eq 2
    test ! -s "$tmp/out"
    grep -q "^$code:" "$tmp/err"
    test ! -e "$tmp/AttoGuest.dll" && test ! -L "$tmp/AttoGuest.dll"
    echo "PASS rejection $code"
done <<'EOF'
E_NAMED_STAGE_CLOSED_INT|pub fnc main(): int {return 1 |> fnc f(int v): int {return v+x;};}
E_NAMED_STAGE_CLOSED_INT|pub fnc main(): int {return 1 |> fnc f(int v): int {return f(v,v);};}
E_NAMED_STAGE_NAME|pub fnc main(): int {return 1 |> fnc f(int f): int {return f;};}
E_NAMED_STAGE_MATCH|pub fnc main(): int {return 1 |> fnc f(int v): int {^ match v over on 0 -> 0 end};}
E_NAMED_STAGE_MATCH|pub fnc main(): int {return 1 |> fnc f(int v): int {^ match v over on 0 -> 0, on 0 -> 1, on _ -> 2 end};}
E_NAMED_STAGE_TRAILING|pub fnc main(): int {return 1 |> fnc f(int v): int {return v;};} fnc x(): int {return 42;}
E_NAMED_STAGE_UPSTREAM|pub fnc main(): int {return 1+2 |> fnc f(int v): int {return v;};}
E_NAMED_STAGE_NESTED_PIPE|pub fnc main(): int {return 1 |> fnc f(int v): int {return v |> f;};}
E_CYCLE|pub fnc main(): int {return 42;} fnc f(int v): int {return f(v);}
EOF
