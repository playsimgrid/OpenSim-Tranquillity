#!/usr/bin/env python3
"""Package the optional S3 module's existing Release build; never deploy it."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    version = json.loads((root / "version.json").read_text())["version"]
    build = root / "Source/OpenSim.Services.S3AssetService/bin/Release/net8.0"
    names = ["OpenSim.Services.S3AssetService.dll", "OpenSim.Services.S3AssetService.deps.json",
             "AWSSDK.S3.dll", "AWSSDK.Core.dll"]
    missing = [name for name in names if not (build / name).is_file()]
    if missing:
        parser.error("Build the Release module first; missing: " + ", ".join(missing))
    destination = args.output.resolve() / ("playsim-s3-assets-" + version + "-evaluation")
    destination.mkdir(parents=True, exist_ok=False)
    for name in names:
        shutil.copy2(build / name, destination / name)
    shutil.copy2(root / "Docs/S3_ASSET_SERVICE.md", destination)
    shutil.copy2(root / "Docs/DH_R2_MIGRATION.md", destination)
    shutil.copy2(root / "Source/OpenSim.Server.GridServer/AppData/S3Assets.ini.example", destination)
    shutil.copy2(root / "LICENSE.txt", destination)
    for license_file in (root / "ThirdPartyLicenses").glob("AWS-SDK-*.txt"):
        shutil.copy2(license_file, destination)
    assets = json.loads((root / "Source/OpenSim.Services.S3AssetService/obj/project.assets.json").read_text())
    source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    metadata = {"target_release": version, "base_commit": source,
                "status": "evaluation; not deployed; live provider validation required",
                "sdk_dependencies": [key for key in assets["libraries"] if key.startswith("AWSSDK.")]}
    source_files = sorted((root / "Source/OpenSim.Services.S3AssetService").glob("*.cs"))
    source_files += sorted((root / "Source/OpenSim.Services.S3AssetService").glob("*.csproj"))
    metadata["module_source_sha256"] = {
        str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest() for path in source_files}
    (destination / "BUILD.json").write_text(json.dumps(metadata, indent=2) + "\n")
    sums = [hashlib.sha256(path.read_bytes()).hexdigest() + "  " + path.name
            for path in sorted(destination.iterdir()) if path.is_file()]
    (destination / "SHA256SUMS").write_text("\n".join(sums) + "\n")
    archive = Path(str(destination) + ".zip")
    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED) as zip_file:
        for path in sorted(destination.iterdir()):
            zip_file.write(path, destination.name + "/" + path.name)
    print(archive)


if __name__ == "__main__":
    main()
