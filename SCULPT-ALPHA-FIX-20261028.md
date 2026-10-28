# Sculpt maps with alpha: the geometry was being thrown away after the decode

Ray / Releases · 2026-10-28 · fix `51d234ec85` on `playsim-1.0.49` (GitHub + GitLab)

## Answer first

Bram's symptom is real and I reproduced it on our own build with our own assets.
**His stated cause is wrong, and the fix he suggested would have done nothing.**

- The decode does **not** premultiply. `J2kImage.FromBytes(...).As<SKBitmap>()` returns
  `Unpremul` with the RGB intact — measured on 11 real PlaySim sculpt maps that carry
  alpha, 6 of them with alpha uniformly 0, all keeping 246–48,584 distinct RGB values.
  Decoding "to Opaque or Unpremul" is a no-op: it is already Unpremul.
- What destroys them is the **next** step. To Skia, alpha is coverage, so anything that
  **writes or composites** a pixel discards the colour where alpha is low.

## The two destructive steps (measured)

| step | alpha-0 maps | partial alpha |
|---|---|---|
| `SKBitmap.Decode` of the cached PNG — the `smap_` sculpt-map cache **read** in `Meshmerizer.cs`, `cacheSculptMaps = true` by default | **6 of 6 → 1 distinct RGB** | 48,584 → 8 (alpha 1); 49,032 → 17,155 (alpha 0..255) |
| `SKCanvas.DrawBitmap` scale in `Meshing/Meshmerizer/SculptMap.cs` | **6 of 6 → 1 distinct RGB** | partial loss |
| hand copy via `new SKColor(r,g,b)` — what `ubODEMeshing/SculptMap.cs` already does | no loss | no loss |

### Correction: it is the read, not the encode

I first reported this as the PNG **encode** losing the colour, because I measured the round
trip as one step. Bram read a real cached file on DarkHearts and found it was **not** black
— 357 distinct RGB, alpha 0 throughout. He was right. Re-measured, reading the files I
wrote with a stdlib-only PNG decoder (no Skia anywhere near it):

| file on disk | distinct RGB in the file | what `SKBitmap.Decode` returns |
|---|---:|---:|
| pre-fix, RGBA, alpha 0..0 | 246 / 254 / 306 / 755 / 994 / 3,257 — **the full set** | **1** |
| pre-fix, RGBA, alpha 1..1 | 48,584 — the full set | 8 |
| post-fix, **3-channel RGB, no alpha channel** | the full set | the full set |

So `SKImage.Encode(Png)` is lossless and `SKBitmap.Decode` of an alpha-bearing PNG is what
destroys the colour, on load. Every one of DH's 329 existing `smap_` files is intact on
disk and poison on read, which is exactly why the `smap2_` prefix change is the operative
part of the fix on an upgraded grid.

One distinct RGB means every vertex sits at the same point, every triangle is degenerate,
and `BSShape.VerifyMeshCreated` leaves the prim as the `SHAPE_BOX` it installs while
waiting for the asset. A hollow sculpt becomes a solid invisible box.

Both are in `Meshing/` — the BulletSim path — which is exactly why PlaySim has never shown
this. ubODE's scaler drops alpha by hand.

**Worth knowing separately:** `SKBitmap.SetPixel` premultiplies whatever the declared
`AlphaType` says, `Opaque` and `Unpremul` included. Write `(200,100,50,0)`, read it back,
get `(0,0,0,0)`. `SKBitmap.Copy()` does the same. Only *reading* is safe.

## The fix

`FlattenSculptAlpha` reads the pixels out and reinstalls them opaque with alpha 255,
immediately after the decode and **before** the cache write, in both meshmerizers. The
cache prefix becomes **`smap2_`**.

Before the write, not after, and the reason is structural: a flattened map encodes to a
**3-channel PNG with no alpha channel at all** (measured: `ctype=2, ch=3`), so the cache
read has nothing to premultiply against. Flattening after the read could not undo the loss.
The flatten on the cache-read path is kept as a no-op safety net.

The prefix change matters: a file written before this commit may hold an
alpha-flattened-to-black copy of its sculpt map, and such a file reproduces the degenerate
mesh on every rez for as long as it is read. **That is why re-rezzing the object brings the
blockage back** — the poison is on disk in `DecodedSculptMapPath` (default
`j2kDecodeCache`), not in the asset. The new prefix retires the old entries without anyone
having to empty the directory.

## Verification

Driving the **shipping assemblies**, invoking the real private method by reflection, on
real sculpt maps at lod 8/16/32/64:

| path | (asset, lod) cases | degenerate before | after |
|---|---:|---:|---:|
| BulletSim (`Meshing`) | 32 | **12** | **0** |
| ubODE (PlaySim's) | 188 | 0 | 0 — and **188/188 vertex counts identical** |

The ubODE run is the regression control: the change is a no-op there, which is what it
should be. All dependent projects build (`Meshing`, `ubODEMeshing`, `BulletS`, `ubOde`,
`RegionServer`), no new warnings.

Note the lod pattern: the degenerate cases are at lod 8 and 16, where the canvas scaler
runs. At lod 32 a 4096-pixel map skips the scaler — so the PNG cache, which runs
unconditionally, is the one that bites a default configuration.

## Open

- **Settled: it is the cache, not the scaler, on DH.** Bram checked (Robert approved that
  read; nothing was changed): `MeshLevelOfDetail`, `SculptLevelOfDetail` and
  `MeshLevelOfDetailMegaPrim` are set nowhere in DH's config, so DH runs our defaults of
  32, and `DecodedSculptMapPath` / `CacheSculptMaps` are commented out, so caching is on
  at `j2kDecodeCache`. At lod 32 a 4,096-pixel map skips the canvas scaler, so the scaler
  never fires for Bram's assets. The cache read is the whole of it. His file —
  `smap_26a04032…`, 658 bytes, 16×256 RGBA, 357 distinct RGB, alpha 0 throughout, first
  pixel (54,35,5,0) — is the same shape as my `7460d7d7` case exactly.
- 329 `smap_` files and no `smap2_` files in that directory, all of them intact on disk.
  They stop being read the moment the fix is deployed; nothing has to be deleted.
- Not built into an image and not rolled. Tag `1.0.49.4` still points at `1f5008688f`.
- **I have touched nothing on DarkHearts.**
