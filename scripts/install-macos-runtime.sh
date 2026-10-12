#!/usr/bin/env bash
# One-time installation into an existing macOS client bundle, never a game launcher.
# Usage: bash install-macos-runtime.sh <extracted-payload> <existing-app-bundle> <fresh-backup>
# Requires bash and Python 3 (standard library only). Close the game first.
# Validates the official client and the payload against their profiles, refuses to
# replace files it does not own, keeps an external backup, writes dependencies before
# activation, and activates by renaming the apphost to Vintagestory.bin behind a hook
# wrapper under the canonical name. Failure before activation leaves the bundle usable.
set -eu
if [ "$#" -ne 3 ]; then
    echo 'usage: bash install-macos-runtime.sh <extracted-payload> <existing-app-bundle> <fresh-external-backup>' >&2
    exit 2
fi
command -v python3 >/dev/null 2>&1 || { echo 'Python 3 is required for inventory validation.' >&2; exit 2; }
exec python3 - "$@" <<'PY'
import datetime
import hashlib
import json
import os
import shutil
import sys
from pathlib import Path

payload = Path(sys.argv[1]).resolve()
app = Path(sys.argv[2]).resolve()
backup = Path(sys.argv[3]).resolve()

PROFILE = 'vs-1.22.7-osx-arm64'
RECEIPT = 'VulkanStory/install.json'
APPHOST = 'Vintagestory'
RENAMED = 'Vintagestory.bin'
WRAPPER_MARK = b'# VulkanStory activation wrapper v1'
WRAPPER = b'''#!/bin/sh
# VulkanStory activation wrapper v1
# Installed by install-macos-runtime.sh. Runs the original apphost with the managed
# startup hook armed for this process only; removed by remove-macos-runtime.sh.
app_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
# LaunchServices gives the app a minimal PATH; the in-game capture command
# launches the packaged harness with `dotnet`.
case ":$PATH:" in
    *":/usr/local/share/dotnet:"*) ;;
    *) PATH="${PATH:+$PATH:}/usr/local/share/dotnet" ;;
esac
export PATH
# [Bootstrap] Enabled=0 in loader.ini skips the hook entirely, as the Linux
# launcher block does; the managed bootstrap repeats the same check.
ini="$app_dir/VulkanStory/loader.ini"
enabled=0
if [ -f "$ini" ]; then
    size=$(wc -c < "$ini" | tr -d ' ')
    if [ "$size" -le 65536 ]; then
        enabled=$(awk '
            /^[ \t]*[;#]/ { next }
            /^[ \t]*\[/ { section = tolower($0); gsub(/[ \t]/, "", section); next }
            {
                eq = index($0, "="); if (eq == 0) next
                key = tolower(substr($0, 1, eq - 1)); gsub(/[ \t]/, "", key)
                if (section == "[bootstrap]" && key == "enabled") {
                    value = substr($0, eq + 1); gsub(/[ \t]/, "", value)
                    print (value ~ /^-?[0-9]+$/) ? ((value + 0 != 0) ? 1 : 0) : 1
                }
            }' "$ini" | tail -1)
        [ -n "$enabled" ] || enabled=1
    fi
fi
if [ "$enabled" = "0" ]; then exec "$app_dir/Vintagestory.bin" "$@"; fi
hook="$app_dir/VulkanStory/managed/VulkanStory.Bootstrap.dll"
if [ -n "${DOTNET_STARTUP_HOOKS:-}" ]; then
    DOTNET_STARTUP_HOOKS="$hook:$DOTNET_STARTUP_HOOKS"
else
    DOTNET_STARTUP_HOOKS="$hook"
fi
export DOTNET_STARTUP_HOOKS
exec "$app_dir/Vintagestory.bin" "$@"
'''

def digest(path):
    result = hashlib.sha256()
    with open(path, 'rb') as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b''):
            result.update(chunk)
    return result.hexdigest().upper()

def fail(message):
    print('error: ' + message, file=sys.stderr)
    sys.exit(1)

if not (app / 'Info.plist').is_file():
    fail(f'not a Vintage Story bundle: {app}')
inventory_path = payload / 'VulkanStory/package.json'
if not inventory_path.is_file():
    fail('payload has no VulkanStory/package.json')
inventory = json.loads(inventory_path.read_text())
if inventory.get('product') != 'VulkanStory' or inventory.get('profile') != PROFILE:
    fail('payload identity does not match this installer')
manifest = inventory.get('files') or {}

# The activation state decides where the official apphost lives before any check.
wrapper_path = app / APPHOST
renamed_path = app / RENAMED
activated = wrapper_path.is_file() and WRAPPER_MARK in wrapper_path.read_bytes()
if activated and not renamed_path.is_file():
    fail('activation wrapper is present but Vintagestory.bin is missing')
if not activated and renamed_path.exists():
    fail('Vintagestory.bin exists without a VulkanStory wrapper; refusing to guess')

for relative, expected in manifest.items():
    path = payload / relative
    if not path.is_file() or digest(path) != expected.upper():
        fail('payload hash mismatch: ' + relative)

profile_path = payload / 'VulkanStory/managed/profiles' / (PROFILE + '.json')
profile = json.loads(profile_path.read_text())
for relative, expected in profile['files'].items():
    path = renamed_path if (activated and relative == APPHOST) else app / relative
    if not path.is_file():
        fail('official client file is missing: ' + relative)
    if digest(path) != expected.upper():
        fail('official client hash mismatch: ' + relative)

if backup.exists() and any(backup.iterdir()):
    fail(f'backup directory is not fresh: {backup}')

owned = {}
receipt_path = app / RECEIPT
if receipt_path.is_file():
    receipt = json.loads(receipt_path.read_text())
    if receipt.get('product') != 'VulkanStory':
        fail('existing install receipt belongs to another product')
    owned = receipt.get('files') or {}

copies = []
# The ICD manifest is installed with an absolute MoltenVK path: the loader resolves a
# bare library name through the system search paths, not the manifest directory. Its
# installed form is derived here so the ownership check accepts our own earlier install
# even when the receipt predates the rewrite.
manifest_relative = 'VulkanStory/native/osx-arm64/MoltenVK_icd.json'
derived_expected = {}
derived_content = {}
if manifest_relative in manifest:
    icd = json.loads((payload / manifest_relative).read_text())
    icd['ICD']['library_path'] = str(app / 'VulkanStory/native/osx-arm64/libMoltenVK.dylib')
    derived_content[manifest_relative] = json.dumps(icd, indent=2) + '\n'
    derived_expected[manifest_relative] = hashlib.sha256(
        derived_content[manifest_relative].encode()).hexdigest().upper()

for relative, expected in manifest.items():
    target = app / relative
    if target.is_symlink():
        fail('refusing to replace a symlink: ' + relative)
    if target.exists():
        current = digest(target)
        if owned.get(relative) != current and derived_expected.get(relative) != current:
            fail('refusing to overwrite a file this installer does not own: ' + relative)
        if current == expected.upper():
            continue
    copies.append(relative)

wrapper = wrapper_path
renamed = renamed_path
backup.mkdir(parents=True, exist_ok=True)
if not activated:
    if not wrapper.is_file():
        fail('official apphost is missing: ' + APPHOST)
    shutil.copy2(wrapper, backup / APPHOST)

for relative in copies:
    target = app / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(payload / relative, target)
    os.chmod(target, 0o644)

# Write the derived forms recorded above (same bytes that were hashed there).
derived = dict(derived_expected)
for relative, content in derived_content.items():
    (app / relative).write_text(content)

if not activated:
    os.rename(wrapper, renamed)
if wrapper.read_bytes() != WRAPPER:
    # Refresh an activation installed by an older revision of this script.
    wrapper.write_bytes(WRAPPER)
    os.chmod(wrapper, 0o755)

files = dict(owned)
for relative, expected in manifest.items():
    files[relative] = expected.upper()
files.update(derived)
receipt = {
    'schema': 1,
    'product': 'VulkanStory',
    'profile': PROFILE,
    'rid': 'osx-arm64',
    'activation': {
        'type': 'wrapper',
        'apphost': RENAMED,
        'backupApphost': str(backup / APPHOST),
        'wrapperSha256': digest(wrapper),
    },
    'installedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds'),
    'files': dict(sorted(files.items())),
}
receipt_path.write_text(json.dumps(receipt, indent=2) + '\n')
os.utime(app, None)

print(f'vulkanstory-install: {len(copies)} file(s) written, {len(files)} owned; activation '
      + ('updated' if activated else 'installed'))
print('Close the game before installing; it starts with VulkanStory at the next launch.')
PY
