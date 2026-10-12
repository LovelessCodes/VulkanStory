#!/usr/bin/env bash
# Removes the VulkanStory runtime from a macOS client bundle, never launches it.
# Usage: bash remove-macos-runtime.sh <existing-app-bundle> [external-backup]
# Requires bash and Python 3 (standard library only). Close the game first.
# Removes the activation first (wrapper removed, Vintagestory.bin renamed back),
# moves unchanged owned files into the backup directory, leaves modified files,
# saves, settings and empty directories in place.
set -eu
if [ "$#" -lt 1 ] || [ "$#" -gt 2 ]; then
    echo 'usage: bash remove-macos-runtime.sh <existing-app-bundle> [external-backup]' >&2
    exit 2
fi
command -v python3 >/dev/null 2>&1 || { echo 'Python 3 is required.' >&2; exit 2; }
exec python3 - "$@" <<'PY'
import hashlib
import json
import os
import shutil
import sys
from pathlib import Path

app = Path(sys.argv[1]).resolve()
backup = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else None

RECEIPT = 'VulkanStory/install.json'
APPHOST = 'Vintagestory'
RENAMED = 'Vintagestory.bin'
WRAPPER_MARK = b'# VulkanStory activation wrapper v1'

def digest(path):
    result = hashlib.sha256()
    with open(path, 'rb') as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b''):
            result.update(chunk)
    return result.hexdigest().upper()

def fail(message):
    print('error: ' + message, file=sys.stderr)
    sys.exit(1)

receipt_path = app / RECEIPT
if not receipt_path.is_file():
    fail('no VulkanStory install receipt; nothing to remove')
receipt = json.loads(receipt_path.read_text())
if receipt.get('product') != 'VulkanStory':
    fail('install receipt belongs to another product')
files = receipt.get('files') or {}
activation = receipt.get('activation') or {}

wrapper = app / APPHOST
renamed = app / RENAMED
if activation.get('type') == 'wrapper':
    if wrapper.is_file() and WRAPPER_MARK in wrapper.read_bytes():
        recorded = activation.get('wrapperSha256')
        if recorded and digest(wrapper) != recorded:
            print('warning: the activation wrapper was modified; removing it anyway')
        wrapper.unlink()
        if renamed.is_file():
            os.rename(renamed, wrapper)
        else:
            print('warning: Vintagestory.bin is missing; the apphost cannot be restored')
    elif renamed.is_file():
        print('warning: the activation wrapper is gone; renaming Vintagestory.bin back')
        os.rename(renamed, wrapper)

moved = 0
kept = 0
for relative in sorted(files, reverse=True):
    target = app / relative
    if not target.is_file():
        continue
    if digest(target) != files[relative].upper():
        kept += 1
        print('kept modified file: ' + relative)
        continue
    if backup is None:
        target.unlink()
    else:
        destination = backup / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(target), str(destination))
    moved += 1

if receipt_path.is_file():
    receipt_path.unlink()

for directory in ('VulkanStory/native/osx-arm64', 'VulkanStory/managed/profiles', 'VulkanStory/managed',
                  'VulkanStory/native', 'VulkanStory', 'Mods/vulkanstoryinput', 'Mods/vulkanstory'):
    try:
        (app / directory).rmdir()
    except OSError:
        pass

os.utime(app, None)
print(f'vulkanstory-remove: {moved} file(s) removed, {kept} kept, activation removed')
PY
