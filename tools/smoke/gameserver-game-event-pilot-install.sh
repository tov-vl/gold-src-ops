#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
installer="$repo_root/ops/gameserver/game-event-pilot-install.sh"
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
        fail "Game-event pilot installer case '$name' passed unexpectedly."
    fi
    printf "Game-event pilot installer case '%s' failed as expected.\n" "$name"
}

write_runtime_marker() {
    local path="$1"
    local sha="1111111111111111111111111111111111111111111111111111111111111111"
    cat > "$path" <<EOF
schema_version=1
steam_app_id=90
steam_branch=steam_legacy
steamcmd_bootstrap_sha256=$sha
steamcmd_script_sha256=$sha
steamcmd_binary_sha256=$sha
steamclient_binary_sha256=$sha
hlds_build_id=123456
hlds_app_manifest_sha256=$sha
base_hlds_linux_sha256=$sha
rehlds_version=3.15.0.896
rehlds_archive_sha256=$sha
rehlds_hlds_linux_sha256=$sha
rehlds_engine_sha256=$sha
regamedll_version=5.30.0.814
regamedll_archive_sha256=$sha
regamedll_binary_sha256=$sha
service_unit_sha256=$sha
EOF
}

bash -n "$installer"

plan_output="$smoke_directory/plan.out"
bash "$installer" > "$plan_output"
for expected in \
    'reviewed active plugin-free game runtime and service account' \
    'root-owned 0600 bundle' \
    'manifest schema 1, linux-x64 target, source identity, pins, and every payload hash' \
    'content-addressed pilot release' \
    'default-off environment and constrained goldsrcops-game-event-agent.service' \
    'leave liblist.gam, live plugin files, credentials, delivery, and both running services unchanged' \
    'PLAN_ONLY: no host changes were made'; do
    grep -Fq "$expected" "$plan_output" || fail "Pilot installer plan is missing '$expected'."
done
if grep -Eiq 'password|bearer|client[_-]?secret=|203\.0\.113\.|198\.51\.100\.' "$plan_output"; then
    fail "Pilot installer plan exposed secret-shaped or host-specific content."
fi

rollback_plan="$smoke_directory/rollback-plan.out"
bash "$installer" --rollback > "$rollback_plan"
grep -Fq 'inactive and disabled pilot with no activation marker, credential, or state files' "$rollback_plan" ||
    fail "Pilot rollback plan is missing its pre-activation boundary."
grep -Fq 'PLAN_ONLY: no host changes were made' "$rollback_plan" ||
    fail "Pilot rollback did not remain plan-only."

stdin_plan="$smoke_directory/stdin-plan.out"
bash -s -- < "$installer" > "$stdin_plan"
grep -Fq 'PLAN_ONLY: no host changes were made' "$stdin_plan" ||
    fail "Pilot installer did not execute its plan when read from stdin."

valid_sha="1111111111111111111111111111111111111111111111111111111111111111"
expect_failure root-service bash "$installer" --service-user root
expect_failure relative-bundle bash "$installer" --bundle pilot.zip --bundle-sha256 "$valid_sha"
expect_failure missing-digest bash "$installer" --bundle /tmp/pilot.zip
expect_failure invalid-digest bash "$installer" --bundle /tmp/pilot.zip --bundle-sha256 abc
expect_failure rollback-with-bundle bash "$installer" --rollback --bundle /tmp/pilot.zip --bundle-sha256 "$valid_sha"
expect_failure unknown-option bash "$installer" --replace
expect_failure non-root-apply bash "$installer" --bundle /tmp/pilot.zip --bundle-sha256 "$valid_sha" --apply

runtime_marker_fixture="$smoke_directory/runtime-installed"
write_runtime_marker "$runtime_marker_fixture"
runtime_result="$smoke_directory/runtime-result.out"
(
    # shellcheck source=/dev/null
    source "$installer"
    read_runtime_marker "$runtime_marker_fixture"
    # Values are assigned by the sourced installer's strict marker parser.
    # shellcheck disable=SC2154
    printf '%s\n%s\n' "$runtime_rehlds_version" "$runtime_regamedll_version"
) > "$runtime_result"
printf '3.15.0.896\n5.30.0.814\n' > "$smoke_directory/runtime-expected.out"
cmp -s "$runtime_result" "$smoke_directory/runtime-expected.out" ||
    fail "Pilot installer parsed the runtime marker incorrectly."

incomplete_marker="$smoke_directory/runtime-incomplete"
printf 'schema_version=1\nrehlds_version=3.15.0.896\nregamedll_version=5.30.0.814\n' > "$incomplete_marker"
# shellcheck disable=SC2016
expect_failure incomplete-runtime-marker bash -c \
    'source "$1"; read_runtime_marker "$2"' \
    _ "$installer" "$incomplete_marker"

duplicate_marker="$smoke_directory/runtime-duplicate"
cp "$runtime_marker_fixture" "$duplicate_marker"
printf 'rehlds_version=3.15.0.896\n' >> "$duplicate_marker"
# shellcheck disable=SC2016
expect_failure duplicate-runtime-marker bash -c \
    'source "$1"; read_runtime_marker "$2"' \
    _ "$installer" "$duplicate_marker"

unknown_marker="$smoke_directory/runtime-unknown"
cp "$runtime_marker_fixture" "$unknown_marker"
printf 'provider_id=forbidden\n' >> "$unknown_marker"
# shellcheck disable=SC2016
expect_failure unknown-runtime-marker bash -c \
    'source "$1"; read_runtime_marker "$2"' \
    _ "$installer" "$unknown_marker"

if command -v jq >/dev/null 2>&1; then
    content_fixture="$smoke_directory/content"
    comparison_fixture="$smoke_directory/comparison"
    mkdir -p "$content_fixture" "$comparison_fixture"
    required_payload_paths=(
    agent/GoldSrcOps.GameEventAgent
    agent/appsettings.json
    agent/run.sh
    gameserver/cstrike/addons/metamod/metamod_i386.so
    gameserver/cstrike/addons/metamod/plugins.ini
    gameserver/cstrike/addons/amxmodx/dlls/amxmodx_mm_i386.so
    gameserver/cstrike/addons/amxmodx/modules/reapi_amxx_i386.so
    gameserver/cstrike/addons/amxmodx/plugins/goldsrcops_game_events.amxx
    gameserver/cstrike/addons/amxmodx/configs/plugins.ini
    gameserver/cstrike/addons/amxmodx/configs/modules.ini
    gameserver/cstrike/addons/amxmodx/configs/amxx.cfg
    )
    payload_json='[]'
    for payload_path in "${required_payload_paths[@]}"; do
    mkdir -p "$(dirname "$content_fixture/$payload_path")"
    printf 'fixture:%s\n' "$payload_path" > "$content_fixture/$payload_path"
    payload_length="$(stat -c '%s' "$content_fixture/$payload_path")"
    payload_sha="$(sha256sum "$content_fixture/$payload_path" | awk '{ print $1 }')"
    payload_mode=0640
    if [[ "$payload_path" == 'agent/GoldSrcOps.GameEventAgent' || "$payload_path" == 'agent/run.sh' ]]; then
        payload_mode=0750
    fi
    payload_json="$(jq -c \
        --arg path "$payload_path" \
        --argjson length "$payload_length" \
        --arg sha "$payload_sha" \
        --arg mode "$payload_mode" \
        '. + [{path: $path, length: $length, sha256: $sha, mode: $mode}]' \
        <<< "$payload_json")"
    done

    jq -n \
    --argjson payload "$payload_json" \
    --arg source "1111111111111111111111111111111111111111" \
    --arg amxxBaseSha "ee33b31ae92afd94802c43eae14ecdfa1ffa2ba0b11658e8bec98f48a5881272" \
    --arg amxxCstrikeSha "76ff2bdd39f6dc14a2088ff4af724599c69a8ae7e3526679927aaef4ca898bf2" \
    --arg metamodSha "ede7f59c4e0220afe8c02aa348a130cce527f87d36ffdb674e37a501ce57be94" \
    --arg reapiSha "16114cf5a782e9d3d0c9443c23cd937c17cc09dc9b16ff998ce48bda5faf0a81" '
    {
        schemaVersion: 1,
        bundleVersion: "2.11.0-pilot.1",
        sourceRevision: $source,
        sourceDirty: false,
        productionEligible: true,
        targetRuntime: "linux-x64",
        activation: {
            changesGameServerRuntime: false,
            producerEnabled: false,
            spoolImportEnabled: false,
            deliveryEnabled: false
        },
        components: [
            {name: "AMX Mod X base", version: "1.10.0.5481", archiveSha256: $amxxBaseSha},
            {name: "AMX Mod X Counter-Strike", version: "1.10.0.5481", archiveSha256: $amxxCstrikeSha},
            {name: "Metamod-R", version: "1.3.0.149", archiveSha256: $metamodSha},
            {name: "ReAPI", version: "5.24.0.300", archiveSha256: $reapiSha}
        ],
        producer: {
            sourcePath: "samples/amxmodx-game-event-producer/goldsrcops_game_events.sma",
            sourceSha256: "2222222222222222222222222222222222222222222222222222222222222222",
            compiledSha256: ($payload[] |
                select(.path == "gameserver/cstrike/addons/amxmodx/plugins/goldsrcops_game_events.amxx") |
                .sha256)
        },
        payload: $payload
    }
        ' > "$content_fixture/manifest.json"

    (
        # shellcheck source=/dev/null
        source "$installer"
        validate_manifest_contract "$content_fixture/manifest.json"
        verify_manifest_payload "$content_fixture" "$comparison_fixture"
    )

    tampered_fixture="$smoke_directory/content-tampered"
    cp -a "$content_fixture" "$tampered_fixture"
    printf 'tampered\n' >> "$tampered_fixture/agent/appsettings.json"
    mkdir "$smoke_directory/comparison-tampered"
    # shellcheck disable=SC2016
    expect_failure tampered-payload bash -c \
        'source "$1"; validate_manifest_contract "$2/manifest.json"; verify_manifest_payload "$2" "$3"' \
        _ "$installer" "$tampered_fixture" "$smoke_directory/comparison-tampered"

    development_fixture="$smoke_directory/content-development"
    cp -a "$content_fixture" "$development_fixture"
    jq '.sourceDirty = true | .productionEligible = false' \
        "$development_fixture/manifest.json" > "$development_fixture/manifest.tmp"
    mv "$development_fixture/manifest.tmp" "$development_fixture/manifest.json"
    # shellcheck disable=SC2016
    expect_failure development-manifest bash -c \
        'source "$1"; validate_manifest_contract "$2"' \
        _ "$installer" "$development_fixture/manifest.json"

    extra_fixture="$smoke_directory/content-extra"
    cp -a "$content_fixture" "$extra_fixture"
    printf 'unexpected\n' > "$extra_fixture/extra.txt"
    mkdir "$smoke_directory/comparison-extra"
    # shellcheck disable=SC2016
    expect_failure extra-payload bash -c \
        'source "$1"; validate_manifest_contract "$2/manifest.json"; verify_manifest_payload "$2" "$3"' \
        _ "$installer" "$extra_fixture" "$smoke_directory/comparison-extra"
else
    printf 'jq is unavailable; manifest fixture cases are deferred to CI.\n'
fi

environment_fixture="$smoke_directory/game-event-agent.env"
unit_fixture="$smoke_directory/goldsrcops-game-event-agent.service"
(
    # shellcheck source=/dev/null
    source "$installer"
    # Consumed by the sourced unit renderer.
    # shellcheck disable=SC2034
    service_group=goldsrc
    render_environment_file "$environment_fixture"
    render_service_unit "$unit_fixture" \
        /opt/goldsrcops/gameserver/game-event-pilot/releases/1111111111111111111111111111111111111111111111111111111111111111
)

for expected in \
    'GameEventAgent__QueuePath=/var/lib/goldsrc/game-event-agent/queue/game-event-agent.db' \
    'GameEventAgent__Spool__Enabled=false' \
    'GameEventAgent__Spool__RootPath=/var/lib/goldsrc/server/cstrike/addons/amxmodx/data/goldsrcops-spool' \
    'GameEventAgent__Delivery__Enabled=false'; do
    grep -Fxq "$expected" "$environment_fixture" || fail "Pilot environment is missing '$expected'."
done
if grep -Eiq 'serverid|clientid|tokenendpoint|audience|secret|password|bearer' "$environment_fixture"; then
    fail "The default-off pilot environment contains identity or secret material."
fi

for expected in \
    'User=goldsrc' \
    'Group=goldsrc' \
    'ConditionPathExists=/etc/goldsrcops/gameserver/game-event-pilot-enabled' \
    'ConditionPathExists=/etc/goldsrcops/gameserver/secrets/game-event-agent-client-secret' \
    'EnvironmentFile=/etc/goldsrcops/gameserver/game-event-agent.env' \
    'Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/goldsrc/game-event-agent/dotnet-bundle' \
    'LoadCredential=oauth-client-secret:/etc/goldsrcops/gameserver/secrets/game-event-agent-client-secret' \
    'ExecStartPre=/opt/goldsrcops/gameserver/game-event-pilot/releases/1111111111111111111111111111111111111111111111111111111111111111/agent/run.sh verify-access-token' \
    'ExecStart=/opt/goldsrcops/gameserver/game-event-pilot/releases/1111111111111111111111111111111111111111111111111111111111111111/agent/run.sh' \
    'Restart=on-failure' \
    'MemoryMax=256M' \
    'NoNewPrivileges=true' \
    'CapabilityBoundingSet=' \
    'PrivateDevices=true' \
    'ProtectProc=invisible' \
    'ProtectSystem=strict' \
    'ReadWritePaths=/var/lib/goldsrc/game-event-agent -/var/lib/goldsrc/server/cstrike/addons/amxmodx/data/goldsrcops-spool'; do
    grep -Fxq "$expected" "$unit_fixture" || fail "Pilot systemd unit is missing '$expected'."
done
if grep -Eiq 'Environment=.*(secret|password|token)=' "$unit_fixture"; then
    fail "Pilot systemd unit contains unsafe inline secret transport."
fi

run_install_body="$(sed -n '/^run_install() {$/,/^}$/p' "$installer")"
previous_line=0
for expected_call in \
    '    require_install_environment' \
    '    prepare_and_verify_bundle' \
    '    prepare_rendered_files' \
    '    promote_release' \
    '    install_state_directories' \
    '    install_configuration' \
    '    write_pilot_marker' \
    '    verify_installed_state'; do
    current_line="$(grep -nFx "$expected_call" <<< "$run_install_body" | cut -d: -f1)"
    [[ -n "$current_line" && "$current_line" -gt "$previous_line" ]] ||
        fail "Pilot installer apply order is invalid at '$expected_call'."
    previous_line="$current_line"
done

# The literal source contracts are intentionally not expanded by this smoke.
# shellcheck disable=SC2016
for guard in \
    '[[ ! -e "$pilot_enabled_marker" ]]' \
    '[[ ! -e "$client_secret_file" ]]' \
    'Pre-activation rollback refuses to remove pilot state.' \
    'validate_pristine_game_runtime'; do
    grep -Fq "$guard" "$installer" || fail "Pilot rollback is missing guard '$guard'."
done

if grep -Eq 'systemctl[[:space:]]+(enable|start|restart)' "$installer"; then
    fail "Pilot installer must not enable, start, or restart a service."
fi
if grep -Eq '(sed|perl)[^\n]*liblist\.gam|cp[^\n]*gameserver/cstrike' "$installer"; then
    fail "Pilot installer must not mutate the live plugin loader or game tree."
fi
if grep -Eq '/releases/(latest|download/latest)|:[[:space:]]*latest' "$installer"; then
    fail "Pilot installer contains a mutable latest reference."
fi

printf 'Game-server game-event pilot installer smoke test passed.\n'
