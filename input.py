import json
import re
import subprocess
import sys
import time
import uuid

KEYS = {'Enter', 'Escape', 'C-c', 'Up', 'Down', 'Left', 'Right', 'Tab', 'BTab', 'C-o', 'C-t', 'C-r', 'C-l', 'C-b', 'C-s', 'M-p', 'M-t', 'M-o', 'Rewind', 'QueueNow'}

def run(*args, data=None):
    result = subprocess.run(args, input=data, capture_output=True, text=True, timeout=5)
    if result.returncode:
        raise RuntimeError('共享终端操作失败')
    return result.stdout

def empty_prompt(screen):
    lines = screen.splitlines()
    indexes = [i for i, line in enumerate(lines) if re.match(r'^\s*❯', line)]
    if not indexes:
        return False
    i = indexes[-1]
    if re.search(r'esc to interrupt|ctrl.c to interrupt|enter to confirm|esc to cancel', '\n'.join(lines[i:]), re.I):
        return False
    if lines[i].strip() != '❯':
        return False
    # An empty prompt is followed immediately by its bottom separator.
    following = [line.strip() for line in lines[i+1:] if line.strip()]
    return bool(following and re.fullmatch(r'[─━╌\-]+', following[0]))

def execute(session, request):
    action = request.get('action')
    if action not in {'send', 'key', 'check'}:
        raise ValueError('未知输入操作')
    rows = run('tmux', 'list-panes', '-t', session, '-F', '#{pane_id}\t#{pane_current_command}').splitlines()
    panes = [row.split('\t') for row in rows]
    pane = next((p[0] for p in panes if p[1] == 'claude'), None)
    if not pane:
        raise RuntimeError('共享会话中没有运行 Claude Code；未发送任何内容')
    before = run('tmux', 'capture-pane', '-p', '-t', pane, '-S', '-50')
    if action == 'key':
        key = request.get('key')
        if key not in KEYS:
            raise ValueError('不支持的快捷键')
        if key == 'Rewind':
            run('tmux', 'send-keys', '-t', pane, 'Escape')
            time.sleep(0.1)
            run('tmux', 'send-keys', '-t', pane, 'Escape')
        elif key == 'QueueNow':
            run('tmux', 'send-keys', '-t', pane, 'C-x', 'C-s')
        else:
            run('tmux', 'send-keys', '-t', pane, key)
        time.sleep(0.15)
        return {'ok': True, 'message': '已发送按键 ' + key}
    text = request.get('text', '').replace('\r\n', '\n').replace('\r', '\n').strip()
    if not text or len(text) > 20000 or any(ord(c) < 32 and c not in '\n\t' for c in text):
        raise ValueError('输入不能为空、超过 20000 字或包含控制字符')
    if not empty_prompt(before):
        raise RuntimeError('终端正在工作、显示菜单或已有输入；未覆盖，请先查看输出')
    if action == 'check':
        return {'ok': True, 'message': '输入通道检查通过（没有发送内容）'}
    buffer = 'desktop-' + uuid.uuid4().hex
    pasted = False
    try:
        run('tmux', 'load-buffer', '-b', buffer, '-', data=text)
        if run('tmux', 'save-buffer', '-b', buffer, '-') != text:
            raise RuntimeError('缓冲区校验失败，未粘贴')
        # Check again immediately before mutating the shared input.
        if not empty_prompt(run('tmux', 'capture-pane', '-p', '-t', pane, '-S', '-50')):
            raise RuntimeError('终端输入发生变化，未粘贴')
        run('tmux', 'paste-buffer', '-p', '-b', buffer, '-t', pane)
        pasted = True
        time.sleep(0.45)
        after = run('tmux', 'capture-pane', '-p', '-t', pane, '-S', '-80')
        # Claude may collapse bracketed paste into a numbered pasted-text chip.
        compact = re.sub(r'\s+', '', after)
        expected = re.sub(r'\s+', '', text)
        if expected not in compact and not re.search(r'\[Pasted text #\d+', after):
            raise RuntimeError('内容已粘贴，但无法确认显示完整；尚未提交，请查看终端后使用 Enter')
        run('tmux', 'send-keys', '-t', pane, 'Enter')
        time.sleep(0.2)
        run('tmux', 'capture-pane', '-p', '-t', pane, '-S', '-30')
        return {'ok': True, 'message': '已粘贴、校验并发送到共享 Claude 会话'}
    except Exception as error:
        if pasted:
            return {'ok': False, 'message': str(error), 'pasted': True}
        raise
    finally:
        try:
            run('tmux', 'delete-buffer', '-b', buffer)
        except Exception:
            pass

if __name__ == '__main__':
    try:
        print(json.dumps(execute(sys.argv[1], json.load(sys.stdin)), ensure_ascii=False))
    except Exception as error:
        print(json.dumps({'ok': False, 'message': str(error)}, ensure_ascii=False))
