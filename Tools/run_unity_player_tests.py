#!/usr/bin/env python3
"""Build and run the Windows UI render suite in a real Unity test player.

Requires a licensed Unity editor with Windows build support and a graphics
device. Uses this project's scripting backend unless --backend is supplied. Keep each
run in a fresh output directory so failed evidence cannot be overwritten.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

import psutil


def stop_owned_players(output):
    """Unity may exit after reporting results while its test player keeps drawing."""
    stopped = []
    player_root = (output / "player").resolve()
    for process in psutil.process_iter(['pid', 'exe']):
        executable = process.info['exe']
        if not executable or not Path(executable).resolve().is_relative_to(player_root):
            continue
        try:
            process.terminate()
            try:
                process.wait(timeout=5)
            except psutil.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
            stopped.append({"pid": process.pid, "executable": executable})
        except psutil.NoSuchProcess:
            pass
    return stopped


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--backend", choices=("mono", "il2cpp"),
                        help="Override the test player's scripting backend through Unity Test Framework")
    parser.add_argument("--release", action="store_true",
                        help="Build non-development IL2CPP with high managed stripping")
    args = parser.parse_args()
    if args.release and args.backend != "il2cpp":
        parser.error("--release requires --backend il2cpp")
    root = Path(__file__).resolve().parents[1]
    out = args.out.resolve()
    if not args.unity.is_file():
        parser.error("--unity must identify the installed Unity executable")
    if out.exists():
        parser.error("--out must be a new directory; preserve previous run evidence")
    out.mkdir(parents=True)
    env = dict(os.environ, WEVA_TEST_PLAYER="1", WEVA_RENDERPASS_DUMP=str(out / "renders"))
    env["WEVA_TEST_PLAYER_BACKEND"] = args.backend or "project"
    env["WEVA_TEST_PLAYER_RELEASE"] = "1" if args.release else "0"
    env["WEVA_TEST_BUILD_RECEIPT"] = str(out / "build-settings.json")
    if args.release:
        env["WEVA_TEST_LOCAL_RESULTS"] = str(out / "results.xml")
    else:
        env.pop("WEVA_TEST_LOCAL_RESULTS", None)
    command = [str(args.unity.resolve()), "-batchmode", "-projectPath", str(root),
               "-runTests", "-testPlatform", "StandaloneWindows64",
               "-testFilter", "Weva.Tests.Rendering.NativeRenderPassTests",
               "-buildPlayerPath", str(out / "player"),
               "-testResults", str(out / "results.xml"), "-logFile", str(out / "build.log")]
    if args.backend:
        settings = out / "test-settings.json"
        settings.write_text(json.dumps({"scriptingBackend":
                            "IL2CPP" if args.backend == "il2cpp" else "Mono2x"}) + "\n",
                            encoding="utf-8")
        command += ["-testSettingsFile", str(settings)]
    settings_path = root / "ProjectSettings/ProjectSettings.asset"
    original_settings = settings_path.read_bytes()
    player_result = None
    try:
        result = subprocess.run(command, env=env, check=False)
        if args.release and result.returncode == 0:
            players = list((out / "player").rglob("PlayerWithTests.exe"))
            if len(players) != 1:
                raise RuntimeError("Expected exactly one standalone test player")
            player_result = subprocess.run([str(players[0]), '-logFile', str(out / 'player.log')],
                                           env=env, check=False, timeout=180)
    finally:
        try:
            stopped = stop_owned_players(out)
        finally:
            # Unity restores logical settings, but serializing them still
            # changes defaults and metadata in the user's project file.
            settings_path.write_bytes(original_settings)
    receipt = {"command": command, "requested_backend": args.backend or "project",
               "release": args.release, "exit_code": result.returncode,
               "player_exit_code": player_result.returncode if player_result else None,
               "stopped_players": stopped, "passed": False}
    xml = out / "results.xml"
    if xml.exists():
        run = ET.parse(xml).getroot()
        receipt["test_run"] = run.attrib
        cases = run.findall(".//test-case")
        receipt["passed"] = (result.returncode == 0 and len(cases) >= 7
                             and (not args.release or (player_result is not None and player_result.returncode == 0))
                             and all(case.get("result") == "Passed" for case in cases))
    (out / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt, indent=2))
    return 0 if receipt["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
