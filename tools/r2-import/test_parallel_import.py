import contextlib
import gzip
import hashlib
import http.server
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import ssl
import sqlite3
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import patch

import import_assets as job
import parallel_import as parallel
from test_import_assets import MemoryS3


class ParallelTests(unittest.TestCase):
    def setUp(self):
        job.STOP.clear()
        parallel.STOP.clear()
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.source = self.root / 'data'
        self.source.mkdir()
        self.spool = self.root / 'spool'
        self.spool.mkdir()
        self.state = self.root / 'state'
        self.state.mkdir()
        self.config = dict(source=str(self.source), spools=[str(self.spool)], bucket='test',
                           endpoint='https://test.invalid', workers=2, processes=8,
                           bytes_per_second=0, minimum_age_seconds=0, max_decoded_bytes=1024,
                           create_first=True)
        self.client = MemoryS3()

    def tearDown(self):
        self.tmp.cleanup()

    def make_assets(self, count=96):
        for index in range(count):
            data = f'asset {index}'.encode()
            digest = hashlib.sha256(data).hexdigest()
            # Keep each top-level directory's casing consistent on macOS too.
            if int(digest[:2], 16) % 2:
                digest = digest.upper()
            path = self.source / (job.relative_key(digest) + '.gz')
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(gzip.compress(data))

    def test_partitions_are_disjoint_and_cover_files_and_spools(self):
        self.make_assets()
        digest = hashlib.sha256(b'spool').hexdigest().upper()
        (self.spool / (digest + '.asset')).write_bytes(b'spool')
        (self.source / 'unexpected-file').write_bytes(b'invalid')
        original = {str(p) for p, _, _ in job.sources(self.config)}
        seen = set()
        for index in range(8):
            current = {str(p) for p, _, _ in job.sources(parallel.child_config(self.config, index))}
            self.assertFalse(seen & current)
            seen.update(current)
        self.assertEqual(original, seen)

    def test_seed_and_resume_preserve_every_checkpoint_without_reupload(self):
        self.make_assets()
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, job.run(self.config, self.state, self.client, limit=12))
            parallel.prepare(self.state, self.config)
            self.assertEqual(12, parallel.totals(self.state, 8)['verified']['source_files'])
            for index in range(8):
                config = parallel.child_config(self.config, index)
                folder = self.state / 'shards' / f'{index:02d}'
                self.assertEqual(0, job.run(config, folder, self.client))
            self.assertEqual(96, parallel.totals(self.state, 8)['verified']['source_files'])
            self.assertEqual(96, self.client.puts)
            self.assertEqual(96, self.client.put_attempts)
            parallel.prepare(self.state, self.config)
            self.assertEqual(96, parallel.totals(self.state, 8)['verified']['source_files'])
        db = sqlite3.connect(self.state / 'manifest.sqlite3')
        self.assertEqual(12, db.execute('SELECT COUNT(*) FROM files').fetchone()[0])
        db.close()

    def test_changed_partition_layout_is_refused(self):
        with contextlib.redirect_stdout(io.StringIO()):
            parallel.prepare(self.state, self.config)
        with self.assertRaises(ValueError):
            parallel.prepare(self.state, dict(self.config, processes=4))

    def test_wrong_partition_identity_is_refused(self):
        with contextlib.redirect_stdout(io.StringIO()):
            parallel.prepare(self.state, self.config)
        with self.assertRaises(ValueError):
            job.open_manifest(self.state / 'shards' / '00', parallel.child_config(self.config, 1))

    def test_missing_partition_is_not_silently_recreated(self):
        with contextlib.redirect_stdout(io.StringIO()):
            parallel.prepare(self.state, self.config)
        (self.state / 'shards' / '03' / 'manifest.sqlite3').unlink()
        with self.assertRaises(ValueError):
            parallel.prepare(self.state, self.config)

    def test_unknown_source_path_prevents_publishing_partitions(self):
        db = job.open_manifest(self.state, self.config)
        db.execute('INSERT INTO files VALUES (?,?,?,?,?,?,?,?,?,?)',
                   (str(self.root / 'outside'), '', '', 'invalid', 0, 0, '', '', '', ''))
        db.commit()
        db.close()
        with self.assertRaises(ValueError):
            parallel.prepare(self.state, self.config)
        self.assertFalse((self.state / 'shards').exists())

    def test_child_invalid_exit_is_not_reported_as_completed_migration(self):
        class Finished:
            pid = 123
            returncode = 2
            def poll(self): return self.returncode
            def wait(self): return self.returncode
        with patch.object(parallel.subprocess, 'Popen', return_value=Finished()), \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(2, parallel.run(self.state, self.config))
        status = json.loads((self.state / 'status.json').read_text())
        self.assertEqual('scan_complete_with_errors', status['phase'])

    @unittest.skipUnless(importlib.util.find_spec('boto3') and shutil.which('openssl'),
                         'requires boto3 and openssl; exercised on fs01')
    def test_real_child_processes_copy_and_resume_against_local_https(self):
        self.make_assets(32)
        cert, key = self.root / 'cert.pem', self.root / 'key.pem'
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                        '-days', '1', '-keyout', str(key), '-out', str(cert),
                        '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost'],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        objects, mutex = {}, threading.Lock()
        counts = {'puts': 0, 'gets': 0}
        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version = 'HTTP/1.1'
            def log_message(self, *args): pass
            def reply(self, code, data=b''):
                self.send_response(code)
                self.send_header('Content-Length', str(len(data)))
                self.end_headers()
                if self.command != 'HEAD': self.wfile.write(data)
            def do_HEAD(self):
                with mutex:
                    if self.path in ('/test', '/test/'):
                        self.reply(200)
                    elif self.path in objects:
                        self.reply(200, objects[self.path])
                    else:
                        self.reply(404)
            def do_PUT(self):
                data = self.rfile.read(int(self.headers['Content-Length']))
                with mutex:
                    if self.headers.get('If-None-Match') != '*':
                        self.reply(400)
                    elif self.path in objects:
                        self.reply(412, b'<Error><Code>PreconditionFailed</Code></Error>')
                    else:
                        objects[self.path] = data
                        counts['puts'] += 1
                        self.reply(200)
            def do_GET(self):
                with mutex:
                    if self.path not in objects:
                        self.reply(404, b'<Error><Code>NoSuchKey</Code></Error>')
                    else:
                        counts['gets'] += 1
                        self.reply(200, objects[self.path])
        server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(cert, key)
        server.socket = context.wrap_socket(server.socket, server_side=True)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        config = dict(self.config, endpoint=f'https://localhost:{server.server_port}')
        env = {'OPENSIM_S3_ACCESS_KEY_ID': 'test-key',
               'OPENSIM_S3_SECRET_ACCESS_KEY': 'test-secret', 'AWS_CA_BUNDLE': str(cert),
               'NO_PROXY': 'localhost,127.0.0.1'}
        try:
            with patch.dict(os.environ, env), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, parallel.run(self.state, config))
                self.assertEqual(0, parallel.run(self.state, config))
            self.assertEqual(32, parallel.totals(self.state, 8)['verified']['source_files'])
            self.assertEqual({'puts': 32, 'gets': 32}, counts)
            data = b'new asset for verification-only coverage'
            digest = hashlib.sha256(data).hexdigest().upper()
            path = self.source / (job.relative_key(digest) + '.gz')
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(gzip.compress(data))
            with patch.dict(os.environ, env), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(2, parallel.run(self.state, config, verify_only=True))
            self.assertEqual({'puts': 32, 'gets': 64}, counts)
            self.assertEqual(1, parallel.totals(self.state, 8)['invalid']['source_files'])
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=5)


if __name__ == '__main__':
    unittest.main()
