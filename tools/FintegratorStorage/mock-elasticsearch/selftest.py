#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Self-test for ``mock_elasticsearch.py``.

Starts the mock on free ports and exercises every endpoint it implements over real
HTTP (no in-process shortcuts), asserts the responses, asserts the request log the
mock writes, then re-starts the server with ``--require-auth`` and with ``--state``
and checks authentication and persistence.  Exits non-zero when a check fails.

Run::

    python tools/FintegratorStorage/mock-elasticsearch/selftest.py

Output ends with ``N/M checks passed``.
"""

import base64
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime

HERE = os.path.dirname(os.path.abspath(__file__))
MOCK = os.path.join(HERE, "mock_elasticsearch.py")
START_TIMEOUT = 25.0

CHECKS = []
SERVER_OUTPUT = {}


# ---------------------------------------------------------------------------
# check bookkeeping
# ---------------------------------------------------------------------------


def check(name, ok, detail=""):
    CHECKS.append((name, bool(ok), detail))
    if ok:
        sys.stdout.write("  ok    %s\n" % name)
    else:
        sys.stdout.write("  FAIL  %s  %s\n" % (name, detail))
    sys.stdout.flush()
    return bool(ok)


def summary(actual, limit=220):
    text = actual if isinstance(actual, str) else json.dumps(actual, ensure_ascii=False, default=str)
    return text if len(text) <= limit else text[:limit] + "..."


def eq(name, actual, expected):
    return check(name, actual == expected, "expected %r, got %r" % (expected, actual))


def truthy(name, actual, note=""):
    return check(name, bool(actual), "expected truthy, got %r %s" % (actual, note))


# ---------------------------------------------------------------------------
# HTTP client
# ---------------------------------------------------------------------------


class Response(object):
    def __init__(self, status, body, headers):
        self.status = status
        self.body = body
        self.raw = body
        self.headers = {str(k).lower(): v for k, v in (headers or {}).items()}
        try:
            self.json = json.loads(body.decode("utf-8")) if body else None
        except (ValueError, UnicodeDecodeError):
            self.json = None

    def header(self, name):
        return self.headers.get(name.lower())

    def __repr__(self):
        return "<%s %s %s>" % (self.status, self.header("content-type"), summary(self.raw, 120))


def http(port, method, path, body=None, headers=None, auth=None, timeout=30.0):
    url = "http://127.0.0.1:%d%s" % (port, path)
    data = None
    hdrs = dict(headers or {})
    if body is not None:
        if isinstance(body, (bytes, bytearray)):
            data = bytes(body)
            hdrs.setdefault("Content-Type", "application/x-ndjson")
        else:
            data = json.dumps(body).encode("utf-8")
            hdrs.setdefault("Content-Type", "application/json")
    if auth is not None:
        hdrs["Authorization"] = auth
    request = urllib.request.Request(url, data=data, method=method, headers=hdrs)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return Response(response.status, response.read(), dict(response.headers.items()))
    except urllib.error.HTTPError as exc:
        return Response(exc.code, exc.read(), dict(exc.headers.items()))


def basic(user, password):
    token = base64.b64encode(("%s:%s" % (user, password)).encode("utf-8")).decode("ascii")
    return "Basic " + token


def epoch_millis(text):
    """``2026-10-16T00:00:00Z`` -> epoch millis (used by the range checks)."""
    normalized = text[:-1] + "+00:00" if text.endswith("Z") else text
    return int(datetime.fromisoformat(normalized).timestamp() * 1000)


def bulk_body(actions):
    lines = []
    for op, meta, document in actions:
        lines.append(json.dumps({op: meta}, separators=(",", ":")))
        if document is not None:
            lines.append(json.dumps(document, separators=(",", ":")))
    return ("\n".join(lines) + "\n").encode("utf-8")


# ---------------------------------------------------------------------------
# server harness
# ---------------------------------------------------------------------------


def free_port():
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]
    finally:
        sock.close()


class MockServer(object):
    def __init__(self, port, log_path, extra_args=(), quiet=True, state=None, cwd=None):
        args = [sys.executable, "-u", MOCK, "--port", str(port), "--log", log_path]
        args.extend(extra_args)
        if quiet:
            args.append("--quiet")
        self.args = args
        self.log_path = log_path
        self.lines = []
        self.errors = []
        self._lock = threading.Lock()
        self.proc = subprocess.Popen(
            args,
            cwd=cwd or HERE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            stdin=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        self.port = port
        self._threads = [
            threading.Thread(target=self._pump, args=(self.proc.stdout, self.lines), daemon=True),
            threading.Thread(target=self._pump, args=(self.proc.stderr, self.errors), daemon=True),
        ]
        for thread in self._threads:
            thread.start()

    def _pump(self, stream, sink):
        try:
            for line in stream:
                with self._lock:
                    sink.append(line.rstrip("\r\n"))
        except (ValueError, OSError):
            pass

    def stdout(self):
        with self._lock:
            return list(self.lines)

    def stderr(self):
        with self._lock:
            return list(self.errors)

    @property
    def exited(self):
        return self.proc.poll() is not None

    def detected_port(self):
        for line in self.stdout():
            match = re.search(r"listening on http://127\.0\.0\.1:(\d+)", line)
            if match:
                return int(match.group(1))
        return None

    def wait_ready(self, timeout=START_TIMEOUT):
        """Ready = the process answers HTTP at all (200, or 401 under --require-auth)."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.exited:
                return False
            try:
                response = http(self.port, "GET", "/", timeout=3.0)
                if response.status in (200, 401, 403):
                    return True
            except Exception:  # noqa: BLE001 - not up yet
                time.sleep(0.1)
        return False

    def stop(self):
        if self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=10)
        for thread in self._threads:
            thread.join(timeout=3)


def start_server(tag, extra_args=(), quiet=True, log_path=None, use_port_zero=False, attempts=6):
    for attempt in range(attempts):
        port = 0 if use_port_zero else free_port()
        server = MockServer(port, log_path or os.path.join(TMP, "%s-requests.jsonl" % tag), extra_args, quiet=quiet)
        if use_port_zero:
            deadline = time.time() + START_TIMEOUT
            while time.time() < deadline and server.detected_port() is None and not server.exited:
                time.sleep(0.05)
            detected = server.detected_port()
            if detected is None:
                server.stop()
                continue
            server.port = detected
        if server.wait_ready():
            SERVER_OUTPUT[tag] = server
            return server
        detail = " | ".join((server.stderr() or server.stdout())[-2:])
        sys.stdout.write(
            "  ...   could not start %s on port %s (attempt %d, exit %s): %s\n"
            % (tag, server.port, attempt + 1, server.proc.poll(), summary(detail, 200))
        )
        server.stop()
    raise RuntimeError("could not start the mock server (%s)" % tag)


def read_log(path):
    entries = []
    if not os.path.exists(path):
        return entries
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                entries.append(json.loads(line))
            except ValueError:
                entries.append({"_unparseable": line})
    return entries


def find_log(entries, method=None, path=None, summary_prefix=None, status=None):
    for entry in entries:
        if method and entry.get("method") != method:
            continue
        if path and entry.get("path") != path:
            continue
        if status is not None and entry.get("status") != status:
            continue
        if summary_prefix and not str(entry.get("summary", "")).startswith(summary_prefix):
            continue
        return entry
    return None


def get(obj, path, default=None):
    current = obj
    for part in path.split("."):
        if isinstance(current, dict) and part in current:
            current = current[part]
        elif isinstance(current, list) and part.lstrip("-").isdigit():
            index = int(part)
            if -len(current) <= index < len(current):
                current = current[index]
            else:
                return default
        else:
            return default
    return current


# ---------------------------------------------------------------------------
# phases
# ---------------------------------------------------------------------------


def phase_root_and_health(server, port):
    response = http(port, "GET", "/")
    eq("GET / returns 200", response.status, 200)
    eq("GET / .name is es-mock", get(response.json, "name"), "es-mock")
    eq("GET / .cluster_name", get(response.json, "cluster_name"), "es-mock-cluster")
    eq("GET / .version.number", get(response.json, "version.number"), "8.13.0")
    eq("GET / .version.build_flavor", get(response.json, "version.build_flavor"), "default")
    eq("GET / .version.lucene_version", get(response.json, "version.lucene_version"), "9.10.0")
    eq("GET / .tagline", get(response.json, "tagline"), "You Know, for Search")
    eq("X-Elastic-Product header on GET /", response.header("x-elastic-product"), "Elasticsearch")

    head = http(port, "HEAD", "/")
    eq("HEAD / returns 200", head.status, 200)
    eq("X-Elastic-Product header on HEAD /", head.header("x-elastic-product"), "Elasticsearch")
    eq("HEAD / has no body", head.raw, b"")

    health = http(port, "GET", "/_cluster/health")
    eq("GET /_cluster/health status", get(health.json, "status"), "green")
    eq("GET /_cluster/health number_of_nodes", get(health.json, "number_of_nodes"), 1)
    eq("GET /_cluster/health number_of_data_nodes", get(health.json, "number_of_data_nodes"), 1)
    eq("GET /_cluster/health relocating_shards", get(health.json, "relocating_shards"), 0)
    eq("GET /_cluster/health initializing_shards", get(health.json, "initializing_shards"), 0)
    eq("GET /_cluster/health unassigned_shards", get(health.json, "unassigned_shards"), 0)
    eq("GET /_cluster/health cluster_name", get(health.json, "cluster_name"), "es-mock-cluster")
    eq("GET /_cluster/health X-Elastic-Product header", health.header("x-elastic-product"), "Elasticsearch")
    eq("HEAD /_cluster/health returns 200", http(port, "HEAD", "/_cluster/health").status, 200)
    return health.json


def phase_indices_and_aliases(port):
    created = http(port, "PUT", "/ft-index-a", {"settings": {"number_of_shards": 1}, "mappings": {"properties": {"message": {"type": "text"}}}, "aliases": {"ft-alias-a": {"is_write_index": True}}})
    eq("PUT /ft-index-a acknowledged", get(created.json, "acknowledged"), True)
    eq("PUT /ft-index-a shards_acknowledged", get(created.json, "shards_acknowledged"), True)
    eq("PUT /ft-index-a index echo", get(created.json, "index"), "ft-index-a")
    eq("PUT /ft-index-a status", created.status, 200)

    conflict = http(port, "PUT", "/ft-index-a", {})
    eq("PUT existing index -> 400", conflict.status, 400)
    eq("PUT existing index error type", get(conflict.json, "error.type"), "resource_already_exists_exception")
    eq("PUT existing index body status field", get(conflict.json, "status"), 400)
    truthy("PUT existing index error reason mentions index", "already exists" in str(get(conflict.json, "error.reason")))

    eq("HEAD /ft-index-a -> 200", http(port, "HEAD", "/ft-index-a").status, 200)
    eq("HEAD /ft-index-a header", http(port, "HEAD", "/ft-index-a").header("x-elastic-product"), "Elasticsearch")
    missing_head = http(port, "HEAD", "/ft-index-missing")
    eq("HEAD missing index -> 404", missing_head.status, 404)
    eq("HEAD missing index has no body", missing_head.raw, b"")

    http(port, "PUT", "/ft-index-b", {"aliases": {"ft-alias-a": {"is_write_index": False}}})
    alias = http(port, "GET", "/_alias/ft-alias-a")
    eq("GET /_alias/ft-alias-a status", alias.status, 200)
    truthy("GET /_alias/ft-alias-a lists ft-index-a", "ft-index-a" in (alias.json or {}))
    truthy("GET /_alias/ft-alias-a lists ft-index-b", "ft-index-b" in (alias.json or {}))
    eq(
        "alias write index starts on ft-index-a",
        get(alias.json, "ft-index-a.aliases.ft-alias-a.is_write_index"),
        True,
    )
    eq(
        "alias non-write index reported as false",
        get(alias.json, "ft-index-b.aliases.ft-alias-a.is_write_index"),
        False,
    )

    moved = http(port, "POST", "/_aliases", {"actions": [{"add": {"index": "ft-index-b", "alias": "ft-alias-a", "is_write_index": True}}]})
    eq("POST /_aliases add acknowledged", get(moved.json, "acknowledged"), True)
    alias2 = http(port, "GET", "/_alias/ft-alias-a")
    eq("alias write index moved to ft-index-b", get(alias2.json, "ft-index-b.aliases.ft-alias-a.is_write_index"), True)
    eq("previous write index demoted to false", get(alias2.json, "ft-index-a.aliases.ft-alias-a.is_write_index"), False)

    added = http(port, "POST", "/_aliases", {"actions": [{"add": {"index": "ft-index-a", "alias": "ft-alias-read"}}]})
    eq("POST /_aliases add read alias acknowledged", get(added.json, "acknowledged"), True)
    truthy("new alias visible", "ft-index-a" in (http(port, "GET", "/_alias/ft-alias-read").json or {}))

    removed = http(port, "POST", "/_aliases", {"actions": [{"remove": {"index": "ft-index-a", "alias": "ft-alias-read"}}]})
    eq("POST /_aliases remove acknowledged", get(removed.json, "acknowledged"), True)
    eq("removed alias -> 404", http(port, "GET", "/_alias/ft-alias-read").status, 404)
    eq(
        "404 for a missing alias is alias_not_found_exception",
        get(http(port, "GET", "/_alias/ft-alias-read").json, "error.type"),
        "alias_not_found_exception",
    )


def phase_ilm_and_templates(port, policy_body, template_body):
    put = http(port, "PUT", "/_ilm/policy/ft-policy", policy_body)
    eq("PUT /_ilm/policy/ft-policy acknowledged", get(put.json, "acknowledged"), True)
    got = http(port, "GET", "/_ilm/policy/ft-policy")
    eq("GET /_ilm/policy/ft-policy status", got.status, 200)
    eq("GET /_ilm/policy/ft-policy is verbatim", got.json, {"ft-policy": policy_body})
    eq("HEAD /_ilm/policy/ft-policy -> 200", http(port, "HEAD", "/_ilm/policy/ft-policy").status, 200)
    gone = http(port, "GET", "/_ilm/policy/ft-missing-policy")
    eq("GET missing policy -> 404", gone.status, 404)
    eq("missing policy error type", get(gone.json, "error.type"), "resource_not_found_exception")
    eq("HEAD missing policy -> 404", http(port, "HEAD", "/_ilm/policy/ft-missing-policy").status, 404)

    tput = http(port, "PUT", "/_index_template/ft-logs-template", template_body)
    eq("PUT /_index_template acknowledged", get(tput.json, "acknowledged"), True)
    tget = http(port, "GET", "/_index_template/ft-logs-template")
    eq("GET /_index_template status", tget.status, 200)
    eq("GET /_index_template index_templates[0].name", get(tget.json, "index_templates.0.name"), "ft-logs-template")
    eq("GET /_index_template body is verbatim", get(tget.json, "index_templates.0.index_template"), template_body)
    eq("HEAD /_index_template -> 200", http(port, "HEAD", "/_index_template/ft-logs-template").status, 200)
    tmissing = http(port, "GET", "/_index_template/nope")
    eq("GET missing index template -> 404", tmissing.status, 404)
    eq("missing template error type", get(tmissing.json, "error.type"), "resource_not_found_exception")
    eq("HEAD missing index template -> 404", http(port, "HEAD", "/_index_template/nope").status, 404)


def phase_bulk(port):
    body = bulk_body(
        [
            ("index", {"_index": "ft-index-a", "_id": "d1"}, {"level": "error", "message": "boom happened", "code": 500, "duration": 12.5, "tags": ["a", "b"], "@timestamp": "2026-10-16T10:00:00Z"}),
            ("index", {"_index": "ft-index-a", "_id": "d2"}, {"level": "info", "message": "all good", "code": 200, "duration": 3, "tags": ["a"], "@timestamp": "2026-10-16T11:00:00Z"}),
            ("create", {"_index": "ft-index-a", "_id": "d3"}, {"level": "error", "message": "not found", "code": 404, "duration": 7, "@timestamp": "2026-10-17T09:00:00Z"}),
            ("create", {"_index": "ft-index-a", "_id": "d3"}, {"level": "error", "message": "duplicate", "code": 404, "@timestamp": "2026-10-17T09:00:00Z"}),
            ("index", {"_index": "ft-index-b", "_id": "b1"}, {"level": "info", "message": "other index", "code": 200, "@timestamp": "2026-10-16T12:00:00Z"}),
        ]
    )
    response = http(port, "POST", "/_bulk", body)
    eq("POST /_bulk HTTP status is 200 even with errors", response.status, 200)
    eq("POST /_bulk errors=true", get(response.json, "errors"), True)
    items = get(response.json, "items") or []
    eq("POST /_bulk item count", len(items), 5)
    eq("bulk index d1 status", get(items, "0.index.status"), 201)
    eq("bulk index d1 result", get(items, "0.index.result"), "created")
    eq("bulk index d2 status", get(items, "1.index.status"), 201)
    eq("bulk create d3 status", get(items, "2.create.status"), 201)
    eq("bulk create d3 conflict status is 409", get(items, "3.create.status"), 409)
    eq(
        "bulk conflict error type",
        get(items, "3.create.error.type"),
        "version_conflict_engine_exception",
    )
    truthy("bulk conflict reason names the document", "document already exists" in str(get(items, "3.create.error.reason")))
    eq("bulk item echoes _index", get(items, "3.create._index"), "ft-index-a")
    eq("bulk item echoes _id", get(items, "3.create._id"), "d3")
    eq("bulk into second index via meta _index", get(items, "4.index._index"), "ft-index-b")
    truthy("bulk response has took", isinstance(get(response.json, "took"), int), str(get(response.json, "took")))

    path_bulk = http(port, "POST", "/ft-index-a/_bulk", bulk_body([("index", {}, {"level": "warn", "message": "auto id doc", "@timestamp": "2026-10-18T09:00:00Z"})]))
    eq("POST /<index>/_bulk status", path_bulk.status, 200)
    eq("POST /<index>/_bulk errors", get(path_bulk.json, "errors"), False)
    generated = get(path_bulk.json, "items.0.index._id")
    truthy("POST /<index>/_bulk generated an _id", bool(generated), repr(generated))
    eq("POST /<index>/_bulk targets the path index", get(path_bulk.json, "items.0.index._index"), "ft-index-a")

    delete_bulk = http(port, "POST", "/_bulk", bulk_body([("delete", {"_index": "ft-index-b", "_id": "b1"}, None)]))
    eq("bulk delete existing doc status", get(delete_bulk.json, "items.0.delete.status"), 200)
    eq("bulk delete result", get(delete_bulk.json, "items.0.delete.result"), "deleted")

    auto = http(port, "POST", "/_bulk", bulk_body([("index", {"_index": "ft-logs-2026.10.18", "_id": "l1"}, {"level": "error", "message": "auto created index"})]))
    eq("bulk auto-create errors=false", get(auto.json, "errors"), False)
    eq("auto-created index exists", http(port, "HEAD", "/ft-logs-2026.10.18").status, 200)


def phase_cat_indices(port):
    for name in ("ft-logs-2026.10.16", "ft-logs-2026.10.17"):
        http(port, "PUT", "/%s" % name, {})
    for index, doc_id in (("ft-logs-2026.10.16", "x1"), ("ft-logs-2026.10.16", "x2"), ("ft-logs-2026.10.17", "y1")):
        http(port, "POST", "/%s/_doc/%s" % (index, doc_id), {"level": "error", "message": "log line %s" % doc_id})

    pattern = http(port, "GET", "/_cat/indices/ft-logs-*?format=json&bytes=b&h=index,health,status,docs.count,store.size,creation.date")
    eq("GET /_cat/indices/<pattern> status", pattern.status, 200)
    truthy("_cat/indices returns a JSON array", isinstance(pattern.json, list))
    names = [row.get("index") for row in (pattern.json or [])]
    eq("_cat/indices pattern matches only ft-logs-*", names, ["ft-logs-2026.10.16", "ft-logs-2026.10.17", "ft-logs-2026.10.18"])
    truthy("auto-created index picked up the template alias", "ft-logs-2026.10.18" in (http(port, "GET", "/_alias/ft-logs-read").json or {}))
    row = (pattern.json or [{}])[0]
    eq("_cat/indices column health", row.get("health"), "green")
    eq("_cat/indices column status", row.get("status"), "open")
    eq("_cat/indices docs.count for the .16 index", row.get("docs.count"), 2)
    truthy("_cat/indices store.size is numeric", isinstance(row.get("store.size"), (int, float)), repr(row.get("store.size")))
    truthy("_cat/indices creation.date is epoch millis", isinstance(row.get("creation.date"), int) and row.get("creation.date") > 1_600_000_000_000, repr(row.get("creation.date")))
    eq("_cat/indices honors the h= column list", sorted(row.keys()), sorted(["index", "health", "status", "docs.count", "store.size", "creation.date"]))

    everything = http(port, "GET", "/_cat/indices?format=json&h=index,docs.count")
    all_names = [r.get("index") for r in (everything.json or [])]
    for expected in ("ft-index-a", "ft-index-b", "ft-logs-2026.10.16", "ft-logs-2026.10.17", "ft-logs-2026.10.18"):
        truthy("_cat/indices lists %s" % expected, expected in all_names, str(all_names))
    single = http(port, "GET", "/_cat/indices/ft-index-a?format=json&h=index,docs.count")
    eq("_cat/indices with an exact name", [r.get("index") for r in (single.json or [])], ["ft-index-a"])
    eq("_cat/indices without h returns default columns", http(port, "GET", "/_cat/indices/ft-index-a?format=json").status, 200)


def phase_search(port):
    response = http(port, "POST", "/ft-index-a/_search", {"query": {"match_all": {}}, "size": 2})
    eq("search match_all status", response.status, 200)
    eq("search response timed_out", get(response.json, "timed_out"), False)
    eq("search total relation", get(response.json, "hits.total.relation"), "eq")
    eq("search total value counts all matches", get(response.json, "hits.total.value"), 4)
    eq("search size limits hits", len(get(response.json, "hits.hits") or []), 2)
    hit = get(response.json, "hits.hits.0")
    eq("search hit _index", get(hit, "_index"), "ft-index-a")
    eq("search hit _score", get(hit, "_score"), None)
    truthy("search hit carries _source", isinstance(get(hit, "_source"), dict))
    eq("search hits are in insertion order", get(hit, "_id"), "d1")
    truthy("search response has took", isinstance(get(response.json, "took"), int))

    boolq = http(
        port,
        "POST",
        "/ft-index-a/_search",
        {
            "query": {
                "bool": {
                    "must": [{"term": {"level": "error"}}],
                    "filter": [{"range": {"@timestamp": {"gte": "2026-10-16T00:00:00Z"}}}],
                    "should": [{"term": {"code": 500}}, {"term": {"code": 404}}],
                    "must_not": [{"term": {"code": 500}}],
                }
            }
        },
    )
    eq("bool must/filter/must_not reduces hits to d3", [h.get("_id") for h in get(boolq.json, "hits.hits")], ["d3"])

    must_not = http(port, "POST", "/ft-index-a/_search", {"query": {"bool": {"must_not": [{"term": {"level": "warn"}}]}}, "size": 100})
    eq("bool must_not excludes the warn doc", get(must_not.json, "hits.total.value"), 3)

    should_only = http(port, "POST", "/ft-index-a/_search", {"query": {"bool": {"should": [{"term": {"level": "warn"}}]}}})
    eq("bool should alone requires one match", get(should_only.json, "hits.total.value"), 1)

    date_range = http(port, "POST", "/ft-index-a/_search", {"query": {"range": {"@timestamp": {"gte": "2026-10-17", "lt": "2026-10-18"}}}})
    eq("range on ISO date strings", [h.get("_id") for h in get(date_range.json, "hits.hits")], ["d3"])

    start_millis = epoch_millis("2026-10-16T00:00:00Z")
    end_millis = epoch_millis("2026-10-17T00:00:00Z")
    epoch_range = http(port, "POST", "/ft-index-a/_search", {"query": {"range": {"@timestamp": {"gte": start_millis, "lt": end_millis}}}})
    eq("range accepts epoch millis", [h.get("_id") for h in get(epoch_range.json, "hits.hits")], ["d1", "d2"])

    number_range = http(port, "POST", "/ft-index-a/_search", {"query": {"range": {"duration": {"gt": 10}}}})
    eq("range on numbers", [h.get("_id") for h in get(number_range.json, "hits.hits")], ["d1"])

    terms_value = http(port, "POST", "/ft-index-a/_search", {"query": {"terms": {"level": {"value": ["info", "warn"]}}}})
    eq("terms with {value: [...]}", get(terms_value.json, "hits.total.value"), 2)
    terms_plain = http(port, "POST", "/ft-index-a/_search", {"query": {"terms": {"level": ["error"]}}})
    eq("terms with a plain array", get(terms_plain.json, "hits.total.value"), 2)

    exists = http(port, "POST", "/ft-index-a/_search", {"query": {"exists": {"field": "code"}}})
    eq("exists query", get(exists.json, "hits.total.value"), 3)

    ids = http(port, "POST", "/ft-index-a/_search", {"query": {"ids": {"values": ["d1", "d3"]}}})
    eq("ids query", [h.get("_id") for h in get(ids.json, "hits.hits")], ["d1", "d3"])

    qs_wild = http(port, "POST", "/ft-index-a/_search", {"query": {"query_string": {"query": "boom*"}}})
    eq("query_string with a wildcard", [h.get("_id") for h in get(qs_wild.json, "hits.hits")], ["d1"])
    qs_field = http(port, "POST", "/ft-index-a/_search", {"query": {"query_string": {"query": "level:err*"}}})
    eq("query_string with field:pattern", get(qs_field.json, "hits.total.value"), 2)
    qs_fields = http(port, "POST", "/ft-index-a/_search", {"query": {"query_string": {"query": "not found", "fields": ["message"], "default_operator": "and"}}})
    eq("query_string with fields + default_operator AND", [h.get("_id") for h in get(qs_fields.json, "hits.hits")], ["d3"])
    qs_or = http(port, "POST", "/ft-index-a/_search", {"query": {"query_string": {"query": "level:info OR level:error"}}})
    eq("query_string OR is an explicit operator", sorted(h.get("_id") for h in get(qs_or.json, "hits.hits")), ["d1", "d2", "d3"])
    qs_case = http(port, "POST", "/ft-index-a/_search", {"query": {"query_string": {"query": "BOOM"}}})
    eq("query_string plain term ignores case", [h.get("_id") for h in get(qs_case.json, "hits.hits")], ["d1"])
    qs_param = http(port, "GET", "/ft-index-a/_search?q=level:error&size=10")
    eq("GET _search with q= URI search", get(qs_param.json, "hits.total.value"), 2)

    sorted_response = http(port, "POST", "/ft-index-a/_search", {"query": {"match_all": {}}, "sort": [{"duration": {"order": "desc"}}], "size": 2})
    eq("sort desc by number", [h.get("_id") for h in get(sorted_response.json, "hits.hits")], ["d1", "d3"])
    asc = http(port, "POST", "/ft-index-a/_search", {"query": {"match_all": {}}, "sort": [{"duration": "asc"}], "size": 100})
    eq("sort asc starts with the smallest duration", get(asc.json, "hits.hits.0._id"), "d2")
    eq("sort asc keeps missing values last", get(asc.json, "hits.hits.3._source.level"), "warn")
    sorted_ts = http(port, "POST", "/ft-index-a/_search", {"query": {"match_all": {}}, "sort": [{"@timestamp": {"order": "desc"}}], "size": 1})
    eq("sort desc on ISO timestamps", get(sorted_ts.json, "hits.hits.0._source.level"), "warn")
    eq("sort attaches the sort values to hits", get(sorted_response.json, "hits.hits.0.sort.0"), 12.5)

    paged = http(port, "POST", "/ft-index-a/_search", {"query": {"match_all": {}}, "size": 1, "from": 1})
    eq("from/size paging", [h.get("_id") for h in get(paged.json, "hits.hits")], ["d2"])
    paged_param = http(port, "GET", "/ft-index-a/_search?from=2&size=1")
    eq("from/size from the query string", len(get(paged_param.json, "hits.hits") or []), 1)

    tth_int = http(port, "POST", "/ft-index-a/_search", {"query": {"match_all": {}}, "track_total_hits": 2, "size": 1})
    eq("track_total_hits as an int degrades relation", get(tth_int.json, "hits.total"), {"value": 2, "relation": "gte"})
    tth_true = http(port, "POST", "/ft-index-a/_search", {"query": {"match_all": {}}, "track_total_hits": True, "size": 1})
    eq("track_total_hits=true keeps relation eq", get(tth_true.json, "hits.total.relation"), "eq")
    as_int = http(port, "GET", "/ft-index-a/_search?rest_total_hits_as_int=true")
    eq("rest_total_hits_as_int returns a plain number", isinstance(get(as_int.json, "hits.total"), int), True)

    aggs = http(port, "POST", "/ft-index-a/_search", {"size": 0, "aggs": {"by_level": {"terms": {"field": "level", "size": 10}}, "by_tag": {"terms": {"field": "tags"}}}})
    eq("terms agg by_level bucket count", len(get(aggs.json, "aggregations.by_level.buckets") or []), 3)
    eq("terms agg first bucket key", get(aggs.json, "aggregations.by_level.buckets.0.key"), "error")
    eq("terms agg first bucket doc_count", get(aggs.json, "aggregations.by_level.buckets.0.doc_count"), 2)
    eq("terms agg second bucket", get(aggs.json, "aggregations.by_level.buckets.1.key"), "info")
    eq("terms agg on a multi-valued field", get(aggs.json, "aggregations.by_tag.buckets.0.key"), "a")
    eq("terms agg on a multi-valued field counts", get(aggs.json, "aggregations.by_tag.buckets.0.doc_count"), 2)
    eq("terms agg sum_other_doc_count", get(aggs.json, "aggregations.by_level.sum_other_doc_count"), 0)

    pattern_search = http(port, "POST", "/ft-logs-*/_search", {"query": {"match_all": {}}, "size": 100})
    eq("search across an index pattern", get(pattern_search.json, "hits.total.value"), 4)

    missing_index = http(port, "POST", "/ft-no-such-index/_search?ignore_unavailable=true&allow_no_indices=true", {"query": {"match_all": {}}})
    eq("search on a missing index -> 200", missing_index.status, 200)
    eq("search on a missing index -> empty hits", get(missing_index.json, "hits.total.value"), 0)
    eq("search on a missing index -> no error", missing_index.json.get("error") if isinstance(missing_index.json, dict) else "x", None)

    bad = http(port, "POST", "/ft-index-a/_search", {"query": {"definitely_not_a_query": {"field": "x"}}})
    eq("unknown query type -> 400", bad.status, 400)
    eq("unknown query type error body", get(bad.json, "error.type"), "parsing_exception")

    eq("HEAD on _search is not a handler", http(port, "HEAD", "/ft-index-a/_search").status, 404)


def phase_count_delete_mget(port):
    count = http(port, "POST", "/ft-index-a/_count", {"query": {"term": {"level": "error"}}})
    eq("POST /_count with a query", get(count.json, "count"), 2)
    eq("_count shards", get(count.json, "_shards.successful"), 1)
    eq("POST /_count without a body counts everything", get(http(port, "POST", "/ft-index-a/_count", {}).json, "count"), 4)
    eq("GET /_count", get(http(port, "GET", "/ft-index-a/_count").json, "count"), 4)
    eq("_count on a missing index is empty", get(http(port, "POST", "/ft-nope/_count?allow_no_indices=true", {}).json, "count"), 0)

    deleted = http(port, "POST", "/ft-index-a/_delete_by_query?refresh=true", {"query": {"term": {"level": "warn"}}})
    eq("_delete_by_query deleted count", get(deleted.json, "deleted"), 1)
    eq("_delete_by_query total", get(deleted.json, "total"), 1)
    eq("_delete_by_query failures empty", get(deleted.json, "failures"), [])
    eq("deleted doc is gone", get(http(port, "POST", "/ft-index-a/_count", {}).json, "count"), 3)
    eq("delete_by_query matching nothing", get(http(port, "POST", "/ft-index-a/_delete_by_query", {"query": {"term": {"level": "nope"}}}).json, "deleted"), 0)

    mget = http(port, "POST", "/ft-index-a/_mget", {"ids": ["d1", "nope"]})
    eq("POST /<index>/_mget first doc found", get(mget.json, "docs.0.found"), True)
    eq("POST /<index>/_mget first doc _source", get(mget.json, "docs.0._source.level"), "error")
    eq("POST /<index>/_mget second doc not found", get(mget.json, "docs.1.found"), False)
    eq("POST /<index>/_mget second doc has no _source", get(mget.json, "docs.1._source"), None)

    mget_docs = http(port, "POST", "/_mget", {"docs": [{"_index": "ft-index-a", "_id": "d2"}, {"_index": "ft-index-a", "_id": "zzz"}]})
    eq("POST /_mget docs[] first found", get(mget_docs.json, "docs.0.found"), True)
    eq("POST /_mget docs[] second not found", get(mget_docs.json, "docs.1.found"), False)
    eq("POST /_mget echoes the index", get(mget_docs.json, "docs.0._index"), "ft-index-a")

    refresh = http(port, "POST", "/ft-index-a/_refresh")
    eq("POST /<index>/_refresh", get(refresh.json, "_shards.successful"), 1)


def phase_delete_and_404s(port):
    delete = http(port, "DELETE", "/ft-logs-2026.10.17")
    eq("DELETE /<index> acknowledged", get(delete.json, "acknowledged"), True)
    eq("deleted index HEAD -> 404", http(port, "HEAD", "/ft-logs-2026.10.17").status, 404)
    again = http(port, "DELETE", "/ft-logs-2026.10.17")
    eq("DELETE missing index -> 404", again.status, 404)
    eq("DELETE missing index error type", get(again.json, "error.type"), "index_not_found_exception")
    eq("DELETE missing index body status", get(again.json, "status"), 404)

    alias_delete = http(port, "DELETE", "/ft-index-b")
    eq("DELETE ft-index-b acknowledged", get(alias_delete.json, "acknowledged"), True)
    alias = http(port, "GET", "/_alias/ft-alias-a")
    eq("aliases of a deleted index are gone", "ft-index-b" in (alias.json or {}), False)
    eq("GET /ft-index-b after delete -> 404", http(port, "GET", "/ft-index-b").status, 404)
    eq("GET unknown single-segment path -> index_not_found_exception", get(http(port, "GET", "/ft-index-b").json, "error.type"), "index_not_found_exception")

    unknown = http(port, "GET", "/definitely/not/an/endpoint")
    eq("unknown path -> 404", unknown.status, 404)
    eq("unknown path body status", get(unknown.json, "status"), 404)
    truthy("unknown path error.type present", bool(get(unknown.json, "error.type")), summary(unknown.json))
    truthy("unknown path error.reason present", bool(get(unknown.json, "error.reason")), summary(unknown.json))
    eq("unknown path keeps the product header", unknown.header("x-elastic-product"), "Elasticsearch")
    eq("unknown POST path -> 404", http(port, "POST", "/_nope", {}).status, 404)
    eq("unknown DELETE path -> 404", http(port, "DELETE", "/_nope").status, 404)


def phase_log(log_path, server):
    entries = read_log(log_path)
    truthy("request log exists and has entries", len(entries) > 30, "%d entries" % len(entries))
    required = ("at", "method", "path", "query", "status", "auth", "body_bytes", "summary")
    bad = [e for e in entries if any(key not in e for key in required)]
    check("every log line has the required keys", not bad, "first bad: %s" % summary(bad[:1]))
    auths = set(e.get("auth") for e in entries)
    truthy("log records auth kinds such as none", "none" in auths, str(auths))
    truthy("every entry has a string summary", all(isinstance(e.get("summary"), str) for e in entries))

    created = find_log(entries, "PUT", "/ft-index-a", summary_prefix="create-index")
    truthy("log has the create-index entry", created is not None)
    eq("create-index summary", get(created, "summary"), "create-index ft-index-a")
    eq("create-index status", get(created, "status"), 200)
    eq("create-index keeps the full body (string)", get(created, "body").strip().startswith("{") if get(created, "body") else False, True)
    eq("create-index body_json is the request JSON", get(created, "body_json.mappings.properties.message.type"), "text")

    head_entry = find_log(entries, "HEAD", "/ft-index-a", status=200)
    truthy("log records HEAD /<index>", head_entry is not None)
    eq("HEAD entry body_bytes is zero", get(head_entry, "body_bytes"), 0)

    bulk_entry = find_log(entries, "POST", "/_bulk", summary_prefix="bulk ")
    truthy("log has a bulk entry", bulk_entry is not None)
    truthy("bulk summary reports the action count", bool(re.search(r"^bulk 5 action\(s\)", str(get(bulk_entry, "summary")))), str(get(bulk_entry, "summary")))
    truthy("bulk log line keeps big bodies out", "body" not in (bulk_entry or {}), summary(bulk_entry))
    truthy("bulk log line records body_bytes", get(bulk_entry, "body_bytes", 0) > 100, str(get(bulk_entry, "body_bytes")))

    search_entry = find_log(entries, "POST", "/ft-index-a/_search", summary_prefix="search")
    truthy("log has a search entry", search_entry is not None)
    truthy("search summary includes the hit count", bool(re.search(r"\[\d+ hit\(s\)\]", str(get(search_entry, "summary")))), str(get(search_entry, "summary")))
    truthy("search log line has no body", "body" not in (search_entry or {}))
    truthy("log records the query string", any(e.get("query") for e in entries if e.get("path") == "/_cat/indices/ft-logs-*"))

    alias_entry = find_log(entries, "POST", "/_aliases")
    truthy("log has the /_aliases entry", alias_entry is not None)
    eq("alias request body kept verbatim", get(alias_entry, "body_json.actions.0.add.alias"), "ft-alias-a")

    template_entry = find_log(entries, "PUT", "/_index_template/ft-logs-template")
    truthy("log has the template entry", template_entry is not None)
    eq("template body kept verbatim (index_patterns)", get(template_entry, "body_json.index_patterns.0"), "ft-logs-*")
    eq("template body kept verbatim (_meta)", get(template_entry, "body_json._meta.owner"), "fusetor")

    policy_entry = find_log(entries, "PUT", "/_ilm/policy/ft-policy")
    truthy("log has the policy entry", policy_entry is not None)
    eq("policy body kept verbatim", get(policy_entry, "body_json.policy.phases.hot.actions.rollover.max_age"), "1d")

    unauthorized = find_log(entries, "GET", "/", status=401)
    eq("no 401s while auth is not required", unauthorized, None)

    truthy("request lines are also written to stdout", any("[mock-es]" in line for line in server.stdout()), "\n".join(server.stdout()[:3]))
    truthy("startup banner mentions the log path", any(log_path in line for line in server.stdout()), "\n".join(server.stdout()[:6]))
    truthy("startup banner mentions the port", any((":%d" % server.port) in line for line in server.stdout()), "\n".join(server.stdout()[:6]))


def phase_auth(base_dir):
    log_path = os.path.join(base_dir, "auth-requests.jsonl")
    server = start_server("auth", ["--require-auth", "elastic:changeme"], log_path=log_path)
    port = server.port
    try:
        without = http(port, "GET", "/")
        eq("require-auth: GET / without credentials -> 401", without.status, 401)
        eq("401 body error type", get(without.json, "error.type"), "security_exception")
        truthy("401 body has a reason", "credentials" in str(get(without.json, "error.reason")), summary(without.json))
        eq("401 keeps the product header", without.header("x-elastic-product"), "Elasticsearch")
        truthy("401 sends WWW-Authenticate", "Basic" in str(without.header("www-authenticate")))

        wrong = http(port, "GET", "/", auth=basic("elastic", "nope"))
        eq("require-auth: wrong Basic password -> 401", wrong.status, 401)
        wrong_user = http(port, "GET", "/", auth=basic("nobody", "changeme"))
        eq("require-auth: wrong Basic user -> 401", wrong_user.status, 401)
        apikey = http(port, "GET", "/", auth="ApiKey abc123")
        eq("require-auth: ApiKey alone -> 401", apikey.status, 401)

        good = http(port, "GET", "/", auth=basic("elastic", "changeme"))
        eq("require-auth: correct Basic credentials -> 200", good.status, 200)
        eq("require-auth: correct Basic credentials body", get(good.json, "name"), "es-mock")
        eq("require-auth: product header on success", good.header("x-elastic-product"), "Elasticsearch")
        head_good = http(port, "HEAD", "/", auth=basic("elastic", "changeme"))
        eq("require-auth: HEAD / with credentials -> 200", head_good.status, 200)
        eq("require-auth: HEAD / without credentials -> 401", http(port, "HEAD", "/").status, 401)

        entries = read_log(log_path)
        truthy("auth log written", len(entries) >= 7, "%d entries" % len(entries))
        ok_entry = find_log(entries, "GET", "/", status=200)
        eq("auth log records basic", get(ok_entry, "auth"), "basic")
        bad_entry = find_log(entries, "GET", "/", status=401)
        eq("auth log records the 401", get(bad_entry, "status"), 401)
        eq("auth log summary for a rejection", get(bad_entry, "summary"), "unauthorized")
    finally:
        server.stop()


def phase_auth_recorded_without_requirement(base_dir):
    """Auth scheme is recorded even when --require-auth is off."""
    log_path = os.path.join(base_dir, "scheme-requests.jsonl")
    server = start_server("scheme", [], log_path=log_path)
    port = server.port
    try:
        eq("Basic accepted when auth is not required", http(port, "GET", "/", auth=basic("u", "p")).status, 200)
        eq("ApiKey accepted when auth is not required", http(port, "GET", "/", auth="ApiKey abc:def").status, 200)
        eq("No auth header accepted when auth is not required", http(port, "GET", "/").status, 200)
        entries = read_log(log_path)
        schemes = [e.get("auth") for e in entries]
        eq("log records basic", "basic" in schemes, True)
        eq("log records apikey", "apikey" in schemes, True)
        eq("log records none", "none" in schemes, True)
    finally:
        server.stop()


def phase_fail_fast():
    holder = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    holder.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    holder.bind(("127.0.0.1", 0))
    holder.listen(5)
    port = holder.getsockname()[1]
    try:
        proc = subprocess.run(
            [sys.executable, MOCK, "--port", str(port), "--log", os.path.join(TMP, "bind-requests.jsonl"), "--quiet"],
            cwd=HERE,
            capture_output=True,
            text=True,
            timeout=30,
        )
        truthy("port in use -> non-zero exit", proc.returncode != 0, "exit code %s" % proc.returncode)
        output = (proc.stdout or "") + (proc.stderr or "")
        truthy("port in use -> clear message", "already in use" in output, summary(output))
        truthy("port in use -> message names the port", str(port) in output, summary(output))
        truthy("port in use -> nothing was logged", not os.path.exists(os.path.join(TMP, "bind-requests.jsonl")))
    finally:
        holder.close()


def phase_state(base_dir):
    state_path = os.path.join(base_dir, "state.json")
    log_one = os.path.join(base_dir, "state-1-requests.jsonl")
    log_two = os.path.join(base_dir, "state-2-requests.jsonl")

    first = start_server("state-1", ["--state", state_path], log_path=log_one, use_port_zero=True)
    try:
        truthy("--port 0 binds a real port from the banner", first.detected_port() == first.port, str(first.detected_port()))
        eq("PUT /ft-state-index", http(first.port, "PUT", "/ft-state-index", {"aliases": {"ft-state-alias": {"is_write_index": True}}}).status, 200)
        body = bulk_body([("index", {"_index": "ft-state-index", "_id": "s1"}, {"level": "error", "message": "persisted"})])
        eq("bulk into the persisted index", get(http(first.port, "POST", "/ft-state-index/_bulk", body).json, "errors"), False)
        http(first.port, "PUT", "/_ilm/policy/ft-state-policy", {"policy": {"phases": {"hot": {"actions": {"rollover": {"max_age": "7d"}}}}}})
        truthy("state file written while running", os.path.exists(state_path))
    finally:
        first.stop()

    second = start_server("state-2", ["--state", state_path], log_path=log_two, use_port_zero=True)
    try:
        eq("state: index survives a restart", http(second.port, "HEAD", "/ft-state-index").status, 200)
        eq("state: documents survive a restart", get(http(second.port, "POST", "/ft-state-index/_search", {"query": {"match_all": {}}}).json, "hits.total.value"), 1)
        eq("state: document source survives", get(http(second.port, "POST", "/ft-state-index/_search", {}).json, "hits.hits.0._source.message"), "persisted")
        eq("state: aliases survive", get(http(second.port, "GET", "/_alias/ft-state-alias").json, "ft-state-index.aliases.ft-state-alias.is_write_index"), True)
        eq("state: ILM policies survive", get(http(second.port, "GET", "/_ilm/policy/ft-state-policy").json, "ft-state-policy.policy.phases.hot.actions.rollover.max_age"), "7d")
    finally:
        second.stop()


def phase_concurrency(base_dir):
    """The product runs an API host and an Engine worker against the same mock."""
    log_path = os.path.join(base_dir, "concurrency-requests.jsonl")
    server = start_server("concurrency", [], log_path=log_path)
    port = server.port
    vendor_accept = {"Accept": "application/vnd.elasticsearch+json; compatible-with=8"}
    vendor_ndjson = {
        "Accept": vendor_accept["Accept"],
        "Content-Type": "application/vnd.elasticsearch+x-ndjson; compatible-with=8",
    }
    try:
        eq("vendor Accept header is accepted on GET /", http(port, "GET", "/", headers=vendor_accept).status, 200)
        eq("PUT /concurrent-index", http(port, "PUT", "/concurrent-index", {}, headers=vendor_accept).status, 200)
        vendor_bulk = http(
            port,
            "POST",
            "/concurrent-index/_bulk",
            bulk_body([("index", {"_index": "concurrent-index", "_id": "warmup"}, {"worker": -1})]),
            headers=vendor_ndjson,
        )
        eq("vendor x-ndjson content type is accepted", vendor_bulk.status, 200)
        eq("bulk response Content-Type", vendor_bulk.header("content-type"), "application/json")
        eq("bulk response product header", vendor_bulk.header("x-elastic-product"), "Elasticsearch")

        problems = []
        lock = threading.Lock()

        def worker(worker_id):
            for iteration in range(10):
                search = http(
                    port,
                    "POST",
                    "/concurrent-index/_search",
                    {"query": {"term": {"worker": worker_id}}},
                    headers=vendor_accept,
                )
                if search.status != 200 or get(search.json, "timed_out") is not False:
                    with lock:
                        problems.append("search %s %s" % (search.status, summary(search.raw, 80)))
                bulk = http(
                    port,
                    "POST",
                    "/concurrent-index/_bulk",
                    bulk_body(
                        [
                            (
                                "create",
                                {"_index": "concurrent-index", "_id": "%d-%d" % (worker_id, iteration)},
                                {"worker": worker_id, "iteration": iteration},
                            )
                        ]
                    ),
                    headers=vendor_ndjson,
                )
                if bulk.status != 200 or get(bulk.json, "errors") is not False:
                    with lock:
                        problems.append("bulk %s %s" % (bulk.status, summary(bulk.raw, 80)))

        threads = [threading.Thread(target=worker, args=(worker_id,)) for worker_id in range(8)]
        started = time.time()
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(timeout=120)
        elapsed = time.time() - started
        check("8 concurrent clients: no failed request", not problems, summary(problems[:3]))
        truthy("concurrency finished quickly", elapsed < 60, "%.1fs" % elapsed)

        entries = read_log(log_path)
        searches = [e for e in entries if e.get("path") == "/concurrent-index/_search"]
        bulks = [e for e in entries if e.get("path") == "/concurrent-index/_bulk" and e.get("method") == "POST"]
        eq("every concurrent search was logged", len(searches), 80)
        eq("every concurrent search returned 200", all(e.get("status") == 200 for e in searches), True)
        eq("every concurrent bulk was logged", len(bulks), 81)
        eq("every concurrent bulk returned 200", all(e.get("status") == 200 for e in bulks), True)
        eq("all 80 concurrent documents stored", get(http(port, "POST", "/concurrent-index/_count", {}).json, "count"), 81)
        eq("log stays well formed under concurrency", all("at" in e and "summary" in e for e in entries), True)
    finally:
        server.stop()


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------


def main():
    global TMP
    started = time.time()
    TMP = tempfile.mkdtemp(prefix="mock-es-selftest-")
    log_path = os.path.join(TMP, "requests.jsonl")
    policy_body = {
        "policy": {
            "phases": {
                "hot": {"min_age": "0ms", "actions": {"rollover": {"max_age": "1d", "max_primary_shard_size": "1gb"}, "set_priority": {"priority": 100}}},
                "delete": {"min_age": "30d", "actions": {"delete": {}}},
            }
        }
    }
    template_body = {
        "index_patterns": ["ft-logs-*"],
        "priority": 200,
        "template": {
            "settings": {"number_of_shards": 1, "refresh_interval": "5s"},
            "mappings": {"properties": {"@timestamp": {"type": "date"}, "message": {"type": "text"}, "level": {"type": "keyword"}}},
            "aliases": {"ft-logs-read": {}},
        },
        "_meta": {"owner": "fusetor", "managed_by": "mock-elasticsearch-selftest"},
    }

    print("mock-elasticsearch self-test")
    print("  mock   : %s" % MOCK)
    print("  workdir: %s" % TMP)
    print("")
    print("phase 1: endpoints, search, bulk, aliases, ILM, templates")
    server = start_server("main", [], quiet=False, log_path=log_path)
    port = server.port
    try:
        phase_root_and_health(server, port)
        phase_indices_and_aliases(port)
        phase_ilm_and_templates(port, policy_body, template_body)
        phase_bulk(port)
        phase_cat_indices(port)
        phase_search(port)
        phase_count_delete_mget(port)
        phase_delete_and_404s(port)
    finally:
        server.stop()
    print("")
    print("phase 2: request log")
    phase_log(log_path, server)
    print("")
    print("phase 3: auth (--require-auth) and auth recording")
    phase_auth(TMP)
    phase_auth_recorded_without_requirement(TMP)
    print("")
    print("phase 4: fail fast when the port is taken")
    phase_fail_fast()
    print("")
    print("phase 5: --state persistence across restarts (--port 0)")
    phase_state(TMP)
    print("")
    print("phase 6: concurrent clients (API host + Engine worker) and vendor content types")
    phase_concurrency(TMP)
    print("")

    passed = sum(1 for _, ok, _ in CHECKS if ok)
    total = len(CHECKS)
    failures = [(name, detail) for name, ok, detail in CHECKS if not ok]
    if failures:
        print("failures:")
        for name, detail in failures:
            print("  - %s  %s" % (name, detail))
        print("")
    print("server stdout (first lines):")
    for tag, mock in SERVER_OUTPUT.items():
        for line in mock.stdout()[:3]:
            print("  [%s] %s" % (tag, line))
    print("")
    print("elapsed: %.1fs" % (time.time() - started))
    print("%d/%d checks passed" % (passed, total))
    return 0 if passed == total else 1


TMP = tempfile.mkdtemp(prefix="mock-es-selftest-")


if __name__ == "__main__":
    try:
        code = main()
    finally:
        shutil.rmtree(TMP, ignore_errors=True)
    sys.exit(code)
