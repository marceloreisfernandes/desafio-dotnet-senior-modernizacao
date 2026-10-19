#!/usr/bin/env python3
"""Read-only Oracle integration: compare the two existing queries without saving SOAP/PII."""
import argparse
import hashlib
import json
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, ProxyHandler, build_opener
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--legacy", required=True, help="Direct legacy origin (internal container IP)")
    parser.add_argument("--facade", default="http://localhost:8080")
    parser.add_argument("--public-service", default="http://localhost:8080/NfseWS.asmx")
    args = parser.parse_args()
    opener = build_opener(ProxyHandler({}))

    def request(origin, path, body=None, operation=None):
        headers = {}
        if body is not None:
            headers = {"Content-Type": "text/xml; charset=utf-8",
                       "SOAPAction": '"http://notacarioca.rio.gov.br/' + operation + '"'}
        req = Request(origin.rstrip("/") + path, data=body, headers=headers)
        try:
            response = opener.open(req, timeout=130)
        except HTTPError as error:
            response = error
        with response:
            return response.status, response.headers.get("Content-Type"), response.read()

    examples = Path(__file__).resolve().parent / "exemplos"
    for file, operation in [("04-consultar-nfse.xml", "ConsultarNfse"),
                            ("05-consultar-nfse-servico-tomado.xml", "ConsultarNfseServicoTomado")]:
        payload = (examples / file).read_bytes()
        direct = request(args.legacy, "/NfseWS.asmx", payload, operation)
        proxied = request(args.facade, "/NfseWS.asmx", payload, operation)
        if direct != proxied:
            raise SystemExit(f"Response mismatch: {operation}")
        if proxied[0] != 200:
            raise SystemExit(f"HTTP failure: {operation}")
        envelope = ET.fromstring(proxied[2])
        if envelope.find(".//{http://schemas.xmlsoap.org/soap/envelope/}Fault") is not None:
            raise SystemExit(f"SOAP Fault: {operation}")
        output = envelope.find(".//{http://notacarioca.rio.gov.br/}outputXML")
        if output is None or not output.text:
            raise SystemExit("Missing SOAP output")
        result = ET.fromstring(output.text)
        invoices = sum(1 for node in result.iter() if node.tag.split("}")[-1] == "CompNfse")
        if invoices == 0:
            raise SystemExit(f"Query returned no invoices: {operation}")
        print(json.dumps({"operation": operation, "status": proxied[0], "content_type": proxied[1],
                          "request_sha256": hashlib.sha256(payload).hexdigest(),
                          "response_sha256": hashlib.sha256(proxied[2]).hexdigest(),
                          "response_bytes": len(proxied[2]), "invoices": invoices, "exact_match": True}))

    for path in ["/health", "/health/ready", "/health/live"]:
        status, _, body = request(args.facade, path)
        if status != 200 or body != b"OK":
            raise SystemExit(f"Unhealthy: {path}")
        print(json.dumps({"path": path, "status": status, "body": "OK"}))

    direct = request(args.legacy, "/NfseWS.asmx?wsdl")
    proxied = request(args.facade, "/NfseWS.asmx?wsdl")
    if proxied[:2] != direct[:2] or proxied[0] != 200:
        raise SystemExit("WSDL HTTP status or content type mismatch")
    expected, actual = ET.fromstring(direct[2]), ET.fromstring(proxied[2])
    addresses = actual.findall(".//{http://schemas.xmlsoap.org/wsdl/soap/}address")
    if not addresses or any(node.get("location") != args.public_service for node in addresses):
        raise SystemExit("WSDL public service address mismatch")
    for node in expected.findall(".//{http://schemas.xmlsoap.org/wsdl/soap/}address"):
        node.set("location", args.public_service)
    if ET.tostring(expected) != ET.tostring(actual):
        raise SystemExit("WSDL changed beyond its address")
    print(json.dumps({"path": "/NfseWS.asmx?wsdl", "status": 200,
                      "public_service": args.public_service, "only_address_changed": True}))


if __name__ == "__main__":
    main()
