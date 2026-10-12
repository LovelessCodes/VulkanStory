#!/usr/bin/env bash
# Flips or reports the VulkanStory enable switch in an installed macOS client.
# Usage: bash toggle-macos-runtime.sh [on|off|status] [app-bundle]
# Without an app bundle it uses the client this script lives in
# (<app>/VulkanStory/tools/toggle-macos-runtime.sh). The switch is
# [Bootstrap] Enabled in VulkanStory/loader.ini and applies at the next launch.
set -eu
command -v python3 >/dev/null 2>&1 || { echo 'Python 3 is required.' >&2; exit 2; }
exec python3 - "$@" "$(cd -- "$(dirname -- "$0")" && pwd)" <<'PY'
import os
import re
import sys
from pathlib import Path

usage = 'usage: toggle-macos-runtime.sh [on|off|status] [app-bundle]'
script_dir = Path(sys.argv[-1])
args = list(sys.argv[1:-1])
action = 'status'
app = None
for arg in args:
    if arg in ('on', 'off', 'status'):
        action = arg
    elif app is None:
        app = arg
    else:
        print(usage, file=sys.stderr); sys.exit(2)
if app is None:
    app = str(script_dir.parent.parent)
app = Path(app).resolve()

if not (app / 'Info.plist').is_file():
    print(f'not a Vintage Story bundle: {app}\n{usage}', file=sys.stderr)
    sys.exit(2)

ini = app / 'VulkanStory' / 'loader.ini'
if not ini.is_file():
    print(f'no {ini}; install the runtime first (install-macos-runtime.sh)', file=sys.stderr)
    sys.exit(1)

text = ini.read_text()
pattern = re.compile(r'(?im)^(?P<indent>[ \t]*)enabled(?P<sp>[ \t]*)=(?P<value>[^\r\n]*)$')
sections = [(m.start(), m.group(1).strip().lower()) for m in re.finditer(r'(?m)^[ \t]*\[([^\]]+)\]', text)]

def section_of(position):
    current = ''
    for start, name in sections:
        if start <= position:
            current = name
        else:
            break
    return current

matches = [m for m in pattern.finditer(text) if section_of(m.start()) == 'bootstrap']
if action == 'status':
    if matches:
        print(f'VulkanStory is {"enabled" if matches[-1].group("value").strip() not in ("0",) else "disabled"} '
              f'(Enabled={matches[-1].group("value").strip()} in {ini})')
    else:
        print(f'VulkanStory is enabled (no Enabled key in {ini}; the bootstrap default)')
    sys.exit(0)

value = '1' if action == 'on' else '0'
if matches:
    last = matches[-1]
    updated = text[:last.start()] + f'{last.group("indent")}Enabled={value}' + text[last.end():]
else:
    header = re.search(r'(?im)^[ \t]*\[bootstrap\][ \t]*$', text)
    if header:
        updated = text[:header.end()] + f'\nEnabled={value}' + text[header.end():]
    else:
        updated = text.rstrip('\n') + f'\n\n[Bootstrap]\nEnabled={value}\n'

temporary = ini.with_suffix('.ini.tmp')
temporary.write_text(updated)
os.replace(temporary, ini)
print(f'VulkanStory {"enabled" if action == "on" else "disabled"}; applies at the next launch')
PY
