import fcntl
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import uuid
from datetime import datetime, timezone

CLI = str(Path.home() / '.local/bin/claude')
ROOT = Path.home() / 'projects'
STORE = Path.home() / '.local/share/claude-monitor/conversations.json'
UUID = re.compile(r'^[0-9a-f-]{36}$')

def run(*args, check=True):
    p = subprocess.run(args, capture_output=True, text=True, timeout=8)
    if check and p.returncode:
        raise RuntimeError('共享对话操作失败：' + p.stderr.strip()[:200])
    return p.stdout

def state(output, command):
    if command != 'claude':
        return 'stopped'
    lines = output.splitlines()
    indexes = [i for i,l in enumerate(lines) if re.match(r'^\s*❯',l)]
    i = indexes[-1] if indexes else max(0,len(lines)-20)
    tail = '\n'.join(lines[i:])
    if re.search(r'esc to interrupt|ctrl.c to interrupt',tail,re.I):
        return 'working'
    if re.search(r'Do you want to proceed|Enter to confirm|Esc to cancel',tail,re.I):
        return 'waiting'
    if indexes:
        return 'finished' if re.search(r'\bdone \d{1,2}:\d{2}|[✻✽✶✢·].*\b(done|Brewed|Worked|Cooked|Baked|Sautéed|Cogitated)\b','\n'.join(lines[max(0,i-12):i]),re.I) else 'idle'
    return 'unknown'

def history():
    found = {}
    for file in (Path.home()/'.claude/projects').glob('*/*.jsonl'):
        sid = file.stem
        if not UUID.fullmatch(sid):
            continue
        meta = {'id':sid, 'title':'历史对话 '+sid[:8], 'dir':'', 'changed':file.stat().st_mtime, 'file':str(file), 'target':''}
        # Bounded reads; never open credential files or export entire records.
        with file.open('rb') as f:
            first = f.read(180000)
            f.seek(max(0,file.stat().st_size-180000))
            last = f.read(180000)
        for line in (first+b'\n'+last).splitlines():
            try:
                row = json.loads(line)
            except (ValueError, UnicodeDecodeError):
                continue
            if row.get('cwd'):
                meta['dir'] = row['cwd']
            if row.get('type') == 'custom-title' and row.get('customTitle'):
                meta['title'] = row['customTitle'][:100]
            elif row.get('type') == 'summary' and row.get('summary') and meta['title'].startswith('历史对话 '):
                meta['title'] = row['summary'][:65]
            elif row.get('type') == 'user' and meta['title'].startswith('历史对话 '):
                content = row.get('message',{}).get('content','')
                if isinstance(content,str) and content.strip() and not content.startswith('<'):
                    meta['title'] = re.sub(r'\s+',' ',content)[:65]
        if sid not in found or meta['changed'] > found[sid]['changed']:
            found[sid] = meta
    return found

def panes(session):
    raw = run('tmux','list-panes','-s','-t',session,'-F','#{window_index}\t#{pane_id}\t#{pane_current_command}\t#{pane_current_path}\t#{pane_pid}\t#{pane_dead}',check=False)
    return [parts[:5] for line in raw.splitlines()
            if len(parts := line.split('\t')) == 6 and parts[5] == '0']


def resume_command(item, flag):
    recovery = STORE.parent/'recoveries'/(item['id']+'.json')
    if flag == '--resume' and recovery.is_file():
        saved = json.loads(recovery.read_text(encoding='utf-8'))
        config = Path(saved['config']).resolve()
        options = saved.get('launch_options', [])
        allowed = {'--permission-mode', '--model', '--effort'}
        if (saved.get('sid') != item['id'] or Path(saved['cwd']).resolve() != Path(item['dir']).resolve()
                or Path.home() not in config.parents or not config.is_dir()
                or len(options) % 2 or any(options[i] not in allowed for i in range(0,len(options),2))):
            raise ValueError('回收恢复信息不完整，未启动会话')
        return 'export PATH='+shlex.quote(str(Path.home()/'.local/bin'))+':$PATH; exec '+shlex.join(
            ['env', 'CLAUDE_CONFIG_DIR='+str(config), CLI, '--resume', item['id'], *options])
    return 'export PATH='+shlex.quote(str(Path.home()/'.local/bin'))+':$PATH; exec '+shlex.join(
        [str(Path.home()/'.local/bin/cswap'),'run','--share-history','--',flag,item['id']])

def process_ids():
    raw = run('ps','-eo','pid=,ppid=,args=')
    return [line.strip().split(None,2) for line in raw.splitlines() if len(line.strip().split(None,2))==3]

def sid_for(pid, processes):
    descendants = {pid}
    for _ in range(8):
        descendants.update(p[0] for p in processes if p[1] in descendants)
    for proc_pid, parent, cmd in processes:
        if proc_pid in descendants:
            match = re.search(r'--(?:resume|session-id)\s+([0-9a-f-]{36})(?:\s|$)',cmd)
            if match:
                return match[1]
    return None

def save(records):
    temp = STORE.with_suffix('.tmp')
    temp.write_text(json.dumps(records,ensure_ascii=False),encoding='utf-8')
    temp.replace(STORE)

def historical_snapshot(item):
    file=Path(item.get('file',''))
    if not file.is_file():
        return None
    texts=[]
    with file.open('rb') as source:
        source.seek(max(0,file.stat().st_size-180000))
        for line in source.read(180000).splitlines():
            try:
                row=json.loads(line)
            except (ValueError,UnicodeDecodeError):
                continue
            if row.get('type') not in ('user','assistant'):
                continue
            content=row.get('message',{}).get('content','')
            if isinstance(content,list):
                content='\n'.join(block.get('text','') for block in content if isinstance(block,dict) and block.get('type')=='text')
            if not isinstance(content,str) or not content.strip():
                continue
            texts.append(('你' if row['type']=='user' else 'Claude')+'\n'+content[:5000])
    return {'state':'history','reason':'缓存的历史记录；切换后将恢复该对话','output':'\n\n'.join(texts[-10:]),'project':item['dir'],'checkedAt':datetime.fromtimestamp(file.stat().st_mtime,timezone.utc).isoformat()}

def manage(session, request):
    if not re.fullmatch(r'[\w-]+',session):
        raise ValueError('非法会话名称')
    if request.get('action') == 'validate-project':
        directory=Path(request.get('dir','')).resolve()
        if not directory.is_dir() or directory == Path.home() or Path.home() not in directory.parents:
            raise ValueError('请选择服务器用户目录下的现有项目目录')
        return {'ok':True,'path':str(directory)}
    roots=[Path(p).resolve() for p in request.get('roots',[str(ROOT)])]
    def allowed(directory):
        return directory.is_dir() and any(directory==root or root in directory.parents for root in roots)
    STORE.parent.mkdir(parents=True,exist_ok=True)
    with (STORE.parent/'lock').open('w') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        records = json.loads(STORE.read_text(encoding='utf-8')) if STORE.exists() else {}
        for sid, item in history().items():
            if sid not in records:
                records[sid] = item
            else:
                records[sid].update({k:item[k] for k in ('file','changed','dir')})
        current_panes = panes(session)
        processes = process_ids()
        alive = set()
        for index,pane,command,cwd,pid in current_panes:
            sid = sid_for(pid,processes) or 'live-'+pane.replace('%','')
            alive.add(sid)
            if sid not in records:
                records[sid]={'id':sid,'title':'当前对话','dir':cwd,'changed':0,'target':''}
            records[sid].update(target=session+':'+index,dir=cwd)
        for sid,item in records.items():
            if sid not in alive:
                item['target']=''
        action = request.get('action','list')
        selected = request.get('id')
        if action == 'new':
            cwd = Path(request.get('dir','')).resolve()
            if not allowed(cwd):
                raise ValueError('工作目录必须属于已添加的项目')
            sid = str(uuid.uuid4())
            records[sid]={'id':sid,'title':request.get('title','').strip()[:100] or '新对话','dir':str(cwd),'changed':datetime.now().timestamp(),'target':''}
            selected=sid
        elif action == 'rename':
            if selected not in records or not request.get('title','').strip():
                raise ValueError('请选择对话并填写标题')
            records[selected]['title']=request['title'].strip()[:100]
        elif action in ('archive','restore'):
            if selected not in records:
                raise ValueError('请选择需要归档或恢复的对话')
            records[selected]['archived']=action=='archive'
        elif action not in ('list','switch'):
            raise ValueError('未知对话操作')
        if action in ('new','switch'):
            if selected not in records:
                raise ValueError('对话不存在')
            item=records[selected]
            if not item['target']:
                if not UUID.fullmatch(selected):
                    raise ValueError('该旧终端已关闭，缺少可恢复的对话标识')
                cwd=Path(item['dir']).resolve()
                if not allowed(cwd):
                    raise ValueError('原工作目录不可用')
                flag='--session-id' if action=='new' else '--resume'
                command=resume_command(item,flag)
                target=run('tmux','new-window','-d','-P','-F','#{session_name}:#{window_index}','-t',session,'-n','chat-'+selected[:8],'-c',str(cwd),command).strip()
                item['target']=target
                save(records)  # Persist the identity before selecting; retry reuses it.
            run('tmux','select-window','-t',item['target'])
        save(records)
        active_target=run('tmux','display-message','-p','-t',session,'#{session_name}:#{window_index}',check=False).strip()
        active=next((sid for sid,item in records.items() if item['target']==active_target),None)
        selected=selected if selected in records else active
        conversations=[]
        snapshots={}
        for item in records.values():
            if item['id'].startswith('live-') and not item['target']:
                continue
            entry={k:item.get(k,'') for k in ('id','title','dir','target','changed')}
            entry['archived']=bool(item.get('archived',False))
            entry['state']='history'
            if item['target']:
                p=next((p for p in current_panes if session+':'+p[0]==item['target']),None)
                if p:
                    screen=run('tmux','capture-pane','-p','-t',p[1],'-S','-160')
                    entry['state']=state(screen,p[2])
                    if not request.get('metadataOnly'):
                        snapshots[item['id']]={'output':screen.rstrip(),'project':p[3],'command':p[2],'checkedAt':datetime.now(timezone.utc).isoformat()}
                else:
                    entry['state']='starting'
            else:
                preview=None if request.get('metadataOnly') else historical_snapshot(item)
                if preview:
                    snapshots[item['id']]=preview
            conversations.append(entry)
        conversations.sort(key=lambda v:(-float(v['changed'] or 0),not bool(v['target']),v['id']))
        working=[c for c in conversations if c['target'] and c['state']=='working']
        running=next((c['id'] for c in working if c['id']==active),working[0]['id'] if working else active)
        directories=sorted({str(p) for root in roots if root.is_dir() for p in [root,*root.iterdir()] if p.is_dir()})
        return {'ok':True,'conversations':conversations,'snapshots':snapshots,'selected':selected,'active':active,'running':running,'dirs':directories,'message':'已更新对话列表'}

if __name__=='__main__':
    try:
        print(json.dumps(manage(sys.argv[1],json.load(sys.stdin)),ensure_ascii=False))
    except Exception as error:
        print(json.dumps({'ok':False,'message':str(error)},ensure_ascii=False))
