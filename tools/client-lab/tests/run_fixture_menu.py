"""Run only the observer unit plugin in the existing network-isolated engine harness."""
import argparse
import importlib.util
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location("engine_ci", ROOT / "tools/smoke/engine-ci.py")
engine = importlib.util.module_from_spec(spec)
spec.loader.exec_module(engine)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--compiled", required=True, type=Path)
    parser.add_argument("--cache", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    lock = json.loads(engine.LOCK.read_bytes())
    # Offline assets only: do not download or pull a missing input for this test.
    for asset in lock["assets"]:
        engine.verify(args.cache / asset["name"], asset["sha256"])
    for image in lock["images"].values():
        engine.docker("image", "inspect", image["reference"], timeout=30)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    compiled = output / "compiled"
    compiled.mkdir()
    shutil.copyfile(args.compiled, compiled / "fixture_menu_smoke.amxx")
    engine.FIXTURES = {"fixture_menu_smoke.amxx": "tools/client-lab/tests/fixture-menu-smoke.sma"}
    original_identity = engine.source_identity

    def identity():
        sources = original_identity()
        path = "tools/client-lab/fixture_vgui_guard.sma"
        sources[path] = engine.digest(ROOT / path)
        return sources

    engine.source_identity = identity
    bundle = output / "bundle"
    engine.package_fixtures(compiled, bundle)
    lock["cases"] = [{"plugin": "fixture_menu_smoke", "command": "gso_fixture_menu_smoke",
                      "map": "de_dust2", "markers": ["FIXTURE_MENU_SMOKE"]}]
    engine.LOCK = output / "test-lock.json"
    engine.LOCK.write_text(json.dumps(lock, indent=2) + "\n", encoding="utf-8")
    engine.execute(args.cache.resolve(), output / "runtime", bundle)


if __name__ == "__main__":
    main()
