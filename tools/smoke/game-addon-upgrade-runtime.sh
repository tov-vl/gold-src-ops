#!/usr/bin/env bash
set -Eeuo pipefail
# Pinned read-only inputs: /runtime, /assets, /compiled, /previous, /candidate, /repo.
# Run as root in a disposable --network none container without published ports.
check() { printf '%s  %s\n' "$2" "$1" | sha256sum -c -; }
check /runtime/rehlds-bin-3.15.0.896.zip 997baeb7ef3842dab3e034d82dc651ebfe560c23b158adce660a6b97976b4e2b
check /runtime/regamedll-bin-5.30.0.814.zip 457f5c96a4d10280fcad47f106cbd5be86363a47df1ccab91c514e9d1bc6fd18
check /assets/amxmodx-1.10.0-git5481-base-linux.tar.gz ee33b31ae92afd94802c43eae14ecdfa1ffa2ba0b11658e8bec98f48a5881272
check /assets/amxmodx-1.10.0-git5481-cstrike-linux.tar.gz 76ff2bdd39f6dc14a2088ff4af724599c69a8ae7e3526679927aaef4ca898bf2
check /assets/metamod-bin-1.3.0.149.zip ede7f59c4e0220afe8c02aa348a130cce527f87d36ffdb674e37a501ce57be94
check /assets/reapi-bin-5.24.0.300.zip 16114cf5a782e9d3d0c9443c23cd937c17cc09dc9b16ff998ce48bda5faf0a81
python3 -m zipfile -e /runtime/rehlds-bin-3.15.0.896.zip /engine-components/rehlds
python3 -m zipfile -e /runtime/regamedll-bin-5.30.0.814.zip /engine-components/regamedll
python3 -m zipfile -e /assets/metamod-bin-1.3.0.149.zip /engine-components/metamod
python3 -m zipfile -e /assets/reapi-bin-5.24.0.300.zip /engine-components/reapi
mkdir /engine-base
cp -a /opt/cs16/serverfiles-base/. /engine-base/
cp -a /engine-components/rehlds/bin/linux32/. /engine-base/
cp -a /engine-components/regamedll/bin/linux32/cstrike/. /engine-base/cstrike/
tar -xzf /assets/amxmodx-1.10.0-git5481-base-linux.tar.gz -C /engine-base/cstrike
tar -xzf /assets/amxmodx-1.10.0-git5481-cstrike-linux.tar.gz -C /engine-base/cstrike
cp -a /engine-components/metamod/addons/. /engine-base/cstrike/addons/
cp -a /engine-components/reapi/addons/. /engine-base/cstrike/addons/
printf 'linux addons/amxmodx/dlls/amxmodx_mm_i386.so\n' > /engine-base/cstrike/addons/metamod/plugins.ini
printf 'reapi\nfakemeta\nhamsandwich\n' > /engine-base/cstrike/addons/amxmodx/configs/modules.ini
: > /engine-base/cstrike/addons/amxmodx/configs/plugins.ini
sed -i 's|^gamedll_linux .*|gamedll_linux "addons/metamod/metamod_i386.so"|' /engine-base/cstrike/liblist.gam
chmod 0755 /engine-base/hlds_run /engine-base/hlds_linux
exec python3 -B /repo/tools/smoke/addon-upgrade-engine.py
