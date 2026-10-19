#!/usr/bin/env python3
"""Characterize/compare only ConsultarNfse. Never send fiscal write actions.

Stores hashes and aggregate counts only. Does not persist response payloads.
"""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
NS = "http://notacarioca.rio.gov.br/"
SOAP = "http://schemas.xmlsoap.org/soap/envelope/"


def payload(case):
    if "envelope" in case:
        return case["envelope"].encode()
    source = ROOT / "tools/exemplos" / case.get("example", "04-consultar-nfse.xml")
    inner = ET.fromstring(source.read_bytes()).find(f".//{{{NS}}}inputXML").text
    for before, after in case.get("replace", []):
        if before not in inner:
            raise ValueError(f"Replacement not found in {case['name']}")
        inner = inner.replace(before, after, 1)
    if "input" in case:
        inner = case["input"]
    return (f'<soap:Envelope xmlns:soap="{SOAP}"><soap:Body>'
            f'<ConsultarNfseRequest xmlns="{NS}"><inputXML>{escape(inner)}</inputXML>'
            '</ConsultarNfseRequest></soap:Body></soap:Envelope>').encode()


def query(base, body):
    req = urllib.request.Request(base.rstrip("/") + "/NfseWS.asmx", body,
                                 {"Content-Type": "text/xml; charset=utf-8",
                                  "SOAPAction": f'"{NS}ConsultarNfse"'})
    try:
        response = urllib.request.urlopen(req, timeout=45)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, response.headers.get("Content-Type"), response.read()


def describe(result):
    status, content_type, body = result
    envelope = ET.fromstring(body)
    output = envelope.find(f".//{{{NS}}}outputXML")
    inner = ET.fromstring(output.text) if output is not None else None
    return {"status": status, "content_type": content_type,
            "sha256": hashlib.sha256(body).hexdigest(), "bytes": len(body),
            "invoices": len(inner.findall(".//CompNfse")) if inner is not None else 0,
            "code": inner.findtext(".//Codigo") if inner is not None else None,
            "next": inner.findtext("ProximaPagina") if inner is not None else None}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--legacy", required=True)
    parser.add_argument("--modern")
    parser.add_argument("--cases", type=Path, default=ROOT / "novo/tests/cases/consultar-nfse.json")
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    cases = json.loads(args.cases.read_text())
    report = []
    failures = 0
    for case in cases:
        body = payload(case)
        original = query(args.legacy, body)
        summary = describe(original)
        expected = case["expected"]
        characterized = all(summary.get(key) == value for key, value in expected.items())
        row = {"case": case["name"], "request_sha256": hashlib.sha256(body).hexdigest(),
               "legacy": summary, "characterized": characterized}
        if not characterized:
            failures += 1
        if args.modern:
            modern = query(args.modern, body)
            row["modern"] = describe(modern)
            row["modern_expected"] = all(row["modern"].get(key) == value
                                         for key, value in case.get("modern_expected", {}).items())
            row["exact_match"] = original == modern
            row["classification"] = "equal" if original == modern else case.get("divergence", "unresolved")
            if not row["modern_expected"] or (not row["exact_match"] and row["classification"] == "unresolved"):
                failures += 1
        report.append(row)
        print(json.dumps(row, ensure_ascii=False), flush=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"cases": len(report), "failures": failures,
                      "exact_matches": sum(row.get("exact_match", False) for row in report)}))
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
