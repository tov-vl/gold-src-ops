#!/usr/bin/env bash
set -Eeuo pipefail
[[ $EUID == 0 ]]
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
temporary="$(mktemp -d)"
trap 'rm -rf -- "$temporary"' EXIT
chmod 0755 "$temporary"
mkdir "$temporary/scripts" "$temporary/bin" "$temporary/config" "$temporary/vault"
cp "$root/ops/gameserver/player-data-schedule-guard.sh" "$temporary/scripts/"
printf 'fixture\n' > "$temporary/vault/example"
sha256sum "$temporary/vault/example" > "$temporary/config/player-data-backup-baseline.sha256"
chmod 0600 "$temporary/config/player-data-backup-baseline.sha256"
cat > "$temporary/scripts/game-event-persistent.sh" <<'EOF'
configuration_directory="$TEST_ROOT/config"
live_amxx_root="$TEST_ROOT"
service_user=root
service_group=root
GAME_SERVICE_NAME=goldsrcops-gameserver.service
AGENT_SERVICE_NAME=goldsrcops-game-event-agent.service
prepared_marker=fixture
marker_value() { echo 27015; }
verify_persistent_files() { [[ "$TEST_BASELINE" == yes ]]; }
validate_file_metadata() { [[ "$(stat -c %a "$1")" == "$4" && ! -L "$1" ]]; }
validate_directory_metadata() { [[ "$TEST_VAULT_MODE" == yes ]]; }
require_unit_state() { [[ "$TEST_ACTIVE" == active ]]; }
require_settled_agent_status() { [[ "$TEST_QUEUE" == empty ]]; }
EOF
cat > "$temporary/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
[[ "$1" == show && "$2" == goldsrcops-gameserver.service && "$5" == --value ]]
case "$4" in
    ActiveState) echo "$TEST_ACTIVE";;
    MainPID) echo "$TEST_PID";;
    *) exit 1;;
esac
EOF
cat > "$temporary/bin/pgrep" <<'EOF'
#!/usr/bin/env bash
exit "$TEST_PGREP"
EOF
chmod +x "$temporary/bin/"*
export PATH="$temporary/bin:$PATH" TEST_ROOT="$temporary"
export TEST_BASELINE=yes TEST_VAULT_MODE=yes TEST_ACTIVE=active TEST_QUEUE=empty TEST_PID=0 TEST_PGREP=1
guard="$temporary/scripts/player-data-schedule-guard.sh"
[[ "$(bash "$guard" endpoint)" == 27015 ]]
bash "$guard" preflight
bash "$guard" postflight
reject() { if bash "$guard" "$1" >/dev/null 2>&1; then echo "Unexpected guard acceptance: $1"; exit 1; fi; }
TEST_BASELINE=no reject preflight
TEST_QUEUE=pending reject preflight
TEST_VAULT_MODE=no reject preflight
TEST_ACTIVE=inactive reject preflight
TEST_ACTIVE=inactive bash "$guard" quiescent
TEST_ACTIVE=failed reject quiescent
TEST_ACTIVE=inactive TEST_PID=42 reject quiescent
TEST_ACTIVE=inactive TEST_PGREP=0 reject quiescent
TEST_ACTIVE=inactive TEST_PGREP=2 reject quiescent
printf 'changed\n' >> "$temporary/vault/example"
reject preflight
reject unsupported
cp "$root/ops/gameserver/player-data-backup-ssh.sh" "$temporary/dispatcher"
cat > "$temporary/bin/sudo" <<'EOF'
#!/usr/bin/env bash
[[ $# == 4 && "$1" == -n && "$2" == -- && "$3" == /usr/local/libexec/goldsrcops-player-data-backup ]] && exit 1
[[ $# == 5 && "$1" == -n && "$2" == -- && "$3" == /usr/local/libexec/goldsrcops-player-data-backup ]]
printf 'DISPATCH=%s,%s\n' "$4" "$5"
EOF
chmod 0755 "$temporary/bin/sudo"
for operation in capture status bundle; do
    result="$(runuser -u nobody -- env PATH="$temporary/bin:/usr/bin:/bin" SSH_ORIGINAL_COMMAND="$operation 2026-10-11" bash "$temporary/dispatcher")"
    [[ "$result" == "DISPATCH=$operation,2026-10-11" ]]
done
for command in 'sh' 'capture' 'capture ../vault' 'capture 2026-10-11;id' 'bundle 2026-10-11 extra' 'status 2026-10-11/../../secret' $'capture 2026-10-11\n'; do
    if runuser -u nobody -- env PATH="$temporary/bin:/usr/bin:/bin" SSH_ORIGINAL_COMMAND="$command" bash "$temporary/dispatcher" >/dev/null 2>&1; then
        echo 'Dispatcher accepted an unsupported command' >&2
        exit 1
    fi
done
echo PLAYER_DATA_SCHEDULE_GUARD_AND_DISPATCH=passed
