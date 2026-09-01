#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
installer="$repo_root/ops/gameserver/runtime-install.sh"
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
        fail "Game-runtime installer case '$name' passed unexpectedly."
    fi
    printf "Game-runtime installer case '%s' failed as expected.\n" "$name"
}

write_marker() {
    local path="$1"
    cat > "$path" <<'EOF'
schema_version=1
operator_user=gsoadmin
service_user=goldsrc
ssh_port=22
game_port=27015
EOF
}

bash -n "$installer"

plan_output="$smoke_directory/plan.out"
bash "$installer" > "$plan_output"
for expected in \
    'require the reviewed game-host foundation and an empty runtime target' \
    'cebf0046bfd08cf45da6bc094ae47aa39ebf4155e5ede41373b579b8f1071e7c' \
    'HLDS app 90 from pinned branch steam_legacy' \
    'ReHLDS 3.15.0.896 SHA-256 and detached signature' \
    'ReGameDLL_CS 5.30.0.814 SHA-256 and detached signature' \
    'disabled-by-default goldsrcops-gameserver.service' \
    'leave RCON secrets, runtime activation, service start, and public UDP unchanged' \
    'PLAN_ONLY: no host changes were made'; do
    grep -Fq "$expected" "$plan_output" ||
        fail "Game-runtime plan is missing '$expected'."
done

if grep -Eiq 'password|token|203\.0\.113\.|198\.51\.100\.' "$plan_output"; then
    fail "Game-runtime plan exposed a secret-shaped or host-specific value."
fi

stdin_plan_output="$smoke_directory/stdin-plan.out"
bash -s -- < "$installer" > "$stdin_plan_output"
grep -Fq 'PLAN_ONLY: no host changes were made' "$stdin_plan_output" ||
    fail "Game-runtime installer did not execute its plan when read from stdin."

expect_failure root-service bash "$installer" --service-user root
expect_failure invalid-port bash "$installer" --game-port 70000
expect_failure unknown-option bash "$installer" --replace
expect_failure non-root-apply bash "$installer" --apply

valid_marker="$smoke_directory/host-prepared"
write_marker "$valid_marker"
marker_result="$smoke_directory/marker-result.out"
(
    # shellcheck source=/dev/null
    source "$installer"
    read_prepared_marker "$valid_marker"
    # Values are assigned by the sourced installer's strict marker parser.
    # shellcheck disable=SC2154
    printf '%s\n' \
        "$prepared_operator_user" \
        "$prepared_service_user" \
        "$prepared_ssh_port" \
        "$prepared_game_port"
) > "$marker_result"
cat > "$smoke_directory/marker-expected.out" <<'EOF'
gsoadmin
goldsrc
22
27015
EOF
cmp -s "$marker_result" "$smoke_directory/marker-expected.out" ||
    fail "Game-runtime installer parsed the host marker incorrectly."

duplicate_marker="$smoke_directory/host-prepared-duplicate"
write_marker "$duplicate_marker"
printf 'game_port=27015\n' >> "$duplicate_marker"
# shellcheck disable=SC2016
expect_failure duplicate-marker bash -c \
    'source "$1"; read_prepared_marker "$2"' \
    _ "$installer" "$duplicate_marker"

unknown_marker="$smoke_directory/host-prepared-unknown"
write_marker "$unknown_marker"
printf 'provider_id=forbidden\n' >> "$unknown_marker"
# shellcheck disable=SC2016
expect_failure unknown-marker-key bash -c \
    'source "$1"; read_prepared_marker "$2"' \
    _ "$installer" "$unknown_marker"

mismatched_marker="$smoke_directory/host-prepared-mismatch"
sed 's/service_user=goldsrc/service_user=gameserver/' "$valid_marker" > "$mismatched_marker"
# shellcheck disable=SC2016
expect_failure marker-service-mismatch bash -c \
    'source "$1"; read_prepared_marker "$2"' \
    _ "$installer" "$mismatched_marker"

checksum_fixture="$smoke_directory/checksum-fixture"
printf 'verified runtime fixture\n' > "$checksum_fixture"
checksum="$(sha256sum "$checksum_fixture" | awk '{ print $1 }')"
(
    # shellcheck source=/dev/null
    source "$installer"
    verify_sha256 "$checksum_fixture" "$checksum"
)
# shellcheck disable=SC2016
expect_failure checksum-mismatch bash -c \
    'source "$1"; verify_sha256 "$2" "$3"' \
    _ "$installer" "$checksum_fixture" \
    0000000000000000000000000000000000000000000000000000000000000000

runtime_mode_root="$smoke_directory/runtime-mode"
mkdir -p "$runtime_mode_root/steamcmd" "$runtime_mode_root/server"
chmod 0755 "$runtime_mode_root/steamcmd" "$runtime_mode_root/server"
# shellcheck disable=SC2016
bash -c \
    'source "$1"; staging_root="$2"; normalize_runtime_directory_modes' \
    _ "$installer" "$runtime_mode_root"
[[ "$(stat -c '%a' "$runtime_mode_root/steamcmd")" == 750 ]] ||
    fail "The SteamCMD root mode was not normalized after installation."
[[ "$(stat -c '%a' "$runtime_mode_root/server")" == 750 ]] ||
    fail "The game-server root mode was not normalized after installation."

key_file="$smoke_directory/rehlds-signing-key.asc"
key_home="$smoke_directory/gnupg"
mkdir "$key_home"
chmod 0700 "$key_home" 2>/dev/null || true
(
    # shellcheck source=/dev/null
    source "$installer"
    write_rehlds_signing_key "$key_file"
)
fingerprint="$(GNUPGHOME="$key_home" gpg \
    --batch \
    --with-colons \
    --import-options show-only \
    --import "$key_file" 2>/dev/null |
    awk -F: '$1 == "fpr" { print $10; exit }')"
[[ "$fingerprint" == '63547829004F07716F7BE4856C32C4282E60FB67' ]] ||
    fail "The embedded ReHLDS signing key fingerprint changed."

unit_file="$smoke_directory/goldsrcops-gameserver.service"
(
    # shellcheck source=/dev/null
    source "$installer"
    # Values are consumed by the sourced unit renderer.
    # shellcheck disable=SC2034
    service_group=goldsrc
    # shellcheck disable=SC2034
    service_home=/var/lib/goldsrc
    render_service_unit "$unit_file"
)
for expected in \
    'User=goldsrc' \
    'Group=goldsrc' \
    'ConditionPathExists=/etc/goldsrcops/gameserver/runtime-enabled' \
    'ConditionPathExists=/etc/goldsrcops/gameserver/secrets/server-private.cfg' \
    'LoadCredential=server-private.cfg:/etc/goldsrcops/gameserver/secrets/server-private.cfg' \
    'ExecStart=/var/lib/goldsrc/server/hlds_run -game cstrike -console -strictportbind -ip 0.0.0.0 -port 27015 +servercfgfile goldsrcops-public.cfg +maxplayers 4 +map de_dust2' \
    'Restart=on-failure' \
    'MemoryMax=1G' \
    'NoNewPrivileges=true' \
    'CapabilityBoundingSet=' \
    'PrivateDevices=true' \
    'ProtectProc=invisible' \
    'ProcSubset=all' \
    'ProtectSystem=strict' \
    'ProtectHome=true' \
    'RestrictNamespaces=true' \
    'InaccessiblePaths=/opt/goldsrcops/gameserver/artifacts /var/backups/goldsrcops/gameserver'; do
    grep -Fxq "$expected" "$unit_file" ||
        fail "Game-runtime unit is missing '$expected'."
done

if grep -Eiq 'rcon_password|Environment=.*password|EnvironmentFile' "$unit_file"; then
    fail "Game-runtime unit contains an unsafe secret transport."
fi
if grep -Fxq 'ProcSubset=pid' "$unit_file"; then
    fail "Game-runtime unit hides /proc APIs required by ReHLDS."
fi

run_apply_body="$(sed -n '/^run_apply() {$/,/^}$/p' "$installer")"
previous_line=0
for expected_call in \
    '    require_apply_environment' \
    '    prepare_staging' \
    '    acquire_and_verify_artifacts' \
    '    install_steamcmd' \
    '    install_hlds' \
    '    apply_runtime_overlays' \
    '    validate_runtime_tree' \
    '    promote_runtime' \
    '    install_service_unit' \
    '    write_runtime_marker'; do
    current_line="$(grep -nFx "$expected_call" <<< "$run_apply_body" | cut -d: -f1)"
    [[ -n "$current_line" && "$current_line" -gt "$previous_line" ]] ||
        fail "Game-runtime apply order is invalid at '$expected_call'."
    previous_line="$current_line"
done

if grep -Eq 'systemctl[[:space:]]+(enable|start|restart)' "$installer"; then
    fail "Game-runtime installer must not enable or start the server."
fi
if grep -Eq '/releases/(latest|download/latest)|:[[:space:]]*latest' "$installer"; then
    fail "Game-runtime installer contains a mutable latest reference."
fi

printf 'Game-server runtime installer smoke test passed.\n'
