# The 121 "missing sculpt assets" — what they actually are

Ray / Releases · 2026-10-28 · PlaySim, 46 regions, measured post-roll on
`playsim/region-tranquillity:1.0.49.4-1f500868`

## Answer first

**None of the 121 is a missing or zero-length asset.** All 46 textures they name are
present in `playsim_grid.fsassets`, and all 46 blobs are on the NFS store, gzip-intact
and non-zero. The warning was wrong, and it was wrong because I wrote it.

**The real asset fault is a different message, and it is 34× bigger: 4,169 lines of
`asset provider returned null asset`, 4,162 of them on one region — Cozumel — naming
2,734 distinct mesh assets, of which 0 exist in the asset service.** Cozumel's prims
were copied in on 2026-09-01 without their assets.

## What was measured

All 46 running containers, 60,752 log lines (control: the harvest counted them, so it
read real logs):

| count | message |
|------:|---------|
| 121 | `No sculpt data for prim … - The asset is missing or zero length.` |
| 0 | `Sculpty texture decode failed for prim …` |
| 0 | `… decoded to nothing` |
| 0 | `asset provider returned invalid mesh data for prim …` |
| **4,169** | **`asset provider returned null asset for mesh of prim …`** |
| 0 | `fail to process sculpt map for prim` / `invalid degenerated mesh` |

The 121: **46 distinct textures, 121 distinct prims, 119 on `misty-isle`** and 2 on
`robinwood`.

### The 46 textures are all fine

| check | target (46) | control (12 unrelated assets, same create window) |
|---|---|---|
| row in `playsim_grid.fsassets` | 46 / 46 | 12 / 12 |
| blob on NFS (`…/data/XX/XX/XX/XXXX/HASH.gz`) | 46 found | 12 found |
| `gzip -t` integrity | 46 OK | 12 OK |
| uncompressed length > 0 | 46 / 46 (1,157 – 10,666 bytes) | 12 / 12 |

The control matters: without it, "all 46 found" could equally have meant my path formula
was wrong and I was stat-ing nothing.

## Why the warning is a false alarm

`ODEMeshWorker.GetMesh` (`ODEMeshWorker.cs:516`) calls `CreateMesh` **with
`SculptData` deliberately empty**, as a probe of the mesh caches. A null return is how it
learns it has to go and fetch the asset:

```csharp
IMesh mesh = m_mesher.CreateMesh(actor.Name, pbs, size, clod, true, convex, true);
if (mesh is null && pbs.SculptEntry && (pbs.SculptData is null || pbs.SculptData.Length == 0))
{
    repData.meshState = MeshState.needAsset;   // <-- not a failure. a question.
    return;
}
```

So the branch I put a `Warn` in is the **normal first pass for every sculpt prim whose
mesh is not already cached**. `BSShape.VerifyMeshCreated` does the same thing with a
filler shape, so BulletSim's meshmerizer had the identical false alarm. 119 of 121 landing
on one region is a cold mesh cache, not a sick asset store — and it is why the count
barely moved across the roll (303 → 301): recreating a container cools the cache.

The genuine conditions were **already** detected, upstream of my change, in
`ODEAssetRequest.ODEassetReceived`: `null asset` and `invalid mesh data`. I added a third
message in the one place that cannot tell them apart.

## Cozumel: 2,734 mesh assets that do not exist

The real message names only the prim, not the asset, so getting from 4,169 lines to the
assets meant pulling all 6,912 of Cozumel's prims and hand-decoding `ExtraParams`
(`0x30`/`0x60` → 16-byte UUID + 1 sculpt-type byte; all type 5 = Mesh).

| set | distinct assets | present in `fsassets` |
|---|---:|---:|
| referenced by the 4,162 **failing** prims | 2,734 | **0** |
| referenced by Cozumel's other prims (control) | 1,482 | 1,161 (78%) |

Not in the legacy `playsim_grid.assets` table either (0 of a 200 sample). No Cozumel OAR
on the archive share. 4,162 of Cozumel's 6,912 prims — 60% — have no physics proxy and
nothing to render, permanently, until the assets are supplied.

`/playsim/backups/preimport/cozumel.log` (2026-09-01 13:07) shows the import copied
**content**, prim by prim: `prims: 6912`, ownership transferred to Sally Jackson,
estate 106 created. `cozumel-assets.log` (13:27) contains two header lines and nothing
else:

```
=== Cozumel asset references ===
=== of the missing, how many can we hash-verify from the DH index? ===
```

The asset side was started, the question was the right one, and it was never finished.

## Fixed in 1cbbb4057b (pushed to GitHub + GitLab)

1. `ubODEMeshmerizer.cs` — the cache-probe branch returns at `Debug`, with the reason
   written down so nobody re-adds the warning.
2. `Meshmerizer.cs` — same guard, same fix (BulletSim path).
3. `ODEMeshWorker.cs` — the null branch now **names the asset**
   (`returned null asset <uuid> for mesh of prim <name> - asset does not exist in the
   asset service`), and the invalid-data branch reports the byte count. This is the
   difference between 4,169 unactionable lines and a list of 2,734 assets to go and find.

Log-level only; no behavioural change. All three projects compile under SDK 8.0.424.

## Open, and not mine to decide

- **The 2,734 assets.** The import log says the source index was DarkHearts'. Recovering
  them means reading DH's asset store. **That is a DarkHearts operation and I have not
  touched it.** Alternatives: re-import Cozumel from a complete OAR, or leave it.
- **BulletSim's own asset failures go only to `DetailLog`** (debug), so on a BulletSim
  region a failed sculpt fetch is silent. The fleet is ubODE, so I left it; it is a real
  gap if a region ever goes back.
- `1f5008688f`'s commit message overstates the decoder upgrade (66 → actually 1 texture).
  The correction is recorded in `1cbbb4057b`'s message, since the pushed commit and the
  tag cannot be rewritten.
