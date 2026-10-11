#!/usr/bin/env python3
"""Build and run the Windows UI render suite in a real Unity test player.

Requires a licensed Unity editor with Windows build support and a graphics
device. Uses this project's scripting backend (Mono by default). Keep each
run in a fresh output directory so failed evidence cannot be overwritten.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    out = args.out.resolve()
    if not args.unity.is_file():
        parser.error("--unity must identify the installed Unity executable")
    if out.exists():
        parser.error("--out must be a new directory; preserve previous run evidence")
    out.mkdir(parents=True)
    env = dict(os.environ, WEVA_TEST_PLAYER="1", WEVA_RENDERPASS_DUMP=str(out / "renders"))
    command = [str(args.unity.resolve()), "-batchmode", "-projectPath", str(root),
               "-runTests", "-testPlatform", "StandaloneWindows64",
               "-testFilter", "Weva.Tests.Rendering.NativeRenderPassTests",
               "-buildPlayerPath", str(out / "player"),
               "-testResults", str(out / "results.xml"), "-logFile", str(out / "build.log")]
    result = subprocess.run(command, env=env, check=False)
    receipt = {"command": command, "exit_code": result.returncode, "passed": False}
    xml = out / "results.xml"
    if xml.exists():
        run = ET.parse(xml).getroot()
        receipt["test_run"] = run.attrib
        cases = run.findall(".//test-case")
        receipt["passed"] = (result.returncode == 0 and len(cases) >= 6
                             and all(case.get("result") == "Passed" for case in cases))
    (out / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt, indent=2))
    return 0 if receipt["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
