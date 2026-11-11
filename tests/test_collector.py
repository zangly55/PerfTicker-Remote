import importlib.util
import pathlib
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('collector', pathlib.Path(__file__).parents[1] / 'remote' / 'collector.py')
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)


class CollectorTests(unittest.TestCase):
    def test_counter_resets_and_new_devices_do_not_create_spikes(self):
        self.assertEqual(c.rates({'a': (5, 40), 'new': (999, 999)}, {'a': (10, 20)}, 2, 2), [0, 10])

    def test_guest_time_is_not_counted_twice(self):
        with patch.object(c, 'read', return_value='cpu 10 0 5 75 5 2 1 2 8 0\nctxt 80\n'):
            cpus, ctxt = c.cpu_stats()
        self.assertEqual(cpus['cpu'], (100, 80))
        self.assertEqual(ctxt, 80)
        self.assertEqual(c.cpu_percent((200, 160), (100, 80)), 20)

    def test_gpu_unsupported_sensors_are_null_not_zero(self):
        row = 'NVIDIA GPU, 580.0, 25, 1, 1200, 2000, 2400, 100, 200, 60, 120.5, 300, [N/A]\n'
        with patch.object(c.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, row, '')):
            gpu, driver, warning = c.gpu(0)
        self.assertIsNone(gpu['FanPct'])
        self.assertEqual(gpu['UtilPct'], 25)
        self.assertTrue(gpu['Available'])
        self.assertFalse(warning)

    def test_gpu_failure_keeps_cpu_monitoring_possible(self):
        with patch.object(c.subprocess, 'run', side_effect=FileNotFoundError()):
            gpu, _, warning = c.gpu(0)
        self.assertFalse(gpu['Available'])
        self.assertIsNone(gpu['PowerW'])
        self.assertTrue(warning)

    def test_pid_reuse_and_parentheses_in_process_names(self):
        fields = ['S'] + ['0'] * 23
        fields[11], fields[12], fields[17], fields[19], fields[21] = '300', '100', '4', '900', '1024'
        stat = '42 (model (worker)) ' + ' '.join(fields)
        with patch.object(c.glob, 'glob', return_value=['/proc/42']), patch.object(c, 'read', return_value=stat), \
             patch.object(c.os, 'sysconf', side_effect=lambda n: 100 if n == 'SC_CLK_TCK' else 4096, create=True), \
             patch.object(c.os, 'listdir', return_value=['0', '1']):
            current, rows = c.processes({42: (999, 123)}, 1, 8)
        self.assertEqual(rows[0]['Name'], 'model (worker)')
        self.assertEqual(rows[0]['CpuPct'], 0)
        self.assertEqual(rows[0]['MemMB'], 4)
        self.assertEqual(current[42], (400, 900))


if __name__ == '__main__':
    unittest.main()
