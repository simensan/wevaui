#!/usr/bin/env python3
"""Build a versioned, host-independent static Weva SDK for a Rust target.

Release assets are ordinary files plus a SHA-256 manifest: consumers need no
CMake, archive extractor or Python. ICU data is embedded in the archives.
"""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import re
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
TARGETS = {
    "x86_64-pc-windows-msvc": ("Windows", "x86_64"),
    "x86_64-unknown-linux-gnu": ("Linux", "x86_64"),
    "aarch64-apple-darwin": ("Darwin", "aarch64"),
    "x86_64-apple-darwin": ("Darwin", "x86_64"),
}
LIBRARIES = ("weva", "weva_icu_i18n", "weva_icu_common")
LICENSES = {
    "Weva.txt": "LICENSE.md",
    "ICU.txt": "third_party/icu/LICENSE.txt",
    "Unicode.txt": "third_party/unicode/LICENSE.txt",
    "Ada-MIT.txt": "third_party/ada/LICENSE-MIT",
    "Ada-Apache.txt": "third_party/ada/LICENSE-APACHE",
    "Decimal.txt": "third_party/decimal/LICENSE.txt",
}


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def architecture(value):
    return {"amd64": "x86_64", "x64": "x86_64", "arm64": "aarch64"}.get(value.lower(), value.lower())


def validate_build(target, metadata):
    system, arch = TARGETS[target]
    actual = (metadata.get("osx_architectures") or metadata.get("compiler_architecture")
              or metadata.get("processor", ""))
    if (metadata.get("configuration") != "Release" or metadata.get("system") != system
            or architecture(actual) != arch or metadata.get("pointer_size") != 8):
        raise ValueError(f"Build does not match {target}: {metadata}")
    if set(metadata.get("libraries", {})) != set(LIBRARIES):
        raise ValueError("Native SDK must contain the core and both ICU archives")


def package(root, build, output, target, allow_dirty=False):
    metadata = json.loads((build / "native-Release.json").read_text(encoding="utf-8"))
    validate_build(target, metadata)
    revision = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("A full source commit is required")
    # Do not describe changed or untracked native inputs as a published commit.
    inputs = ["CMakeLists.txt", "libweva", "third_party", "Tools/native", "Tools/package_native.py"]
    dirty = bool(subprocess.check_output(["git", "-C", str(root), "status", "--porcelain", "--", *inputs], text=True).strip())
    if dirty and not allow_dirty:
        raise ValueError("Commit native SDK inputs before packaging a release; --allow-dirty is for local validation only")
    header = root / "libweva/include/weva_c.h"
    text = header.read_text(encoding="utf-8")
    abi = {part: int(re.search(r"#define\s+WEVA_ABI_VERSION_" + part.upper() + r"\s+(\d+)", text).group(1))
           for part in ("major", "minor")}
    sdk = output / "sdk" / target
    release = output / "release"
    sdk.mkdir(parents=True, exist_ok=True)
    release.mkdir(parents=True, exist_ok=True)
    files = {"include/weva_c.h": header}
    for name, path in metadata["libraries"].items():
        source = Path(path)
        expected = name + ".lib" if "windows" in target else "lib" + name + ".a"
        if source.name != expected or not source.is_file() or source.stat().st_size < 8:
            raise ValueError(f"Missing or unexpected native archive: {source}")
        with source.open("rb") as archive:
            if archive.read(8) != b"!<arch>\n":
                raise ValueError(f"Not a static archive: {source}")
        files["lib/" + source.name] = source
    files.update({"licenses/" + name: root / path for name, path in LICENSES.items()})
    inventory = {}
    for relative, source in sorted(files.items()):
        destination = sdk / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, destination)
        asset = target + "--" + relative.replace("/", "--")
        shutil.copyfile(destination, release / asset)
        inventory[relative] = {"asset": asset, "sha256": digest(destination), "size": destination.stat().st_size}
    manifest = {"schema": 1, "target": target, "source_revision": revision, "source_dirty": dirty,
                "abi": abi, "configuration": "Release", "crt": "dynamic" if "windows" in target else "system",
                "compiler": metadata["compiler"], "cmake": metadata["cmake"], "files": inventory}
    encoded = (json.dumps(manifest, indent=2, sort_keys=True) + "\n").encode("utf-8")
    (sdk / "manifest.json").write_bytes(encoded)
    (release / (target + ".json")).write_bytes(encoded)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", choices=TARGETS, required=True)
    parser.add_argument("--build-dir", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cmake-arg", action="append", default=[])
    parser.add_argument("--allow-dirty", action="store_true")
    args = parser.parse_args()
    build = args.build_dir.resolve()
    configure = ["cmake", "-S", str(ROOT / "Tools/native"), "-B", str(build),
                 "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_POLICY_DEFAULT_CMP0168=NEW"]
    if "windows" in args.target:
        configure += ["-A", "x64"]
    elif "apple" in args.target:
        configure += ["-DCMAKE_OSX_ARCHITECTURES=" + ("arm64" if args.target.startswith("aarch64") else "x86_64"),
                      "-DCMAKE_OSX_DEPLOYMENT_TARGET=11.0"]
    subprocess.run([*configure, *args.cmake_arg], check=True)
    subprocess.run(["cmake", "--build", str(build), "--config", "Release", "--parallel", "4"], check=True)
    system, arch = TARGETS[args.target]
    if platform.system() != system or architecture(platform.machine()) != arch:
        raise ValueError("Run SDK qualification on a matching native host; cross-compiled libraries must not claim a native smoke pass")
    subprocess.run(["ctest", "--test-dir", str(build), "-C", "Release", "--output-on-failure", "--no-tests=error"], check=True)
    result = package(ROOT, build, args.output.resolve(), args.target, args.allow_dirty)
    print(json.dumps({"source_revision": result["source_revision"], "source_dirty": result["source_dirty"],
                      "target": result["target"], "files": len(result["files"])}))


if __name__ == "__main__":
    main()
