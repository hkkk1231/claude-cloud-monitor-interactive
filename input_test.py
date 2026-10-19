import unittest
from unittest.mock import patch
import input as channel

SCREEN = 'Previous output\n❯ \n────────────\nauto mode on'

class InputTests(unittest.TestCase):
    def test_prompt_guards(self):
        self.assertTrue(channel.empty_prompt(SCREEN))
        for screen in ['❯ unfinished\n────', '❯ \nmore draft\n────', 'Working…', '❯ \n────\nesc to interrupt', '❯ \n────\nEsc to cancel']:
            self.assertFalse(channel.empty_prompt(screen))

    def fake(self, text='中文\nquotes ` $() ; /model', screen=SCREEN, pasted=None):
        calls = []
        captures = 0
        def run(*args, data=None):
            nonlocal captures
            calls.append((args, data))
            if args[1] == 'list-panes':
                return '%0\tclaude\n'
            if args[1] == 'capture-pane':
                captures += 1
                return screen if captures <= 2 else (pasted if pasted is not None else '❯ '+text+'\n────')
            if args[1] == 'save-buffer':
                return text
            return ''
        return calls, run

    def test_send_is_one_paste_then_enter(self):
        text = '中文\nquotes ` $() ; /model'
        calls, fake = self.fake(text)
        with patch.object(channel, 'run', fake), patch.object(channel.time, 'sleep'):
            result = channel.execute('claude-shared', {'action':'send', 'text':text})
        self.assertTrue(result['ok'])
        writes = [(args[1], data) for args, data in calls if args[1] in ('load-buffer','paste-buffer','send-keys')]
        self.assertEqual([name for name, data in writes], ['load-buffer','paste-buffer','send-keys'])
        self.assertEqual(writes[0][1], text)
        self.assertEqual(next(args[-1] for args, _ in calls if args[1]=='send-keys'), 'Enter')

    def test_existing_draft_is_never_overwritten(self):
        calls, fake = self.fake(screen='❯ user draft\n────')
        with patch.object(channel, 'run', fake):
            with self.assertRaises(RuntimeError):
                channel.execute('claude-shared', {'action':'send','text':'new'})
        self.assertFalse(any(args[1] in ('load-buffer','paste-buffer','send-keys') for args, _ in calls))

    def test_dry_check_never_mutates(self):
        calls, fake = self.fake()
        with patch.object(channel, 'run', fake):
            self.assertTrue(channel.execute('claude-shared', {'action':'check','text':'dry'})['ok'])
        self.assertTrue(all(args[1] in ('list-panes','capture-pane') for args, _ in calls))

    def test_unverified_paste_does_not_submit(self):
        calls, fake = self.fake(text='new', pasted='unrecognized display')
        with patch.object(channel, 'run', fake), patch.object(channel.time, 'sleep'):
            result = channel.execute('claude-shared', {'action':'send','text':'new'})
        self.assertFalse(result['ok'])
        self.assertTrue(result['pasted'])
        self.assertFalse(any(args[1]=='send-keys' for args, _ in calls))

    def test_claude_paste_chip(self):
        calls, fake = self.fake(text='many lines', pasted='❯ [Pasted text #1 +3 lines]\n────')
        with patch.object(channel, 'run', fake), patch.object(channel.time, 'sleep'):
            self.assertTrue(channel.execute('claude-shared', {'action':'send','text':'many lines'})['ok'])

    def test_shortcuts_are_sent_directly_not_tmux_prefix(self):
        for key in ['BTab','M-p','M-t','M-o','C-o','C-t','C-r','C-l','C-b','C-s']:
            calls, fake = self.fake()
            with patch.object(channel, 'run', fake), patch.object(channel.time, 'sleep'):
                channel.execute('claude-shared', {'action':'key','key':key})
            self.assertEqual(next(args[-1] for args, _ in calls if args[1]=='send-keys'),key)

    def test_rewind_and_queue_sequences(self):
        for key, expected in [('Rewind',[('Escape',),('Escape',)]),('QueueNow',[('C-x','C-s')])]:
            calls, fake = self.fake()
            with patch.object(channel, 'run', fake), patch.object(channel.time, 'sleep'):
                channel.execute('claude-shared', {'action':'key','key':key})
            self.assertEqual([args[4:] for args, _ in calls if args[1]=='send-keys'],expected)

    def test_invalid_key_rejected(self):
        calls, fake = self.fake()
        with patch.object(channel, 'run', fake):
            with self.assertRaises(ValueError):
                channel.execute('claude-shared', {'action':'key','key':'C-d; shell'})
        self.assertFalse(any(args[1]=='send-keys' for args, _ in calls))

if __name__ == '__main__':
    unittest.main()
