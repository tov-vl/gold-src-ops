#!/usr/bin/env bash
set -Eeuo pipefail
[[ $EUID == 0 ]] || { echo 'Run this isolated fixture as root.' >&2; exit 1; }
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
test_root="$(mktemp -d)"
trap 'rm -rf -- "$test_root"' EXIT
mkdir -p "$test_root/scripts" "$test_root/bin" "$test_root/config" "$test_root/amxx/data/vault"
cp "$root/ops/gameserver/player-data-capture.sh" "$root/ops/gameserver/player-data-bundle.py" "$test_root/scripts/"
cat > "$test_root/scripts/game-event-persistent.sh" <<'EOF'
configuration_directory="$TEST_ROOT/config"
transition_lock="$configuration_directory/transition.lock"
GAME_SERVICE_NAME=goldsrcops-gameserver.service
service_user=root
service_group=root
live_amxx_root="$TEST_ROOT/amxx"
verify_persistent_files() { [[ "$TEST_VALID_BASELINE" == yes ]]; }
validate_directory_metadata() { [[ "$1" == "$live_amxx_root/data/vault" ]]; }
EOF
cat > "$test_root/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
[[ "$1" == show && "$2" == goldsrcops-gameserver.service && "$5" == --value ]] || exit 99
case "$4" in ActiveState) echo "$TEST_ACTIVE";; MainPID) echo "$TEST_PID";; *) exit 99;; esac
EOF
cat > "$test_root/bin/pgrep" <<'EOF'
#!/usr/bin/env bash
exit "$TEST_PGREP_STATUS"
EOF
chmod +x "$test_root/bin/"*
for stem in goldsrcops-player-stats-v1 goldsrcops-player-preferences-v1; do
    printf 'synthetic storage\n' > "$test_root/amxx/data/vault/$stem.vault"
done
export PATH="$test_root/bin:$PATH" TEST_ROOT="$test_root"
export TEST_ACTIVE=inactive TEST_PID=0 TEST_PGREP_STATUS=1 TEST_VALID_BASELINE=yes
capture="$test_root/scripts/player-data-capture.sh"
bash "$capture" "$test_root/accepted.tar" >/dev/null
reject() {
    local name="$1"
    if bash "$capture" "$test_root/$name.tar" >/dev/null 2>&1; then
        echo "Capture unexpectedly accepted $name" >&2; exit 1
    fi
    [[ ! -e "$test_root/$name.tar" ]]
}
TEST_ACTIVE=active reject active
TEST_ACTIVE=failed reject failed
TEST_PID=123 reject pid
TEST_PGREP_STATUS=0 reject outside_process
TEST_PGREP_STATUS=2 reject process_probe_error
TEST_VALID_BASELINE=no reject baseline
exec 7>"$test_root/config/transition.lock"
flock --nonblock 7
reject concurrent_transition
flock --unlock 7
echo PLAYER_DATA_CAPTURE_GUARDS=passed_active_failed_pid_process_probe_baseline_lock
