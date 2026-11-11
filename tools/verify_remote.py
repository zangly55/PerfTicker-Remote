"""Integration check using the current Windows user's encrypted connection settings."""
import base64
import ctypes
import hashlib
import json
import os
import pathlib
import sys
import paramiko

root = pathlib.Path(__file__).resolve().parents[1]
cfg = json.loads((pathlib.Path(os.environ['LOCALAPPDATA']) / 'PerfTickerRemote' / 'remote.json').read_text(encoding='utf-8-sig'))

class Blob(ctypes.Structure):
    _fields_ = [('size', ctypes.c_uint32), ('data', ctypes.POINTER(ctypes.c_ubyte))]

def unprotect(value):
    if not value:
        return ''
    data = base64.b64decode(value)
    buffer = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
    source, output = Blob(len(data), buffer), Blob()
    if not ctypes.windll.crypt32.CryptUnprotectData(ctypes.byref(source), None, None, None, None, 0, ctypes.byref(output)):
        raise RuntimeError('Cannot decrypt the current user credential')
    try:
        return ctypes.string_at(output.data, output.size).decode('utf-8')
    finally:
        ctypes.windll.kernel32.LocalFree(output.data)

class PinnedHost(paramiko.MissingHostKeyPolicy):
    def missing_host_key(self, client, hostname, key):
        fp = 'SHA256:' + base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip('=')
        if fp != cfg['Fingerprint']:
            raise paramiko.SSHException('Host key mismatch')

client = paramiko.SSHClient()
client.set_missing_host_key_policy(PinnedHost())
auth = {'key_filename': cfg['PrivateKeyPath'], 'passphrase': unprotect(cfg.get('KeyPassphraseProtected')) or None} \
    if cfg.get('AuthMethod') == 'key' else {'password': unprotect(cfg.get('PasswordProtected'))}
client.connect(cfg['Host'], port=cfg['Port'], username=cfg['User'], **auth,
               look_for_keys=False, allow_agent=False, timeout=8, auth_timeout=8)
try:
    code = base64.b64encode((root / 'remote' / 'collector.py').read_bytes()).decode()
    cmd = 'python3 -u -c "import base64;exec(base64.b64decode(\'' + code + '\'))" 1 0 3'
    stdin, stdout, stderr = client.exec_command(cmd, timeout=30)
    samples = [json.loads(line) for line in stdout]
    error = stderr.read().decode('utf-8', 'replace')
    status = stdout.channel.recv_exit_status()
    if status or len(samples) != 3:
        raise RuntimeError('Collector failed: ' + error[:1000])
    for s in samples:
        assert s['IsRemote'] and s['Protocol'] == 2
        assert 0 <= s['CpuPct'] <= 100
        assert s['MemTotalGB'] > 0 and isinstance(s['Gpus'], list)
        assert len({g['Id'] for g in s['Gpus']}) == len(s['Gpus'])
        assert s['Procs'] and len(s['CoreMHz']) == len(s['CorePct'])
    out = root / 'artifacts'
    out.mkdir(exist_ok=True)
    (out / 'linux-samples.json').write_text(json.dumps(samples, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'host': samples[-1]['HostName'], 'cores': len(samples[-1]['CoreMHz']),
                      'cpu': samples[-1]['CpuName'], 'gpus': samples[-1]['Gpus'],
                      'processes': samples[-1]['ProcCount'], 'samples': len(samples),
                      'os': samples[-1]['OsName']}, ensure_ascii=False))
finally:
    client.close()
