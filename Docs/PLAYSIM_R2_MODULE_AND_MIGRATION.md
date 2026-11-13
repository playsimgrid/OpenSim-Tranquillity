# PlaySim R2 Asset Module and Migration

**Target release:** 1.0.49.10

**Document date:** November 12, 2026

**Deployment stage:** ROBUST is using Cloudflare R2 for assets. The initial import and checksum recovery finished with one unresolved zero-byte source file.

This document describes the implementation and the deployment verified on November 12. Import progress changes continuously; use the monitoring commands below for current figures.

## What we are building

We are adding an optional asset storage module to ROBUST so PlaySim can keep asset contents in Cloudflare R2. Regions and viewers continue using the existing asset service endpoint. MySQL continues holding the asset metadata that connects asset UUIDs to their stored content.

The module is `OpenSim.Services.S3AssetService.dll`, implementing OpenSim's existing `IAssetService` interface. It runs inside ROBUST, so it does not require a separate asset server process. The separate migration job runs on fs01 and copies the existing files into R2.

The implementation uses the S3 API and includes support for Cloudflare R2 and Backblaze B2. This deployment uses R2. B2 has not been validated against a live bucket in this work.

## Module versus base code changes

Asset storage behavior lives in the new module. The existing solution and GridServer project receive build references so the module and its AWS SDK dependencies are included in a release build. The default filesystem provider remains selected until configuration explicitly changes it.

No database schema, viewer protocol or region asset endpoint change is required. For Hypergrid, the prepared configuration uses the existing generic `HGAssetService` wrapper with the new module as its backing service. Existing Hypergrid import/export restrictions and identity handling remain in that wrapper.

## How assets will be served

```text
Region / existing asset client
             |
             v
       ROBUST on grid
             |
             v
       S3AssetService --------> MySQL fsassets metadata
             |                  (UUID -> content hash)
             v
       Local disk cache
             |
        on cache miss
             v
   Private playsim-assets bucket in R2
```

The read sequence is metadata lookup, local cache, R2 compressed object, then R2 plain object. If cloud content is absent, configured legacy filesystem/spool and database fallbacks can supply older assets. Fallback reads do not automatically migrate those assets into R2.

Content is checked against its SHA-256 hash before use. Authentication errors, timeouts and corrupt content are reported as failures rather than treated as missing assets. The prepared cache holds up to 10 GiB of payloads on grid and can be rebuilt from storage.

For new writes, the module stores or confirms the content in R2 before committing and checking the metadata. Existing asset UUIDs remain immutable. Identical content can share a stored object. Metadata deletion is disabled in the prepared configuration; the module never deletes shared cloud blobs.

## R2 setup

| Setting | Value |
| --- | --- |
| Bucket | `playsim-assets` |
| Storage class | Standard |
| Location | ENAM |
| Access | Private, authenticated S3 requests |
| Signing region | `auto` |
| Object prefix | Empty |
| Public domains | Managed public access disabled; no custom public domain configured |

The account-specific HTTPS endpoint is recorded in the server configuration. This design uses R2 as the private storage backend; it does not publish an asset website or require public bucket access.

R2 credentials came from the existing secrets on web01. They are installed in `/etc/playsim-r2/credentials.env` on fs01 and grid, owned by root with mode `0600`. Systemd supplies the credentials to the relevant service through environment variables. Secret values are not included in this document, source code or the asset INI.

The existing credential scope was reused; this work did not create a new bucket-restricted credential.

## How the migration works

```text
fs01 local asset files
         |
         v
Stable snapshot and checksum validation on fs01
         |
         | direct HTTPS upload
         v
Private R2 bucket: playsim-assets
         |
         | direct HTTPS readback
         v
Checksum comparison and manifest update on fs01
```

**Asset bytes travel directly between fs01 and Cloudflare. They do not pass through the Mac.** The Mac was used to prepare code and issue administrative commands. The grid module's separate read test fetched its asset directly from R2 to grid.

The importer reads `/srv/grid-assets/data` and completed spool entries in `/srv/grid-assets/tmp` and `/srv/grid-assets/tmp-assets`.

For each eligible file, the job:

1. Checks the hash-based path and waits until the file is at least 60 seconds old.
2. Makes a temporary snapshot on fs01 and checks that the source stayed unchanged during that copy.
3. Validates gzip data where applicable and compares the decoded SHA-256 with the filename.
4. Preserves the original stored bytes, path and hash case when uploading. Completed `.asset` spool files use the corresponding plain-object key.
5. Uses a conditional create so an existing destination is never overwritten by the importer. Copying runs attempt this directly, avoiding a preliminary HEAD request.
6. Downloads the object directly back to fs01 and checks its stored bytes against the snapshot.
7. Records the result in a local SQLite manifest so later runs can skip unchanged, verified files. Checkpoints are committed in one-second groups; interrupted work is safely rechecked on resume.

Each process owns a disjoint part of the hash tree and its own checkpoint manifest. Existing checkpoints were distributed across those manifests without repeating completed transfers. The original manifest is retained as a frozen checkpoint and must not be added to the partition totals.

The job does not modify or delete source assets. Invalid files and destination conflicts are recorded for review. Unfinished spool files are excluded. The import is resumable and protected against overlapping runs.

Following development-grid tuning and a memory-pressure adjustment, it uses eight independent processes with 32 workers each (256 total), no application bandwidth cap, all six CPU cores and a shared 8 GiB memory ceiling. These are limits, not promised throughput. A systemd timer starts another reconciliation pass 30 minutes after a run finishes and also starts the job after boot.

## What is installed

| Host | Component | Location |
| --- | --- | --- |
| fs01 | Import coordinator / worker | `/opt/playsim-r2/parallel_import.py`, `/opt/playsim-r2/import_assets.py` |
| fs01 | Import configuration | `/etc/playsim-r2/import.json` |
| fs01 | Import service | `playsim-r2-import.service` |
| fs01 | Reconciliation timer | `playsim-r2-import.timer` |
| fs01 | Verification manifests | `/var/lib/playsim-r2/shards/00/manifest.sqlite3` through `07/manifest.sqlite3` |
| fs01 | Progress report | `/var/lib/playsim-r2/status.json` |
| grid | Module and AWS dependencies | `/opt/playsim-r2/module/` |
| grid | Live ROBUST provider overlay | `/etc/playsim-r2/robust-r2.ini` |
| grid | Credentials environment drop-in | `/etc/systemd/system/robust.service.d/40-playsim-r2-credentials.conf` |
| grid | Local cache | `/var/cache/opensim/s3-assets` |

The live ROBUST INI includes the R2 overlay. Normal asset requests and Hypergrid use `S3AssetService`, with asset writes enabled and legacy fallback configured. Default asset seeding was disabled for the switch because the existing assets were already provisioned. The previous INI is retained on grid at `/playground/config/robust-1.0.49.10.ini.before-r2`.

## Checks completed

- 53 module tests passed, and the 1.0.49.10 GridServer Release build succeeded.
- 30 importer tests passed on fs01, including eight real child processes using a local HTTPS test server, restart/resume and read-only auditing. The local suite passes 29 tests and skips that server-dependent integration test.
- The initial 16-asset import uploaded and fully read back every test asset successfully.
- A live conditional-create check confirmed that R2 rejected an overwrite and preserved the first object. The generated probe object was removed afterward.
- The actual module loaded on grid under the `playground` account and read a 277,079-byte asset directly from R2 with a matching decoded hash and no cache. The probe used in-memory metadata, not the production database.
- Deployed importer and systemd unit checksums matched the reviewed local files.

- After restarting ROBUST, grid info responded and three existing assets matched their expected hashes through both private and public asset endpoints. Logs confirmed that Hypergrid loaded the S3 backing service. The live smoke checks covered reads.

## What remains after cutover

The first bulk pass found 14,307 files whose decoded content did not match their hash filenames. Targeted recovery restored the expected SHA-256 for 14,214 files by removing a trailing zero byte and for 92 scene objects by reversing the legacy XML namespace rewrite. All recovered copies passed R2 readback under their original keys. The eight manifests now record 3,540,242 verified source files and 201.28 GB. The remaining zero-byte source file requires a matching copy. The module also verifies checksums when reading legacy files.

Reconcile metadata references against verified R2 objects, including assets that exist only in the legacy database. The largest verified decoded asset was 58,874,191 bytes, within the module's 64 MiB limit. Controlled write and viewer acceptance tests remain open.

The module does not write new assets to both R2 and the old filesystem. After cloud writes begin, returning to filesystem-only storage requires copying new cloud content back and preserving its metadata. Existing storage must remain available during evaluation and cutover.

## Checking progress

Run on fs01:

```sh
sudo cat /var/lib/playsim-r2/status.json
sudo journalctl -u playsim-r2-import.service -n 30 --no-pager
sudo systemctl list-timers playsim-r2-import.timer
```

The progress report contains current-pass verified, invalid and error counts. At the end of a pass it also records cumulative manifest totals. Those totals count source files, which can differ from unique R2 object counts when spool and data files refer to the same object.

During a long import, `activating (start)` is normal for this oneshot service. For pause, resume and full verification procedures, use `Docs/R2_IMPORT_OPERATIONS.md` in the source checkout; a copy is installed on fs01 at `/opt/playsim-r2/R2_IMPORT_OPERATIONS.md`.

## Source and review artifacts

The work is on branch `codex/s3-assets-1.0.49.10` in the `asset-storage-104910` checkout, based on commit `8a7bc57bcc` with the existing region reconnect fix.

- Module implementation: `Source/OpenSim.Services.S3AssetService/`
- Module tests: `Tests/OpenSim.Services.S3AssetService.Tests/`
- Importer, service units, tests and read probe: `tools/r2-import/`
- Detailed module guide: `Docs/S3_ASSET_SERVICE.md`
- Operations runbook: `Docs/R2_IMPORT_OPERATIONS.md`
- Review artifacts: the sibling `s3-asset-release-104910` directory, containing the module evaluation ZIP, migration package and source patch.

## Throughput measured during tuning

The original 16-worker importer verified roughly 4 GB/hour. A single process
with higher limits reached 11–12 GB/hour. Eight independent pipelines reached
52 GB/hour with 32 workers each and approximately 57 GB/hour in a subsequent
sample with 64 workers each. Repeated out-of-memory interruptions at the 6 GiB
service limit led to reducing concurrency back to 32 workers per process and
raising the ceiling to 8 GiB. Full checksum validation and R2 readback remain
enabled. The adjustment needs sustained monitoring to confirm stability. These
are measured samples, not a guaranteed finish time; a reliable total source-volume
inventory is not yet available.
