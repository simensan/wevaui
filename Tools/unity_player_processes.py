"""Ownership-scoped cleanup shared by Unity test and timing runners."""
from pathlib import Path

def stop_owned_players(output, process_api=None):
    """Unity may exit after reporting results while its test player keeps drawing."""
    if process_api is None:
        import psutil as process_api
    stopped = []
    player_root = (output / "player").resolve()
    for process in process_api.process_iter(['pid', 'exe']):
        executable = process.info['exe']
        if not executable or not Path(executable).resolve().is_relative_to(player_root):
            continue
        try:
            process.terminate()
            try:
                process.wait(timeout=5)
            except process_api.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
            stopped.append({"pid": process.pid, "executable": executable})
        except process_api.NoSuchProcess:
            pass
    return stopped
