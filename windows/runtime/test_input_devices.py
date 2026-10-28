"""Input identity and worker binding checks; no microphone or speaker opens."""
import io
import json
import sys
import unittest
from unittest.mock import MagicMock, Mock, patch

import input_devices as devices
import speech_worker as worker


def mic(name, host=0, channels=1):
    return dict(name=name, hostapi=host, max_input_channels=channels)


class Inventory:
    def __init__(self, rows):
        self.rows = rows
        self.InputStream = MagicMock()
        self.WasapiSettings = Mock(side_effect=lambda **options: options)
    def query_devices(self, selected=None, kind=None):
        if selected is None: return self.rows
        if isinstance(selected, int): return self.rows[selected]
        return next(row for row in self.rows if row['name'] == selected)
    def query_hostapis(self, index):
        return dict(name=['MME', 'Windows WASAPI'][index])


class InputDeviceChecks(unittest.TestCase):
    def test_named_input_survives_index_reordering_without_changing_api(self):
        sd = Inventory([mic('speaker', channels=0), mic('chosen')])
        key = devices.catalog(sd)[0]['Id']
        sd.rows = [mic('other'), mic('speaker', channels=0), mic('chosen')]
        self.assertEqual(devices.bind_input(sd, key), dict(device=2))
        sd.InputStream.assert_not_called()

    def test_wasapi_identity_disambiguates_identical_names_and_survives_reordering(self):
        sd = Inventory([mic('same', 1), mic('same', 1)])
        with patch.object(devices, 'wasapi_endpoint', side_effect=lambda sd, index: ['endpoint-A', 'endpoint-B'][index]):
            key = devices.catalog(sd)[0]['Id']
        with patch.object(devices, 'wasapi_endpoint', side_effect=lambda sd, index: ['endpoint-B', 'endpoint-A'][index]):
            self.assertEqual(devices.bind_input(sd, key), dict(device=1, extra_settings={'auto_convert': True}))
        sd.InputStream.assert_not_called()

    def test_removed_input_does_not_select_a_new_device_at_the_old_index(self):
        sd = Inventory([mic('chosen')]); key = devices.catalog(sd)[0]['Id']; sd.rows = [mic('other')]
        with self.assertRaisesRegex(ValueError, '찾지 못'): devices.bind_input(sd, key)
        sd.InputStream.assert_not_called()

    def test_same_name_collision_requires_explicit_selection(self):
        sd = Inventory([mic('same')]); key = devices.catalog(sd)[0]['Id']; sd.rows.append(mic('same'))
        self.assertEqual([row['Id'] for row in devices.catalog(sd)], ['0', '1'])
        with self.assertRaisesRegex(ValueError, '여러 개'): devices.bind_input(sd, key)

    def test_missing_wasapi_endpoint_never_falls_back_to_an_identical_name(self):
        sd = Inventory([mic('same', 1)])
        with patch.object(devices, 'wasapi_endpoint', return_value='endpoint-A'): key = devices.catalog(sd)[0]['Id']
        with patch.object(devices, 'wasapi_endpoint', return_value='endpoint-B'):
            with self.assertRaisesRegex(ValueError, '찾지 못'): devices.bind_input(sd, key)
        with patch.object(devices, 'wasapi_endpoint', return_value=None):
            with self.assertRaisesRegex(ValueError, '찾지 못'): devices.bind_input(sd, key)

    def test_default_and_legacy_configurations_keep_their_existing_semantics(self):
        empty = Mock(); self.assertEqual(devices.bind_input(empty, ''), dict(device=None)); empty.query_devices.assert_not_called()
        sd = Inventory([mic('one'), mic('two')])
        self.assertEqual(devices.bind_input(sd, '1'), dict(device=1))
        self.assertEqual(devices.bind_input(sd, 'one'), dict(device='one'))
        sd.InputStream.assert_not_called()

    def test_reserved_identifier_validation_happens_before_device_queries(self):
        sd = Mock()
        for value in ['input:v0:named:' + 'a' * 64, 'input:v1:named:' + 'g' * 64, 'input:v1:wasapi:../file', True, ['0']]:
            with self.assertRaises(ValueError): devices.bind_input(sd, value)
        sd.query_devices.assert_not_called()

    def test_catalog_hides_output_only_devices_normalizes_labels_and_keeps_legacy_mapping(self):
        sd = Inventory([mic('output', channels=0), mic('마이크\r\n한글')])
        rows = devices.device_options(sd)
        self.assertEqual(len(rows), 2); self.assertEqual(rows[1]['LegacyId'], '1')
        self.assertNotIn('\n', rows[1]['Label']); self.assertNotIn('마이크', rows[1]['Id'])
        self.assertEqual(rows[1]['IdentityKind'], 'named')
        self.assertLessEqual(len(devices.label('🎤' * 300).encode('utf-16-le')), 480)

    def test_missing_selected_input_fails_before_model_load_and_capture(self):
        sd = Inventory([mic('other')]); key = devices.identity('named', 'MME', 'chosen')
        with patch.dict(sys.modules, {'sounddevice': sd}), patch.object(sys, 'stdin', io.StringIO(json.dumps({'Device': key}) + '\n')), patch.object(sys, 'argv', ['speech_worker.py']), patch.object(worker, 'Recognizer') as load:
            with self.assertRaisesRegex(ValueError, '찾지 못'): worker.main()
        load.assert_not_called(); sd.InputStream.assert_not_called()

    def test_real_worker_passes_resolved_device_to_stream_without_rewriting_saved_identity(self):
        sd = Inventory([mic('chosen')]); key = devices.catalog(sd)[0]['Id']; sd.rows.insert(0, mic('other'))
        cfg = dict(Device=key, InitialCapture=False)
        decoder = Mock(hint_count=0)
        with patch.dict(sys.modules, {'sounddevice': sd}), patch.object(sys, 'stdin', io.StringIO(json.dumps(cfg) + '\n{"type":"shutdown"}\n')), patch.object(sys, 'argv', ['speech_worker.py']), patch.object(worker, 'Recognizer', return_value=decoder), patch.object(worker, 'emit'):
            worker.main()
        self.assertEqual(sd.InputStream.call_args.kwargs['device'], 1)
        self.assertEqual(cfg['Device'], key)


if __name__ == '__main__': unittest.main()
