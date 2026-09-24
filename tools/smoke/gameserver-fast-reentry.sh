#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
workflow="$repo_root/ops/gameserver/game-event-persistent.sh"
profile="$repo_root/ops/gameserver/fast-reentry-v1.cfg"
fixture="$(mktemp -d)"
trap 'rm -rf -- "$fixture"' EXIT

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

expect_rejection() {
    local name="$1"
    shift
    if "$@" > "$fixture/$name.out" 2>&1; then
        fail "Fast re-entry case '$name' passed unexpectedly."
    fi
}

"$BASH" -n "$workflow"
[[ "$(sha256sum "$profile" | cut -d' ' -f1)" == \
    a864b76187618961881275028671cd5943173e8ad4f87c5547a784a2eda2fff2 ]] ||
    fail "The fixed fast-reentry profile has drifted."
for rule in \
    'mp_forcerespawn "1"' \
    'mp_roundrespawn_time "-1"' \
    'mp_respawn_immunitytime "2"' \
    'mp_respawn_immunity_effects "1"' \
    'mp_respawn_immunity_force_unset "2"' \
    'mapcyclefile "goldsrcops-mapcycle.txt"' \
    'mapchangecfgfile "goldsrcops-fast-reentry-v1.cfg"'; do
    grep -Fxq "$rule" "$profile" || fail "The fixed profile is missing '$rule'."
done
! grep -Eq '^mp_round_infinite|^bot_|^rcon_|^goldsrcops_events_' "$profile" ||
    fail "The fixed profile changed an undecided or unrelated boundary."

export GOLDSRCOPS_CONFIGURATION_DIRECTORY="$fixture/config"
export GOLDSRCOPS_SERVICE_HOME="$fixture/service"
mkdir -p "$GOLDSRCOPS_CONFIGURATION_DIRECTORY" \
    "$GOLDSRCOPS_SERVICE_HOME/server/cstrike"
public="$GOLDSRCOPS_CONFIGURATION_DIRECTORY/server-public.cfg"
runtime="$GOLDSRCOPS_CONFIGURATION_DIRECTORY/runtime-enabled"
active="$GOLDSRCOPS_CONFIGURATION_DIRECTORY/managed-profile-active"
persistent="$GOLDSRCOPS_CONFIGURATION_DIRECTORY/game-event-persistent-active"
installed_profile="$GOLDSRCOPS_SERVICE_HOME/server/cstrike/goldsrcops-fast-reentry-v1.cfg"
mapcycle="$GOLDSRCOPS_SERVICE_HOME/server/cstrike/goldsrcops-mapcycle.txt"
cp -- "$profile" "$installed_profile"
printf 'exec goldsrcops-fast-reentry-v1.cfg\necho "GoldSrcOps public runtime configuration loaded"\n' > "$public"
printf 'de_dust2\nde_inferno\nde_nuke\nde_train\ncs_office\n' > "$mapcycle"
printf 'public_config_sha256=%s\n' "$(sha256sum "$public" | cut -d' ' -f1)" > "$runtime"

write_active() {
    cat > "$active" <<EOF
schema_version=1
profile_id=fast-reentry-v1
backup_name=managed-profile-20260924T000000Z-abcdef
baseline_public_sha256=$(printf 'baseline-public' | sha256sum | cut -d' ' -f1)
baseline_runtime_enabled_sha256=$(printf 'baseline-runtime' | sha256sum | cut -d' ' -f1)
public_sha256=$(sha256sum "$public" | cut -d' ' -f1)
runtime_enabled_sha256=$(sha256sum "$runtime" | cut -d' ' -f1)
profile_sha256=$(sha256sum "$installed_profile" | cut -d' ' -f1)
mapcycle_sha256=$(sha256sum "$mapcycle" | cut -d' ' -f1)
EOF
}

write_persistent() {
    local placeholder
    placeholder="$(printf 'unrelated-boundary' | sha256sum | cut -d' ' -f1)"
    cat > "$persistent" <<EOF
schema_version=2
policy_id=persistent-gameplay-v1
backup_name=persistent-gameplay-20260924T000000Z-abcdef
guard_sha256=$placeholder
game_drop_in_sha256=$placeholder
agent_drop_in_sha256=$placeholder
pilot_marker_sha256=$placeholder
pilot_gate_sha256=$placeholder
activation_state_sha256=$placeholder
manifest_sha256=$placeholder
liblist_sha256=$placeholder
environment_sha256=$placeholder
producer_sha256=$placeholder
game_unit_sha256=$placeholder
agent_unit_sha256=$placeholder
profile_id=fast-reentry-v1
public_sha256=$(sha256sum "$public" | cut -d' ' -f1)
runtime_enabled_sha256=$(sha256sum "$runtime" | cut -d' ' -f1)
active_profile_sha256=$(sha256sum "$active" | cut -d' ' -f1)
profile_sha256=$(sha256sum "$installed_profile" | cut -d' ' -f1)
mapcycle_sha256=$(sha256sum "$mapcycle" | cut -d' ' -f1)
EOF
    chmod 0640 "$public" "$runtime" "$active" "$installed_profile" "$mapcycle"
}

guard_case() {
    # shellcheck disable=SC2016
    "$BASH" -c '
        source "$1"
        service_group="$(id -gn)"
        validate_file_metadata() {
            [[ -f "$1" && ! -L "$1" && "$(stat -c %a "$1")" == "$4" ]]
        }
        read_persistent_marker
        verify_fast_profile_files
    ' _ "$workflow"
}

write_active
write_persistent
guard_case || fail "The selected profile was rejected without drift."

printf 'tampered\n' >> "$public"
expect_rejection public-drift guard_case
sed -i '$d' "$public"
printf 'tampered\n' >> "$runtime"
expect_rejection runtime-drift guard_case
sed -i '$d' "$runtime"
printf 'tampered\n' >> "$active"
expect_rejection active-marker-drift guard_case
sed -i '$d' "$active"
printf 'tampered\n' >> "$installed_profile"
expect_rejection profile-drift guard_case
sed -i '$d' "$installed_profile"
printf 'tampered\n' >> "$mapcycle"
expect_rejection mapcycle-drift guard_case
sed -i '$d' "$mapcycle"

sed -i 's/^profile_id=fast-reentry-v1$/profile_id=public-classic-v1/' "$persistent"
expect_rejection wrong-profile-id guard_case
sed -i 's/^profile_id=public-classic-v1$/profile_id=fast-reentry-v1/' "$persistent"
sed -i '/^mapcycle_sha256=/d' "$persistent"
expect_rejection missing-marker-key guard_case
write_persistent
sed -i "s/^profile_sha256=.*/profile_sha256=$(printf 'unreviewed-profile' | sha256sum | cut -d' ' -f1)/" "$persistent"
expect_rejection unreviewed-profile-hash guard_case
write_persistent

printf 'public_config_sha256=%s\n' "$(printf 'another-public-config' | sha256sum | cut -d' ' -f1)" > "$runtime"
write_active
write_persistent
expect_rejection runtime-public-mismatch guard_case
printf 'public_config_sha256=%s\n' "$(sha256sum "$public" | cut -d' ' -f1)" > "$runtime"
write_active
write_persistent

printf 'exec goldsrcops-managed-profile.cfg\n' >> "$public"
printf 'public_config_sha256=%s\n' "$(sha256sum "$public" | cut -d' ' -f1)" > "$runtime"
write_active
write_persistent
expect_rejection double-profile-exec guard_case

sed -i 's/^schema_version=2$/schema_version=1/' "$persistent"
sed -i '/^profile_id=/d; /^public_sha256=/d; /^runtime_enabled_sha256=/d; /^active_profile_sha256=/d; /^profile_sha256=/d; /^mapcycle_sha256=/d' "$persistent"
# shellcheck disable=SC2016
"$BASH" -c '
    source "$1"
    read_persistent_marker
    [[ "$marker_schema_version" == 1 ]]
' _ "$workflow" || fail "The accepted schema-1 persistent marker regressed."

# shellcheck disable=SC2016
"$BASH" -c '
    source "$1"
    require_apply_environment() { :; }
    acquire_lock() { :; }
    verify_persistent_files() { marker_schema_version=2; }
    run_rollback
' _ "$workflow" > "$fixture/rollback-refused.out" 2>&1 &&
    fail "The destructive persistent rollback accepted an active fast profile."
grep -Fq 'Switch back to the accepted classic profile' "$fixture/rollback-refused.out" ||
    fail "The fast-profile rollback refusal was not explicit."

printf 'FAST_REENTRY_PROFILE_SMOKE=passed\n'
