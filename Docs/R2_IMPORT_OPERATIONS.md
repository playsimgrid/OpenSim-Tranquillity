# PlaySim R2 import and live service configuration

Provisioned for the 1.0.49.10 evaluation. ROBUST now uses Cloudflare R2 for
asset reads and writes. The initial import and trailing-zero recovery finished
with one unresolved zero-byte source file.

## Destination and data flow

- Private Cloudflare R2 bucket: `playsim-assets`, Standard storage, ENAM location.
- S3 endpoint: `https://ACCOUNT_ID.r2.cloudflarestorage.com`.
- S3 signing region: `auto`; key prefix: empty.
- Managed public access is disabled and no custom public domain is configured.
- Bulk asset bytes travel directly between fs01 and R2, including verification
  downloads. The Mac only delivered code and issued administrative commands.
- Grid reads assets directly from R2 when using the prepared module.

## Installed on fs01

| Purpose | Path or unit |
| --- | --- |
| Coordinator / worker | `/opt/playsim-r2/parallel_import.py`, `/opt/playsim-r2/import_assets.py` |
| Nonsecret settings | `/etc/playsim-r2/import.json` |
| Root-only credentials | `/etc/playsim-r2/credentials.env` (0600) |
| Source | `/srv/grid-assets/data` |
| Completed spools | `/srv/grid-assets/tmp`, `/srv/grid-assets/tmp-assets` |
| Active manifests | `/var/lib/playsim-r2/shards/00/manifest.sqlite3` through `07/manifest.sqlite3` |
| Frozen original manifest | `/var/lib/playsim-r2/manifest.sqlite3` (already included in the active partitions) |
| Progress | `/var/lib/playsim-r2/status.json` |
| Temporary snapshots | `/var/lib/playsim-r2/shards/NN/tmp` |
| Child logs | `/var/lib/playsim-r2/shards/NN/import.log` |
| Import service | `playsim-r2-import.service` |
| Reconciliation timer | `playsim-r2-import.timer` |

The service runs as azdesign with read-only access to the asset tree. It uses
eight independent processes with 32 workers each (256 total), no application
bandwidth cap, a 600% CPU quota (all six cores),
and an 8 GiB memory ceiling under the development-grid override. It runs at normal
CPU priority and I/O priority 4. The timer starts after boot and 30 minutes after
each completed run; it does not launch overlapping imports. A long-running
oneshot service reports `activating (start)` while copying, which is expected.

Files keep their original path, hash case and encoded bytes. Completed root
`.asset` spool entries become plain hash objects. Files modified within the
last 60 seconds are deferred. Nested unfinished spool files are excluded.
The importer snapshots each source locally on fs01, verifies its stability,
validates gzip/decoded SHA-256, creates the R2 object conditionally, then downloads
and verifies the exact stored bytes. R2 Content-Encoding is left unset.

An existing destination is verified, never overwritten. Source files are never
modified, moved or deleted. Invalid source hashes and destination conflicts are
recorded in the manifest. Unchanged verified files are skipped on subsequent
runs. The manifest is bound to its source, endpoint and bucket.

## Operations

Run these commands on fs01:

```sh
sudo cat /var/lib/playsim-r2/status.json
sudo journalctl -u playsim-r2-import.service -n 30 --no-pager
sudo systemctl list-timers playsim-r2-import.timer
```

Progress counters describe the current pass. At the end, `manifest_totals`
records cumulative source-file counts; these are not necessarily unique object
counts because data and spool entries can refer to the same object.
Exit 2 means invalid assets require review; exit 1 means an interruption or
transfer failure. Transfer failures are retried by systemd, with start-rate
limits. Invalid source data does not cause an immediate restart loop.

### November 12 importer interruption

Repeated unhandled `AttributeError` exceptions caused individual child processes
to exit. The coordinator then stopped the other children; repeated failures
exhausted systemd's immediate retry limit. The reconciliation timer could start
another cycle later. These interruptions preserved verified manifest checkpoints.

At 18:26 America/Phoenix (November 13 01:26 UTC), an error-handler repair and
sanitized traceback diagnostics were installed after all 33 importer tests passed
on fs01. The handler previously assumed every exception response and nested
`Error` value was a dictionary. A malformed response could make error reporting
itself raise `AttributeError`. That defect was reproduced in tests, but the earlier
logs did not establish whether it caused the live interruptions.

New diagnostics record code filenames, line numbers, function names and exception
types, without exception messages, local values or HTTP bodies. Inspect these in
the child logs if a failure recurs. The restart retained 981,730 verified source
files / 51,513,701,666 stored bytes and 4,283 invalid files. A successful restart
alone does not demonstrate that the original cause has been resolved.

To pause safely, stop the timer before the service; SIGTERM stops scheduling
new transfers and allows pending work to finish:

```sh
sudo systemctl stop playsim-r2-import.timer
sudo systemctl stop playsim-r2-import.service
```

To resume, keep the same manifest:

```sh
sudo systemctl start --no-block playsim-r2-import.service
sudo systemctl start playsim-r2-import.timer
```

To request a full read-only remote audit after stopping the normal service and
timer, use a transient unit so systemd supplies the protected credentials:

```sh
sudo systemd-run --unit=playsim-r2-audit --collect \
  --property=User=azdesign --property=Group=azdesign \
  --property=EnvironmentFile=/etc/playsim-r2/credentials.env \
  --property=UMask=0077 \
  /usr/bin/python3 -u /opt/playsim-r2/parallel_import.py --verify-only
```

This updates manifest verification results but does not upload missing objects.
`--reverify` checks previously verified files and also allows missing objects to
be uploaded. Both operations reread source and destination bytes. Resume the
normal service and timer after the audit. Never remove the manifest to restart.

## Live on grid

- Module and AWS dependencies: `/opt/playsim-r2/module/`.
- Actual-module probe: `/opt/playsim-r2/probe/R2AssetProbe.dll`.
- Provider overlay: `/etc/playsim-r2/robust-r2.ini`.
- Credentials: `/etc/playsim-r2/credentials.env`, root-owned 0600.
- ROBUST environment drop-in:
  `/etc/systemd/system/robust.service.d/40-playsim-r2-credentials.conf`.
- Dedicated cache: `/var/cache/opensim/s3-assets`, playground-owned 0700,
  configured for 10 GiB of payloads.

`/playground/config/robust-1.0.49.10.ini` includes the provider overlay. ROBUST
loads S3AssetService for normal assets and HGAssetService with the same S3
backing service for Hypergrid. Writes are enabled, metadata deletion is disabled,
and legacy fallback remains configured. The `45-playsim-r2-cache.conf` systemd
drop-in grants write access to the dedicated cache. Default asset seeding was
disabled for the switch because the existing assets were already provisioned.

After restart, grid info and three existing assets passed live HTTP checks on
the private asset endpoint. The same three assets passed the public Hypergrid
endpoint checks. Before restart, the module read all three directly from R2 and
matched their SHA-256. The live logs confirmed both provider startup messages.
The checks covered reads. A production write and viewer login were not exercised.

The private rollback INI is `/playground/config/robust-1.0.49.10.ini.before-r2`.
Before restoring it after live writes, reconcile any new R2 objects and their
metadata with the filesystem provider so those UUIDs remain readable. Restore
the backup atomically, remove the cache drop-in, reload systemd, restart ROBUST,
and recheck the grid info and asset endpoints.

Credentials were sourced from the existing web01 secrets without printing their
values. fs01 retrieved them directly from web01. Grid received an encrypted
handoff decrypted on grid; only ciphertext crossed the controller. No permanent
SSH authorization was added. Existing credential scope was reused; a newly
restricted bucket-only key was not created.

## Verification and remaining work

- Importer: 30 tests passed on fs01, including eight real child processes against
  a local HTTPS test server, checkpoint resume and read-only auditing. Locally,
  29 tests passed; the boto3-dependent integration test runs on fs01.
- Initial direct fs01 import: 16 uploads and full readbacks passed.
- Live conditional-create test returned PreconditionFailed on a second PUT,
  preserved the first object and deleted only the generated test object.
- Actual S3AssetService loaded on grid as playground with no cache and read a
  277,079-byte asset from R2 whose decoded SHA-256 matched its filename. The
  probe used in-memory metadata and did not access the production database.
- The module's existing 53 tests and GridServer Release build passed during
  module development; see [module guide](S3_ASSET_SERVICE.md).

The first bulk pass found 14,307 decoded-hash mismatches. All decompressed
successfully. For 14,214 files, removing one trailing NUL restored the exact
SHA-256 in the original filename. The legacy FSAssets writer hashes the input
before converting object XML back to bytes, which explains how the stored bytes
can differ from the filename hash.

The targeted recovery used `parallel_import.py --retry-checksum-mismatches`
with `recover_trailing_nul: true` in `/etc/playsim-r2/import.json`. Recovery
recompresses a temporary copy only when removing exactly one trailing NUL
restores the expected hash. It uses conditional creation under the original R2
key, verifies the stored bytes by reading them back, and checkpoints the
original source fingerprint. Source files and UUID mappings are preserved.
When running recovery in a transient systemd unit with `ProtectSystem=strict`,
set `PrivateTmp=true` so SQLite can write temporary files for its final report.

The first recovery verified 14,214 files. A second targeted pass enabled
`recover_legacy_namespaces` and recovered 92 more scene objects by restoring
the repeated `xmlns:` prefixes on their `xsd` and `xsi` declarations. Each
temporary copy matched the expected SHA-256 before upload and passed R2
readback under the original key. All 35 importer tests passed on fs01.

The eight manifests now record 3,540,242 verified source files and
201,278,734,601 stored bytes, with zero outstanding transfer errors. The
remaining exception is a zero-byte source file that requires a matching
copy. The module also checks hashes when reading legacy files.

The largest verified decoded asset was 58,874,191 bytes at cutover, within the
module's 64 MiB limit. Resolve the one invalid record, reconcile database
references against verified objects, and account for legacy-database-only assets.
Keep NFS and the existing fallback storage available for the evaluation and for
rollback. The module writes new assets to R2 only.

## Concurrency adjustment — November 12, 2026

At 22:41 UTC (3:41 p.m. Arizona), the importer was gracefully stopped and resumed
with 64 workers instead of 16. Its manifest and all verified progress were retained.
Source validation, conditional creation, full readback, bandwidth and resource
limits remain unchanged. The previous configuration is saved at
`/etc/playsim-r2/import.before-concurrency64.json`; the final status before restart
is `/var/lib/playsim-r2/status.before-concurrency64.json`. Current-pass counters reset
on restart. Query the manifest for cumulative totals across runs.

## Development-grid throughput tuning — November 12, 2026

The user authorized aggressive use of fs01 because this is a development grid.
The optional override `tools/r2-import/20-dev-throughput.conf` is installed under
`/etc/systemd/system/playsim-r2-import.service.d/`. It allows five CPU cores,
6 GiB memory, normal CPU priority, I/O priority 4, and 4,096 open file descriptors.
The importer uses 128 workers and `bytes_per_second=0` (no application throttle).

`create_first=true` removes the preliminary HEAD request on copying runs. Every
PUT still has signed `If-None-Match: *`; an existing object returns 412 and is
fully read back and compared. Verification-only runs never attempt a PUT.
Source hashes, encoded-byte comparisons and existing-object protection remain
unchanged. This relies on R2 conditional-write support, already verified live.

Directory traversal uses scandir entry types to avoid redundant directory stats
and does not follow directory symlinks. Manifest changes are committed in
one-second groups and before status reports and normal shutdown. If interrupted
before a checkpoint commits, the next run replays those files safely through
conditional creation and readback. SQLite synchronous FULL remains enabled.

The previous importer/configuration are saved in
`/opt/playsim-r2/before-dev-throughput/`; a further code backup is in
`/opt/playsim-r2/before-directory-optimization/`. Progress snapshots before these
planned restarts are in `/var/lib/playsim-r2/status.before-dev-throughput.json`
and `/var/lib/playsim-r2/status.before-directory-optimization.json`.

Measured performance during tuning was approximately 4 GB/hour at 16 workers,
9 GB/hour at 64 workers, and 11–12 GB/hour in short steady-state samples with the
128-worker development settings. These are verified stored bytes (decimal GB),
not a guaranteed sustained rate or a measured network maximum. Tests and live
checks showed no transfer errors or OOM events. The separate directory-size scan
was briefly paused during benchmarks and resumed afterward.

Conditional-write reference: [Cloudflare R2 conditional operations](https://developers.cloudflare.com/r2/api/s3/conditional-operations/).

## Parallel pipelines — November 12, 2026

At approximately 23:09 UTC (4:09 p.m. Arizona), the single-process importer was
replaced by `parallel_import.py`, using eight processes and 32 transfer workers
per process. Source partitions are assigned by the numeric first hash byte modulo
eight, consistently across uppercase/lowercase paths and spool representations.
Each process enumerates its own directories and has its own SQLite manifest,
removing the single coordinator from the filesystem and SDK hot paths.

The transition preserved 162,587 verified source records / 8,575,139,098 stored
bytes and 717 invalid records. Partition seeding validates the original identity
and row count, then atomically publishes the partition directory. The original
manifest is frozen and backed up as `manifest.before-parallel.sqlite3`.
**Do not add its counts to the eight active manifests: those rows are already
included.** Do not change `processes` or remove a partition after initialization;
the coordinator refuses a changed or incomplete layout. Worker counts per
process can be tuned without repartitioning.

The service uses `30-parallel-import.conf` to select the coordinator. It holds the
original import lock for the whole run, preventing a concurrent standalone import.
Each child holds its own lock. Source validation, conditional writes and full R2
readback are unchanged. A failed child causes the coordinator to stop its peers;
invalid assets alone allow valid copying to continue. Completion with invalid
records returns exit 2, not a successful migration. Timer and pause/resume commands
remain the same. The coordinator supports `--verify-only` and `--reverify`.

The parent progress report aggregates current-pass counts and includes each
child's phase, PID, timestamp and exit status. For exact cumulative totals, run
on fs01 (no credentials required):

```sh
sudo python3 - <<'PY_STATUS'
import sys, json
from pathlib import Path
sys.path.insert(0, '/opt/playsim-r2')
from parallel_import import totals
print(json.dumps(totals(Path('/var/lib/playsim-r2'), 8), indent=2))
PY_STATUS
```

The first 30-second warm-up sample measured 36.49 GB/hour of verified stored
bytes. This is an observed sample, not a confirmed completion estimate.

At 23:13 UTC the worker count increased to 64 per process (512 total), retaining
the same eight partitions. A subsequent 31-second sample verified 9,956 files
and 489,608,243 stored bytes, equivalent to 56.85 GB/hour. No transfer errors,
automatic restarts or OOM events occurred in these checks. This configuration
was retained. The previous configuration is
`/etc/playsim-r2/import.before-64-per-process.json`; its final checkpoint report
is `/var/lib/playsim-r2/status.before-64-per-process.json`. Rates remain subject
to asset sizes, filesystem latency and provider response times.

### Memory-pressure adjustment after repeated interruptions

On November 12 Arizona time, the 512-worker configuration repeatedly hit its
6 GiB service memory limit. Kernel logs confirmed `CONSTRAINT_MEMCG`, with about
6.19 GB of anonymous process memory at one failure. This was the importer's
resource limit, not host-wide memory exhaustion. Automatic retries preserved
checkpoints but repeatedly spent time rescanning previously verified files.

After the request to keep the migration going, concurrency was reduced to 32
workers per process (256 total) and the service ceiling raised to 8 GiB. The
approximately 12 GiB host had about 10.4 GiB available while the importer was
stopped, leaving about 4 GiB outside the new ceiling. All eight partitions,
checksums, conditional creation and full R2 readback remain in place. The
previously measured 52 GB/hour with 256 workers is a historical sample, not a
promise for this run. This resource adjustment is a mitigation; continued memory
and progress monitoring is required to establish sustained stability.

The adjustment retained 2,338,296 verified source files / 122,773,761,857 stored
bytes, 10,159 invalid source checksums and one outstanding readback error. Config
backups use `import.before-memory-adjustment-<UTC>.json` under `/etc/playsim-r2`
and `20-dev-throughput.before-memory-adjustment-<UTC>.conf` under `/opt/playsim-r2`.
