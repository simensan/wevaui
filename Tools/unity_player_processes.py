"""Ownership-scoped cleanup shared by Unity test and timing runners."""
from pathlib import Path

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
