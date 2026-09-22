#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
workflow="$repo_root/ops/gameserver/managed-profile.sh"
smoke_directory="$(mktemp -d)"

cleanup() {
    rm -rf -- "$smoke_directory"
}
trap cleanup EXIT

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

expect_failure() {
    local name="$1"
    shift
    if "$@" >"$smoke_directory/$name.out" 2>&1; then
        fail "Managed-profile case '$name' passed unexpectedly."
    fi
}

bash -n "$workflow"

plan_output="$smoke_directory/plan.out"
bash "$workflow" > "$plan_output"
for expected in \
    'verify the active plugin-free runtime and exact baseline public configuration' \
    'preserve an owner-only exact rollback copy' \
    'install the fixed public-classic-v1 rules and five-map rotation' \
    'restart once, require current-invocation load markers' \
    'on any failed transition, restore the same pre-profile baseline' \
    'PLAN_ONLY: no host state was inspected or changed'; do
    grep -Fq "$expected" "$plan_output" ||
        fail "The apply plan is missing '$expected'."
done

rollback_plan="$smoke_directory/rollback-plan.out"
bash "$workflow" --rollback > "$rollback_plan"
grep -Fq 'restore the exact pre-profile public configuration' "$rollback_plan" ||
    fail "The rollback plan does not describe exact restoration."
grep -Fq 'PLAN_ONLY: no host state was inspected or changed' "$rollback_plan" ||
    fail "The rollback plan is not explicitly non-mutating."

expect_failure unknown-option bash "$workflow" --profile arbitrary
expect_failure apply-without-root bash "$workflow" --apply

profile="$smoke_directory/profile.cfg"
mapcycle="$smoke_directory/mapcycle.txt"
baseline="$smoke_directory/server-public.cfg"
managed="$smoke_directory/server-public-managed.cfg"
cat > "$baseline" <<'EOF'
hostname "GoldSrcOps Controlled Baseline"
sv_lan "0"
sv_password ""
sv_rcon_condebug "0"
rcon_adduser 203.0.113.10/32
exec goldsrcops-private.cfg
echo "GoldSrcOps public runtime configuration loaded"
EOF

(
    # shellcheck source=/dev/null
    source "$workflow"
    render_profile "$profile"
    render_mapcycle "$mapcycle"
    render_managed_public_configuration "$baseline" "$managed"
)

cat > "$smoke_directory/profile.expected" <<'EOF'
hostname "GoldSrcOps Public Classic"
mp_timelimit "25"
mp_roundtime "2.5"
mp_freezetime "5"
mp_startmoney "800"
mp_friendlyfire "0"
mp_autoteambalance "1"
mp_limitteams "2"
mp_c4timer "35"
mp_buytime "0.5"
mapcyclefile "goldsrcops-mapcycle.txt"
echo "GoldSrcOps managed profile public-classic-v1 loaded"
EOF
cat > "$smoke_directory/mapcycle.expected" <<'EOF'
de_dust2
de_inferno
de_nuke
de_train
cs_office
EOF

cmp -s "$profile" "$smoke_directory/profile.expected" ||
    fail "The rendered profile changed unexpectedly."
cmp -s "$mapcycle" "$smoke_directory/mapcycle.expected" ||
    fail "The rendered map cycle changed unexpectedly."
grep -Fqx 'exec goldsrcops-managed-profile.cfg' "$managed" ||
    fail "The managed public configuration does not load the profile."
[[ "$(grep -Fxc 'exec goldsrcops-managed-profile.cfg' "$managed")" -eq 1 ]] ||
    fail "The managed public configuration loads the profile more than once."
grep -Fqx 'exec goldsrcops-private.cfg' "$managed" ||
    fail "The private credential load was not preserved."
! grep -Fq 'rcon_password' "$profile" ||
    fail "The managed profile contains a credential command."

runtime_enabled="$smoke_directory/runtime-enabled"
hash="0000000000000000000000000000000000000000000000000000000000000000"
(
    # shellcheck source=/dev/null
    source "$workflow"
    runtime_marker_sha256="$hash"
    service_unit_sha256="$hash"
    render_runtime_enabled_marker "$runtime_enabled" "$managed"
    read_runtime_enabled_marker "$runtime_enabled" "$managed"
    [[ "$runtime_marker_sha256" == "$hash" ]]
    [[ "$service_unit_sha256" == "$hash" ]]
)
cp -- "$managed" "$smoke_directory/server-public-tampered.cfg"
printf '%s\n' '// drift' >> "$smoke_directory/server-public-tampered.cfg"
# shellcheck disable=SC2016
expect_failure drifted-runtime-enabled bash -c \
    'source "$1"; read_runtime_enabled_marker "$2" "$3"' \
    _ "$workflow" "$runtime_enabled" "$smoke_directory/server-public-tampered.cfg"

reviewed_file="$smoke_directory/reviewed-runtime"
printf '%s\n' 'reviewed runtime bytes' > "$reviewed_file"
reviewed_file_sha256="$(sha256sum "$reviewed_file" | awk '{ print $1 }')"
(
    # shellcheck source=/dev/null
    source "$workflow"
    verify_file_sha256 "$reviewed_file" "$reviewed_file_sha256"
)
printf '%s\n' 'drift' >> "$reviewed_file"
# shellcheck disable=SC2016
expect_failure drifted-reviewed-runtime bash -c \
    'source "$1"; verify_file_sha256 "$2" "$3"' \
    _ "$workflow" "$reviewed_file" "$reviewed_file_sha256"

missing_marker="$smoke_directory/missing-marker.cfg"
printf '%s\n' 'exec goldsrcops-private.cfg' > "$missing_marker"
# shellcheck disable=SC2016
expect_failure missing-public-marker bash -c \
    'source "$1"; render_managed_public_configuration "$2" "$3"' \
    _ "$workflow" "$missing_marker" "$smoke_directory/invalid.cfg"

run_apply_body="$(sed -n '/^run_apply() {$/,/^}$/p' "$workflow")"
previous_line=0
# shellcheck disable=SC2016
for expected_call in \
    '    require_apply_environment' \
    '    acquire_transition_lock' \
    '    prepare_apply' \
    '    install_profile_files'; do
    current_line="$(grep -nFx "$expected_call" <<< "$run_apply_body" | cut -d: -f1)"
    [[ -n "$current_line" && "$current_line" -gt "$previous_line" ]] ||
        fail "The apply order is invalid at '$expected_call'."
    previous_line="$current_line"
done

run_rollback_body="$(sed -n '/^run_rollback() {$/,/^}$/p' "$workflow")"
grep -Fq '    acquire_transition_lock' <<< "$run_rollback_body" ||
    fail "Rollback is not serialized with apply."
grep -Fq 'flock --nonblock 9' "$workflow" ||
    fail "The workflow does not fail closed on a concurrent transition."
grep -Fq 'verify_file_sha256 "$RUNTIME_MARKER" "$runtime_marker_sha256"' "$workflow" ||
    fail "The workflow does not bind apply to the reviewed runtime marker."
grep -Fq 'verify_file_sha256 "$SYSTEMD_UNIT_FILE" "$service_unit_sha256"' "$workflow" ||
    fail "The workflow does not bind apply to the reviewed systemd unit."

grep -Fq "trap 'restore_baseline 129' HUP" "$workflow" ||
    fail "The workflow does not rollback after a lost controlling session."
grep -Fq 'systemctl stop "$SERVICE_NAME"' "$workflow" ||
    fail "The workflow does not stop the service before changing runtime files."
grep -Fq 'systemctl start "$SERVICE_NAME"' "$workflow" ||
    fail "The workflow does not restore the active service boundary."
grep -Fq 'ss -H -lun "sport = :$prepared_game_port"' "$workflow" ||
    fail "The workflow does not require the reviewed UDP listener."
grep -Fq '"$backup_directory/runtime-enabled"' "$workflow" ||
    fail "The workflow does not preserve and restore runtime-enabled."
if grep -Eq 'systemctl[[:space:]]+enable' "$workflow"; then
    fail "The workflow can enable the game service across boot."
fi
if grep -Eiq 'password[[:space:]]*=|rcon_password[[:space:]]+"|--secret|authorization:' \
    "$workflow"; then
    fail "The workflow contains a credential input or value."
fi

printf '%s\n' 'Game-server managed-profile smoke passed.'
