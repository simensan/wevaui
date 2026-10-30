import unittest
from summarize_render_trace import summarize, union_duration


class TraceTests(unittest.TestCase):
    def test_nested_and_overlapping_intervals(self):
        self.assertEqual(union_duration([(0, 10), (2, 5), (8, 15), (20, 22)]), 17)

    def test_window_process_filter_and_parallel_workers(self):
        events = [{'name': 'renderbench:start', 'ts': 100, 'pid': 1},
                  {'name': 'renderbench:end', 'ts': 200, 'pid': 1}]
        for name, pid, tid, ts, dur in [('Paint', 1, 1, 90, 60), ('PrePaint', 1, 1, 110, 10),
                                        ('Paint', 2, 1, 100, 100), ('RasterTask', 1, 2, 100, 80),
                                        ('RasterTask', 1, 3, 150, 80)]:
            events.append(dict(name=name, pid=pid, tid=tid, ts=ts, dur=dur, ph='X'))
        result = summarize(events, 2)
        self.assertEqual(result['paint_recording_cpu_ms_per_update'], .025)
        self.assertEqual(result['raster_workers_cpu_ms_per_update'], .065)
        self.assertIsNone(result['gpu_execution_ms'])

    def test_missing_window_rejected(self):
        with self.assertRaises(ValueError):
            summarize([], 2)


if __name__ == '__main__':
    unittest.main()
