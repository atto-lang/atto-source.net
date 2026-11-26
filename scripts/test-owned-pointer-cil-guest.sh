#!/usr/bin/env bash
# Real .atto -> direct CIL -> CoreCLR and Linux NativeAOT. No host oracle substitution.
set -euo pipefail
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj
guest=artifacts/AttoGuest.dll
mkdir -p artifacts
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
dotnet build "$compiler" -c Release
expect_42() {
  set +e
  "$@"
  local rc=$?
  set -e
  test "$rc" -eq 42
}
positive() {
  local name="$1" source="$2"
  printf '%s\n' "$source" >"$work/$name.atto"
  rm -f "$guest"
  dotnet run --no-build -c Release --project "$compiler" -- "$work/$name.atto" "$guest"
  test -s "$guest"
  dotnet build "$host" -c Release -t:Rebuild
  expect_42 dotnet run --no-build -c Release --project "$host"
  dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o "$work/$name-native"
  expect_42 "$work/$name-native/Atto.Cil.Guest.Host"
  echo "ATTO_OWNED_POINTER_GUEST_PASS=$name CoreCLR+NativeAOT"
}
positive singleton 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(1); p.write(21); val int first = p.read(); p.write(first * 2); val int answer = p.read(); p.close(); return answer; }'
positive initialized 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(21 * 2); val int answer = p.read(); p.close(); return answer; }'
positive metadata 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(4); p.write_at(3, 6); val int n = p.len(); val int b = p.size_bytes(); val int x = p.read_at(3); p.close(); return n + b + x; }'
positive fill 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(3); p.write_at(0, 13); p.fill(p.read_at(0) + 1); val int a = p.read_at(0); val int b = p.read_at(1); p.close(); return a + b + 14; }'
positive swap 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(2); p.write_at(0, 7); p.write_at(1, 35); p.swap_at(0, 1); val int a = p.read_at(0); val int b = p.read_at(1); p.close(); return a + b + (a - 35); }'
positive self_swap 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(42); p.swap_at(0, 0); val int a = p.read(); p.close(); return a; }'
helper='fnc inspect(Pointer<int> p): int { return p.read(); }'
positive implicit_borrow "$helper pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(21); val int a = inspect(p); p.write(21); val int b = inspect(p); p.close(); return a + b; }"
positive explicit_borrow "$helper pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(21); val int a = inspect(borrow! p); p.write(21); val int b = inspect(borrow! p); p.close(); return a + b; }"
positive borrow_observes_current_cell "$helper pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(13); val int a = inspect(p); p.write(29); val int b = inspect(borrow! p); p.close(); return a + b; }"
negative() {
  local name="$1" code="$2" source="$3"
  printf '%s\n' "$source" >"$work/$name.atto"
  rm -f "$work/rejected.dll"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- "$work/$name.atto" "$work/rejected.dll" >"$work/out" 2>"$work/err"
  local rc=$?
  set -e
  test "$rc" -eq 2
  grep -Fq "$code:" "$work/err"
  test ! -e "$work/rejected.dll"
  echo "ATTO_OWNED_POINTER_REJECT_PASS=$name"
}
negative uninitialized E_POINTER_INITIALIZE 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(1); val int x = p.read(); p.close(); return x; }'
negative bounds E_POINTER_BOUNDS 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(2); p.write(42); p.close(); return 0; }'
negative index E_POINTER_BOUNDS 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(2); p.write_at(2, 42); p.close(); return 0; }'
negative closed E_POINTER_LIFETIME 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(1); p.close(); val int x = p.len(); return x; }'
negative missing_close E_POINTER_LIFETIME 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(42); return 42; }'
negative self_initializer E_POINTER_LIFETIME 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(p.len()); p.close(); return 0; }'
negative overflow E_POINTER_OVERFLOW 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(9223372036854775807 + 1); p.close(); return 0; }'
negative double_close E_POINTER_LIFETIME 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(1); p.close(); p.close(); return 0; }'
negative fill_closed E_POINTER_LIFETIME 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(2); p.close(); p.fill(42); return 0; }'
negative fill_uninitialized E_POINTER_INITIALIZE 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(2); p.fill(p.read_at(0)); p.close(); return 0; }'
negative fill_overflow E_POINTER_OVERFLOW 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(2); p.fill(9223372036854775807 + 1); p.close(); return 0; }'
negative swap_uninitialized E_POINTER_INITIALIZE 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(2); p.write_at(0, 42); p.swap_at(0, 1); p.close(); return 0; }'
negative swap_bounds E_POINTER_BOUNDS 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(2); p.fill(42); p.swap_at(0, 2); p.close(); return 0; }'
negative swap_closed E_POINTER_LIFETIME 'pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(2); p.fill(42); p.close(); p.swap_at(0, 1); return 0; }'
negative borrow_uninitialized E_POINTER_INITIALIZE "$helper pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(1); val int a = inspect(p); p.close(); return a; }"
negative borrow_closed E_POINTER_LIFETIME "$helper pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(42); p.close(); return inspect(p); }"
negative borrow_wrong_owner E_POINTER_AUTHORITY "$helper pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(42); val int a = inspect(other); p.close(); return a; }"
negative borrow_wrong_extent E_POINTER_BOUNDS "$helper pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc<int>(2); p.fill(42); val int a = inspect(p); p.close(); return a; }"
negative borrow_bang_gap E_POINTER_SYNTAX "$helper pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(42); val int a = inspect(borrow ! p); p.close(); return a; }"
negative borrow_mut_not_admitted E_POINTER_AUTHORITY "$helper pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(42); val int a = inspect(borrow! mut p); p.close(); return a; }"
negative borrow_close E_POINTER_PROFILE 'fnc inspect(Pointer<int> p): int { p.close(); return 42; } pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(42); val int a = inspect(p); p.close(); return a; }'
negative borrow_escape E_POINTER_PROFILE 'fnc inspect(Pointer<int> p): int { return p; } pub fnc main(): int { let mut Pointer<int> p = Pointer.alloc_value<int>(42); val int a = inspect(p); p.close(); return a; }'
echo ATTO_OWNED_POINTER_CIL_NATIVEAOT_PASS
