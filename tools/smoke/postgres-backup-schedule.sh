#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
installer="$repo_root/ops/production/install-postgres-backup-schedule.sh"
service="$repo_root/ops/production/systemd/goldsrcops-postgres-backup.service"
timer="$repo_root/ops/production/systemd/goldsrcops-postgres-backup.timer"

bash -n "$installer"

plan="$(bash "$installer" --source-root "$repo_root")"
grep -Fq "MODE: plan" <<<"$plan"
grep -Fq "RETENTION: last=3 daily=14 weekly=8 monthly=12" <<<"$plan"
grep -Fq "FRESHNESS_LIMIT_HOURS: 36" <<<"$plan"
grep -Fq "NO_CHANGES:" <<<"$plan"
grep -Fq 'Unsafe production schedule path: $protected_file' "$installer"
grep -Fq 'owner uid=$owner_id, mode=$mode_bits' "$installer"

if bash "$installer" --source-root "$repo_root" --unknown >/dev/null 2>&1; then
    echo "Installer accepted an unknown argument." >&2
    exit 1
fi

grep -Fq -- "-Action Scheduled -ApplyRetention" "$service"
grep -Fq -- "-Kind ScheduledCycle -MaximumAgeHours 36" "$service"
grep -Fq "Persistent=true" "$timer"

if command -v systemd-analyze >/dev/null 2>&1; then
    systemd-analyze calendar '*-*-* 03:15:00 UTC' >/dev/null

    if [[ -x /usr/bin/pwsh ]]; then
        temporary_directory="$(mktemp -d)"
        trap 'rm -rf -- "$temporary_directory"' EXIT
        printf '%s\n' \
            '[Unit]' \
            'Description=Container runtime stub for unit verification' \
            '[Service]' \
            'Type=oneshot' \
            'ExecStart=/bin/true' >"$temporary_directory/docker.service"
        systemd-analyze verify "$temporary_directory/docker.service" "$service" "$timer"
    fi
fi

echo "PostgreSQL backup systemd schedule smoke test passed."
