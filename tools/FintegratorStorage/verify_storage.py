"""End-to-end verification of the Elasticsearch storage backend.

Drives the real API the way the settings page does, then proves that the growing datasets land in
the mock cluster and are served back from it, and that switching a dataset back to the database
leaves the database path working.

    python tools/FintegratorStorage/verify_storage.py --api http://localhost:5000 \
        --elasticsearch http://localhost:9210 \
        --es-log tools/FintegratorStorage/mock-elasticsearch/es-requests.jsonl

Exit code 0 when every check passes.
"""

import argparse
import json
import os
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request

CHECKS = []

VENDOR_MOCK_PORT = 5099
VENDOR_MOCK = os.path.join("tools", "FintegratorShopifyBc", "mock-vendors", "server.py")
VENDOR_MOCK_HEALTH = "http://127.0.0.1:%d/__control/health" % VENDOR_MOCK_PORT


def vendor_mock_health():
    """True when a healthy instance of the vendor double answers on VENDOR_MOCK_PORT.

    A session that already started the double (run_e2e.py, another verification agent) must not be
    treated as a failure: the flow only needs *a* working double on that port.
    """
    try:
        with urllib.request.urlopen(VENDOR_MOCK_HEALTH, timeout=3) as response:
            if response.status != 200:
                return False
            payload = json.loads(response.read().decode("utf-8", "replace"))
            return isinstance(payload, dict) and payload.get("ok") is True
    except Exception:
        return False


def port_answers(port):
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=2):
            return True
    except OSError:
        return False


def start_vendor_mock():
    """Makes the Shopify/Business Central double the order-create flow talks to available.

    Returns ``(process, reused)``:

    * ``(None, True)``  — a healthy double was already listening; it is reused and left alone.
    * ``(Popen, False)`` — this call started it and owns its lifetime.
    * ``(None, False)`` — the port is taken by something else, or the double did not come up.
    """
    if vendor_mock_health():
        print("   reusing the vendor double already listening on port %s" % VENDOR_MOCK_PORT)
        return None, True

    if not os.path.exists(VENDOR_MOCK) or port_answers(VENDOR_MOCK_PORT):
        return None, False

    process = subprocess.Popen(
        [sys.executable, VENDOR_MOCK, "--port", str(VENDOR_MOCK_PORT)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    deadline = time.time() + 20
    while time.time() < deadline:
        if process.poll() is not None:
            return None, False
        if vendor_mock_health():
            return process, False
        time.sleep(0.5)

    stop_vendor_mock(process)
    return None, False


def stop_vendor_mock(process):
    # None here means either "nothing was started" or "an instance owned by somebody else was
    # reused": either way this harness must not kill it.
    if process is None:
        return
    try:
        process.terminate()
        process.wait(timeout=10)
    except Exception:
        process.kill()


def check(label, ok, detail=""):
    CHECKS.append((label, bool(ok), detail))
    status = "ok  " if ok else "FAIL"
    print("  %s  %s%s" % (status, label, "" if not detail else "  -> " + str(detail)[:220]))
    return bool(ok)


def request(method, url, api_key, body=None, timeout=60):
    payload = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(url, data=payload, method=method)
    req.add_header("Accept", "application/json")
    if payload is not None:
        req.add_header("Content-Type", "application/json")
    if api_key:
        req.add_header("X-Api-Key", api_key)

    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            raw = response.read().decode("utf-8", "replace")
            return response.status, (json.loads(raw) if raw.strip().startswith(("{", "[")) else raw)
    except urllib.error.HTTPError as error:
        raw = error.read().decode("utf-8", "replace")
        try:
            return error.code, json.loads(raw)
        except ValueError:
            return error.code, raw


def wait_for(url, api_key, seconds, label):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            status, _ = request("GET", url, api_key, timeout=5)
            if status < 500:
                return True
        except Exception:
            pass
        time.sleep(1)
    print("timed out waiting for %s (%s)" % (label, url))
    return False


def read_es_log(path, since_offset):
    """Returns (new lines, new offset) from the mock cluster's request log."""
    if not os.path.exists(path):
        return [], since_offset

    with open(path, "r", encoding="utf-8") as handle:
        handle.seek(since_offset)
        lines = [json.loads(line) for line in handle if line.strip()]
        return lines, handle.tell()


def settings_payload(datasets, enabled=True, url="", prefix="fusetor"):
    return {
        "elasticsearchEnabled": enabled,
        "url": url,
        "username": "",
        "password": None,
        "clearPassword": False,
        "apiKey": None,
        "clearApiKey": False,
        "indexPrefix": prefix,
        "allowInvalidCertificate": False,
        "timeoutSeconds": 15,
        "datasets": datasets,
    }


def dataset(name, use_elasticsearch, granularity="Daily", retention_days=0, use_ilm=True):
    return {
        "dataset": name,
        "useElasticsearch": use_elasticsearch,
        "granularity": granularity,
        "retentionDays": retention_days,
        "useIlm": use_ilm,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--api", default="http://localhost:5000")
    parser.add_argument("--elasticsearch", default="http://localhost:9210")
    parser.add_argument("--api-key", default="dev-api-key")
    parser.add_argument("--es-log", default=os.path.join("tools", "FintegratorStorage", "mock-elasticsearch", "es-requests.jsonl"))
    parser.add_argument("--flow-id", default="3b29296a-a809-4ffd-80a3-04a7496bac57", help="standalone flow to execute")
    args = parser.parse_args()

    api = args.api.rstrip("/")
    print("storage verification\n  api           : %s\n  elasticsearch : %s\n" % (api, args.elasticsearch))

    if not wait_for(api + "/health", args.api_key, 120, "the API"):
        return 1

    if not wait_for(args.elasticsearch + "/", "", 60, "the mock cluster"):
        return 1

    log_offset = os.path.getsize(args.es_log) if os.path.exists(args.es_log) else 0

    # ---------------------------------------------------------------- 1. settings + index plan
    print("1. configure Elasticsearch and create the index plan")
    status, current = request("GET", api + "/api/storage/settings", args.api_key)
    check("GET /api/storage/settings returns the dataset catalog", status == 200 and len(current.get("catalog", [])) == 4, status)

    status, test = request("POST", api + "/api/storage/test", args.api_key)
    if current.get("url"):
        # A previous run already configured the cluster.
        check("POST /api/storage/test succeeds against the configured cluster",
              status == 200 and test.get("success") is True, test.get("message"))
    else:
        check("POST /api/storage/test rejects an unconfigured cluster with a readable message",
              status == 200 and test.get("success") is False and "No cluster URL" in test.get("message", ""), test.get("message"))

    payload = settings_payload(
        [
            dataset("logs", True, "Daily", 3),
            dataset("packet-logs", True, "Daily", 0),
            dataset("cross-references", True, "Monthly", 0),
            dataset("executions", False, "Monthly", 0),
        ],
        enabled=True,
        url=args.elasticsearch,
        prefix="fusetor",
    )
    status, saved = request("PUT", api + "/api/storage/settings", args.api_key, payload)
    check("PUT /api/storage/settings saves and prepares the index plan", status == 200 and saved.get("success") is True, saved.get("message"))
    applied = {item["dataset"]: item for item in saved.get("applied", [])}
    check("all three Elasticsearch datasets were prepared", set(applied) == {"logs", "packet-logs", "cross-references"}, sorted(applied))
    check("the logs template/policy/alias were created",
          applied.get("logs", {}).get("success") and applied["logs"]["writeAlias"] == "fusetor-logs-write",
          applied.get("logs"))

    status, test = request("POST", api + "/api/storage/test", args.api_key)
    check("POST /api/storage/test connects to the cluster", status == 200 and test.get("success") is True, test.get("message"))

    lines, log_offset = read_es_log(args.es_log, log_offset)
    paths = [line["path"] + " " + line.get("method", "") for line in lines]
    check("index templates were pushed for every dataset",
          sum(1 for line in lines if line["path"].startswith("/_index_template/")) == 3,
          [line["path"] for line in lines if line["path"].startswith("/_index_template/")])
    check("ILM policies were pushed",
          sum(1 for line in lines if line["path"].startswith("/_ilm/policy/")) == 3,
          [line["path"] for line in lines if line["path"].startswith("/_ilm/policy/")])

    template_bodies = [line.get("body_json") for line in lines if line["path"].startswith("/_index_template/")]
    lifecycle = [body["template"]["settings"].get("index.lifecycle.name") for body in template_bodies if body]
    check("each template attaches its ILM policy and rollover alias",
          all(name for name in lifecycle) and len(lifecycle) == 3, lifecycle)

    policy_bodies = [line.get("body_json") for line in lines if line["path"].startswith("/_ilm/policy/")]
    delete_phases = [body["policy"]["phases"].get("delete", {}).get("min_age") for body in policy_bodies if body]
    check("the ILM policies carry a delete phase (or keep forever when retention is 0)",
          len(delete_phases) == 3 and "3d" in delete_phases, delete_phases)

    status, inventory = request("GET", api + "/api/storage/status", args.api_key)
    by_dataset = {item["dataset"]: item for item in inventory.get("datasets", [])}
    check("GET /api/storage/status reports Elasticsearch for the moved datasets",
          by_dataset["logs"]["backend"] == "Elasticsearch" and by_dataset["executions"]["backend"] == "Database",
          {name: item["backend"] for name, item in by_dataset.items()})
    check("the log index exists in the cluster",
          any(index["name"].startswith("fusetor-logs-") for index in by_dataset["logs"]["indices"]),
          [index["name"] for index in by_dataset["logs"]["indices"]])

    # ---------------------------------------------------------------- 2. write path
    print("\n2. write path: a real flow run goes to the cluster")
    status, flows = request("GET", api + "/api/integrationflow", args.api_key)
    flow_id = args.flow_id
    if isinstance(flows, list) and flows:
        # Prefer the configured flow when it still exists, otherwise take whatever this database has.
        known = {flow.get("id") for flow in flows}
        if flow_id not in known:
            flow_id = flows[0]["id"]
    check("a flow is available to execute", bool(flow_id), flow_id)

    status, before_packets = request("GET", api + "/api/packetlogs?page=1&pageSize=1&hours=24", args.api_key)
    status, before_logs = request("GET", api + "/api/logs?page=1&pageSize=1", args.api_key)

    status, run = request("POST", api + "/api/integrationflow/%s/run" % flow_id, args.api_key)
    check("POST /api/integrationflow/{id}/run queues a run", status in (200, 202), (status, run))

    print("   waiting for the engine to finish the run…")
    time.sleep(25)

    lines, log_offset = read_es_log(args.es_log, log_offset)
    bulk_targets = {}
    for line in lines:
        if "_bulk" in line["path"]:
            bulk_targets[line["path"]] = bulk_targets.get(line["path"], 0) + 1
    check("packet documents were bulked into the packet-logs write alias",
          any("/fusetor-packet-logs-write/_bulk" in path for path in bulk_targets), sorted(bulk_targets))
    check("log documents were bulked into the logs write alias",
          any("/fusetor-logs-write/_bulk" in path for path in bulk_targets), sorted(bulk_targets))

    status, indices = request("GET", args.elasticsearch + "/_cat/indices/fusetor-*?format=json&bytes=b", "", timeout=30)
    documents = {row["index"]: row.get("docs.count", 0) for row in indices} if isinstance(indices, list) else {}
    check("packet documents are indexed in the cluster",
          any(name.startswith("fusetor-packet-logs-") and count > 0 for name, count in documents.items()), documents)

    # ---------------------------------------------------------------- 2b. cross-reference writes
    print("\n2b. cross-reference writes through the full flow path")
    status, all_flows = request("GET", api + "/api/integrationflow", args.api_key)
    cross_flow = next(
        (flow for flow in all_flows if "(e2e mock)" in flow.get("name", "") and "Order Create" in flow.get("name", "")),
        None) if isinstance(all_flows, list) else None

    vendor_mock = None
    vendor_mock_reused = False
    if cross_flow is not None:
        # The flow pulls orders from the Shopify/BC double and stores one ledger entry per order.
        # Without it the flow stops at the first HTTP action and never reaches the store.
        vendor_mock, vendor_mock_reused = start_vendor_mock()

    if cross_flow is None:
        check("an end-to-end order-create flow exists to exercise the cross-reference store", False, "not deployed")
    elif vendor_mock is None and not vendor_mock_reused:
        check("the vendor double started (needed to drive the order-create flow)", False,
              "port %s is taken by something that does not answer /__control/health" % VENDOR_MOCK_PORT)
    else:
        check("the vendor double is available (started or reused)",
              True, "reused an already-healthy instance" if vendor_mock_reused else "started a fresh instance")
        try:
            lines, log_offset = read_es_log(args.es_log, log_offset)
            status, run = request("POST", api + "/api/integrationflow/%s/run" % cross_flow["id"], args.api_key)
            check("the order-create flow was queued", status in (200, 202), (status, run))
            print("   waiting for the engine to finish the run…")
            time.sleep(30)

            lines, log_offset = read_es_log(args.es_log, log_offset)
            check("the flow checked the cluster for already-imported keys",
                  any("/fusetor-cross-references-*/_mget" in line["path"] for line in lines),
                  sorted({line["path"] for line in lines if "_mget" in line["path"]}))
            check("the import ledger was written through the cross-references write alias",
                  any("/fusetor-cross-references-write/_bulk" in line["path"] for line in lines),
                  sorted({line["path"] for line in lines if "_bulk" in line["path"]}))

            status, entries = request("GET", api + "/api/crossreferences/shopify-orders-imported/entries?page=1&pageSize=5", args.api_key)
            check("the ledger entries page back from the cluster",
                  status == 200 and isinstance(entries, dict) and entries.get("totalCount", 0) > 0,
                  (status, entries.get("totalCount") if isinstance(entries, dict) else entries))
        finally:
            stop_vendor_mock(vendor_mock)

        # The entry store's write side, exercised live: clearing a list runs a delete-by-query
        # against the dataset pattern instead of a row-by-row delete.
        lines, log_offset = read_es_log(args.es_log, log_offset)
        status, cleared = request("DELETE", api + "/api/crossreferences/es-verification/entries", args.api_key)
        check("clearing a list is accepted", status in (200, 204), (status, cleared))
        lines, log_offset = read_es_log(args.es_log, log_offset)
        check("the clear ran as a delete-by-query against the cluster",
              any("/fusetor-cross-references-*/_delete_by_query" in line["path"] for line in lines),
              sorted({line["path"] for line in lines if "delete_by_query" in line["path"]}))

    # ---------------------------------------------------------------- 3. read path
    print("\n3. read path: the pages are served from the cluster")
    status, packets = request("GET", api + "/api/packetlogs?page=1&pageSize=10&hours=24", args.api_key)
    check("GET /api/packetlogs returns rows while packet logs live in Elasticsearch",
          status == 200 and isinstance(packets, list) and len(packets) > 0, (status, len(packets) if isinstance(packets, list) else packets))
    if isinstance(packets, list) and packets:
        first = packets[0]
        check("packet rows keep flow/integration/node context",
              bool(first.get("flowName")) and bool(first.get("nodeName")),
              {key: first.get(key) for key in ("flowName", "integrationName", "nodeName", "connectionName")})

        status, detail = request("GET", api + "/api/packetlogs/%s" % first["packetId"], args.api_key)
        check("GET /api/packetlogs/{id} returns the stored bodies", status == 200 and "requestBody" in detail, status)

    status, logs = request("GET", api + "/api/logs?page=1&pageSize=5", args.api_key)
    check("GET /api/logs pages from Elasticsearch", status == 200 and logs.get("total", 0) > 0, (status, logs.get("total")))
    check("the log page reports a cursor the console can tail from", logs.get("cursor", 0) > 0, logs.get("cursor"))

    status, tail = request("GET", api + "/api/logs/tail?afterId=0&limit=5", args.api_key)
    check("GET /api/logs/tail emits entries in cursor order",
          status == 200 and len(tail.get("entries", [])) > 0 and tail.get("cursor", 0) > 0,
          (status, len(tail.get("entries", [])), tail.get("cursor")))

    status, facets = request("GET", api + "/api/logs/facets", args.api_key)
    check("facets are aggregated in the cluster", status == 200 and len(facets.get("services", [])) > 0, facets)

    status, lists = request("GET", api + "/api/crossreferences", args.api_key)
    check("cross-reference lists are still read from the database", status == 200 and isinstance(lists, list), status)

    # A cross-reference write through the API exercises the entry store directly.
    status, created = request("POST", api + "/api/crossreferences", args.api_key, {"name": "es-verification", "description": "verified by verify_storage.py"})
    check("a cross-reference list can be created", status in (200, 201, 409), (status, created))

    status, entries = request("GET", api + "/api/crossreferences/es-verification/entries?page=1&pageSize=10", args.api_key)
    check("cross-reference entries page from the cluster (empty list is a valid answer)",
          status == 200 and "entries" in entries, (status, entries))

    # ---------------------------------------------------------------- 4. retention
    print("\n4. retention")
    status, report = request("POST", api + "/api/storage/retention/run", args.api_key)
    by_dataset = {item["dataset"]: item for item in report.get("datasets", [])}
    check("POST /api/storage/retention/run reports every dataset", status == 200 and len(by_dataset) == 4, status)
    check("logs report that ILM owns their deletion",
          "ILM" in by_dataset["logs"]["action"], by_dataset["logs"])
    check("the database-only executions dataset is still covered", by_dataset["executions"]["backend"] == "Database", by_dataset["executions"])

    # ---------------------------------------------------------------- 5. switching back
    print("\n5. switching a dataset back to the database")
    payload = settings_payload(
        [
            dataset("logs", False, "Daily", 3),
            dataset("packet-logs", True, "Daily", 0),
            dataset("cross-references", True, "Monthly", 0),
            dataset("executions", False, "Monthly", 0),
        ],
        enabled=True,
        url=args.elasticsearch,
        prefix="fusetor",
    )
    status, saved = request("PUT", api + "/api/storage/settings", args.api_key, payload)
    check("logs can be moved back to the database", status == 200 and saved.get("success") is True, saved.get("message"))

    time.sleep(6)
    status, logs_after = request("GET", api + "/api/logs?page=1&pageSize=5", args.api_key)
    check("the log page still answers after the switch", status == 200 and "items" in logs_after, status)

    status, inventory = request("GET", api + "/api/storage/status", args.api_key)
    by_dataset = {item["dataset"]: item for item in inventory.get("datasets", [])}
    check("status reflects the new routing",
          by_dataset["logs"]["backend"] == "Database" and by_dataset["packet-logs"]["backend"] == "Elasticsearch",
          {name: item["backend"] for name, item in by_dataset.items()})

    # ---------------------------------------------------------------- summary
    failed = [item for item in CHECKS if not item[1]]
    print("\n%d/%d checks passed" % (len(CHECKS) - len(failed), len(CHECKS)))
    for label, _, detail in failed:
        print("  FAILED: %s (%s)" % (label, detail))

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
