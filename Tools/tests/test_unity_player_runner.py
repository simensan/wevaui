import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

import psutil

spec = importlib.util.spec_from_file_location(
    'unity_player_runner', Path(__file__).resolve().parents[1] / 'run_unity_player_tests.py')
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class PlayerOwnershipTests(unittest.TestCase):
    def test_stops_only_players_inside_this_runs_output(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'run'
            owned = Mock(pid=101, info={'exe': str(output / 'player' / 'game.exe')})
            neighbor = Mock(pid=102, info={'exe': str(output / 'player-other' / 'game.exe')})
            inaccessible = Mock(pid=103, info={'exe': None})
            with patch.object(runner.psutil, 'process_iter', return_value=[owned, neighbor, inaccessible]):
                stopped = runner.stop_owned_players(output)
            self.assertEqual([101], [row['pid'] for row in stopped])
            owned.terminate.assert_called_once()
            neighbor.terminate.assert_not_called()
            inaccessible.terminate.assert_not_called()

    def test_owned_player_that_ignores_termination_is_killed_and_waited(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            owned = Mock(pid=101, info={'exe': str(output / 'player' / 'game.exe')})
            owned.wait.side_effect = [psutil.TimeoutExpired(5), 0]
            with patch.object(runner.psutil, 'process_iter', return_value=[owned]):
                runner.stop_owned_players(output)
            owned.kill.assert_called_once()
            self.assertEqual(2, owned.wait.call_count)


if __name__ == '__main__':
    unittest.main()
