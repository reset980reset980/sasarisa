"""Read-only input identities and binding. Does not open an audio stream."""
import ctypes
import hashlib
import json
import os
import re
from collections import Counter

PREFIX = 'input:v1:'
KEY = re.compile(r'input:v1:(wasapi|named):[0-9a-f]{64}\Z')


def identity(kind, *parts):
    payload = json.dumps(['v1', kind, *parts], ensure_ascii=False, separators=(',', ':'))
    return PREFIX + kind + ':' + hashlib.sha256(payload.encode('utf-8')).hexdigest()


def wasapi_endpoint(sd, index):
    """Borrow PortAudio's IMMDevice, read its opaque ID, and free only the ID string."""
    if os.name != 'nt':
        return None
    try:
        library = ctypes.CDLL(sd._libname)
        get_device = library.PaWasapi_GetIMMDevice
        get_device.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_void_p)]
        get_device.restype = ctypes.c_int
        device = ctypes.c_void_p()
        if get_device(index, ctypes.byref(device)) != 0 or not device.value:
            return None
        table = ctypes.cast(device, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        get_id = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p))(table[5])
        text = ctypes.c_void_p()
        if get_id(device, ctypes.byref(text)) < 0 or not text.value:
            return None
        ole = ctypes.WinDLL(os.path.join(os.environ['SystemRoot'], 'System32', 'ole32.dll'))
        free = ole.CoTaskMemFree
        free.argtypes = [ctypes.c_void_p]; free.restype = None
        try:
            return ctypes.wstring_at(text.value)
        finally:
            free(text)
        # PaWasapi_GetIMMDevice does not AddRef. PortAudio owns the interface.
    except (AttributeError, OSError, ValueError, KeyError, ctypes.ArgumentError):
        return None


def is_wasapi(host):
    return host.get('type') == 13 or host.get('name', '').casefold() == 'windows wasapi'


def label(text):
    clean = ''.join(' ' if ord(c) < 32 or 127 <= ord(c) <= 159 else c for c in text)
    return ' '.join(clean.split()).encode('utf-16-le')[:480].decode('utf-16-le', errors='ignore')


def catalog(sd, endpoint_lookup=None):
    endpoint_lookup = endpoint_lookup or (lambda index: wasapi_endpoint(sd, index))
    rows = []
    for index, device in enumerate(sd.query_devices()):
        if device['max_input_channels'] <= 0:
            continue
        host = sd.query_hostapis(device['hostapi'])
        endpoint = endpoint_lookup(index) if is_wasapi(host) else None
        kind = 'wasapi' if endpoint else 'named'
        key = identity(kind, endpoint) if endpoint else identity(kind, host['name'], device['name'])
        rows.append(dict(Index=index, Key=key, Id=key, LegacyId=str(index), IdentityKind=kind,
                         Label=label(device['name'] + ' · ' + host['name']), Wasapi=is_wasapi(host)))
    counts = Counter(row['Key'] for row in rows)
    for row in rows:
        if counts[row['Key']] > 1:
            row['Id'] = row['LegacyId']; row['IdentityKind'] = 'index'
            row['Label'] = label(row['Label'] + ' · 동일 이름 중복 · 순번 ' + row['LegacyId'])
    return rows


def device_options(sd):
    rows = [dict(Id='', Label='Windows 기본 마이크', LegacyId='', IdentityKind='default')]
    rows.extend({key: row[key] for key in ('Id', 'Label', 'LegacyId', 'IdentityKind')} for row in catalog(sd))
    return rows


def bind_input(sd, value):
    if value is None or value == '':
        return dict(device=None)
    if not isinstance(value, (str, int)) or isinstance(value, bool):
        raise ValueError('입력 마이크 설정 형식을 확인하세요.')
    value = str(value)
    if value.startswith('input:'):
        if not KEY.fullmatch(value):
            raise ValueError('저장된 마이크 식별자 형식을 확인하세요.')
        rows = catalog(sd)
        candidates = [row for row in rows if row['Key'] == value]
        if len(candidates) > 1:
            raise ValueError('같은 이름의 입력 마이크가 여러 개입니다. 설정에서 사용할 장치를 다시 선택하세요.')
        if not candidates:
            raise ValueError('선택한 마이크를 찾지 못했어요. 연결을 확인하거나 설정에서 입력 장치를 다시 선택하세요.')
        selected = candidates[0]
        options = dict(device=selected['Index'])
        if selected['Wasapi']:
            options['extra_settings'] = sd.WasapiSettings(auto_convert=True)
        return options
    # Preserve numeric/name-based configurations until the user saves a selection.
    device = int(value) if value.isdigit() else value
    selected = sd.query_devices(device, 'input')
    options = dict(device=device)
    if is_wasapi(sd.query_hostapis(selected['hostapi'])):
        options['extra_settings'] = sd.WasapiSettings(auto_convert=True)
    return options
