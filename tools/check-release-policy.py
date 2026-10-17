#!/usr/bin/env python3
"""Offline checks for tools/release-metadata.sh, tools/publish-release.sh and tools/release-retention.py.

Runs the real scripts with a fake GitHub environment and a recording `gh`; never contacts GitHub.
"""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import sys
sys.dont_write_bytecode = True
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('retention', ROOT / 'tools/release-retention.py')
retention = importlib.util.module_from_spec(spec)
spec.loader.exec_module(retention)


class ReleasePolicyTests(unittest.TestCase):
    def metadata(self, ref, event='push', attempt='1', run_number='123', publish_tag='', success=True):
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / 'output'
            env = dict(os.environ, GITHUB_REF=ref, GITHUB_EVENT_NAME=event,
                       GITHUB_RUN_ID='12345', GITHUB_RUN_ATTEMPT=attempt,
                       GITHUB_RUN_NUMBER=run_number, PUBLISH_TAG=publish_tag,
                       GITHUB_OUTPUT=str(output))
            result = subprocess.run(['bash', 'tools/release-metadata.sh'], cwd=ROOT,
                                    env=env, text=True, capture_output=True)
            self.assertEqual(result.returncode == 0, success, result.stderr)
            return dict(line.split('=', 1) for line in output.read_text().splitlines()) if output.exists() else {}

    def test_exact_versions_for_publishing_branches(self):
        for branch in ('main', 'feature/new-ux', 'fix/watcher', 'chore/deps', 'release/1.2',
                       'feature/nested/some-work', 'mainline', 'feature/$(echo-untrusted)'):
            beta = branch != 'main'
            for event in ('push', 'workflow_dispatch'):
                for run_number, attempt in (('123', '1'), ('124', '1'), ('123', '2')):
                    with self.subTest(branch=branch, event=event, run=run_number, attempt=attempt):
                        version = f'1.{run_number}.0' + ('-beta' if beta else '')
                        data = self.metadata('refs/heads/' + branch, event,
                                             attempt=attempt, run_number=run_number)
                        self.assertEqual(data, {
                            'publish': 'true', 'version': version, 'modversion': version,
                            'tag': 'v' + version, 'title': 'catcom ' + version,
                            'prerelease': str(beta).lower(),
                            'channel': 'beta' if beta else 'main',
                        })

    def test_invalid_run_numbers(self):
        for branch in ('main', 'feature/test'):
            for invalid in ('', '0', '01', '-1', '1.2', '$(echo-untrusted)'):
                self.metadata('refs/heads/' + branch, run_number=invalid, success=False)

    def test_only_branch_push_or_dispatch_can_publish(self):
        for ref, event in [('refs/pull/42/merge', 'pull_request'),
                           ('refs/heads/feature/test', 'pull_request'),
                           ('refs/heads/main', 'pull_request'),
                           ('refs/tags/main', 'workflow_dispatch'),
                           ('refs/tags/v1.2.0', 'push')]:
            self.assertEqual(self.metadata(ref, event), {'publish': 'false'})
        self.assertEqual(self.metadata('refs/heads/feature/test', 'workflow_dispatch')['publish'], 'true')

    def test_natives_runs_publish_no_mod_release(self):
        for branch in ('main', 'feature/test'):
            self.assertEqual(self.metadata('refs/heads/' + branch, 'workflow_dispatch', publish_tag='natives-v3'),
                             {'publish': 'false'})

    def test_retention_isolates_channels(self):
        def release(number, tag, **flags):
            return dict(id=number, tag_name=tag, published_at='2026-10-17T00:00:00Z',
                        prerelease=False, draft=False) | flags
        pages = [[release(n, f'v1.{n}.0') for n in range(1, 151)],
                 [release(n, f'v1.{n}.0-beta', prerelease=True) for n in range(151, 251)]]
        pages[1] += [release(1000, 'v1.1000.0', draft=True),
                     release(1001, 'v1.1001.0', prerelease=True),
                     release(1002, 'v1.1002.0-beta'),
                     release(1003, 'natives-v1'),
                     release(1004, 'natives-v2', prerelease=True),
                     release(1005, 'v1.2.3'), release(1006, 'v1.1006.0-beta.1', prerelease=True)]
        self.assertEqual(retention.stale_tags(pages, 'main', 10), [f'v1.{n}.0' for n in range(140, 0, -1)])
        self.assertEqual(retention.stale_tags(pages, 'beta', 5), [f'v1.{n}.0-beta' for n in range(245, 150, -1)])
        self.assertEqual(retention.stale_tags([pages[0][:10]], 'main', 10), [])
        self.assertEqual(retention.stale_tags([], 'beta', 5), [])
        for channel in ('main', 'beta'):
            with self.assertRaises(ValueError): retention.stale_tags(pages, channel, 0)
        with self.assertRaises(ValueError): retention.stale_tags(pages, 'feature', 5)

    def test_retention_uses_publication_time(self):
        for channel, keep, suffix in (('main', 10, ''), ('beta', 5, '-beta')):
            releases = [dict(id=n, tag_name=f'v1.{n}.0{suffix}',
                             prerelease=channel == 'beta', draft=False,
                             created_at=f'2026-09-{n:02}T00:00:00Z',
                             published_at=f'2026-09-{keep+2-n:02}T00:00:00Z')
                        for n in range(1, keep + 2)]
            self.assertEqual(retention.stale_tags([releases], channel, keep),
                             [f'v1.{keep+1}.0{suffix}'])

    def publish(self, tag, exists, prerelease='false'):
        # Exercise the real publisher with a recording CLI; never contact GitHub.
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            gh = folder / 'gh'
            gh.write_text('#!/usr/bin/env python3\n'
                          'import json, os, sys\n'
                          'with open(os.environ["GH_CALLS"], "a") as log:\n'
                          '    log.write(json.dumps(sys.argv[1:]) + "\\n")\n'
                          'if sys.argv[1:3] == ["release", "view"]:\n'
                          '    sys.exit(0 if os.environ["GH_EXISTS"] == "true" else 1)\n')
            gh.chmod(0o755)
            log = folder / 'calls.jsonl'
            env = dict(os.environ, PATH=str(folder) + os.pathsep + os.environ['PATH'],
                       GH_CALLS=str(log), GH_EXISTS=str(exists).lower(),
                       GITHUB_REPOSITORY='example/repo', GITHUB_SHA='abc123',
                       ZIP=str(folder / f'catcom-{tag[1:]}.zip'), TAG=tag,
                       TITLE='catcom ' + tag[1:], PRERELEASE=prerelease)
            result = subprocess.run(['bash', 'tools/publish-release.sh'], cwd=ROOT,
                                    env=env, text=True, capture_output=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            return [json.loads(line) for line in log.read_text().splitlines()]

    def test_main_publish_and_rerun_never_delete(self):
        calls = self.publish('v1.123.0', exists=False)
        self.assertEqual([call[1] for call in calls], ['view', 'create'])
        self.assertIn('--target', calls[-1])
        self.assertTrue(calls[-1][3].endswith('catcom-1.123.0.zip'))
        self.assertNotIn('--prerelease', calls[-1])
        calls = self.publish('v1.123.0', exists=True)
        self.assertEqual([call[1] for call in calls], ['view'])

    def test_beta_publish_and_rerun(self):
        calls = self.publish('v1.123.0-beta', exists=False, prerelease='true')
        self.assertEqual([call[1] for call in calls], ['view', 'create'])
        self.assertIn('--prerelease', calls[-1])
        self.assertIn('--latest=false', calls[-1])
        calls = self.publish('v1.123.0-beta', exists=True, prerelease='true')
        self.assertEqual([call[1] for call in calls], ['view'])


if __name__ == '__main__':
    unittest.main()
