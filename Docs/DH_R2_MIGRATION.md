# DH live FSAssets migration to Cloudflare R2

DH can run ROBUST with the S3 asset module while the bulk copy is in progress.
ROBUST keeps the existing asset UUIDs and metadata database. On a read it uses
the local cache for previously verified R2 content, then tries the gzip and
plain R2 keys. If both are missing, it reads from the existing FSAssets spool
or data directory and verifies the content. With `MigrationMode = true`,
filesystem reads stay out of the R2 cache, so the next uncached request can
pick up the completed R2 copy. Known legacy object XML differences are accepted
only if the original checksum can be reproduced. R2 permission, transport,
and integrity errors are surfaced as failures.

New writes go to R2 before ROBUST acknowledges the existing metadata
transaction. Conditional creation protects objects when ROBUST and the bulk
importer encounter the same content. The importer can verify an existing gzip
by decoded SHA-256 when another writer used a different gzip encoding.

## 1. Survey DH and run a pilot

- Confirm the DH ROBUST and asset-file hosts, exact DH build and .NET runtime,
  metadata provider and realm, data and spool mounts, service users, and
  Hypergrid settings. Inspect a few paths to confirm hash casing and the value
  of `UseOsgridFormat`. Confirm the largest asset fits the chosen decoded
  size limit. The packaged module targets the PlaySim 1.0.49.10 runtime and
  needs compatibility verification against DH before activation.
- Measure source bytes and object count on the storage host. Check available
  disk space for SQLite and temporary snapshots, memory, outbound bandwidth
  and normal simulator load. Use a separate private Standard R2 bucket
  such as `dh-assets`, with bucket-scoped credentials delivered through
  the service environment. Keep the metadata and asset backups available.
- Use a separate ROBUST test instance with a copy of DH metadata and
  representative assets. Validate an R2 hit, a filesystem-only hit, a cache hit,
  a miss that becomes an R2 hit after import, a new upload and Hypergrid
  import/export. Check a recovered legacy object and both gzip and plain keys.
  Exercise the rollback procedure before accepting live R2-only writes.

## 2. Configure ROBUST and start the bulk copy

Merge the existing S3 overlay example into DH's configuration. Keep its
metadata connection, legacy database fallback, and Hypergrid permissions.
Set `[AssetService] LocalServiceModule` to `S3AssetService` and use
`HGAssetService` with the same S3 backing module. In `[S3AssetService]`,
set the DH bucket and endpoint, `Region = auto`, `MigrationMode = true`,
`ReadOnly = false` for a live writer, and DH's `LegacyBaseDirectory` and
`LegacySpoolDirectory`. Use a dedicated local cache and grant the ROBUST
service write access to it. Disable default asset seeding for an already
provisioned grid. Keep the previous INI and module available for rollback.

Run the importer on DH's asset storage host, directly to R2. Use a new state
directory for DH. The source directories are mounted read-only for the
importer. Example settings, with paths and concurrency to be measured on DH:

```json
{
  "endpoint": "https://ACCOUNT_ID.r2.cloudflarestorage.com",
  "bucket": "dh-assets",
  "source": "/srv/dh-assets/data",
  "spools": ["/srv/dh-assets/tmp"],
  "use_osgrid_format": false,
  "processes": 8,
  "workers": 8,
  "minimum_age_seconds": 60,
  "max_decoded_bytes": 67108864,
  "bytes_per_second": 0,
  "create_first": true,
  "allow_equivalent_gzip": true,
  "recover_trailing_nul": true,
  "recover_legacy_namespaces": true
}
```

Match `use_osgrid_format` and the decoded size limit to ROBUST. The importer
writes at the bucket root, so use an empty module `KeyPrefix`. Use DH-specific
systemd unit, state, and credential paths. Run the coordinator as the importer
service user using `parallel_import.py --config /etc/dh-r2/import.json --state
/var/lib/dh-r2`. Configure `PrivateTmp=true` and a memory ceiling with room for
the simulator and filesystem services. Set the partition count before the
first pass. Worker concurrency can be tuned without repartitioning checkpoints.

Start with a measured sample, then increase concurrency while monitoring
verified GB/hour, memory, source I/O, R2 errors and live viewer performance.
At 50 GB/hour a decimal TB takes 20 hours of sustained copying. Use DH's
inventory and measured rate for scheduling. Enable the live module after the
pilot and before the bulk pass finishes so ordinary reads continue from
both stores. Keep repeating the scan to catch older writers and completed spools.

## 3. Verify coverage and retire the old read path

Sum only DH's active shard manifests. Investigate checksum exceptions and
transport failures separately. Reconcile live metadata hashes with verified
R2 keys, including assets that only exist in the legacy database. Verify
representative viewer and Hypergrid operations after clearing the viewer
cache. Remove the filesystem fallback only after coverage is verified and
the old writers have stopped. Retain the filesystem backup.

The old filesystem provider cannot read new R2-only assets. If a full rollback
is needed after live writes begin, stop new writes, export and verify the
new R2 blobs back to the filesystem under their metadata hashes, then restore
the old provider and restart. Preserve the metadata database throughout.

DH admin access is needed to verify host details and run the pilot. The public
DH endpoint is reachable, but no DH management SSH connection has been verified.
