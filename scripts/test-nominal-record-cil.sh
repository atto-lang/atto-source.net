#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj
mkdir -p artifacts
guest=artifacts/AttoGuest.dll
work="$(mktemp -d)"
trap 'rm -rf "$work"; rm -f "$guest"' EXIT
python3 - "$work" <<'PY'
import json,pathlib,sys
out=pathlib.Path(sys.argv[1])
data=json.loads(pathlib.Path('tests/fixtures/collection-subtypes-v1-candidate.json').read_text())
for c in data['cases']:
    if c['scenario'].startswith('R'):
        (out/(c['id']+'.atto')).write_text(c['source'])
PY
dotnet build "$compiler" -c Release
compile() { dotnet run --no-build -c Release --project "$compiler" -- "$1" "$2"; }
positive() {
  local name="$1" status=0
  rm -f "$guest"
  compile "$work/$name.atto" "$guest"
  test -s "$guest"
  dotnet build "$host" -c Release -t:Rebuild
  dotnet run --no-build -c Release --project "$host" || status=$?
  test "$status" = 42
  echo "PASS RECORD_CORECLR $name exit=42"
}
negative() {
  local name="$1" diagnostic="$2" status=0
  rm -f "$work/rejected.dll"
  compile "$work/$name.atto" "$work/rejected.dll" >"$work/stdout" 2>"$work/stderr" || status=$?
  test "$status" = 2
  grep -Fq "$diagnostic:" "$work/stderr"
  test ! -e "$work/rejected.dll"
  echo "PASS RECORD_REJECT $name $diagnostic"
}
positive record_custom
positive record_multilevel
# Verify metadata directly, without executing or reflecting over guest methods.
mkdir -p "$work/metadata"
cat >"$work/metadata/Metadata.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
</Project>
EOF
cat >"$work/metadata/Program.cs" <<'EOF'
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var md = pe.GetMetadataReader();
int privateMethods = 0, publicMain = 0;
foreach (var handle in md.MethodDefinitions)
{
    var method = md.GetMethodDefinition(handle);
    var name = md.GetString(method.Name);
    var access = method.Attributes & MethodAttributes.MemberAccessMask;
    if (name.StartsWith("_atto_rec_v1_method_", StringComparison.Ordinal))
    {
        if (access != MethodAttributes.Private) throw new Exception("generated Record method is not private");
        privateMethods++;
    }
    if (name == "main" && access == MethodAttributes.Public) publicMain++;
}
if (privateMethods != 2 || publicMain != 1) throw new Exception("unexpected method metadata");
Console.WriteLine("RECORD_CIL_METADATA_PASS private_helpers=2 public_main=1");
EOF
dotnet run --project "$work/metadata/Metadata.csproj" -c Release -- "$(realpath "$guest")"
if [[ "${ATTO_RECORD_NATIVEAOT:-0}" == 1 ]]; then
  dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o "$work/native"
  native_status=0
  "$work/native/Atto.Cil.Guest.Host" || native_status=$?
  test "$native_status" = 42
  echo 'RECORD_NATIVEAOT_PASS record_multilevel exit=42'
fi
negative record_field_shadow E_RECORD_SUBTYPE_FIELD
negative record_missing_field E_RECORD_SUBTYPE_SHAPE
# Exact string-typed case is still lexically unsupported: NOT semantic parity.
negative record_wrong_field_type E_LEX
person='define record Person extends Record as int age; pub add(int delta):int{return self.age+delta;} end'
printf '%s\n' "$person pub fnc main():int{const Person p=(age=20);return p.add(22);}" >"$work/arguments.atto"
positive arguments
for body in 'const Person p=(age=42);return p.add();' 'const Person p=(age=42);return p.add(1,2);'; do
  printf '%s\n' "$person pub fnc main():int{$body}" >"$work/arity.atto"
  negative arity E_RECORD_SUBTYPE_ARITY
done
printf '%s\n' "$person pub fnc main():int{const Person p=(age=true);return p.add(0);}" >"$work/bool.atto"
negative bool E_RECORD_SUBTYPE_BODY
printf '%s\n' "$person pub fnc main():int{const Person p=(age=42);return p.absent();}" >"$work/member.atto"
negative member E_RECORD_SUBTYPE_METHOD
printf '%s\n' "$person pub fnc main():int{return _atto_rec_v1_method_0(42,0);}" >"$work/generated.atto"
negative generated E_RECORD_SUBTYPE_NAME
echo 'RECORD_CIL_PASS exact_positive=2 exact_semantic_negative=2 exact_lexical_unsupported=1 extra_positive=1 extra_negative=5'
