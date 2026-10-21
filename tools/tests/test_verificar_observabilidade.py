"""Exercise the verifier in both Python modes without network or real routing changes."""
import io
import json
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.response import addinfourl


SCRIPT = Path(__file__).resolve().parents[1] / "verificar-observabilidade.py"


def probe(fail_at):
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        script = root / "tools/verificar-observabilidade.py"
        script.parent.mkdir()
        script.write_bytes(SCRIPT.read_bytes())
        examples = root / "tools/exemplos"
        examples.mkdir()
        request = b"&lt;ConsultarNfseEnvio&gt;&lt;/ConsultarNfseEnvio&gt;"
        (examples / "04-consultar-nfse.xml").write_bytes(request)
        routing = root / "fachada/config/routing.json"
        routing.parent.mkdir(parents=True)
        original = b'{"KillSwitch":true,"Operations":{}}\n'
        routing.write_bytes(original)
        calls = []

        def open_response(call, timeout):
            config = json.loads(routing.read_bytes())
            calls.append((config["Operations"]["ConsultarNfse"]["Mode"], config["KillSwitch"]))
            number = len(calls)
            expected = [("LEGACY", False), ("NEW", False), ("SHADOW", False),
                        ("SHADOW", False), ("NEW", True)]
            if calls[-1] != expected[number - 1]:
                raise RuntimeError("Unexpected routing at HTTP call")
            if call.full_url != "http://127.0.0.1:8080/NfseWS.asmx" or call.get_method() != "POST":
                raise RuntimeError("Unexpected HTTP request")
            if call.get_header("Soapaction") != '"http://notacarioca.rio.gov.br/ConsultarNfse"':
                raise RuntimeError("Unexpected SOAP operation")
            if (call.data != request) != (number == 4):
                raise RuntimeError("Unexpected divergence request")
            body = b"incorrect" if number == fail_at else b"baseline"
            return addinfourl(io.BytesIO(body), {}, call.full_url, 200)

        try:
            with patch("urllib.request.build_opener") as build:
                build.return_value.open.side_effect = open_response
                runpy.run_path(str(script), run_name="__main__")
        finally:
            if routing.read_bytes() != original or routing.with_name("observability-next.json").exists():
                raise RuntimeError("Routing was not restored exactly")
            print(json.dumps({"calls": len(calls), "restored": True}))


class ObservabilityCliTests(unittest.TestCase):
    def test_calls_validation_and_restoration_in_both_modes(self):
        for optimized in (False, True):
            for fail_at in (0, 2, 3, 5):
                with self.subTest(optimized=optimized, fail_at=fail_at):
                    command = [sys.executable] + (["-O"] if optimized else [])
                    result = subprocess.run(
                        command + [str(Path(__file__).resolve()), "--probe", str(fail_at)],
                        capture_output=True, text=True, timeout=10)
                    self.assertEqual(1 if fail_at else 0, result.returncode, result.stderr)
                    self.assertEqual({"calls": fail_at or 5, "restored": True},
                                     json.loads(result.stdout.splitlines()[-1]))
                    if fail_at:
                        message = ("KillSwitch response differs from LEGACY" if fail_at == 5
                                   else "Response differs from LEGACY")
                        self.assertIn(message, result.stderr)


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--probe":
        probe(int(sys.argv[2]))
    else:
        unittest.main()
