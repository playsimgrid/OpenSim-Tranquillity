#!/usr/bin/env python3
"""Resume verified, create-only FSAssets imports directly from the source host to R2."""
import argparse
import base64
import concurrent.futures as futures
import fcntl
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import sqlite3
import stat
import tempfile
import threading
import time
import traceback

HEX = re.compile(r'^[0-9a-fA-F]{64}$')
OBJECT_XML = re.compile(rb'(?:<\?xml.*?\?>\s*)?<SceneObjectGroup(?:[\s>])', re.S)
LEGACY_NAMESPACES = re.compile(rb'(?<![\w:])xmlns:(?=(?:xsd|xsi)\s*=)')
STOP = threading.Event()


class SourceIssue(Exception):
    pass


class Deferred(Exception):
    pass


def relative_key(digest, osgrid=False):
    if not HEX.fullmatch(digest):
        raise SourceIssue('invalid_hash_filename')
    if osgrid:
        return f'{digest[:3]}/{digest[3:6]}/{digest}'
    return f'{digest[:2]}/{digest[2:4]}/{digest[4:6]}/{digest[6:10]}/{digest}'


def file_key(path, root, spool=False, osgrid=False):
    if spool:
        if path.parent != root or not path.name.endswith('.asset'):
            raise SourceIssue('unexpected_spool_path')
        digest = path.name[:-6]
        return relative_key(digest, osgrid), digest, False
    compressed = path.name.endswith('.gz')
    digest = path.name[:-3] if compressed else path.name
    expected = relative_key(digest, osgrid) + ('.gz' if compressed else '')
    actual = path.relative_to(root).as_posix()
    if actual != expected:
        raise SourceIssue('unexpected_hash_directory_layout')
    return actual, digest, compressed


def fingerprint(info):
    return f'{info.st_dev}:{info.st_ino}:{info.st_size}:{info.st_mtime_ns}:{info.st_ctime_ns}'


def repair_trailing_nul(temp, expected_hash):
    """Replace a temporary gzip only if removing exactly one NUL restores its hash."""
    fixed = temp.with_suffix('.repaired')
    digest = hashlib.sha256()
    last = b''
    try:
        with gzip.open(temp, 'rb') as source, fixed.open('xb') as target:
            with gzip.GzipFile(filename='', mode='wb', fileobj=target, mtime=0) as encoded:
                while chunk := source.read(1024 * 1024):
                    data = last + chunk[:-1]
                    digest.update(data)
                    encoded.write(data)
                    last = chunk[-1:]
        if last != b'\0' or digest.hexdigest() != expected_hash.lower():
            raise SourceIssue('decoded_sha256_mismatch')
        fixed.replace(temp)
        encoded_sha = hashlib.sha256()
        md5 = hashlib.md5()
        with temp.open('rb') as source:
            while chunk := source.read(1024 * 1024):
                encoded_sha.update(chunk)
                md5.update(chunk)
        return temp.stat().st_size, encoded_sha, md5
    finally:
        fixed.unlink(missing_ok=True)


def repair_legacy_namespaces(temp, expected_hash, decoded_size):
    """Recover the exact bytes that the legacy writer hashed before sanitizing XML."""
    if not 0 < decoded_size <= 8 * 1024 * 1024:
        raise SourceIssue('decoded_sha256_mismatch')
    with gzip.open(temp, 'rb') as source:
        data = source.read(decoded_size + 1)
    if not data.endswith(b'\0') or not OBJECT_XML.match(data[:4096].lstrip()):
        raise SourceIssue('decoded_sha256_mismatch')
    original = LEGACY_NAMESPACES.sub(b'xmlns:xmlns:', data)
    if original == data or hashlib.sha256(original).hexdigest() != expected_hash.lower():
        raise SourceIssue('decoded_sha256_mismatch')
    encoded = gzip.compress(original, mtime=0)
    temp.write_bytes(encoded)
    return len(encoded), len(original), hashlib.sha256(encoded), hashlib.md5(encoded)


def snapshot(path, expected_hash, compressed, state, max_bytes, recover_nul=False, recover_xml=False):
    """Take a stable local copy, preserving encoded bytes, then validate decoded SHA-256/CRC."""
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    temp = None
    try:
        with os.fdopen(fd, 'rb') as source:
            before = os.fstat(source.fileno())
            if not stat.S_ISREG(before.st_mode):
                raise SourceIssue('not_regular_file')
            if before.st_size > max_bytes + 1048576:
                raise SourceIssue('encoded_asset_exceeds_limit')
            temp_fd, temp_name = tempfile.mkstemp(prefix='snapshot-', dir=state / 'tmp')
            temp = Path(temp_name)
            encoded_sha = hashlib.sha256()
            md5 = hashlib.md5()
            size = 0
            with os.fdopen(temp_fd, 'wb') as target:
                while chunk := source.read(1024 * 1024):
                    size += len(chunk)
                    if size > max_bytes + 1048576:
                        raise SourceIssue('encoded_asset_exceeds_limit')
                    encoded_sha.update(chunk)
                    md5.update(chunk)
                    target.write(chunk)
            after = os.fstat(source.fileno())
            try:
                current = path.lstat()
            except FileNotFoundError:
                raise Deferred('source_moved_during_snapshot')
            if fingerprint(before) != fingerprint(after) or fingerprint(after) != fingerprint(current):
                raise Deferred('source_changed_during_snapshot')
        decoded_sha = hashlib.sha256()
        decoded_size = 0
        try:
            with (gzip.open(temp, 'rb') if compressed else temp.open('rb')) as decoded:
                while chunk := decoded.read(1024 * 1024):
                    decoded_size += len(chunk)
                    if decoded_size > max_bytes:
                        raise SourceIssue('decoded_asset_exceeds_limit')
                    decoded_sha.update(chunk)
        except (gzip.BadGzipFile, EOFError, OSError) as error:
            raise SourceIssue('invalid_gzip_or_unreadable_snapshot') from error
        if decoded_sha.hexdigest().lower() != expected_hash.lower():
            if not compressed or not (recover_nul or recover_xml):
                raise SourceIssue('decoded_sha256_mismatch')
            try:
                if not recover_nul:
                    raise SourceIssue('decoded_sha256_mismatch')
                size, encoded_sha, md5 = repair_trailing_nul(temp, expected_hash)
                decoded_size -= 1
            except SourceIssue:
                if not recover_xml:
                    raise
                size, decoded_size, encoded_sha, md5 = repair_legacy_namespaces(temp, expected_hash, decoded_size)
                if decoded_size > max_bytes:
                    raise SourceIssue('decoded_asset_exceeds_limit')
        return {'temp': temp, 'fingerprint': fingerprint(before), 'bytes': size,
                'decoded_bytes': decoded_size, 'stored_sha256': encoded_sha.hexdigest(),
                'md5': base64.b64encode(md5.digest()).decode()}
    except BaseException:
        if temp:
            temp.unlink(missing_ok=True)
        raise


class Throttle:
    def __init__(self, bytes_per_second):
        self.rate = bytes_per_second
        self.next = 0.0
        self.lock = threading.Lock()

    def wait(self, size):
        if not self.rate:
            return
        with self.lock:
            now = time.monotonic()
            delay = max(0.0, self.next - now)
            self.next = max(now, self.next) + size / self.rate
        if STOP.wait(delay):
            raise Deferred('stopping')


def error_code(error):
    fallback = type(error).__name__
    response = getattr(error, 'response', None)
    detail = response.get('Error') if isinstance(response, dict) else None
    code = detail.get('Code') if isinstance(detail, dict) else None
    return code if isinstance(code, (str, int)) and code != '' else fallback


def exception_info(error):
    """Keep code locations and types, never exception messages, locals or HTTP bodies."""
    result = {'error_type': type(error).__name__,
              'error_code': re.sub(r'[^A-Za-z0-9_]', '', str(error_code(error)))[:100],
              'frames': [{'file': Path(frame.filename).name, 'line': frame.lineno,
                          'function': frame.name}
                         for frame in traceback.extract_tb(error.__traceback__)[-12:]]}
    if isinstance(error, AttributeError):
        name = getattr(error, 'name', None)
        if isinstance(name, str) and re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', name):
            result['attribute'] = name
        result['attribute_owner_type'] = type(getattr(error, 'obj', None)).__name__
    cause = error.__cause__ or error.__context__
    if cause is not None:
        result['cause_type'] = type(cause).__name__
    return result


def head(client, bucket, key):
    try:
        return client.head_object(Bucket=bucket, Key=key)
    except Exception as error:
        if error_code(error) in ('404', 'NoSuchKey', 'NotFound'):
            return None
        raise


def verify_remote(client, bucket, key, info, throttle):
    throttle.wait(info['bytes'])
    response = client.get_object(Bucket=bucket, Key=key)
    try:
        if response.get('ContentEncoding'):
            raise SourceIssue('remote_content_encoding_must_be_unset')
        if response['ContentLength'] != info['bytes']:
            raise SourceIssue('remote_size_conflict')
        digest = hashlib.sha256()
        size = 0
        while chunk := response['Body'].read(1024 * 1024):
            size += len(chunk)
            if size > info['bytes']:
                raise SourceIssue('remote_size_conflict')
            digest.update(chunk)
        if size != info['bytes'] or digest.hexdigest() != info['stored_sha256']:
            raise SourceIssue('remote_sha256_conflict')
        return response.get('ETag', '').strip('"')
    finally:
        response['Body'].close()


def verify_equivalent_gzip(client, bucket, key, info, digest, throttle):
    """Verify a gzip written by another writer and record its actual stored bytes."""
    throttle.wait(info['bytes'])
    response = client.get_object(Bucket=bucket, Key=key)
    try:
        if response.get('ContentEncoding'):
            raise SourceIssue('remote_content_encoding_must_be_unset')
        max_size = info['decoded_bytes'] + 1048576
        size = 0
        stored_hash = hashlib.sha256()
        with tempfile.TemporaryFile(dir=info['temp'].parent) as encoded:
            while chunk := response['Body'].read(1024 * 1024):
                size += len(chunk)
                if size > max_size:
                    raise SourceIssue('remote_size_conflict')
                stored_hash.update(chunk)
                encoded.write(chunk)
            if size != response['ContentLength']:
                raise SourceIssue('remote_size_conflict')
            encoded.seek(0)
            decoded_hash = hashlib.sha256()
            decoded_size = 0
            try:
                with gzip.GzipFile(fileobj=encoded) as body:
                    while chunk := body.read(1024 * 1024):
                        decoded_size += len(chunk)
                        if decoded_size > info['decoded_bytes']:
                            raise SourceIssue('remote_size_conflict')
                        decoded_hash.update(chunk)
            except (OSError, EOFError) as error:
                raise SourceIssue('remote_sha256_conflict') from error
            if decoded_size != info['decoded_bytes'] or decoded_hash.hexdigest() != digest.lower():
                raise SourceIssue('remote_sha256_conflict')
        return response.get('ETag', '').strip('"'), size, stored_hash.hexdigest()
    finally:
        response['Body'].close()


def transfer(client, config, state, throttle, path, key, digest, compressed, verify_only=False):
    info = None
    result = {'path': str(path), 'key': key, 'status': 'error', 'bytes': 0, 'decoded_bytes': 0,
              'fingerprint': '', 'stored_sha256': '', 'etag': '', 'error': '', 'uploaded': False}
    try:
        info = snapshot(path, digest, compressed, state, config['max_decoded_bytes'],
                        config.get('recover_trailing_nul', False), config.get('recover_legacy_namespaces', False))
        result.update({k: v for k, v in info.items() if k != 'temp' and k != 'md5'})
        # A conditional PUT already proves absence atomically. During a bulk import,
        # avoid an extra HEAD round trip; a 412 still takes the full readback path.
        existing = (None if config.get('create_first', False) and not verify_only
                    else head(client, config['bucket'], key))
        if existing is None:
            if verify_only:
                raise SourceIssue('destination_missing')
            throttle.wait(info['bytes'])
            try:
                with info['temp'].open('rb') as content:
                    client.put_object(Bucket=config['bucket'], Key=key, Body=content,
                                      ContentLength=info['bytes'], ContentMD5=info['md5'],
                                      ContentType='application/octet-stream',
                                      Metadata={'stored-sha256': info['stored_sha256'],
                                                'asset-sha256': digest.lower(),
                                                'decoded-bytes': str(info['decoded_bytes'])})
                result['uploaded'] = True
            except Exception as error:
                # Conditional create handles retries and competing writers without overwriting.
                if error_code(error) not in ('PreconditionFailed', '412'):
                    raise
        if config.get('allow_equivalent_gzip', False) and compressed and not result['uploaded']:
            result['etag'], result['bytes'], result['stored_sha256'] = verify_equivalent_gzip(
                client, config['bucket'], key, info, digest, throttle)
        else:
            result['etag'] = verify_remote(client, config['bucket'], key, info, throttle)
        result['status'] = 'verified'
    except Deferred as error:
        result['status'], result['error'] = 'deferred', str(error)
    except SourceIssue as error:
        result['status'], result['error'] = 'invalid', str(error)
    except FileNotFoundError:
        result['status'], result['error'] = 'deferred', 'source_moved'
    except Exception as error:
        # Never persist SDK exception text: only its sanitized code/type, never request credentials.
        result['error'] = re.sub(r'[^A-Za-z0-9_]', '', str(error_code(error)))[:100]
        result['diagnostic'] = exception_info(error)
    finally:
        if info:
            info['temp'].unlink(missing_ok=True)
    return result


def shard_for_path(path, config):
    """Keep every representation of a hash in one disjoint process partition."""
    count = config.get('shard_count', 1)
    path = Path(path)
    try:
        first = path.relative_to(Path(config['source'])).parts[0]
        width = 3 if config.get('use_osgrid_format', False) else 2
        prefix = first[:2] if len(first) == width and all(c in '0123456789abcdefABCDEF' for c in first) else '00'
    except (ValueError, IndexError):
        if path.parent not in [Path(p) for p in config.get('spools', [])]:
            raise ValueError('Manifest path is outside the configured sources')
        digest = path.name[:-6] if path.name.endswith('.asset') else ''
        prefix = digest[:2] if HEX.fullmatch(digest) else '00'
    return int(prefix, 16) % count


def sources(config):
    root = Path(config['source'])
    shard = config.get('shard_index', 0)
    def walk(directory, top=False):
        # DirEntry uses the directory's type information instead of issuing an
        # extra lstat for every hash directory. Never follow a directory symlink.
        with os.scandir(directory) as listing:
            entries = sorted(listing, key=lambda entry: entry.name)
        directories = []
        for entry in entries:
            if top and shard_for_path(Path(entry.path), config) != shard:
                continue
            if entry.is_dir(follow_symlinks=False):
                directories.append(entry.path)
            else:
                yield Path(entry.path), root, False
        for child in directories:
            yield from walk(child)
    yield from walk(root, top=True)
    for spool in config.get('spools', []):
        root = Path(spool)
        if not root.is_dir():
            continue
        # Never import the unfinished nested spool/ directory or any .tmp files.
        for path in sorted(root.glob('*.asset')):
            if shard_for_path(path, config) == shard:
                yield path, root, True


def open_manifest(state, config):
    db = sqlite3.connect(state / 'manifest.sqlite3')
    db.execute('PRAGMA journal_mode=WAL')
    db.execute('PRAGMA synchronous=FULL')
    db.execute('CREATE TABLE IF NOT EXISTS identity (value TEXT NOT NULL)')
    identity_fields = {k: config[k] for k in ('endpoint', 'bucket', 'source')}
    if config.get('use_osgrid_format', False):
        identity_fields['use_osgrid_format'] = True
    if config.get('shard_count', 1) > 1:
        identity_fields.update(shard_count=config['shard_count'], shard_index=config['shard_index'])
    identity = json.dumps(identity_fields, sort_keys=True)
    old = db.execute('SELECT value FROM identity').fetchone()
    if old and old[0] != identity:
        db.close()
        raise ValueError('Manifest belongs to a different source or destination')
    if not old:
        db.execute('INSERT INTO identity VALUES (?)', (identity,))
    db.execute('''CREATE TABLE IF NOT EXISTS files (
        path TEXT PRIMARY KEY, object_key TEXT, fingerprint TEXT, status TEXT,
        stored_bytes INTEGER, decoded_bytes INTEGER, stored_sha256 TEXT, etag TEXT,
        error TEXT, updated_utc TEXT)''')
    db.commit()
    return db


def record(db, item):
    db.execute('''INSERT INTO files VALUES (?,?,?,?,?,?,?,?,?,?)
        ON CONFLICT(path) DO UPDATE SET object_key=excluded.object_key,
        fingerprint=excluded.fingerprint,status=excluded.status,stored_bytes=excluded.stored_bytes,
        decoded_bytes=excluded.decoded_bytes,stored_sha256=excluded.stored_sha256,
        etag=excluded.etag,error=excluded.error,updated_utc=excluded.updated_utc''',
        (item['path'], item['key'], item['fingerprint'], item['status'], item['bytes'],
         item['decoded_bytes'], item['stored_sha256'], item['etag'], item['error'],
         time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())))


def report(state, counters, phase):
    payload = dict(counters, phase=phase, updated_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()))
    target = state / 'status.json'
    temp = target.with_suffix('.new')
    temp.write_text(json.dumps(payload, indent=2) + '\n')
    temp.replace(target)
    print(json.dumps(payload, sort_keys=True), flush=True)


def run(config, state, client, limit=0, reverify=False, verify_only=False, retry_checksum_mismatches=False):
    state.mkdir(parents=True, exist_ok=True)
    (state / 'tmp').mkdir(exist_ok=True)
    with (state / 'import.lock').open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        for old in (state / 'tmp').glob('snapshot-*'):
            old.unlink()
        if not Path(config['source']).is_dir():
            raise ValueError('Source directory is unavailable')
        client.head_bucket(Bucket=config['bucket'])
        db = open_manifest(state, config)
        counters = dict(started_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
                        bucket=config['bucket'], source=config['source'], transport='fs01-direct-to-r2',
                        scanned=0, skipped_verified=0, submitted=0, verified=0, uploaded=0,
                        uploaded_bytes=0, verified_bytes=0, invalid=0, error=0, deferred=0)
        throttle = Throttle(config.get('bytes_per_second', 52428800))
        last_report = time.monotonic()
        last_commit = time.monotonic()
        pending = set()
        workers = config.get('workers', 16)
        report(state, counters, 'copying')

        def collect(block=False):
            nonlocal pending, last_report, last_commit
            if pending:
                done, pending = futures.wait(pending, timeout=1 if block else 0,
                                             return_when=futures.FIRST_COMPLETED)
                for future in done:
                    item = future.result()
                    # Keep failed recovery candidates eligible for another targeted retry.
                    if not (retry_checksum_mismatches and item['status'] in ('error', 'deferred')):
                        record(db, item)
                    counters[item['status']] += 1
                    if item['status'] == 'verified':
                        counters['verified_bytes'] += item['bytes']
                    if item['uploaded']:
                        counters['uploaded'] += 1
                        counters['uploaded_bytes'] += item['bytes']
                    if item['status'] in ('invalid', 'error'):
                        entry = {'file': item['path'], 'status': item['status'], 'reason': item['error']}
                        if 'diagnostic' in item:
                            entry['diagnostic'] = item['diagnostic']
                        print(json.dumps(entry), flush=True)
                    if item['error'] in ('AccessDenied', 'InvalidAccessKeyId', 'SignatureDoesNotMatch', 'NoSuchBucket'):
                        STOP.set()
                # Keep fsync out of the per-asset hot path. A lost checkpoint is
                # safely replayed through conditional creation and full readback.
                if done and time.monotonic() - last_commit >= 1:
                    db.commit()
                    last_commit = time.monotonic()
            if time.monotonic() - last_report >= 30:
                db.commit()
                last_commit = time.monotonic()
                report(state, counters, 'copying')
                last_report = time.monotonic()

        try:
            if retry_checksum_mismatches:
                rows = db.execute("SELECT path FROM files WHERE status='invalid' "
                                  "AND error='decoded_sha256_mismatch' AND path LIKE '%.gz'").fetchall()
                candidates = ((Path(row[0]), Path(config['source']), False) for row in rows)
            else:
                candidates = sources(config)
            with futures.ThreadPoolExecutor(max_workers=workers) as pool:
                for path, root, spool in candidates:
                    if STOP.is_set() or (limit and counters['submitted'] >= limit):
                        break
                    counters['scanned'] += 1
                    try:
                        info = path.lstat()
                        if not stat.S_ISREG(info.st_mode):
                            raise SourceIssue('not_regular_file')
                        key, digest, compressed = file_key(path, root, spool, config.get('use_osgrid_format', False))
                        current = fingerprint(info)
                        prior = db.execute('SELECT fingerprint,status FROM files WHERE path=?', (str(path),)).fetchone()
                        if prior and prior[0] == current and prior[1] == 'verified' and not (reverify or verify_only):
                            counters['skipped_verified'] += 1
                            collect()
                            continue
                        if time.time() - info.st_mtime < config.get('minimum_age_seconds', 60):
                            counters['deferred'] += 1
                            continue
                    except FileNotFoundError:
                        counters['deferred'] += 1
                        continue
                    except SourceIssue as error:
                        counters['invalid'] += 1
                        item = dict(path=str(path), key='', fingerprint='', status='invalid', bytes=0,
                                    decoded_bytes=0, stored_sha256='', etag='', error=str(error))
                        record(db, item)
                        print(json.dumps({'file': str(path), 'status': 'invalid', 'reason': str(error)}), flush=True)
                        continue
                    pending.add(pool.submit(transfer, client, config, state, throttle, path, key, digest,
                                            compressed, verify_only))
                    counters['submitted'] += 1
                    while len(pending) >= workers * 2:
                        collect(True)
                    collect()
                while pending:
                    collect(True)
            db.commit()
            totals = db.execute('SELECT status,COUNT(*),COALESCE(SUM(stored_bytes),0) FROM files GROUP BY status').fetchall()
            counters['manifest_totals'] = {status: {'source_files': count, 'stored_bytes': size} for status, count, size in totals}
            counters['largest_decoded_asset'] = db.execute('SELECT COALESCE(MAX(decoded_bytes),0) FROM files WHERE status="verified"').fetchone()[0]
            phase = 'interrupted' if STOP.is_set() else ('sample_complete' if limit else 'scan_complete')
            if counters['error'] or counters['invalid']:
                phase += '_with_errors'
            report(state, counters, phase)
            return 1 if STOP.is_set() or counters['error'] else (2 if counters['invalid'] else 0)
        finally:
            db.close()


def build_client(config):
    import boto3
    from botocore.config import Config
    client = boto3.client('s3', endpoint_url=config['endpoint'], region_name='auto',
                          aws_access_key_id=os.environ['OPENSIM_S3_ACCESS_KEY_ID'],
                          aws_secret_access_key=os.environ['OPENSIM_S3_SECRET_ACCESS_KEY'],
                          config=Config(signature_version='s3v4', connect_timeout=10, read_timeout=90,
                                        retries={'mode': 'standard', 'max_attempts': 5},
                                        max_pool_connections=config.get('workers', 16) + 4,
                                        s3={'addressing_style': 'path'}))
    # Ubuntu's boto3 predates the modeled IfNoneMatch parameter. Add it before signing.
    def create_only(request, **kwargs):
        request.headers['If-None-Match'] = '*'
    client.meta.events.register('before-sign.s3.PutObject', create_only)
    return client


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', default='/etc/playsim-r2/import.json')
    parser.add_argument('--state', default='/var/lib/playsim-r2')
    parser.add_argument('--limit', type=int, default=0, help='Bounded initial sample; zero means full scan')
    parser.add_argument('--reverify', action='store_true', help='Recheck unchanged verified source files against R2')
    parser.add_argument('--verify-only', action='store_true', help='Read/check R2; never upload missing objects')
    parser.add_argument('--retry-checksum-mismatches', action='store_true',
                        help='Scan only files recorded with decoded checksum mismatches')
    args = parser.parse_args()
    signal.signal(signal.SIGTERM, lambda *_: STOP.set())
    signal.signal(signal.SIGINT, lambda *_: STOP.set())
    config = json.loads(Path(args.config).read_text())
    shards = config.get('shard_count', 1)
    if not 1 <= shards <= 16 or not 0 <= config.get('shard_index', 0) < shards:
        parser.error('Invalid shard assignment')
    if not config['endpoint'].startswith('https://') or not 1 <= config.get('workers', 16) <= 256:
        parser.error('Invalid endpoint or worker count')
    try:
        return run(config, Path(args.state), build_client(config), args.limit, args.reverify,
                   args.verify_only, args.retry_checksum_mismatches)
    except Exception as error:
        print(json.dumps(dict(phase='fatal', **exception_info(error))), flush=True)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
