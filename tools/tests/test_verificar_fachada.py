"""Exercise the acceptance CLI with synthetic responses and no network access."""
import io
from pathlib import Path
import runpy
import subprocess
import sys
import unittest
from unittest.mock import patch
from urllib.response import addinfourl


SCRIPT = Path(__file__).resolve().parents[1] / "verificar-fachada.py"
PUBLIC_SERVICE = "https://public.example/NfseWS.asmx"


def probe(scenario):
    def open_response(request, timeout):
        direct = request.full_url.startswith("http://legacy.example/")
        status, content_type = 200, "text/xml; charset=utf-8"
        if request.data is not None:
            body = (b'<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">'
                    b'<soap:Body><outputXML xmlns="http://notacarioca.rio.gov.br/">'
                    b'&lt;Resultado&gt;&lt;CompNfse/&gt;&lt;/Resultado&gt;'
                    b'</outputXML></soap:Body></soap:Envelope>')
            if scenario == "response_bytes" and not direct:
                body += b" "
            if scenario == "response_status" and not direct:
                status = 202
            if scenario == "response_content_type" and not direct:
                content_type = "application/xml"
            if scenario == "http_failure":
                status = 500
            if scenario == "soap_fault":
                body = (b'<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">'
                        b'<soap:Body><soap:Fault/></soap:Body></soap:Envelope>')
            if scenario == "missing_output":
                body = b"<Envelope/>"
            if scenario == "empty_output":
                body = b'<Envelope><outputXML xmlns="http://notacarioca.rio.gov.br/"/></Envelope>'
            if scenario == "no_invoices":
                body = body.replace(b'&lt;CompNfse/&gt;', b'')
        elif "?wsdl" in request.full_url:
            address = "http://legacy.example/NfseWS.asmx" if direct else PUBLIC_SERVICE
            body = (f'<definitions xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/">'
                    f'<soap:address location="{address}"/></definitions>').encode()
            if scenario == "wsdl_status" and not direct:
                status = 202
            if scenario == "wsdl_content_type" and not direct:
                content_type = "application/xml"
            if scenario == "wsdl_http_failure":
                status = 503
            if scenario == "missing_address" and not direct:
                body = b"<definitions/>"
            if scenario == "wrong_address" and not direct:
                body = body.replace(PUBLIC_SERVICE.encode(), b"http://legacy.example/NfseWS.asmx")
            if scenario == "wsdl_contract" and not direct:
                body = body.replace(b"</definitions>", b"<changed/></definitions>")
        else:
            content_type, body = "text/plain", b"OK"
            if scenario == "health_status":
                status = 503
            if scenario == "health_body":
                body = b"UNHEALTHY"
        return addinfourl(io.BytesIO(body), {"Content-Type": content_type}, request.full_url, status)

    with patch("urllib.request.build_opener") as build_opener:
        build_opener.return_value.open.side_effect = open_response
        sys.argv = [str(SCRIPT), "--legacy", "http://legacy.example",
                    "--facade", "http://facade.example", "--public-service", PUBLIC_SERVICE]
        runpy.run_path(str(SCRIPT), run_name="__main__")


class AcceptanceCliTests(unittest.TestCase):
    def run_probe(self, scenario, optimized):
        command = [sys.executable]
        if optimized:
            command.append("-O")
        return subprocess.run(command + [str(Path(__file__).resolve()), "--probe", scenario],
                              capture_output=True, text=True, timeout=10)

    def test_valid_responses_pass(self):
        for optimized in (False, True):
            with self.subTest(optimized=optimized):
                result = self.run_probe("valid", optimized)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual(6, len(result.stdout.splitlines()))
                self.assertIn('"only_address_changed": true', result.stdout)

    def test_invalid_responses_fail(self):
        scenarios = {
            "response_bytes": "Response mismatch",
            "response_status": "Response mismatch",
            "response_content_type": "Response mismatch",
            "http_failure": "HTTP failure",
            "soap_fault": "SOAP Fault",
            "missing_output": "Missing SOAP output",
            "empty_output": "Missing SOAP output",
            "no_invoices": "Query returned no invoices",
            "health_status": "Unhealthy",
            "health_body": "Unhealthy",
            "wsdl_status": "WSDL HTTP status or content type mismatch",
            "wsdl_content_type": "WSDL HTTP status or content type mismatch",
            "wsdl_http_failure": "WSDL HTTP status or content type mismatch",
            "missing_address": "WSDL public service address mismatch",
            "wrong_address": "WSDL public service address mismatch",
            "wsdl_contract": "WSDL changed beyond its address",
        }
        for optimized in (False, True):
            for scenario, message in scenarios.items():
                with self.subTest(optimized=optimized, scenario=scenario):
                    result = self.run_probe(scenario, optimized)
                    self.assertEqual(1, result.returncode, result.stderr)
                    self.assertIn(message, result.stderr)
                    self.assertNotIn('"only_address_changed": true', result.stdout)


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--probe":
        probe(sys.argv[2])
    else:
        unittest.main()
