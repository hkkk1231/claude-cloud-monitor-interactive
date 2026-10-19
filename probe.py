import json
import subprocess
import sys
from datetime import datetime, timezone

session = sys.argv[1]

def run(*args):
    return subprocess.run(args, capture_output=True, text=True, timeout=5)

result = run('tmux', 'list-panes', '-t', session, '-F', '#{pane_id}\t#{pane_current_command}\t#{pane_current_path}\t#{pane_dead}')
if result.returncode:
    print(json.dumps({'state': 'stopped', 'reason': '共享 tmux 会话未运行', 'output': '', 'project': '', 'command': '', 'checkedAt': datetime.now(timezone.utc).isoformat()}, ensure_ascii=False))
    sys.exit(0)
panes = [line.split('\t') for line in result.stdout.splitlines()]
pane = next((p for p in panes if p[1] == 'claude'), panes[0])
capture = run('tmux', 'capture-pane', '-p', '-t', pane[0], '-S', '-160')
if capture.returncode:
    raise RuntimeError('无法读取共享终端')
output = capture.stdout.rstrip()
print(json.dumps({'output': output, 'project': pane[2], 'command': pane[1], 'dead': pane[3] == '1', 'pane': pane[0], 'checkedAt': datetime.now(timezone.utc).isoformat()}, ensure_ascii=False))
