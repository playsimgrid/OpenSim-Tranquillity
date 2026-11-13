import base64
import gzip
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import import_assets as job


class S3Error(Exception):
    def __init__(self, code):
        self.response = {'Error': {'Code': code}}


class MemoryS3:
    def __init__(self):
        self.objects = {}
        self.puts = 0
        self.put_attempts = 0
        self.gets = 0
        self.fail_put = False
        self.corrupt_get = False
        self.encoding = None

    def head_bucket(self, **_):
        return {}

    def head_object(self, Bucket, Key):
        if Key not in self.objects:
            raise S3Error('404')
        return {'ContentLength': len(self.objects[Key])}

    def put_object(self, Bucket, Key, Body, ContentLength, ContentMD5, **kwargs):
        self.put_attempts += 1
        if self.fail_put:
            raise S3Error('AccessDenied')
        if Key in self.objects:
            raise S3Error('PreconditionFailed')
        data = Body.read()
        assert len(data) == ContentLength
        assert base64.b64encode(hashlib.md5(data).digest()).decode() == ContentMD5
        assert 'ContentEncoding' not in kwargs
        assert Key not in self.objects, 'existing remote data must never be overwritten'
        self.objects[Key] = data
        self.puts += 1

    def get_object(self, Bucket, Key):
        self.gets += 1
        if Key not in self.objects:
            raise S3Error('NoSuchKey')
        data = self.objects[Key]
        if self.corrupt_get:
            data = b'X' * len(data)
        return {'ContentLength': len(data), 'Body': io.BytesIO(data), 'ContentEncoding': self.encoding,
                'ETag': '"' + hashlib.md5(data).hexdigest() + '"'}


class ImportTests(unittest.TestCase):
    def setUp(self):
        job.STOP.clear()
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.source = self.root / 'data'
        self.source.mkdir()
        self.state = self.root / 'state'
        (self.state / 'tmp').mkdir(parents=True)
        self.client = MemoryS3()
        self.config = {'source': str(self.source), 'bucket': 'test-assets', 'endpoint': 'https://test.invalid',
                       'workers': 2, 'bytes_per_second': 0, 'minimum_age_seconds': 0, 'max_decoded_bytes': 1024}
        self.throttle = job.Throttle(0)

    def tearDown(self):
        self.tmp.cleanup()
        job.STOP.clear()

    def asset(self, data=b'asset bytes', compressed=True, lower=False):
        digest = hashlib.sha256(data).hexdigest()
        if not lower:
            digest = digest.upper()
        key = job.relative_key(digest) + ('.gz' if compressed else '')
        path = self.source / key
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(gzip.compress(data) if compressed else data)
        return path, key, digest, compressed

    def transfer(self, asset, verify_only=False):
        return job.transfer(self.client, self.config, self.state, self.throttle, *asset, verify_only)

    def test_copy_preserves_gzip_bytes_and_case_and_reads_back(self):
        for lower in (True, False):
            asset = self.asset(lower=lower)
            original = asset[0].read_bytes()
            result = self.transfer(asset)
            self.assertEqual('verified', result['status'])
            self.assertEqual(original, self.client.objects[asset[1]])
            self.assertEqual(original, asset[0].read_bytes())
        self.assertEqual(2, self.client.gets)
        self.assertEqual([], list((self.state / 'tmp').iterdir()))

    def test_plain_asset(self):
        asset = self.asset(compressed=False)
        self.assertEqual('verified', self.transfer(asset)['status'])
        self.assertFalse(asset[1].endswith('.gz'))

    def test_wrong_source_hash_never_uploads(self):
        asset = self.asset()
        asset[0].write_bytes(gzip.compress(b'wrong data'))
        result = self.transfer(asset)
        self.assertEqual('invalid', result['status'])
        self.assertEqual('decoded_sha256_mismatch', result['error'])
        self.assertEqual(0, self.client.puts)

    def test_concurrent_writer_gzip_is_verified_by_decoded_hash(self):
        data = b'asset bytes' * 80
        asset = self.asset(data)
        existing = gzip.compress(data, compresslevel=1, mtime=0)
        self.client.objects[asset[1]] = existing
        self.config.update(create_first=True, allow_equivalent_gzip=True)
        result = self.transfer(asset)
        self.assertEqual('verified', result['status'])
        self.assertEqual(len(existing), result['bytes'])
        self.assertEqual(hashlib.sha256(existing).hexdigest(), result['stored_sha256'])
        self.assertEqual(0, self.client.puts)
        self.client.objects[asset[1]] = gzip.compress(b'X' * len(data), mtime=0)
        self.assertEqual('invalid', self.transfer(asset)['status'])

    def test_osgrid_layout_uses_the_same_partition_for_spools_and_data(self):
        digest = 'ABCDEF' + '1' * 58
        path = self.source / ('ABC/DEF/' + digest + '.gz')
        spool = self.root / 'spool'
        config = dict(self.config, use_osgrid_format=True, shard_count=8, spools=[str(spool)])
        self.assertEqual(('ABC/DEF/' + digest + '.gz', digest, True),
                         job.file_key(path, self.source, osgrid=True))
        self.assertEqual(3, job.shard_for_path(path, config))
        self.assertEqual(3, job.shard_for_path(spool / (digest + '.asset'), config))

    def test_targeted_recovery_removes_only_one_nul_and_preserves_source(self):
        data = b'x' * (1024 * 1024) + b'last chunk'
        self.config.update(recover_trailing_nul=True, create_first=True, max_decoded_bytes=2 * 1024 * 1024)
        good = self.asset(data)
        bad = self.asset(b'unrelated')
        untouched = self.asset(b'outside the recovery list')
        original = gzip.compress(data + b'\0')
        good[0].write_bytes(original)
        bad[0].write_bytes(gzip.compress(b'unrelated\0\0'))
        with job.open_manifest(self.state, self.config) as db:
            for asset in (good, bad):
                result = job.transfer(self.client, dict(self.config, recover_trailing_nul=False),
                                      self.state, self.throttle, *asset)
                job.record(db, result)
        self.assertEqual(2, job.run(self.config, self.state, self.client, retry_checksum_mismatches=True))
        self.assertEqual({good[1]}, set(self.client.objects))
        self.assertEqual(data, gzip.decompress(self.client.objects[good[1]]))
        self.assertEqual(original, good[0].read_bytes())
        self.assertNotIn(untouched[1], self.client.objects)
        self.assertEqual('verified', self.transfer(good)['status'])
        self.assertEqual(1, self.client.puts)

    def test_corrupt_gzip_never_uploads(self):
        asset = self.asset()
        asset[0].write_bytes(b'broken gzip')
        self.assertEqual('invalid', self.transfer(asset)['status'])
        self.assertEqual(0, self.client.puts)

    def test_legacy_namespace_recovery_requires_the_exact_original_hash(self):
        original = b'<SceneObjectGroup xmlns:xmlns:xsi="a" xmlns:xmlns:xsd="b"/>\0'
        current = gzip.compress(original.replace(b'xmlns:xmlns:', b'xmlns:'))
        asset = self.asset(original)
        asset[0].write_bytes(current)
        self.assertEqual('invalid', self.transfer(asset)['status'])
        self.config.update(recover_trailing_nul=True, recover_legacy_namespaces=True, create_first=True)
        self.assertEqual('verified', self.transfer(asset)['status'])
        self.assertEqual(original, gzip.decompress(self.client.objects[asset[1]]))
        self.assertEqual(current, asset[0].read_bytes())
        empty = self.asset(b'expected content')
        empty[0].write_bytes(b'')
        self.assertEqual('decoded_sha256_mismatch', self.transfer(empty)['error'])
        self.assertEqual(1, self.client.puts)

    def test_destination_conflict_is_not_overwritten(self):
        asset = self.asset()
        self.client.objects[asset[1]] = b'do not overwrite'
        self.assertEqual('invalid', self.transfer(asset)['status'])
        self.assertEqual(b'do not overwrite', self.client.objects[asset[1]])
        self.assertEqual(0, self.client.puts)

    def test_identical_existing_object_is_verified_without_put(self):
        asset = self.asset()
        self.client.objects[asset[1]] = asset[0].read_bytes()
        self.assertEqual('verified', self.transfer(asset)['status'])
        self.assertEqual(0, self.client.puts)
        self.assertEqual(1, self.client.gets)

    def test_create_first_skips_head_and_verifies_new_object(self):
        self.config['create_first'] = True
        def unexpected_head(**kwargs):
            raise AssertionError('create-first must not issue HEAD')
        self.client.head_object = unexpected_head
        self.assertEqual('verified', self.transfer(self.asset())['status'])
        self.assertEqual(1, self.client.puts)
        self.assertEqual(1, self.client.gets)

    def test_create_first_existing_object_is_not_overwritten(self):
        self.config['create_first'] = True
        asset = self.asset()
        original = asset[0].read_bytes()
        self.client.objects[asset[1]] = original
        self.assertEqual('verified', self.transfer(asset)['status'])
        self.assertEqual(1, self.client.put_attempts)
        self.assertEqual(0, self.client.puts)
        self.assertEqual(1, self.client.gets)
        self.assertEqual(original, self.client.objects[asset[1]])

    def test_create_first_conflicting_object_is_rejected_and_preserved(self):
        self.config['create_first'] = True
        asset = self.asset()
        original = b'conflicting authoritative content'
        self.client.objects[asset[1]] = original
        self.assertEqual('invalid', self.transfer(asset)['status'])
        self.assertEqual(0, self.client.puts)
        self.assertEqual(original, self.client.objects[asset[1]])

    def test_create_first_verify_only_still_never_uploads(self):
        self.config['create_first'] = True
        result = self.transfer(self.asset(), verify_only=True)
        self.assertEqual('destination_missing', result['error'])
        self.assertEqual(0, self.client.put_attempts)

    def test_readback_corruption_fails_verification(self):
        self.client.corrupt_get = True
        self.assertEqual('invalid', self.transfer(self.asset())['status'])

    def test_upload_permission_failure_is_recorded_without_secret_text(self):
        self.client.fail_put = True
        result = self.transfer(self.asset())
        self.assertEqual('error', result['status'])
        self.assertEqual('AccessDenied', result['error'])

    def test_error_code_handles_missing_or_malformed_response(self):
        for response in (None, '', [], {'Error': None}, {'Error': 'invalid'},
                         {'Error': {'Code': None}}, {'Error': {'Code': []}}):
            error = RuntimeError('PRIVATE_EXCEPTION_TEXT')
            error.response = response
            self.assertEqual('RuntimeError', job.error_code(error))

    def test_null_response_does_not_escape_the_worker(self):
        error = RuntimeError('PRIVATE_EXCEPTION_TEXT')
        error.response = None
        with patch.object(self.client, 'get_object', side_effect=error):
            result = self.transfer(self.asset())
        self.assertEqual('error', result['status'])
        self.assertEqual('RuntimeError', result['error'])
        self.assertNotIn('PRIVATE_EXCEPTION_TEXT', json.dumps(result))
        self.assertTrue(result['diagnostic']['frames'])

    def test_diagnostics_capture_attribute_location_without_values(self):
        try:
            private_value = 'PRIVATE_LOCAL_VALUE'
            None.missing_attribute
        except AttributeError as error:
            diagnostic = job.exception_info(error)
            if hasattr(error, 'name'):
                self.assertEqual('missing_attribute', diagnostic['attribute'])
                self.assertEqual('NoneType', diagnostic['attribute_owner_type'])
        self.assertTrue(diagnostic['frames'])
        self.assertNotIn(private_value, json.dumps(diagnostic))

    def test_verify_only_never_uploads_missing_object(self):
        result = self.transfer(self.asset(), verify_only=True)
        self.assertEqual('destination_missing', result['error'])
        self.assertEqual(0, self.client.puts)

    def test_checkpoint_resume_skips_verified_unchanged_assets(self):
        self.asset()
        self.assertEqual(0, job.run(self.config, self.state, self.client))
        self.assertEqual(0, job.run(self.config, self.state, self.client))
        self.assertEqual(1, self.client.puts)
        self.assertEqual(1, self.client.gets)
        status = json.loads((self.state / 'status.json').read_text())
        self.assertEqual(1, status['skipped_verified'])
        self.assertEqual(0, job.run(self.config, self.state, self.client, verify_only=True))
        self.assertEqual(2, self.client.gets)

    def test_checkpoint_cannot_be_used_with_another_bucket(self):
        db = job.open_manifest(self.state, self.config)
        db.close()
        config = dict(self.config, bucket='different')
        with self.assertRaises(ValueError):
            job.open_manifest(self.state, config)

    def test_spool_maps_to_canonical_plain_key(self):
        digest = hashlib.sha256(b'test').hexdigest().upper()
        root = self.root / 'spool'
        key, actual, zipped = job.file_key(root / (digest + '.asset'), root, spool=True)
        self.assertEqual(job.relative_key(digest), key)
        self.assertFalse(zipped)
        self.assertEqual(digest, actual)

    def test_source_walk_preserves_files_without_following_directory_symlinks(self):
        asset = self.asset()
        outside = self.root / 'outside'
        outside.mkdir()
        hidden = outside / 'not-an-asset'
        hidden.write_bytes(b'must not traverse')
        (self.source / 'linked-directory').symlink_to(outside, target_is_directory=True)
        paths = [path for path, root, spool in job.sources(self.config)]
        self.assertIn(asset[0], paths)
        self.assertNotIn(hidden, paths)
        self.assertNotIn(self.source / 'linked-directory' / hidden.name, paths)

    def test_source_walk_failure_does_not_claim_a_complete_scan(self):
        self.config['source'] = str(self.root / 'missing')
        with self.assertRaises(FileNotFoundError):
            list(job.sources(self.config))

    def test_hash_directory_mismatch_is_rejected(self):
        with self.assertRaises(job.SourceIssue):
            job.file_key(self.source / ('../' + 'A' * 64), self.source)

    def test_symlink_is_not_read(self):
        asset = self.asset()
        link = self.root / 'link'
        link.symlink_to(asset[0])
        result = self.transfer((link, asset[1], asset[2], asset[3]))
        self.assertEqual('error', result['status'])
        self.assertEqual(0, self.client.puts)

    def test_decompression_limit(self):
        result = self.transfer(self.asset(b'A' * 2048))
        self.assertEqual('decoded_asset_exceeds_limit', result['error'])
        self.assertEqual(0, self.client.puts)

    def test_content_encoding_is_rejected(self):
        self.client.encoding = 'gzip'
        result = self.transfer(self.asset())
        self.assertEqual('remote_content_encoding_must_be_unset', result['error'])


if __name__ == '__main__':
    unittest.main()
