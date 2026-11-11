"""Verify that browser CI uses the matching immutable official image and complete suite."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[2]
workflow = (root / '.github/workflows/ci.yml').read_text(encoding='utf-8')
browser = workflow.split('  browser-smoke:', 1)[1].split('  publish-image:', 1)[0]
project = (root / 'tests/GoldSrcOps.WebTests/GoldSrcOps.WebTests.csproj').read_text(encoding='utf-8')
version = re.search(r'Include="Microsoft.Playwright.Xunit" Version="([0-9.]+)"', project).group(1)
image = re.search(r'(mcr\.microsoft\.com/playwright/dotnet:v([0-9.]+)-noble@sha256:[0-9a-f]{64})', browser)
assert image and image.group(2) == version, 'Pinned official browser image must match the test package'
assert '--TestCaseFilter:FullyQualifiedName~BrowserTokenBoundaryTests' in browser
assert 'GOLDSRCOPS_RUN_BROWSER_TESTS: "true"' in browser
assert 'continue-on-error' not in browser and 'install --with-deps' not in browser
assert 'apt-get' not in browser and '--privileged' not in browser and '--ipc=host' not in browser
assert '--network none' in browser and '--env GOLDSRCOPS_RUN_BROWSER_TESTS' in browser
assert 'vstest tests/GoldSrcOps.WebTests/bin/Debug/net10.0/GoldSrcOps.WebTests.dll' in browser
assert 'source=$NUGET_PACKAGES,target=$NUGET_PACKAGES,readonly' in browser
assert 'NUGET_PACKAGES=$RUNNER_TEMP/browser-nuget-packages' in browser
assert 'source=$DOTNET_ROOT,target=/ci-dotnet,readonly' in browser and '--entrypoint /ci-dotnet/dotnet' in browser
assert 'global-json-file: global.json' in browser and '-p:AuditPipeline=true' in browser
print('Official immutable browser image matches package ' + version + '; complete browser suite and audited SDK restore retained')
