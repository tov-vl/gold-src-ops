#!/usr/bin/env python3
"""Install only the fixed alerts dashboard using the accepted claim protocol."""
import importlib.util
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('dashboard_protocol', ROOT / 'agent-monitor-dashboard.py')
protocol = importlib.util.module_from_spec(spec)
spec.loader.exec_module(protocol)
protocol.UID = 'goldsrcops-game-event-agent-alerts'
protocol.STATE = Path('/var/lib/goldsrcops/agent-alerts-dashboard-rollout')
protocol.DASHBOARD = ROOT / 'agent-alerts-dashboard.json'

if __name__ == '__main__':
    os.umask(0o077)
    try:
        protocol.main()
    except Exception:
        print('AGENT_ALERTS_DASHBOARD=failed; reconcile_before_retry', file=sys.stderr)
        sys.exit(1)
