# Shared executable guest oracle: source from both runtime harnesses.
guest_result_cases=(
    arithmetic:0:42 throw-identity:0:42 option-payload:0:42 callable-and-text-error:0:42 \
    match-error:0:7 match-guarded:0:42 match-void:0:42 match-i64:0:42 main-loop-shift-trap:0:42 guard-match-nonlocal:0:42 match-local-loop:0:42 \
    'panic-bypasses-trap:70:Oreslang panic' 'raise-bypasses-trap:70:raise bypasses trap' \
    'unhandled-division:70:Oreslang division by zero' 'unhandled-remainder:70:Oreslang division by zero'
)
guest_result_rejections=(
    'reject-trap-option:Local initializer has wrong type' \
    'reject-throw-int:throw requires Error' \
    'reject-match-value-return:MATCH_VOID_ARM' \
    'reject-match-nonexhaustive:MATCH_NON_EXHAUSTIVE' \
    'reject-match-unreachable:MATCH_UNREACHABLE_ARM' \
    'reject-propagation-mismatch:Postfix ? requires an enclosing callable' \
    'reject-match-loop-transfer:E_MATCH_LOOP_TRANSFER_UNSUPPORTED' \
    'reject-match-loop-continue:E_MATCH_LOOP_TRANSFER_UNSUPPORTED' \
    'reject-guard-imperative-match:E_GUARD'
)
assert_guest_result() {
  local name="$1" expected_status="$2" expected_text="$3" status="$4" output="$5"
  if [[ "$status" != "$expected_status" ]]; then
    cat "$output/stdout" "$output/stderr" >&2
    echo "Unexpected guest exit $status for $name (expected $expected_status)" >&2
    return 1
  fi
  if [[ "$expected_status" == 0 ]]; then
    [[ "$(cat "$output/stdout")" == "$expected_text" && ! -s "$output/stderr" ]]
  else
    [[ ! -s "$output/stdout" ]] && grep -Fq "$expected_text" "$output/stderr"
  fi
}
