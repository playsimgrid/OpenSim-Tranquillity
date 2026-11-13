# S3 asset service — 1.0.49.10 evaluation

This optional module implements `IAssetService` for private Backblaze B2 or
Cloudflare R2 buckets using AWS SDK for .NET v4 (S3 4.0.104.12, Core 4.0.103,
the .NET 8-compatible line). It is built against release
1.0.49.10 plus the existing region reconnect fix at `8a7bc57bcc`.
The module is not enabled by default. The subsequent private R2 provisioning,
direct fs01 import and staged grid configuration are recorded in
[R2 import operations](R2_IMPORT_OPERATIONS.md); the live provider remains FS.

## Architecture and integration

Regions keep using ROBUST's existing asset endpoint. ROBUST uses the module to
resolve UUIDs through the existing `IFSAssetDataPlugin`/`fsassets` metadata table,
then obtain content from a bounded local disk cache or S3. MySQL remains required.
There is no schema change and no region or viewer change.

The existing generic `HGAssetService` supports `BackingService`, so it can wrap
this module. Switch the HG configuration from `HGFSAssetService` to
`HGAssetService` and set its backing service to this module. Keep existing home
URI, user account service, import restrictions and export restrictions. The
existing HG wrapper supplies creator/object identity rewriting, export/import
checks, and denial of HG deletion/content updates. No HG code was duplicated.

Only the solution and GridServer build references change outside the new module,
tests, documentation and example. The GridServer reference ensures its published
dependency manifest includes AWS assemblies. Default FS service selection stays
unchanged. The module does not implement cloud compute or a new HTTP endpoint.

## Configuration

Merge `Source/OpenSim.Server.GridServer/AppData/S3Assets.ini.example` into a
separate canary configuration. Do not switch a production writer to the
read-only example: it deliberately refuses uploads.

- R2: HTTPS account endpoint; `Region=auto`.
- B2: exact HTTPS S3 endpoint and matching region from the bucket details.
- Use a pre-created private bucket. The module never creates buckets or changes
  their policy, lifecycle, public access, or encryption configuration.
- Provide a bucket-scoped access key and secret through the two named environment
  variables inherited by ROBUST. Do not put secrets in the INI or source tree.
  Use read-only credentials for the first canary, and read/write credentials only
  for an isolated write test. No cloud delete permission is needed by this module.
- `ReadOnly=true` rejects asset writes and deletions and skips default asset
  seeding. It does **not** make the existing metadata provider itself read-only:
  initialization can run its normal schema checks and reads may update access
  timestamps. Use a database clone for evaluation.
- Set the metadata `StorageProvider`, `ConnectionString`, `Realm`, and
  `UseOsgridFormat` consistently with the original service. DatabaseService
  supplies provider/connection defaults. Never point a test at a production
  database expecting ReadOnly to prevent all SQL writes.
- The disk cache must be a dedicated directory on local disk, exclusive to one
  ROBUST process. Normal and HG service instances within that process share it.
  Separate processes need separate cache directories. Avoid symlinks into legacy
  storage; the lexical overlap check cannot detect symlink aliases.
- `CacheMaxBytes` is a strict payload-byte budget (default 10 GiB), excluding
  filesystem allocation/metadata overhead. Set zero to disable it. Leave disk
  headroom. Cache files are expendable. It rebuilds its eviction index on startup.
- Each SDK operation has a cancellation deadline (default 30 seconds), at most
  two retries, and the backend limits concurrent operations to 16. A read that
  tries compressed and plain keys can make two separate operations. Existing
  synchronous OpenSim interfaces still occupy request threads on cold reads.
- Maximum decoded asset size defaults to 64 MiB, configurable up to 512 MiB.
  Inventory asset sizes before selecting the production value.

## Stored representation and compatibility

S3 keys retain existing path and hash case, optionally beneath `KeyPrefix`:

```
AA/BB/CC/DDDD/AABBCCDDDD...<64-character SHA256>.gz
```

`UseOsgridFormat=true` uses the original `AAA/BBB/<hash>` layout. Reads support
both `.gz` and uncompressed files. New content is gzip-compressed, stored as
`application/octet-stream` without an HTTP Content-Encoding, and named using
uppercase SHA-256 of the uncompressed bytes. Copy legacy gzip bytes as-is, without setting Content-Encoding=gzip; do not
rewrite all object names to lowercase. The cache can normalize hash case because
its identity is the verified content, not an object-store path.

Reads verify the decompressed SHA-256 and enforce decoded/compressed size limits.
Corrupt cached content is evicted and refetched; corrupt authoritative content
raises an error. Some historical object writes hashed content before sanitizing
XML. Such pre-existing mismatches must be audited and reconciled before cutover;
this module will reject them rather than silently accept bytes under a wrong
hash. New object writes sanitize before hashing.

Read order: cache, cloud gzip, cloud plain, optional legacy spool/data files,
then optional legacy database asset service. The filesystem and legacy database
fallbacks are read-through only: this module does not promote, mutate, or copy
legacy assets on a read. The cache is the only local write caused by reading.
Cloud authentication failures, timeouts, corruption and server failures propagate
as errors, not missing assets; only `NoSuchKey` permits the cloud miss path.
There is no negative cache in the module. Existing metadata plugins may themselves
return null on a database connection failure; that pre-existing limitation is
not fixed here. Existing region connectors also need outage behavior validated
in a canary before claiming end-to-end negative-cache protection.

For an active bulk copy, set `MigrationMode = true` and configure the existing
FSAssets data and spool paths. The module keeps filesystem fallback results
out of its R2 cache and verifies known legacy scene-object XML repairs by
reproducing the original hash. The migration cache has its own directory
identity so old filesystem-derived cache entries cannot hide a new R2 copy.
New writes in this mode require an endpoint that enforces conditional
`PutObject` with `If-None-Match: *`, as Cloudflare R2 does. If a writer
already created the key, ROBUST checks the existing object's decoded checksum
before acknowledging the write. See [DH migration runbook](DH_R2_MIGRATION.md).

`GetCached` queries metadata and reads only the disk cache; it does not contact
cloud or the legacy fallback. `AssetsExist` retains the FS metadata-existence
semantics, not a full blob-integrity check. Foreign-URL fetching belongs to the
region/HG connector; this storage module does not fetch arbitrary remote URLs.

## Write and delete semantics

Writes are opt-in (`ReadOnly=false`). The module verifies cloud presence or
finishes a PUT before committing metadata. A cache hit is never sufficient proof
of durability. It reads metadata back before acknowledging success because the
1.0.49.10 MySQL FS provider may return success after a failed INSERT. Cloud upload
failures do not create new metadata; failed metadata commits can leave harmless
unreferenced cloud blobs. No automatic garbage collection is included.

Asset UUIDs remain immutable, matching FSAssetConnector: writing different
content under an existing UUID fails, and `UpdateContent` returns false. Store
returns an empty string on failure. Duplicate content shares a blob. Repeated
uploads are serialized by hash within a process; multiple ROBUST processes or
uncertain network retries can still produce extra B2 object versions. Account
for these in migration and lifecycle planning; do not expire live current blobs.

Deletes are disabled by default. With both writes enabled and
`AllowMetadataDelete=true`, only the UUID metadata row is removed. The shared
cloud blob is never deleted. HG deletion remains forbidden by the HG wrapper.
The default asset loader, if configured, runs once per shared backend only in
write-enabled mode and fails startup if its stores cannot be confirmed.

## Build, tests and review package

Use a .NET 8 SDK. The existing repository's global.json specifies the invalid
SDK version `8.0.0`; this change leaves that unrelated file untouched. Run dotnet
from a directory outside the checkout and supply absolute project paths, or
correct your SDK selection in your build environment.

```
dotnet build /path/to/checkout/Source/OpenSim.Services.S3AssetService/OpenSim.Services.S3AssetService.csproj -c Release
dotnet test /path/to/checkout/Tests/OpenSim.Services.S3AssetService.Tests/OpenSim.Services.S3AssetService.Tests.csproj -c Release
python3 /path/to/checkout/tools/package-s3-assets.py --output /path/to/review-output
```

The packaging script uses an existing Release build; it does not install or
activate anything. It packages the module, its dependency manifest and the two
AWS SDK assemblies with checksums, configuration example and this guide. Do not
copy the module project's entire build directory over an existing grid release:
that could overwrite unrelated OpenSim assemblies. For a normal release build,
publish GridServer from this checkout so its manifest includes all dependencies.
For evaluating the additive package against an existing release, test plugin
loading in a copy of that release before considering live installation.

Tests cover cloud-before-metadata ordering, failed/false-success metadata writes,
deduplication and concurrent uploads, immutable UUIDs, mixed-case legacy keys,
both path layouts and compression forms, cache bounds/restart/isolation/corruption,
filesystem fallbacks, read-only behavior, shared-blob deletion safety, oversized
payloads, configuration validation and real plugin construction. Transport tests
run the actual AWS SDK signing/serialization pipeline against an in-memory HTTP
handler, including R2/B2 signing regions, exact PUT payloads, missing-object versus
bucket/authentication failures, and cancellation. Hypergrid tests exercise the
existing wrapper's permissions and creator rewriting.

These tests are not a live B2/R2 certification. Credentials, provider round trips,
network latency, real MySQL persistence and viewer behavior need an isolated
canary. No production endpoint, bucket or database is used by automated tests.

## Evaluation and cutover gates

1. Clone the metadata database and prepare a representative set of copied files
   in a private test bucket. Include every asset type, raw and compressed files,
   mixed-case hashes, and large assets. Preserve a manifest and checksums.
2. Start a separate ROBUST canary with read-only credentials/configuration and
   the clone. Ensure the generic HG wrapper points to this module. Keep the
   normal production asset endpoint on the filesystem service.
3. Verify known UUIDs and decoded hashes. Measure cold/warm response latency,
   concurrency, memory, cache disk use and provider request counts. Test revoked
   credentials, missing bucket, timeout, cache exhaustion and restart.
4. On the isolated canary only, enable writes with a disposable writable database
   clone and test bucket. Verify uploads, duplicate uploads, attachments, scripts,
   rez, and Hypergrid import/export using representative viewers.
5. Bulk copy and reconcile all production files and spool entries before any
   cutover. Include legacy-database-only assets. This module is not a bulk
   migration utility. Check every metadata hash has a matching verified blob.
6. Plan rollback before enabling cloud production writes. This version has no
   dual-write mirror: new cloud-only assets will not exist on the old filesystem.
   A simple config rollback is safe during read-only evaluation, but after cloud
   writes it requires copying all new blobs back (and preserving metadata), or an
   independently implemented and tested replication mechanism. Do not remove
   NFS or the legacy database fallback until this is resolved and tested.

## Initial module validation recorded November 12, 2026

- .NET 8.0.428 SDK, macOS arm64: 53 tests passed, zero failed/skipped.
- Full 1.0.49.10 GridServer Release build succeeded; its dependency manifest
  contains the optional S3 module, AWSSDK.S3 4.0.104.12 and AWSSDK.Core 4.0.103.
- The additive DLL package loaded in a fresh .NET 8 process with only existing
  OpenSim dependencies referenced by its host (no module/AWS SDK host references).
  Both supported SDK assemblies resolved from the package and the service
  constructed in read-only mode. This is a loading check, not a cloud connection.
- `git diff --check` passed. Package contents and SHA-256 manifests were verified.
- Existing build warnings remain for the repository's MySqlConnector 2.5.0
  advisory and unresolved XMLRPC references; this module does not change those
  dependencies. The new module has no build errors.
- This initial build validation did not use a live bucket, real MySQL database,
  viewer session or Linux ROBUST process. Subsequent live R2 read validation and
  the prepared deployment are documented in the R2 operations guide above.

Provider references:
- https://developers.cloudflare.com/r2/examples/aws/aws-sdk-net/
- https://developers.cloudflare.com/r2/api/s3/api/
- https://www.backblaze.com/docs/cloud-storage-use-the-aws-sdk-for-net-with-backblaze-b2
