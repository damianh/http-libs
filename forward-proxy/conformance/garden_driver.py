#!/usr/bin/env python3
"""Replays fixtures.json through the forward proxy using pinned HTTP Garden tooling.

Uses HTTP Garden's socket helpers, HTTP/1 parsers and HTTPRequest.is_valid() from
<garden>/tools. The Garden echo origin (started by run-conformance.mjs) returns the exact
bytes the proxy forwarded, so each fixture observes what an origin would receive.
Garden's Docker targets, REPL and docker/pyyaml dependencies are not used.

Writes {id: {outcome, expect, reference, reasons, observed}} to --output.
"""

import argparse
import concurrent.futures
import json
import re
import socket
import sys
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--garden", required=True, help="Pinned HTTP Garden checkout")
    parser.add_argument("--proxy", required=True, help="host:port of the forward proxy")
    parser.add_argument("--origin", required=True, help="host:port of the Garden echo origin")
    parser.add_argument("--fixtures", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--test-id", default="")
    parser.add_argument("--first-byte-timeout", type=float, default=10.0)
    parser.add_argument("--idle-timeout", type=float, default=1.0)
    args = parser.parse_args()

    sys.path.insert(0, str(Path(args.garden) / "tools"))
    global parse_response, parse_request_stream, sendall, recvall
    from http1 import parse_request_stream, parse_response  # type: ignore  # noqa: E402
    from util import recvall, sendall  # type: ignore  # noqa: E402

    fixtures = json.loads(Path(args.fixtures).read_text(encoding="utf-8"))["fixtures"]
    if args.test_id:
        fixtures = [f for f in fixtures if f["id"] == args.test_id]
        if not fixtures:
            print(f"Unknown fixture {args.test_id!r}.", file=sys.stderr)
            return 2

    host, port = args.proxy.rsplit(":", 1)
    proxy = (host, int(port))

    def run(fixture: dict) -> tuple[str, dict]:
        try:
            return fixture["id"], evaluate(fixture, exchange(fixture, proxy, args), args.origin)
        except Exception as error:  # noqa: BLE001 - recorded as a gating error outcome
            return fixture["id"], {"outcome": "error", "reasons": [f"{type(error).__name__}: {error}"]}

    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        outcomes = dict(pool.map(run, fixtures))

    results = {
        f["id"]: {"outcome": outcomes[f["id"]]["outcome"], "expect": f["expect"], "reference": f["reference"],
                  **{k: v for k, v in outcomes[f["id"]].items() if k != "outcome"}}
        for f in sorted(fixtures, key=lambda f: f["id"])
    }
    Path(args.output).write_text(json.dumps(results, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    passed = sum(1 for r in results.values() if r["outcome"] == "pass")
    print(f"Ran {len(results)} fixtures: {passed} passing.")
    return 0


_STRICT_REQUEST_LINE = re.compile(r"\A[!#$%&'*+.^_`|~0-9A-Za-z-]+ [^\x00-\x20\x7f]+ HTTP/1\.[01]\r\n")


def substitute(text: str, origin: str) -> str:
    return text.replace("{origin}", origin)


def exchange(fixture: dict, proxy: tuple[str, int], args: argparse.Namespace) -> bytes:
    pieces = fixture["payload"] if isinstance(fixture["payload"], list) else [fixture["payload"]]
    data = [substitute(p, args.origin).encode("latin1") for p in pieces]
    received = b""
    with socket.create_connection(proxy, timeout=args.first_byte_timeout) as sock:
        for index, datum in enumerate(data):
            try:
                sendall(sock, datum)
            except OSError:
                break
            last = index == len(data) - 1
            # Wait for the first byte (forwarding includes the echo origin's 0.1 s read window),
            # then read until the connection is idle or closed.
            sock.settimeout(args.first_byte_timeout if last else args.idle_timeout)
            try:
                first = sock.recv(65536)
            except TimeoutError:
                continue
            except OSError:
                break
            received += first
            if not first:
                break
            sock.settimeout(args.idle_timeout)
            received += recvall(sock)
    return received


def decode(value: bytes) -> str:
    return value.decode("latin1")


def observe(raw: bytes) -> dict:
    proxy_responses: list[dict] = []
    forwarded: list = []
    unparsed_forwarded = b""
    origin_received: list[str] = []
    remaining = raw
    while remaining:
        try:
            response, remaining = parse_response(remaining)
        except ValueError:
            break
        headers = {decode(k).lower(): decode(v) for k, v in response.headers}
        if response.code == b"200" and headers.get("server") == "echo-python":
            origin_received.append(decode(response.body))
            requests, leftover = parse_request_stream(response.body)
            forwarded.extend(requests)
            unparsed_forwarded += leftover
        else:
            proxy_responses.append({"status": int(response.code), "proxyStatus": headers.get("proxy-status")})
    return {
        "proxyResponses": proxy_responses,
        "forwarded": forwarded,
        "originReceived": origin_received,
        "unparsedForwarded": decode(unparsed_forwarded),
        "unparsedResponse": decode(remaining),
        "closedWithoutResponse": not raw,
    }


def serializable(observed: dict) -> dict:
    return {
        **observed,
        "forwarded": [
            {
                "method": decode(r.method),
                "uri": decode(r.uri),
                "version": decode(r.version),
                "headers": [[decode(k), decode(v)] for k, v in r.headers],
                "body": decode(r.body),
                "gardenValid": r.is_valid(),
            }
            for r in observed["forwarded"]
        ],
    }


def header_values(request, name: str) -> list[str]:
    return [decode(v) for k, v in request.headers if decode(k).lower() == name]


def check_forward(fixture: dict, observed: dict, origin: str) -> list[str]:
    reasons: list[str] = []
    forwarded = observed["forwarded"]
    if observed["proxyResponses"]:
        reasons.append(f"proxy responded {[r['status'] for r in observed['proxyResponses']]}")
    if not forwarded:
        reasons.append("nothing was forwarded")
        return reasons
    if observed["unparsedForwarded"]:
        reasons.append(f"origin received unparseable bytes {observed['unparsedForwarded']!r}")
    # Garden's request parser is permissive (e.g. "GET  HTTP/1.1" parses), so check raw request lines too.
    for received in observed["originReceived"]:
        if not _STRICT_REQUEST_LINE.match(received):
            reasons.append(f"origin received a malformed request line {received.split(chr(10), 1)[0]!r}")
    for index, request in enumerate(forwarded):
        if not request.is_valid():
            reasons.append(f"forwarded request {index} is invalid under HTTP Garden's HTTPRequest.is_valid()")
    checks = fixture.get("checks", [])
    expected_count = next((c["count"] for c in checks if "count" in c and "header" not in c), 1)
    if len(forwarded) != expected_count:
        reasons.append(f"forwarded {len(forwarded)} requests, expected {expected_count}")
    for check in checks:
        if "count" in check and "header" not in check:
            continue
        index = check.get("request", 0)
        if index >= len(forwarded):
            reasons.append(f"request {index} was not forwarded")
            continue
        request = forwarded[index]
        for field in ("method", "uri", "version", "body"):
            if field in check and decode(getattr(request, field)) != substitute(check[field], origin):
                reasons.append(f"{field} {decode(getattr(request, field))!r} != {substitute(check[field], origin)!r}")
        if "header" in check:
            name = check["header"]
            values = header_values(request, name)
            if check.get("absent") and values:
                reasons.append(f"{name} present: {values!r}")
            if "count" in check and len(values) != check["count"]:
                reasons.append(f"{name} appears {len(values)} times, expected {check['count']}")
            if "equals" in check and values != [substitute(check["equals"], origin)]:
                reasons.append(f"{name} {values!r} != [{substitute(check['equals'], origin)!r}]")
            if "notEquals" in check and substitute(check["notEquals"], origin) in values:
                reasons.append(f"{name} has forbidden value {check['notEquals']!r}")
            if "matches" in check and not any(re.search(check["matches"], v) for v in values):
                reasons.append(f"{name} {values!r} does not match {check['matches']!r}")
            if "notMatches" in check and any(re.search(check["notMatches"], v) for v in values):
                reasons.append(f"{name} {values!r} matches forbidden {check['notMatches']!r}")
    return reasons


def check_not_forwarded(observed: dict) -> list[str]:
    if observed["forwarded"] or observed["unparsedForwarded"]:
        return [f"{len(observed['forwarded'])} request(s) reached the origin"]
    return []


def check_reject(fixture: dict, observed: dict) -> list[str]:
    reasons = check_not_forwarded(observed)
    statuses = [r["status"] for r in observed["proxyResponses"]]
    allowed = fixture.get("status")
    if not statuses:
        reasons.append("no error response (connection closed or timed out)")
    elif not any((s in allowed) if allowed else 400 <= s < 500 for s in statuses):
        reasons.append(f"status {statuses} not in {allowed or '4xx'}")
    return reasons


def evaluate(fixture: dict, raw: bytes, origin: str) -> dict:
    observed = observe(raw)
    expect = fixture["expect"]
    if expect == "forward":
        reasons = check_forward(fixture, observed, origin)
    elif expect == "reject":
        reasons = check_reject(fixture, observed)
    elif expect == "not-forwarded":
        reasons = check_not_forwarded(observed)
    elif expect == "reject-or-forward":
        rejected = check_reject(fixture, observed)
        reasons = [] if not rejected else check_forward(fixture, observed, origin)
        if reasons:
            reasons = [f"not rejected ({'; '.join(rejected)}) and forward checks failed"] + reasons
    else:
        raise ValueError(f"Unknown expectation {expect!r}")
    return {"outcome": "fail" if reasons else "pass", "reasons": reasons, "observed": serializable(observed)}


if __name__ == "__main__":
    sys.exit(main())
