#!/usr/bin/env bash

set -euo pipefail

apply=false
source_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
environment_file="/etc/goldsrcops/deployment.env"
evidence_directory="/var/lib/goldsrcops/evidence"
preview_file="$evidence_directory/postgres-backup-retention-preview.json"
unit_directory="/etc/systemd/system"
service_name="goldsrcops-postgres-backup.service"
timer_name="goldsrcops-postgres-backup.timer"

usage() {
    cat <<'EOF'
Usage: install-postgres-backup-schedule.sh [--source-root PATH] [--apply]

Without --apply, validates the tracked schedule contract and prints a sanitized
plan. Apply is supported only from /opt/goldsrcops and requires a fresh,
owner-only retention preview produced with the tracked production policy.
EOF
}

while (($# > 0)); do
    case "$1" in
        --apply)
            apply=true
            shift
            ;;
        --source-root)
            if (($# < 2)); then
                echo "Missing value for --source-root." >&2
                exit 2
            fi
            source_root="$2"
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ ! -d "$source_root" ]]; then
    echo "Source root does not exist." >&2
    exit 1
fi

source_root="$(cd -- "$source_root" && pwd -P)"
service_source="$source_root/ops/production/systemd/$service_name"
timer_source="$source_root/ops/production/systemd/$timer_name"
backup_script="$source_root/ops/production/postgres-backup.ps1"
status_script="$source_root/ops/production/postgres-backup-status.ps1"
common_script="$source_root/ops/production/postgres-backup-common.ps1"
compose_file="$source_root/ops/production/compose.yml"

for required_file in \
    "$service_source" \
    "$timer_source" \
    "$backup_script" \
    "$status_script" \
    "$common_script" \
    "$compose_file"; do
    if [[ ! -f "$required_file" ]]; then
        echo "Required schedule file is missing: $required_file" >&2
        exit 1
    fi
done

grep -Fq -- "-Action Scheduled -ApplyRetention" "$service_source"
grep -Fq -- "-Kind ScheduledCycle -MaximumAgeHours 36" "$service_source"
grep -Fq -- "OnCalendar=*-*-* 03:15:00 UTC" "$timer_source"
grep -Fq -- "RandomizedDelaySec=30m" "$timer_source"
grep -Fq -- "Persistent=true" "$timer_source"

if command -v systemd-analyze >/dev/null 2>&1; then
    systemd-analyze calendar '*-*-* 03:15:00 UTC' >/dev/null
fi

mode="plan"
if [[ "$apply" == true ]]; then
    mode="apply"
fi

echo "MODE: $mode"
echo "SOURCE_ROOT: $source_root"
echo "SERVICE: $unit_directory/$service_name"
echo "TIMER: $unit_directory/$timer_name"
echo "SCHEDULE: daily at 03:15 UTC with a fixed delay up to 30 minutes"
echo "RETENTION: last=3 daily=14 weekly=8 monthly=12"
echo "FRESHNESS_LIMIT_HOURS: 36"
echo "RETENTION_PREVIEW: $preview_file"

if [[ "$apply" != true ]]; then
    echo "NO_CHANGES: rerun with --apply only after reviewing the retention preview"
    exit 0
fi

if ((EUID != 0)); then
    echo "Apply requires root." >&2
    exit 1
fi

if [[ "$source_root" != "/opt/goldsrcops" ]]; then
    echo "Apply requires the reviewed source tree at /opt/goldsrcops." >&2
    exit 1
fi

for command_name in docker install pwsh stat systemctl systemd-analyze; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Required command is unavailable: $command_name" >&2
        exit 1
    fi
done

if ! systemctl is-active --quiet docker.service; then
    echo "docker.service must be active before installing the backup timer." >&2
    exit 1
fi

if [[ ! -f "$environment_file" ]]; then
    echo "Deployment environment file is missing." >&2
    exit 1
fi

for protected_file in \
    "$backup_script" \
    "$status_script" \
    "$common_script" \
    "$compose_file" \
    "$service_source" \
    "$timer_source" \
    "$environment_file" \
    "$source_root" \
    "$source_root/ops" \
    "$source_root/ops/production" \
    "$source_root/ops/production/systemd"; do
    owner_id="$(stat -c '%u' "$protected_file")"
    mode_bits="$(stat -c '%a' "$protected_file")"
    if [[ "$owner_id" != "0" ]] || (((8#$mode_bits & 0022) != 0)); then
        echo "Production schedule paths must be root-owned and not group/other writable." >&2
        exit 1
    fi
done

install -d -o root -g root -m 0700 "$evidence_directory"
pwsh -NoLogo -NoProfile -File "$status_script" \
    -EnvironmentFile "$environment_file" \
    -StatusFile "$preview_file" \
    -Kind RetentionPreview \
    -MaximumAgeHours 24

install -o root -g root -m 0644 "$service_source" "$unit_directory/$service_name"
install -o root -g root -m 0644 "$timer_source" "$unit_directory/$timer_name"
systemd-analyze verify "$unit_directory/$service_name" "$unit_directory/$timer_name"
systemctl daemon-reload
systemctl enable --now "$timer_name"
systemctl is-enabled --quiet "$timer_name"
systemctl is-active --quiet "$timer_name"

echo "ENABLED: $timer_name"
systemctl list-timers --all --no-pager "$timer_name"
