#!/usr/bin/env python3
"""Read-only SOAP demonstration; temporarily selects routes and restores exact file bytes."""
import hashlib
import json
from pathlib import Path
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
ROUTING = ROOT / 'fachada/config/routing.json'
original = ROUTING.read_bytes()
request = (ROOT / 'tools/exemplos/04-consultar-nfse.xml').read_bytes()
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def configure(mode, kill=False):
    pending = ROUTING.with_name('observability-next.json')
    pending.write_text(json.dumps({'KillSwitch': kill, 'Operations': {
        'ConsultarNfse': {'Mode': mode, 'Percentage': 100, 'Providers': []}}}))
    pending.replace(ROUTING)


def consult(body=request):
    trace = uuid.uuid4().hex
    call = urllib.request.Request('http://127.0.0.1:8080/NfseWS.asmx', data=body, headers={
        'Content-Type': 'text/xml; charset=utf-8',
        'SOAPAction': '"http://notacarioca.rio.gov.br/ConsultarNfse"',
        'traceparent': '00-' + trace + '-0123456789abcdef-01'})
    with opener.open(call, timeout=60) as response:
        data = response.read()
        print(json.dumps({'traceId': trace, 'status': response.status, 'bytes': len(data),
                          'sha256': hashlib.sha256(data).hexdigest()}))
        return data


try:
    baseline = None
    for mode in ('LEGACY', 'NEW', 'SHADOW'):
        configure(mode)
        print(mode)
        result = consult()
        if baseline is None:
            baseline = result
        else:
            if result != baseline:
                raise SystemExit('Response differs from LEGACY')
    print('SHADOW: known strict-character divergence')
    divergent = request.replace(b'&lt;/ConsultarNfseEnvio&gt;',
                               b'&lt;Extra&gt;&amp;#1;&lt;/Extra&gt;&lt;/ConsultarNfseEnvio&gt;')
    consult(divergent)
    configure('NEW', kill=True)
    print('KillSwitch -> LEGACY')
    if consult() != baseline:
        raise SystemExit('KillSwitch response differs from LEGACY')
finally:
    pending = ROUTING.with_name('observability-next.json')
    pending.write_bytes(original)
    pending.replace(ROUTING)
    print('Original routing restored: ' + hashlib.sha256(ROUTING.read_bytes()).hexdigest())
