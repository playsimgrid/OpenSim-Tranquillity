#!/usr/bin/env python3
"""Coordinate disjoint source partitions while preserving single-process checkpoints."""
import argparse
import fcntl
import json
from pathlib import Path
import shutil
import signal
import sqlite3
import subprocess
import sys
import threading
import time

import import_assets as job

STOP = threading.Event()
COUNTERS = ('scanned', 'skipped_verified', 'submitted', 'verified', 'uploaded',
            'uploaded_bytes', 'verified_bytes', 'invalid', 'error', 'deferred')


def child_config(config, index):
    result = dict(config, shard_count=config['processes'], shard_index=index)
    return result


def prepare(state, config):
    """Seed partition manifests once, publishing the directory only after validation."""
    count = config['processes']
    target = state / 'shards'
    expected = {k: config[k] for k in ('endpoint', 'bucket', 'source', 'processes')}
    expected['spools'] = config.get('spools', [])
    if config.get('use_osgrid_format', False):
        expected['use_osgrid_format'] = True
    if target.exists():
        if json.loads((target / 'layout.json').read_text()) != expected:
            raise ValueError('Existing partitions belong to another source or layout')
        for index in range(count):
            if not (target / f'{index:02d}' / 'manifest.sqlite3').is_file():
                raise ValueError('Missing partition manifest; refusing partial restart')
        return
    staging = state / 'shards.initializing'
    if staging.exists():
        # Not published and never used by a child: safe to rebuild after a failed seed.
        shutil.rmtree(staging)
    staging.mkdir()
    children = []
    source = None
    try:
        for index in range(count):
            folder = staging / f'{index:02d}'
            folder.mkdir()
            children.append(job.open_manifest(folder, child_config(config, index)))
        original = state / 'manifest.sqlite3'
        copied = 0
        if original.exists():
            # This validates the original manifest identity before partitioning it.
            original_config = {k: v for k, v in config.items()
                               if k not in ('shard_count', 'shard_index')}
            source = job.open_manifest(state, original_config)
            for row in source.execute('SELECT * FROM files'):
                index = job.shard_for_path(Path(row[0]), dict(config, shard_count=count))
                children[index].execute('INSERT INTO files VALUES (?,?,?,?,?,?,?,?,?,?)', row)
                copied += 1
        for db in children:
            db.commit()
        actual = sum(db.execute('SELECT COUNT(*) FROM files').fetchone()[0] for db in children)
        if actual != copied:
            raise ValueError('Partition checkpoint count mismatch')
        for db in children:
            db.close()
        children.clear()
        (staging / 'layout.json').write_text(json.dumps(expected, indent=2) + '\n')
        staging.rename(target)
        print(json.dumps({'phase': 'partitions_seeded', 'records_preserved': copied,
                          'processes': count}), flush=True)
    finally:
        for db in children:
            db.close()
        if source:
            source.close()


def totals(state, count):
    result = {}
    for index in range(count):
        path = state / 'shards' / f'{index:02d}' / 'manifest.sqlite3'
        db = sqlite3.connect(f'file:{path}?mode=ro', uri=True)
        try:
            for status, number, size in db.execute(
                    'SELECT status,COUNT(*),COALESCE(SUM(stored_bytes),0) FROM files GROUP BY status'):
                item = result.setdefault(status, {'source_files': 0, 'stored_bytes': 0})
                item['source_files'] += number
                item['stored_bytes'] += size
        finally:
            db.close()
    return result


def report(state, config, children, started, phase, final=False):
    result = dict(started_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(started)),
                  bucket=config['bucket'], source=config['source'], transport='fs01-direct-to-r2',
                  processes=config['processes'], workers_per_process=config['workers'],
                  **{key: 0 for key in COUNTERS})
    result['shards'] = []
    for index, child in enumerate(children):
        path = state / 'shards' / f'{index:02d}' / 'status.json'
        current = {}
        if path.exists() and path.stat().st_mtime >= started:
            current = json.loads(path.read_text())
        for key in COUNTERS:
            result[key] += current.get(key, 0)
        result['shards'].append({'index': index, 'pid': child.pid, 'exit_code': child.poll(),
                                 'phase': current.get('phase', 'starting'),
                                 'updated_utc': current.get('updated_utc')})
    if final:
        result['manifest_totals'] = totals(state, config['processes'])
    job.report(state, result, phase)


def run(state, config, verify_only=False, reverify=False, retry_checksum_mismatches=False):
    if not 1 <= config.get('processes', 0) <= 16 or not 1 <= config.get('workers', 0) <= 256:
        raise ValueError('Invalid process or worker count')
    if config.get('bytes_per_second', 0):
        raise ValueError('Parallel coordinator requires an explicit zero application bandwidth cap')
    state.mkdir(parents=True, exist_ok=True)
    with (state / 'import.lock').open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        prepare(state, config)
        started = time.time()
        children, logs = [], []
        try:
            for index in range(config['processes']):
                folder = state / 'shards' / f'{index:02d}'
                cfg = folder / 'config.json'
                cfg.write_text(json.dumps(child_config(config, index), indent=2) + '\n')
                log = (folder / 'import.log').open('a', buffering=1)
                logs.append(log)
                command = [sys.executable, '-u', str(Path(__file__).with_name('import_assets.py')),
                           '--config', str(cfg), '--state', str(folder)]
                if verify_only:
                    command.append('--verify-only')
                if reverify:
                    command.append('--reverify')
                if retry_checksum_mismatches:
                    command.append('--retry-checksum-mismatches')
                children.append(subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT))
            report(state, config, children, started, 'copying')
            last_report = time.monotonic()
            terminated = False
            while any(child.poll() is None for child in children):
                failed = any(child.poll() not in (None, 0, 2) for child in children)
                if (STOP.is_set() or failed) and not terminated:
                    for child in children:
                        if child.poll() is None:
                            child.terminate()
                    terminated = True
                if time.monotonic() - last_report >= 30:
                    report(state, config, children, started, 'stopping' if terminated else 'copying')
                    last_report = time.monotonic()
                time.sleep(0.5)
            codes = [child.returncode for child in children]
            code = 1 if STOP.is_set() or any(c not in (0, 2) for c in codes) else (2 if 2 in codes else 0)
            phase = 'interrupted' if STOP.is_set() else ('failed' if code == 1 else
                    ('scan_complete_with_errors' if code == 2 else 'scan_complete'))
            report(state, config, children, started, phase, final=True)
            return code
        finally:
            for child in children:
                if child.poll() is None:
                    child.terminate()
            for child in children:
                child.wait()
            for log in logs:
                log.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', default='/etc/playsim-r2/import.json')
    parser.add_argument('--state', default='/var/lib/playsim-r2')
    parser.add_argument('--verify-only', action='store_true')
    parser.add_argument('--reverify', action='store_true')
    parser.add_argument('--retry-checksum-mismatches', action='store_true')
    args = parser.parse_args()
    signal.signal(signal.SIGTERM, lambda *_: STOP.set())
    signal.signal(signal.SIGINT, lambda *_: STOP.set())
    try:
        return run(Path(args.state), json.loads(Path(args.config).read_text()),
                   args.verify_only, args.reverify, args.retry_checksum_mismatches)
    except Exception as error:
        print(json.dumps(dict(phase='fatal', **job.exception_info(error))), flush=True)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
