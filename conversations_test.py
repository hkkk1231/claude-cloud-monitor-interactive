import json
import os
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

if os.name == 'nt':
    sys.modules['fcntl'] = types.SimpleNamespace(LOCK_EX=2,flock=lambda *args: None)
import conversations as service
REAL_PANES=service.panes

SID='28b42618-0a01-436a-b31c-42356df15c38'
OLD='8bcdce6f-4e63-453b-8434-4dd890e7638c'

class ConversationTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.root=Path(self.temp.name).resolve()/'projects';self.root.mkdir()
        self.project=self.root/'same project';self.project.mkdir()
        self.store=Path(self.temp.name).resolve()/'state'/'conversations.json'
        self.calls=[]
        def run(*args,**kwargs):
            self.calls.append(args)
            if args[0]=='tmux' and args[1]=='new-window':return 'claude-shared:1\n'
            if args[0]=='tmux' and args[1]=='display-message':return 'claude-shared:0\n'
            if args[0]=='tmux' and args[1]=='capture-pane':return 'Working (esc to interrupt)'
            return ''
        self.patches=[patch.object(service,'ROOT',self.root),patch.object(service,'STORE',self.store),patch.object(service,'history',return_value={SID:{'id':SID,'title':'current','dir':str(self.project),'changed':2,'file':'record'},OLD:{'id':OLD,'title':'old','dir':str(self.project),'changed':1,'file':'record'}}),patch.object(service,'panes',return_value=[['0','%0','claude',str(self.project),'10']]),patch.object(service,'process_ids',return_value=[['11','10','claude --resume '+SID]]),patch.object(service,'run',side_effect=run)]
        for p in self.patches:p.start()
    def tearDown(self):
        for p in reversed(self.patches):p.stop()
        self.temp.cleanup()
    def test_import_active_and_history_without_mutation(self):
        result=service.manage('claude-shared',{'action':'list'})
        self.assertEqual(result['selected'],SID)
        self.assertEqual(len(result['conversations']),2)
        self.assertEqual(result['conversations'][0]['state'],'working')
        self.assertFalse(any(c[1] in ('new-window','send-keys','select-window','kill-window') for c in self.calls if c[0]=='tmux'))
    def test_notification_metadata_never_exports_screens_or_history(self):
        with patch.object(service,'historical_snapshot',side_effect=AssertionError('history must not be opened')):
            result=service.manage('claude-shared',{'action':'list','metadataOnly':True})
        self.assertEqual(result['snapshots'],{})
        self.assertEqual(result['conversations'][0]['state'],'working')
        self.assertNotIn('output',json.dumps(result))
    def test_additional_completion_markers(self):
        for marker in ('Cogitated','Sautéed','Baked','Worked'):
            self.assertEqual(service.state('✻ '+marker+' for 20s\n❯ ','claude'),'finished')
    def test_switch_active_only_selects_existing_window(self):
        service.manage('claude-shared',{'action':'switch','id':SID})
        self.assertIn(('tmux','select-window','-t','claude-shared:0'),self.calls)
        self.assertFalse(any(c[1] in ('new-window','send-keys','kill-window') for c in self.calls))
    def test_resume_history_interactively_in_separate_window(self):
        service.manage('claude-shared',{'action':'switch','id':OLD})
        call=next(c for c in self.calls if c[1]=='new-window')
        self.assertIn('--resume '+OLD,call[-1])
        self.assertNotIn(' -p',call[-1])
        self.assertFalse(any(c[1] in ('kill-window','send-keys') for c in self.calls))
    def test_multiple_conversations_same_directory(self):
        result=service.manage('claude-shared',{'action':'new','title':'新的设计','dir':str(self.project)})
        new_id=result['selected']
        self.assertNotEqual(new_id,SID)
        self.assertIn('--session-id '+new_id,next(c for c in self.calls if c[1]=='new-window')[-1])
        self.assertEqual(json.loads(self.store.read_text(encoding='utf-8'))[new_id]['dir'],str(self.project))
    def test_rename_is_persistent_and_does_not_input_to_claude(self):
        service.manage('claude-shared',{'action':'rename','id':SID,'title':'中文标题'})
        result=service.manage('claude-shared',{'action':'list'})
        self.assertEqual(next(c for c in result['conversations'] if c['id']==SID)['title'],'中文标题')
        self.assertFalse(any(c[1]=='send-keys' for c in self.calls))
    def test_external_working_directory_rejected(self):
        with self.assertRaises(ValueError):service.manage('claude-shared',{'action':'new','dir':str(self.root.parent),'title':'bad'})
        self.assertFalse(any(c[1]=='new-window' for c in self.calls))
    def test_new_conversation_at_project_root(self):
        result=service.manage('claude-shared',{'action':'new','dir':str(self.root),'title':'动画项目'})
        record=next(c for c in result['conversations'] if c['id']==result['selected'])
        self.assertEqual(record['dir'],str(self.root))
        self.assertIn(str(self.root),result['dirs'])
    def test_new_conversation_in_additional_project(self):
        other=self.root.parent/'other';other.mkdir()
        result=service.manage('claude-shared',{'action':'new','dir':str(other),'roots':[str(self.root),str(other)],'title':'其他项目'})
        self.assertEqual(next(c for c in result['conversations'] if c['id']==result['selected'])['dir'],str(other))
    def test_validate_project_only_reads_directory(self):
        with patch.object(Path,'home',return_value=self.root.parent):
            result=service.manage('claude-shared',{'action':'validate-project','dir':str(self.root)})
        self.assertEqual(result['path'],str(self.root))
        self.assertEqual(self.calls,[])
        self.assertFalse(self.store.exists())
    def test_archive_persists_without_stopping_or_switching(self):
        service.manage('claude-shared',{'action':'archive','id':SID})
        result=service.manage('claude-shared',{'action':'list'})
        self.assertTrue(next(c for c in result['conversations'] if c['id']==SID)['archived'])
        self.assertFalse(any(c[1] in ('send-keys','kill-window','new-window','select-window') for c in self.calls))
        self.assertEqual(next(c for c in result['conversations'] if c['id']==SID)['target'],'claude-shared:0')
    def test_restore_preserves_title_and_history(self):
        service.manage('claude-shared',{'action':'rename','id':OLD,'title':'保留标题'})
        service.manage('claude-shared',{'action':'archive','id':OLD})
        result=service.manage('claude-shared',{'action':'restore','id':OLD})
        record=next(c for c in result['conversations'] if c['id']==OLD)
        self.assertFalse(record['archived'])
        self.assertEqual(record['title'],'保留标题')
        self.assertFalse(any(c[1] in ('send-keys','new-window','select-window') for c in self.calls))
    def test_process_identity_uses_descendants(self):
        self.assertEqual(service.sid_for('10',[['11','10','bash'],['12','11','claude --session-id '+SID]]),SID)

    def test_dead_pane_is_not_an_active_conversation(self):
        raw='0\t%0\tclaude\t/project\t10\t0\n1\t%1\tclaude\t/project\t11\t1\n'
        with patch.object(service,'run',return_value=raw):
            self.assertEqual(REAL_PANES('claude-shared'), [['0','%0','claude','/project','10']])

    def test_reclaimed_session_restores_original_account_and_options(self):
        recovery=self.store.parent/'recoveries';recovery.mkdir(parents=True)
        config=self.root.parent/'account C';config.mkdir()
        (recovery/(OLD+'.json')).write_text(json.dumps({'sid':OLD,'cwd':str(self.project),
            'config':str(config),'launch_options':['--permission-mode','auto']}),encoding='utf-8')
        with patch.object(Path,'home',return_value=self.root.parent):
            service.manage('claude-shared',{'action':'switch','id':OLD})
        command=next(c for c in self.calls if c[1]=='new-window')[-1]
        self.assertIn('CLAUDE_CONFIG_DIR=',command)
        self.assertIn('--permission-mode auto',command)
        self.assertIn('--resume '+OLD,command)
        self.assertNotIn('cswap',command)

    def test_dead_live_placeholder_not_listed(self):
        service.manage('claude-shared',{'action':'list'})
        records=json.loads(self.store.read_text(encoding='utf-8'));records['live-9']={'id':'live-9','title':'当前对话','dir':str(self.project),'target':'','changed':0}
        self.store.write_text(json.dumps(records),encoding='utf-8')
        result=service.manage('claude-shared',{'action':'list'})
        self.assertNotIn('live-9',[c['id'] for c in result['conversations']])
    def test_running_locator_and_latest_history_order(self):
        with patch.object(service,'history',return_value={SID:{'id':SID,'title':'current','dir':str(self.project),'changed':2,'file':'record'},OLD:{'id':OLD,'title':'newest','dir':str(self.project),'changed':5,'file':'record'}}):
            result=service.manage('claude-shared',{'action':'list'})
        self.assertEqual(result['running'],SID)
        self.assertEqual(result['active'],SID)
        self.assertEqual(result['conversations'][0]['id'],OLD)

if __name__=='__main__':unittest.main()
