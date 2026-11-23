#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""mock_elasticsearch.py -- a standard-library-only mock of an Elasticsearch 8.x cluster.

Purpose
-------
fusetor.com (C#/.NET) is getting an optional Elasticsearch storage backend.  No
Elasticsearch cluster is reachable from the machines that run the end-to-end checks
(no Docker), so this process stands in for one: it speaks the subset of the REST API
the product uses, answers like Elasticsearch 8.13 would, and writes a JSONL request
log so the harness can assert exactly what the product asked for.

Everything lives in memory (optionally mirrored to a ``--state`` JSON file) and all
state changes are guarded by one re-entrant lock, so the API host and the Engine
worker can hit it concurrently.

Run::

    python tools/FintegratorStorage/mock-elasticsearch/mock_elasticsearch.py --port 9210

See ../README.md for the endpoint matrix, the request-log format and the list of
things this mock deliberately does not do.

Python 3.7+ standard library only.
"""

from __future__ import annotations

import argparse
import base64
import errno
import fnmatch
import functools
import hashlib
import json
import os
import re
import signal
import socket
import sys
import threading
import time
import traceback
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlparse

# ---------------------------------------------------------------------------
# identity / defaults
# ---------------------------------------------------------------------------

ES_VERSION = "8.13.0"
LUCENE_VERSION = "9.10.0"
BUILD_FLAVOR = "default"
CLUSTER_NAME = "es-mock-cluster"
NODE_NAME = "es-mock"
DEFAULT_PORT = 9210
DEFAULT_HOST = "127.0.0.1"
DEFAULT_LOG_FILE = "mock-elasticsearch-requests.jsonl"
PRODUCT_HEADER = "X-Elastic-Product"
PRODUCT_VALUE = "Elasticsearch"
DEFAULT_SEARCH_SIZE = 10
MAX_LOG_BODY_CHARS = 32768
SUPPORTED_AUTH_SCHEMES = ("basic", "apikey")


# ---------------------------------------------------------------------------
# tiny helpers
# ---------------------------------------------------------------------------


def now_ts():
    """UTC now, e.g. ``2026-10-16T09:10:00.123Z`` (used in the request log)."""
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def now_millis():
    return int(time.time() * 1000.0)


def jdump(obj, pretty=False):
    if pretty:
        return json.dumps(obj, ensure_ascii=False, indent=2, sort_keys=False)
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":"))


def clone(obj):
    """Deep copy through JSON -- every piece of state here is JSON safe."""
    return json.loads(json.dumps(obj))


def new_doc_id():
    """Auto-generated ``_id`` (Elasticsearch uses 20 url-safe characters)."""
    return hashlib.sha1(os.urandom(16)).hexdigest()[:20]


def index_uuid(name):
    return hashlib.sha256(name.encode("utf-8")).hexdigest()[:22]


def str_id(value):
    if value is None:
        return None
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return str(value)


def as_list(value):
    if value is None:
        return []
    if isinstance(value, list):
        return value
    return [value]


def segments_of(path):
    return [s for s in path.split("/") if s]


def param(params, name, default=None):
    values = params.get(name)
    if not values:
        return default
    return values[-1]


def param_bool(params, name, default=False):
    value = param(params, name)
    if value is None:
        return default
    return str(value).strip().lower() in ("", "1", "true", "yes", "on")


def param_int(params, name, default):
    value = param(params, name)
    if value is None or str(value).strip() == "":
        return default
    try:
        return int(str(value).strip())
    except (TypeError, ValueError):
        return default


def to_int(value, default=0):
    if isinstance(value, bool):
        return default
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        return int(value)
    if value is None:
        return default
    try:
        return int(str(value).strip())
    except (TypeError, ValueError):
        return default


# ---------------------------------------------------------------------------
# index patterns
# ---------------------------------------------------------------------------

_PATTERN_CACHE = {}
_PATTERN_CACHE_LOCK = threading.Lock()


def pattern_regex(part):
    """Elasticsearch wildcard semantics: ``*`` any chars, ``?`` one char."""
    with _PATTERN_CACHE_LOCK:
        rx = _PATTERN_CACHE.get(part)
        if rx is None:
            out = []
            for ch in part:
                if ch == "*":
                    out.append(".*")
                elif ch == "?":
                    out.append(".")
                else:
                    out.append(re.escape(ch))
            rx = re.compile("^" + "".join(out) + "$", re.DOTALL)
            _PATTERN_CACHE[part] = rx
        return rx


def pattern_matches(part, name):
    if part in ("_all", "*", ""):
        return True
    return bool(pattern_regex(part).match(name))


def index_names_for(pattern, available):
    """Resolve an index expression (``a,b``, ``logs-*``, ``_all``) to sorted names."""
    names = []
    if pattern is None:
        names = list(available)
    else:
        for part in str(pattern).split(","):
            part = part.strip()
            if not part:
                continue
            if part in ("_all", "*"):
                names.extend(available)
                continue
            for available_name in available:
                if pattern_matches(part, available_name):
                    names.append(available_name)
    return sorted(set(names))


# ---------------------------------------------------------------------------
# value coercion (numbers, epoch millis, ISO dates, booleans)
# ---------------------------------------------------------------------------

ISO_DATE_RE = re.compile(
    r"^\d{4}-\d{2}-\d{2}"
    r"([T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?"
    r"(Z|[+-]\d{2}:?\d{2})?)?$"
)
EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)


def as_number(value):
    """Return ``float`` when the value is numeric-ish, else ``None``."""
    if isinstance(value, bool) or value is None:
        return None
    if isinstance(value, (int, float)):
        return float(value)
    if isinstance(value, str):
        text = value.strip()
        if not text:
            return None
        try:
            return float(text)
        except ValueError:
            return None
    return None


def as_millis(value):
    """ISO date string / epoch millis -> epoch millis (float), else ``None``."""
    if isinstance(value, bool) or value is None:
        return None
    if isinstance(value, (int, float)):
        return float(value)
    if not isinstance(value, str):
        return None
    text = value.strip()
    if not ISO_DATE_RE.match(text):
        return None
    candidate = text[:-1] + "+00:00" if text.endswith("Z") else text
    try:
        parsed = datetime.fromisoformat(candidate)
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return (parsed.astimezone(timezone.utc) - EPOCH).total_seconds() * 1000.0


def comparable(value):
    """Coerce a scalar into ``float`` (numbers and dates) or ``str``."""
    if isinstance(value, bool) or value is None:
        return None
    if isinstance(value, (int, float)):
        return float(value)
    if isinstance(value, str):
        text = value.strip()
        number = as_number(text)
        millis = as_millis(text)
        if millis is not None:
            return millis
        if number is not None:
            return number
        return text
    return None


def scalar_text(value):
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, str):
        return value
    if value is None:
        return ""
    return jdump(value)


def scalars_equal(left, right):
    if isinstance(left, bool) or isinstance(right, bool):
        return isinstance(left, bool) and isinstance(right, bool) and left == right
    left_num = as_number(left)
    right_num = as_number(right)
    if left_num is not None and right_num is not None:
        return left_num == right_num
    if isinstance(left, str) and isinstance(right, str):
        return left == right
    return left == right


def compare_scalars(left, right):
    if left is None and right is None:
        return 0
    if left is None:
        return -1
    if right is None:
        return 1
    if isinstance(left, bool) or isinstance(right, bool):
        lv, rv = shape_key(left), shape_key(right)
        return (lv > rv) - (lv < rv)
    left_num = as_number(left)
    right_num = as_number(right)
    if left_num is not None and right_num is not None:
        return (left_num > right_num) - (left_num < right_num)
    lv, rv = scalar_text(left), scalar_text(right)
    if lv == rv:
        return 0
    return 1 if lv > rv else -1


def shape_key(value):
    """Order mixed types deterministically: numbers < strings < other."""
    if isinstance(value, bool):
        return (0, None)
    if as_number(value) is not None:
        return (0, None)
    if isinstance(value, str):
        return (1, scalar_text(value))
    return (2, scalar_text(value))


# ---------------------------------------------------------------------------
# field access
# ---------------------------------------------------------------------------


def flatten(value):
    out = []
    if isinstance(value, list):
        for item in value:
            out.extend(flatten(item))
        return out
    return [value]


def get_path(source, path):
    """Values found at a dotted path (empty list when absent)."""
    current = [source]
    for part in str(path).split("."):
        nxt = []
        for node in current:
            if isinstance(node, dict):
                if part in node:
                    nxt.append(node[part])
            elif isinstance(node, list):
                for item in node:
                    if isinstance(item, dict) and part in item:
                        nxt.append(item[part])
        current = nxt
        if not current:
            return []
    out = []
    for node in current:
        out.extend(flatten(node))
    return out


def walk_paths(source, prefix=""):
    """Yield ``(dotted_path, scalar)`` for every scalar inside ``source``."""
    if isinstance(source, dict):
        for key, value in source.items():
            path = "%s.%s" % (prefix, key) if prefix else str(key)
            for item in walk_paths(value, path):
                yield item
    elif isinstance(source, list):
        for value in source:
            for item in walk_paths(value, prefix):
                yield item
    else:
        yield prefix, source


def doc_values(doc, field):
    """Values of ``field`` for a document; understands the meta fields too."""
    field = str(field)
    if field in ("_id",):
        return [doc.get("_id")]
    if field in ("_index",):
        return [doc.get("_index")]
    if field in ("_score", "_doc", "_type"):
        return []
    if field == "_source":
        return [doc.get("source")]
    return get_path(doc.get("source") or {}, field)


def doc_values_for_fields(doc, fields):
    """Values for a list of field expressions (``*`` and ``err*`` allowed)."""
    out = []
    paths = None
    for expression in fields:
        expression = str(expression).split("^")[0].strip()
        if not expression:
            continue
        if "*" in expression or "?" in expression:
            if expression == "*":
                out.extend(value for _, value in walk_paths(doc.get("source") or {}))
            else:
                for path, value in walk_paths(doc.get("source") or {}):
                    if pattern_matches(expression, path):
                        out.append(value)
            continue
        out.extend(doc_values(doc, expression))
    return out


# ---------------------------------------------------------------------------
# query DSL evaluation
# ---------------------------------------------------------------------------


class QueryError(Exception):
    def __init__(self, kind, reason=None):
        self.kind = kind
        self.reason = reason or ("unknown query [%s]" % kind)
        Exception.__init__(self, self.reason)


def term_value(spec):
    if isinstance(spec, dict) and "value" in spec:
        return spec.get("value")
    return spec


def term_matches(values, wanted):
    """``term``: exact match; falls back to a case-insensitive match (analyzed text)."""
    wanted_text = scalar_text(wanted).strip().lower()
    for value in values:
        if value is None:
            continue
        if scalars_equal(value, wanted):
            return True
        if isinstance(wanted, dict) or isinstance(value, dict):
            continue
        if scalar_text(value).strip().lower() == wanted_text:
            return True
    return False


def range_matches(values, spec):
    """``range`` on numbers, ISO date strings and epoch millis.

    Doc values and bounds are coerced with :func:`comparable`, so ``gte`` on
    ``2026-10-16`` matches both ``"2026-10-16T12:00:00Z"`` and ``1792...000``
    (epoch millis), exactly like a real ``date`` field.
    """
    if not isinstance(spec, dict):
        return False
    bounds = []
    for key in ("gte", "gt", "lte", "lt"):
        if key in spec and spec[key] is not None:
            bounds.append((key, spec[key]))
    if not bounds:
        return False
    for value in values:
        if value is None or isinstance(value, bool) or isinstance(value, (dict, list)):
            continue
        if all(range_compare(value, operator, bound) for operator, bound in bounds):
            return True
    return False


def range_compare(value, operator, bound):
    left = comparable(value)
    right = comparable(bound)
    if left is None or right is None:
        if isinstance(value, str) and isinstance(bound, str):
            left, right = value, bound
        else:
            return False
    if isinstance(left, float) and isinstance(right, float):
        result = (left > right) - (left < right)
    else:
        left_text, right_text = scalar_text(left), scalar_text(right)
        result = (left_text > right_text) - (left_text < right_text)
    if operator == "gte":
        return result >= 0
    if operator == "gt":
        return result > 0
    if operator == "lte":
        return result <= 0
    if operator == "lt":
        return result < 0
    return False


def wildcard_matches(values, pattern, case_insensitive=False):
    haystack_pattern = str(pattern)
    for value in values:
        if value is None:
            continue
        text = scalar_text(value)
        if case_insensitive:
            text = text.lower()
            haystack_pattern_cmp = haystack_pattern.lower()
        else:
            haystack_pattern_cmp = haystack_pattern
        if fnmatch.fnmatchcase(text, haystack_pattern_cmp):
            return True
    return False


def loose_matches(values, wanted, phrases_only=False):
    """Analyzer-free ``match``/``match_phrase``: case-insensitive token/substring."""
    text = scalar_text(wanted).strip().strip('"')
    if not text:
        return False
    needle = text.lower()
    for value in values:
        if value is None:
            continue
        if scalars_equal(value, wanted) and not isinstance(value, str):
            return True
        if needle in scalar_text(value).lower():
            return True
    return False


def minimum_should_match(spec_value, total):
    if spec_value is None:
        return None
    if isinstance(spec_value, bool):
        return None
    if isinstance(spec_value, int):
        return spec_value + total if spec_value < 0 else spec_value
    text = str(spec_value).strip()
    try:
        if text.endswith("%"):
            percent = float(text[:-1]) / 100.0
            needed = int(total * percent)
            return max(0, min(total, needed))
        return int(text)
    except ValueError:
        return None


def q_bool(doc, spec):
    spec = spec or {}
    must = as_list(spec.get("must"))
    filters = as_list(spec.get("filter"))
    should = as_list(spec.get("should"))
    must_not = as_list(spec.get("must_not"))
    for clause in must:
        if not q_match(doc, clause):
            return False
    for clause in filters:
        if not q_match(doc, clause):
            return False
    for clause in must_not:
        if q_match(doc, clause):
            return False
    if should:
        needed = 1 if not must and not filters else 0
        explicit = minimum_should_match(spec.get("minimum_should_match"), len(should))
        if explicit is not None:
            needed = explicit
        if needed > 0:
            matched = 0
            for clause in should:
                if q_match(doc, clause):
                    matched += 1
                    if matched >= needed:
                        break
            if matched < needed:
                return False
    return True


def q_term(doc, spec):
    for field, raw in (spec or {}).items():
        if field in ("boost", "_name"):
            continue
        if not term_matches(doc_values(doc, field), term_value(raw)):
            return False
    return True


def q_terms(doc, spec):
    for field, raw in (spec or {}).items():
        if field in ("boost", "_name"):
            continue
        wanted = as_list(term_value(raw))
        values = doc_values(doc, field)
        if not any(term_matches(values, item) for item in wanted):
            return False
    return True


def q_range(doc, spec):
    for field, rules in (spec or {}).items():
        if field in ("boost", "_name"):
            continue
        if not range_matches(doc_values(doc, field), rules):
            return False
    return True


def q_exists(doc, spec):
    field = spec if isinstance(spec, str) else (spec or {}).get("field")
    if not field:
        return False
    for value in doc_values(doc, field):
        if value is not None:
            return True
    return False


def q_ids(doc, spec):
    if isinstance(spec, list):
        wanted = spec
    else:
        wanted = as_list(term_value((spec or {}).get("values")))
    wanted_ids = set(str_id(item) for item in wanted)
    return str_id(doc.get("_id")) in wanted_ids


def q_prefix(doc, spec):
    for field, raw in (spec or {}).items():
        if field in ("boost", "_name"):
            continue
        prefix = scalar_text(term_value(raw))
        case_insensitive = bool(raw.get("case_insensitive")) if isinstance(raw, dict) else False
        found = False
        for value in doc_values(doc, field):
            if value is None:
                continue
            text = scalar_text(value)
            if case_insensitive:
                found = text.lower().startswith(prefix.lower())
            else:
                found = text.startswith(prefix)
            if found:
                break
        if not found:
            return False
    return True


def q_wildcard(doc, spec):
    for field, raw in (spec or {}).items():
        if field in ("boost", "_name"):
            continue
        pattern = scalar_text(term_value(raw))
        case_insensitive = bool(raw.get("case_insensitive")) if isinstance(raw, dict) else False
        if not wildcard_matches(doc_values(doc, field), pattern, case_insensitive):
            return False
    return True


def q_match_query(doc, spec):
    """``match`` / ``match_phrase`` without an analyzer: case-insensitive substring."""
    for field, raw in (spec or {}).items():
        if field in ("boost", "_name"):
            continue
        if not loose_matches(doc_values(doc, field), term_value(raw)):
            return False
    return True


def q_query_string(doc, spec):
    if isinstance(spec, str):
        spec = {"query": spec}
    spec = spec or {}
    query_text = str(spec.get("query") or "")
    if not query_text.strip():
        return True
    fields = spec.get("fields")
    if fields:
        search_fields = [str(item).split("^")[0] for item in as_list(fields)]
    elif spec.get("default_field"):
        search_fields = [str(spec["default_field"])]
    else:
        search_fields = None

    tokens = [tok for tok in re.findall(r'"[^"]*"|[()]|[^\s()]+', query_text) if tok not in ("(", ")")]
    groups = [[]]
    negate = False
    saw_operator = False
    for token in tokens:
        # Lucene query syntax: only uppercase AND / OR / NOT are operators.
        if token == "OR":
            saw_operator = True
            if groups[-1]:
                groups.append([])
            continue
        if token == "AND":
            saw_operator = True
            continue
        if token == "NOT":
            negate = True
            continue
        groups[-1].append((token, negate))
        negate = False
    if not groups[-1] and len(groups) > 1:
        groups.pop()
    default_operator = str(spec.get("default_operator") or "or").strip().lower()
    if default_operator == "and" and not saw_operator:
        flattened = [clause for group in groups for clause in group]
        groups = [flattened] if flattened else [[]]

    for group in groups:
        if not group:
            continue
        ok = True
        for clause_text, clause_negate in group:
            matched = query_string_clause(doc, clause_text, search_fields)
            if clause_negate:
                matched = not matched
            if not matched:
                ok = False
                break
        if ok:
            return True
    return False


def query_string_clause(doc, clause, search_fields):
    field = None
    pattern = clause
    if ":" in clause and not clause.startswith('"'):
        candidate_field, _, remainder = clause.partition(":")
        if candidate_field and "<" not in candidate_field and ">" not in candidate_field:
            field = candidate_field
            pattern = remainder
    if field is not None:
        if field == "*" and pattern == "*":
            return True
        if "*" in field or "?" in field:
            values = doc_values_for_fields(doc, [field])
        else:
            values = doc_values(doc, field)
        if pattern == "*":
            return any(value is not None for value in values)
        return loose_pattern(values, pattern)
    if pattern == "*":
        return True
    return loose_pattern(doc_values_for_fields(doc, search_fields or ["*"]), pattern)


def loose_pattern(values, pattern):
    text = str(pattern).strip()
    if text.startswith('"') and text.endswith('"') and len(text) >= 2:
        text = text[1:-1]
    if "*" in text or "?" in text:
        return wildcard_matches(values, text, case_insensitive=True)
    return loose_matches(values, text)


QUERY_HANDLERS = {
    "match_all": lambda doc, spec: True,
    "match_none": lambda doc, spec: False,
    "bool": q_bool,
    "term": q_term,
    "terms": q_terms,
    "range": q_range,
    "exists": q_exists,
    "ids": q_ids,
    "prefix": q_prefix,
    "wildcard": q_wildcard,
    "regexp": lambda doc, spec: q_wildcard(
        doc,
        {
            field: {"value": _regexp_to_wildcard(term_value(raw))}
            for field, raw in (spec or {}).items()
            if field not in ("boost", "_name")
        },
    ),
    "match": q_match_query,
    "match_phrase": q_match_query,
    "match_phrase_prefix": q_match_query,
    "multi_match": q_match_query,
    "query_string": q_query_string,
    "simple_query_string": q_query_string,
    "constant_score": lambda doc, spec: q_match_filter(doc, spec),
    "boosting": lambda doc, spec: q_match_filter(doc, spec),
}


def q_match_filter(doc, spec):
    """``constant_score`` / ``boosting`` wrapper: evaluate the inner clause only."""
    inner = (spec or {}).get("filter")
    if inner is None:
        inner = (spec or {}).get("query")
    if inner is None:
        return True
    return q_match(doc, inner)


def _regexp_to_wildcard(pattern):
    text = scalar_text(pattern)
    text = text.replace(".*", "\x00")
    text = text.replace(".", "?")
    text = text.replace("\x00", "*")
    return text


def q_match(doc, query):
    if query is None:
        return True
    if not isinstance(query, dict):
        raise QueryError("parsing_exception", "malformed query clause")
    if not query:
        return True
    if len(query) > 1:
        return all(q_match(doc, {key: value}) for key, value in query.items())
    kind, spec = next(iter(query.items()))
    if kind in ("boost", "_name"):
        return True
    handler = QUERY_HANDLERS.get(kind)
    if handler is None:
        raise QueryError("parsing_exception", "unknown query [%s]" % kind)
    if kind in ("constant_score", "boosting"):
        return q_match_filter(doc, spec)
    return handler(doc, spec)


def sub_queries(kind, spec):
    """Nested query clauses inside a compound clause (used by validate_query)."""
    if kind == "bool":
        out = []
        for key in ("must", "filter", "should", "must_not"):
            out.extend(as_list((spec or {}).get(key)))
        return out
    if kind in ("constant_score", "boosting"):
        spec = spec or {}
        return [clause for clause in (spec.get("filter"), spec.get("query"), spec.get("positive")) if clause]
    return []


def validate_query(query):
    """Reject unsupported clauses up front, the way Elasticsearch parses the DSL."""
    if query is None:
        return
    if not isinstance(query, dict):
        raise QueryError("parsing_exception", "malformed query clause")
    for kind, spec in query.items():
        if kind in ("boost", "_name"):
            continue
        if kind not in QUERY_HANDLERS:
            raise QueryError("parsing_exception", "unknown query [%s]" % kind)
        for clause in sub_queries(kind, spec):
            validate_query(clause)


# ---------------------------------------------------------------------------
# sorting
# ---------------------------------------------------------------------------


def normalize_sort(spec):
    """Normalise ``sort`` (string / dict / list, body or query string) to tuples."""
    out = []
    if spec is None:
        return out
    if isinstance(spec, (str, dict)):
        spec = [spec]
    if not isinstance(spec, list):
        return out
    for item in spec:
        if isinstance(item, str):
            field, order = item, "asc"
            if ":" in item:
                field, _, order = item.partition(":")
            out.append((field.strip(), order.strip().lower() == "desc", None))
        elif isinstance(item, dict):
            for field, config in item.items():
                order, missing = "asc", None
                if isinstance(config, str):
                    order = config
                elif isinstance(config, dict):
                    order = config.get("order", "asc") or "asc"
                    missing = config.get("missing")
                out.append((str(field), str(order).lower() == "desc", missing))
    return [item for item in out if item[0]]


def sort_value(doc, field):
    if field in ("_score", "_doc", "_type"):
        return None
    if field == "_id":
        return doc.get("_id")
    if field == "_index":
        return doc.get("_index")
    values = doc_values(doc, field)
    for value in values:
        if value is not None:
            return value
    return None


def sort_docs(docs, specs):
    if not specs:
        return docs

    def compare(left, right):
        for field, descending, missing in specs:
            left_value = sort_value(left, field)
            right_value = sort_value(right, field)
            left_missing = left_value is None
            right_missing = right_value is None
            missing_first = str(missing or "_last").lower() == "_first"
            if left_missing and right_missing:
                continue
            if left_missing:
                return -1 if missing_first else 1
            if right_missing:
                return 1 if missing_first else -1
            result = compare_scalars(left_value, right_value)
            if result:
                return -result if descending else result
        return 0

    return sorted(docs, key=functools.cmp_to_key(compare))


# ---------------------------------------------------------------------------
# aggregations
# ---------------------------------------------------------------------------


def agg_metric_values(docs, field):
    values = []
    for doc in docs:
        for value in doc_values(doc, field):
            if value is None:
                continue
            values.append(value)
    return values


def run_aggs(spec, docs):
    out = {}
    if not isinstance(spec, dict):
        return out
    for name, body in spec.items():
        if not isinstance(body, dict):
            continue
        sub_spec = body.get("aggs") or body.get("aggregations") or {}
        if "terms" in body:
            out[name] = agg_terms(body["terms"], docs, sub_spec)
        elif "value_count" in body:
            field = (body["value_count"] or {}).get("field")
            out[name] = {"value": len(agg_metric_values(docs, field))}
        elif "cardinality" in body:
            field = (body["cardinality"] or {}).get("field")
            out[name] = {"value": len(set(scalar_text(v) for v in agg_metric_values(docs, field)))}
        elif "sum" in body or "avg" in body or "min" in body or "max" in body:
            kind = next(k for k in ("sum", "avg", "min", "max") if k in body)
            field = (body[kind] or {}).get("field")
            numbers = []
            for value in agg_metric_values(docs, field):
                number = as_number(value)
                if number is not None:
                    numbers.append(number)
            if not numbers:
                out[name] = {"value": None}
            elif kind == "sum":
                total = sum(numbers)
                out[name] = {"value": int(total) if float(total).is_integer() else total}
            elif kind == "avg":
                out[name] = {"value": sum(numbers) / float(len(numbers))}
            elif kind == "min":
                out[name] = {"value": min(numbers)}
            else:
                out[name] = {"value": max(numbers)}
        elif "filter" in body:
            filtered = [doc for doc in docs if q_match(doc, body.get("filter"))]
            result = {"doc_count": len(filtered)}
            if sub_spec:
                result.update(run_aggs(sub_spec, filtered))
            out[name] = result
        else:
            out[name] = {"doc_count": len(docs)}
    return out


def agg_terms(spec, docs, sub_spec):
    spec = spec or {}
    field = spec.get("field")
    size = to_int(spec.get("size", 10), 10)
    missing_value = spec.get("missing")
    buckets = {}
    for doc in docs:
        values = [value for value in doc_values(doc, field) if value is not None]
        if not values:
            if missing_value is not None:
                values = [missing_value]
            else:
                continue
        seen = set()
        for value in values:
            key = scalar_text(value)
            if key in seen:
                continue
            seen.add(key)
            bucket = buckets.get(key)
            if bucket is None:
                bucket = {"key": value, "docs": []}
                buckets[key] = bucket
            bucket["docs"].append(doc)

    ordered = sorted(
        buckets.values(),
        key=lambda bucket: (-len(bucket["docs"]), key_rank(bucket["key"])),
    )
    order = spec.get("order")
    if order:
        ordered = apply_terms_order(ordered, order)

    min_doc_count = to_int(spec.get("min_doc_count", 1), 1)
    visible = [bucket for bucket in ordered if len(bucket["docs"]) >= min_doc_count]
    selected = visible[:size] if size >= 0 else visible
    other = len(visible) - len(selected)

    out_buckets = []
    for bucket in selected:
        entry = {"key": bucket["key"], "doc_count": len(bucket["docs"])}
        if sub_spec:
            entry.update(run_aggs(sub_spec, bucket["docs"]))
        out_buckets.append(entry)
    return {
        "doc_count_error_upper_bound": 0,
        "sum_other_doc_count": other,
        "buckets": out_buckets,
    }


def key_rank(key):
    if isinstance(key, bool):
        return (2, scalar_text(key))
    if as_number(key) is not None:
        return (0, as_number(key))
    return (1, scalar_text(key))


def apply_terms_order(buckets, order):
    if isinstance(order, list) and order:
        order = order[0]
    if not isinstance(order, dict) or not order:
        return buckets
    target, direction = next(iter(order.items()))
    descending = str(direction).lower() == "desc"
    if target in ("_count", "count"):
        return sorted(buckets, key=lambda bucket: len(bucket["docs"]), reverse=descending)
    if target in ("_key", "key"):
        return sorted(buckets, key=lambda bucket: key_rank(bucket["key"]), reverse=descending)
    if target == "_term":
        return sorted(buckets, key=lambda bucket: key_rank(bucket["key"]), reverse=descending)
    return sorted(
        buckets,
        key=lambda bucket: subnet_metric(bucket, target),
        reverse=descending,
    )


def subnet_metric(bucket, agg_name):
    """Ordering by a sub-aggregation value (only the simple metric ones)."""
    return 0.0


# ---------------------------------------------------------------------------
# error bodies
# ---------------------------------------------------------------------------


def error_payload(kind, reason, status, extra=None, root_cause=None):
    detail = dict(extra or {})
    root = [dict(detail)] if root_cause is None else root_cause
    root_entry = {"type": kind, "reason": reason}
    root_entry.update(detail)
    if root_cause is None:
        root = [root_entry]
    error = {"root_cause": root, "type": kind, "reason": reason}
    error.update(detail)
    return {"error": error, "status": status}


def index_not_found(index, status=404):
    return error_payload(
        "index_not_found_exception",
        "no such index [%s]" % index,
        status,
        {
            "resource.type": "index_or_alias",
            "resource.id": index,
            "index_uuid": "_na_",
            "index": index,
        },
    )


def no_handler(path, method):
    return error_payload(
        "no_handler_found_exception",
        "no handler found for uri [%s] and method [%s]" % (path, method),
        404,
    )


# ---------------------------------------------------------------------------
# persistence / cluster state
# ---------------------------------------------------------------------------


class MockElasticsearch(object):
    """All cluster state plus the operations the REST layer needs."""

    def __init__(self, state_path=None):
        self.lock = threading.RLock()
        self.state_path = state_path
        self.indices = {}
        self.ilm_policies = {}
        self.index_templates = {}
        self.legacy_templates = {}
        self.auto_create_index = True
        self.started_at = now_millis()
        self.request_count = 0

    # -- persistence ------------------------------------------------------

    def load(self):
        if not self.state_path or not os.path.exists(self.state_path):
            return []
        try:
            with open(self.state_path, "r", encoding="utf-8") as handle:
                raw = json.load(handle)
        except (OSError, ValueError) as exc:
            warn("could not read --state file %s: %s" % (self.state_path, exc))
            return []
        if not isinstance(raw, dict):
            return []
        self.indices = raw.get("indices") or {}
        for name, index in list(self.indices.items()):
            index.setdefault("aliases", {})
            index.setdefault("docs", {})
            index.setdefault("settings", {})
            index.setdefault("mappings", {})
            index.setdefault("seq", len(index.get("docs") or {}))
            index.setdefault("uuid", index_uuid(name))
        self.ilm_policies = raw.get("ilm_policies") or {}
        self.index_templates = raw.get("index_templates") or {}
        self.legacy_templates = raw.get("legacy_templates") or {}
        return sorted(self.indices)

    def save_locked(self):
        if not self.state_path:
            return
        payload = {
            "version": 1,
            "saved_at": now_ts(),
            "cluster_name": CLUSTER_NAME,
            "indices": self.indices,
            "ilm_policies": self.ilm_policies,
            "index_templates": self.index_templates,
            "legacy_templates": self.legacy_templates,
        }
        tmp = self.state_path + ".tmp"
        try:
            directory = os.path.dirname(os.path.abspath(self.state_path))
            if directory and not os.path.isdir(directory):
                os.makedirs(directory, exist_ok=True)
            with open(tmp, "w", encoding="utf-8") as handle:
                json.dump(payload, handle, ensure_ascii=False)
            os.replace(tmp, self.state_path)
        except OSError as exc:
            warn("could not write --state file %s: %s" % (self.state_path, exc))

    # -- indices ----------------------------------------------------------

    def index_exists(self, name):
        if name in self.indices:
            return True
        return bool(self.alias_index_names(name))

    def create_index(self, name, body=None, auto=False):
        if name in self.indices:
            return False
        body = body or {}
        now = now_millis()
        index = {
            "name": name,
            "uuid": index_uuid(name),
            "creation_date": now,
            "settings": body.get("settings") or {},
            "mappings": body.get("mappings") or {},
            "aliases": {},
            "docs": {},
            "seq": 0,
            "auto_created": bool(auto),
        }
        self.indices[name] = index
        if auto:
            self.apply_matching_templates(name)
        for alias_name, config in (body.get("aliases") or {}).items():
            self.set_alias(name, alias_name, config if isinstance(config, dict) else {})
        self.save_locked()
        return True

    def apply_matching_templates(self, name):
        """Apply the highest-priority matching composable template (auto-create)."""
        matching = []
        for template_name, body in self.index_templates.items():
            for pattern in as_list((body or {}).get("index_patterns")):
                if pattern_matches(str(pattern), name):
                    matching.append((to_int((body or {}).get("priority", 0), 0), template_name, body))
                    break
        for _, _, body in sorted(matching, key=lambda item: (item[0], item[1])):
            template = (body or {}).get("template") or {}
            index = self.indices[name]
            if template.get("settings"):
                index["settings"] = clone(template["settings"])
            if template.get("mappings"):
                index["mappings"] = clone(template["mappings"])
            for alias_name, config in (template.get("aliases") or {}).items():
                self.set_alias(name, alias_name, config if isinstance(config, dict) else {})

    def delete_index(self, name):
        if name not in self.indices:
            return False
        del self.indices[name]
        for other in self.indices.values():
            for alias_name in list(other.get("aliases") or {}):
                if alias_name == name:
                    del other["aliases"][alias_name]
        self.save_locked()
        return True

    def resolve(self, pattern):
        return index_names_for(pattern, self.indices)

    def index_summary(self, name):
        index = self.indices[name]
        docs = index.get("docs") or {}
        store_bytes = 0
        for doc in docs.values():
            store_bytes += len(jdump(doc.get("source") or {}).encode("utf-8")) + 16
        return {
            "index": name,
            "uuid": index.get("uuid") or index_uuid(name),
            "docs.count": len(docs),
            "store.size": store_bytes,
            "creation.date": int(index.get("creation_date") or self.started_at),
        }

    # -- aliases ----------------------------------------------------------

    def alias_config(self, config):
        out = {}
        if isinstance(config, dict):
            for key in ("is_write_index", "routing", "filter", "index_routing", "search_routing"):
                if key in config and config[key] is not None:
                    out[key] = clone(config[key])
            if "is_write_index" in out:
                out["is_write_index"] = bool(out["is_write_index"])
        return out

    def set_alias(self, index_name, alias_name, config):
        index = self.indices.get(index_name)
        if index is None:
            return False
        entry = self.alias_config(config)
        index.setdefault("aliases", {})[alias_name] = entry
        if entry.get("is_write_index") is True:
            for other_name, other in self.indices.items():
                if other_name == index_name:
                    continue
                other_alias = (other.get("aliases") or {}).get(alias_name)
                if isinstance(other_alias, dict) and other_alias.get("is_write_index"):
                    other_alias["is_write_index"] = False
        self.save_locked()
        return True

    def remove_alias(self, index_name, alias_name):
        index = self.indices.get(index_name)
        if index is None:
            return False
        if alias_name not in (index.get("aliases") or {}):
            return False
        del index["aliases"][alias_name]
        self.save_locked()
        return True

    def alias_index_names(self, alias_pattern):
        names = []
        for name, index in self.indices.items():
            for alias_name in (index.get("aliases") or {}):
                if pattern_matches(str(alias_pattern), alias_name):
                    names.append(name)
                    break
        return sorted(names)

    def write_index_for(self, alias_name):
        """The index that receives writes for ``alias_name`` (explicit or implicit)."""
        carrying = []
        explicit = None
        for name, index in sorted(self.indices.items()):
            config = (index.get("aliases") or {}).get(alias_name)
            if config is None:
                continue
            carrying.append(name)
            if config.get("is_write_index") is True and explicit is None:
                explicit = name
        if explicit is not None:
            return explicit
        if len(carrying) == 1:
            return carrying[0]
        return None

    # -- documents --------------------------------------------------------

    def resolve_write_target(self, name):
        """Elasticsearch writes into the backing index of a write alias, never into the alias.

        Getting this wrong makes every realtime lookup (get, mget) miss documents that were
        written through an alias, which is exactly how this application writes.
        """
        if name in self.indices:
            return name
        return self.write_index_for(name) or name

    def put_doc(self, index_name, doc_id, source, create_only=False):
        index_name = self.resolve_write_target(index_name)
        index = self.indices.get(index_name)
        if index is None:
            return {"missing_index": True}
        if doc_id is None:
            doc_id = new_doc_id()
        doc_id = str_id(doc_id)
        existing = index["docs"].get(doc_id)
        if create_only and existing is not None:
            return {
                "conflict": True,
                "current_version": int(existing.get("version") or 1),
                "doc_id": doc_id,
            }
        if existing is None:
            index["seq"] = int(index.get("seq") or 0) + 1
            index["docs"][doc_id] = {
                "source": source,
                "version": 1,
                "seq": index["seq"],
                "created": True,
            }
            return {"created": True, "doc_id": doc_id, "version": 1, "seq": index["seq"]}
        existing["source"] = source
        existing["version"] = int(existing.get("version") or 1) + 1
        return {
            "created": False,
            "doc_id": doc_id,
            "version": existing["version"],
            "seq": existing.get("seq"),
        }

    def delete_doc(self, index_name, doc_id):
        index = self.indices.get(index_name)
        if index is None:
            return {"missing_index": True}
        doc_id = str_id(doc_id)
        existing = index["docs"].get(doc_id)
        if existing is None:
            return {"found": False, "doc_id": doc_id, "version": 1}
        del index["docs"][doc_id]
        self.save_locked()
        return {"found": True, "doc_id": doc_id, "version": int(existing.get("version") or 1) + 1}

    def update_doc(self, index_name, doc_id, partial, upsert=None, doc_as_upsert=False):
        index = self.indices.get(index_name)
        if index is None:
            return {"missing_index": True}
        doc_id = str_id(doc_id)
        existing = index["docs"].get(doc_id)
        if existing is None:
            if upsert is not None:
                return self.put_doc(index_name, doc_id, upsert)
            if doc_as_upsert:
                return self.put_doc(index_name, doc_id, partial)
            return {"found": False, "doc_id": doc_id}
        merged = clone(existing["source"])
        if not isinstance(merged, dict):
            merged = {}
        for key, value in (partial or {}).items():
            merged[key] = clone(value)
        existing["source"] = merged
        existing["version"] = int(existing.get("version") or 1) + 1
        return {"created": False, "updated": True, "doc_id": doc_id, "version": existing["version"]}

    def get_doc(self, index_name, doc_id):
        index = self.indices.get(index_name)
        if index is None:
            return None
        entry = index["docs"].get(str_id(doc_id))
        if entry is None:
            return None
        return entry

    def collect_docs(self, pattern):
        """Matching documents as ``{_index,_id,source,seq}`` in insertion order."""
        out = []
        for name in self.resolve(pattern):
            index = self.indices[name]
            ordered = sorted((index.get("docs") or {}).items(), key=lambda item: item[1].get("seq", 0))
            for doc_id, entry in ordered:
                out.append(
                    {
                        "_index": name,
                        "_id": doc_id,
                        "source": entry.get("source"),
                        "seq": entry.get("seq", 0),
                        "version": entry.get("version", 1),
                        "_uuid": index.get("uuid"),
                    }
                )
        return out

    # -- bulk -------------------------------------------------------------

    def parse_bulk(self, raw_body):
        text = raw_body.decode("utf-8", "replace") if isinstance(raw_body, bytes) else str(raw_body or "")
        lines = [line for line in text.split("\n")]
        while lines and lines[-1] == "":
            lines.pop()
        actions = []
        position = 0
        while position < len(lines):
            header = lines[position]
            position += 1
            if header.strip() == "":
                continue
            try:
                parsed = json.loads(header)
            except ValueError:
                raise QueryError("parse_exception", "Failed to parse bulk action metadata line")
            if not isinstance(parsed, dict) or len(parsed) != 1:
                raise QueryError(
                    "parse_exception",
                    "Malformed action/metadata line [%s], expected a single action" % header,
                )
            op = next(iter(parsed))
            if op not in ("create", "index", "delete", "update"):
                raise QueryError("illegal_argument_exception", "Malformed action [%s] in bulk" % op)
            meta = parsed[op] or {}
            if not isinstance(meta, dict):
                meta = {}
            document = None
            if op in ("create", "index", "update"):
                if position >= len(lines):
                    raise QueryError(
                        "parse_exception",
                        "The bulk request must provide a source line for action [%s]" % op,
                    )
                source_line = lines[position]
                position += 1
                try:
                    document = json.loads(source_line)
                except ValueError:
                    raise QueryError("parse_exception", "Failed to parse bulk source line")
            actions.append((op, meta, document))
        return actions


def warn(message):
    sys.stderr.write("[mock-es] WARN: %s\n" % message)
    sys.stderr.flush()


# ---------------------------------------------------------------------------
# HTTP layer
# ---------------------------------------------------------------------------


def auth_kind(header):
    if not header:
        return "none"
    scheme = header.split(" ", 1)[0].strip().lower()
    return scheme if scheme in SUPPORTED_AUTH_SCHEMES else "none"


def basic_credentials_ok(header, expected_user, expected_password):
    if not header:
        return False
    parts = header.split(None, 1)
    if len(parts) != 2 or parts[0].lower() != "basic":
        return False
    try:
        decoded = base64.b64decode(parts[1].strip().encode("ascii"), validate=False).decode("utf-8", "replace")
    except Exception:  # noqa: BLE001 - any decoding failure is a failed login
        return False
    user, separator, password = decoded.partition(":")
    if not separator:
        return False
    return user == expected_user and password == expected_password


def security_error(path, user=None):
    if user is None:
        reason = "missing authentication credentials for REST request [%s]" % path
    else:
        reason = "unable to authenticate user [%s] for REST request [%s]" % (user, path)
    header = {"WWW-Authenticate": 'Basic realm="security" charset="UTF-8"'}
    return error_payload("security_exception", reason, 401, {"header": header})


def root_info(server):
    return {
        "name": NODE_NAME,
        "cluster_name": CLUSTER_NAME,
        "cluster_uuid": "mock-cluster-uuid-0001",
        "version": {
            "number": ES_VERSION,
            "build_flavor": BUILD_FLAVOR,
            "build_type": "mock",
            "build_hash": "mock000000000000000000000000000000000000",
            "build_date": "2026-01-01T00:00:00.000000000Z",
            "build_snapshot": False,
            "lucene_version": LUCENE_VERSION,
            "minimum_wire_compatibility_version": "7.17.0",
            "minimum_index_compatibility_version": "7.0.0",
        },
        "tagline": "You Know, for Search",
    }


def cluster_health(mock):
    count = len(mock.indices)
    return {
        "cluster_name": CLUSTER_NAME,
        "status": "green",
        "timed_out": False,
        "number_of_nodes": 1,
        "number_of_data_nodes": 1,
        "active_primary_shards": count,
        "active_shards": count,
        "relocating_shards": 0,
        "initializing_shards": 0,
        "unassigned_shards": 0,
        "delayed_unassigned_shards": 0,
        "number_of_pending_tasks": 0,
        "number_of_in_flight_fetch": 0,
        "task_max_waiting_in_queue_millis": 0,
        "active_shards_percent_as_number": 100.0,
    }


def cat_indices(mock, pattern, params):
    columns = param(params, "h")
    if columns:
        requested = [column.strip() for column in columns.split(",") if column.strip()]
    else:
        requested = [
            "health",
            "status",
            "index",
            "uuid",
            "pri",
            "rep",
            "docs.count",
            "docs.deleted",
            "store.size",
            "pri.store.size",
            "creation.date",
        ]
    if "index" not in requested:
        requested.insert(0, "index")
    rows = []
    for name in mock.resolve(pattern):
        summary = mock.index_summary(name)
        row = {}
        for column in requested:
            row[column] = cat_column_value(mock, name, summary, column)
        rows.append(row)
    return rows


def cat_column_value(mock, name, summary, column):
    if column == "index":
        return name
    if column == "health":
        return "green"
    if column == "status":
        return "open"
    if column == "uuid":
        return summary["uuid"]
    if column in ("pri", "rep"):
        return "1" if column == "pri" else "0"
    if column == "docs.count":
        return summary["docs.count"]
    if column == "docs.deleted":
        return 0
    if column in ("store.size", "pri.store.size"):
        return summary["store.size"]
    if column == "creation.date":
        return summary["creation.date"]
    if column == "creation.date.string":
        return datetime.fromtimestamp(
            summary["creation.date"] / 1000.0, tz=timezone.utc
        ).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
    if column == "dataset.size":
        return summary["store.size"]
    return None


def nodes_info(server):
    port = server.server_address[1]
    host = "%s:%s" % (server.server_address[0], port)
    return {
        "_nodes": {"total": 1, "successful": 1, "failed": 0},
        "cluster_name": CLUSTER_NAME,
        "nodes": {
            "mock-node-id-0001": {
                "name": NODE_NAME,
                "transport_address": host,
                "host": server.server_address[0],
                "ip": server.server_address[0],
                "version": ES_VERSION,
                "build_flavor": BUILD_FLAVOR,
                "roles": ["data", "ingest", "master", "remote_cluster_client", "transform"],
                "http": {
                    "bound_address": [host],
                    "publish_address": host,
                    "max_content_length_in_bytes": 104857600,
                },
            }
        },
    }


class EsHttpServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True
    request_queue_size = 64

    def __init__(self, address, handler, mock, log_path, log_lock, require_auth=None, quiet=False):
        self.mock = mock
        self.log_path = log_path
        self.log_lock = log_lock
        self.require_auth = require_auth
        self.quiet = quiet
        ThreadingHTTPServer.__init__(self, address, handler)


class EsRequestHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "mock-elasticsearch"
    sys_version = ""
    timeout = 120

    # -- plumbing ---------------------------------------------------------

    def version_string(self):
        return "mock-elasticsearch"

    def log_message(self, fmt, *args):  # silence BaseHTTPRequestHandler stderr noise
        return

    def do_GET(self):
        self._handle_request()

    def do_HEAD(self):
        self._handle_request()

    def do_POST(self):
        self._handle_request()

    def do_PUT(self):
        self._handle_request()

    def do_DELETE(self):
        self._handle_request()

    def do_PATCH(self):
        self._handle_request()

    def do_OPTIONS(self):
        self._handle_request()

    def read_request_body(self):
        transfer_encoding = (self.headers.get("Transfer-Encoding") or "").lower()
        if "chunked" in transfer_encoding:
            return self._read_chunked_body()
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except (TypeError, ValueError):
            length = 0
        if length <= 0:
            return b""
        return self.rfile.read(length)

    def _read_chunked_body(self):
        chunks = []
        while True:
            line = self.rfile.readline(65536)
            if not line:
                break
            line = line.strip()
            if b";" in line:
                line = line.split(b";", 1)[0]
            if not line:
                continue
            try:
                size = int(line, 16)
            except ValueError:
                raise ValueError("malformed chunk size")
            if size == 0:
                while True:
                    trailer = self.rfile.readline(65536)
                    if trailer in (b"", b"\r\n", b"\n"):
                        break
                break
            chunks.append(self.rfile.read(size))
            self.rfile.read(2)
        return b"".join(chunks)

    # -- request entry point ---------------------------------------------

    def _handle_request(self):
        started = time.time()
        method = (self.command or "GET").upper()
        head = method == "HEAD"
        try:
            url = urlparse(self.path)
        except ValueError:
            self._respond(400, error_payload("illegal_argument_exception", "malformed request path", 400), head=head)
            return
        path = unquote(url.path) or "/"
        query = url.query or ""
        params = parse_qs(query, keep_blank_values=True)
        authorization = self.headers.get("Authorization")
        auth = auth_kind(authorization)

        raw_body = b""
        body_error = None
        try:
            raw_body = self.read_request_body()
        except Exception as exc:  # noqa: BLE001 - malformed framing, answer and move on
            body_error = str(exc)
        ndjson = is_ndjson_path(path)
        body = {} if ndjson else (try_json(raw_body) if body_error is None else None)

        status = 200
        payload = None
        summary = ""
        try:
            if body_error is not None:
                status = 400
                payload = error_payload(
                    "parse_exception", "Failed to read request body: %s" % body_error, 400
                )
                summary = "bad-body"
            elif self.server.require_auth is not None and not basic_credentials_ok(
                authorization, self.server.require_auth[0], self.server.require_auth[1]
            ):
                status = 401
                payload = security_error(path, user=None if not authorization else "unknown")
                summary = "unauthorized"
            elif method == "OPTIONS":
                status = 200
                payload = None
                summary = "options"
            else:
                if body is None and raw_body.strip():
                    status = 400
                    payload = error_payload(
                        "parse_exception", "Failed to parse request body as JSON", 400
                    )
                    summary = "bad-json"
                else:
                    with self.server.mock.lock:
                        status, payload, summary = self._dispatch(method, path, params, body or {}, raw_body)
        except QueryError as exc:
            status = 400 if exc.kind != "resource_not_found_exception" else 404
            payload = error_payload(exc.kind, exc.reason, status)
            summary = "error:%s" % exc.kind
        except Exception as exc:  # noqa: BLE001 - never die on a bad request
            traceback.print_exc()
            status = 500
            payload = error_payload("internal_server_error", "%s: %s" % (type(exc).__name__, exc), 500)
            summary = "internal-error"

        duration_ms = round((time.time() - started) * 1000.0, 3)
        self._write_log(method, path, query, status, auth, raw_body, summary, duration_ms)
        if not self.server.quiet:
            self._print_request(method, path, query, status, summary, duration_ms)
        extra_headers = []
        if status == 401:
            extra_headers.append(("WWW-Authenticate", 'Basic realm="security" charset="UTF-8"'))
        self._respond(status, payload, head=head, extra_headers=extra_headers)

    # -- responses --------------------------------------------------------

    def _respond(self, status, payload, head=False, extra_headers=None):
        try:
            body = b"" if payload is None else jdump(payload).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header(PRODUCT_HEADER, PRODUCT_VALUE)
            self.send_header("Content-Length", "0" if head else str(len(body)))
            for key, value in (extra_headers or []):
                self.send_header(key, value)
            self.end_headers()
            if body and not head:
                self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def _print_request(self, method, path, query, status, summary, duration_ms):
        line = "[mock-es] %-6s %3s %s" % (method, status, path)
        if query:
            line += "?" + query
        line += "  -> %s (%s ms)" % (summary, duration_ms)
        sys.stdout.write(line + "\n")
        sys.stdout.flush()

    def _write_log(self, method, path, query, status, auth, raw_body, summary, duration_ms):
        entry = {
            "at": now_ts(),
            "method": method,
            "path": path,
            "query": query,
            "status": status,
            "auth": auth,
            "body_bytes": len(raw_body or b""),
            "summary": summary,
            "duration_ms": duration_ms,
        }
        if body_is_interesting(method, path) and raw_body:
            text = raw_body.decode("utf-8", "replace")
            if len(text) > MAX_LOG_BODY_CHARS:
                entry["body"] = text[:MAX_LOG_BODY_CHARS]
                entry["body_truncated"] = True
            else:
                entry["body"] = text
            parsed = try_json(raw_body)
            if parsed is not None:
                entry["body_json"] = parsed
        line = jdump(entry)
        with self.server.log_lock:
            try:
                directory = os.path.dirname(os.path.abspath(self.server.log_path))
                if directory and not os.path.isdir(directory):
                    os.makedirs(directory, exist_ok=True)
                with open(self.server.log_path, "a", encoding="utf-8") as handle:
                    handle.write(line + "\n")
            except OSError as exc:
                warn("could not append to request log: %s" % exc)

    # -- routing ----------------------------------------------------------

    def _dispatch(self, method, path, params, body, raw_body):
        mock = self.server.mock
        mock.request_count += 1
        segs = segments_of(path)
        count = len(segs)

        if count == 0:
            if method in ("GET", "HEAD"):
                return 200, root_info(self.server), "cluster-info"
            return 404, no_handler(path, method), "unknown-path"

        head = segs[0]

        if head == "_cluster" and count >= 2 and segs[1] == "health":
            stats = cluster_health(mock)
            if count >= 3:
                for name in index_names_for(segs[2], mock.indices):
                    stats["indices"] = stats.get("indices") or {}
                    stats["indices"][name] = {
                        "status": "green",
                        "number_of_shards": 1,
                        "number_of_replicas": 0,
                        "active_primary_shards": 1,
                        "active_shards": 1,
                        "relocating_shards": 0,
                        "initializing_shards": 0,
                        "unassigned_shards": 0,
                    }
            return 200, stats, "cluster-health"

        if head == "_cat" and count >= 2:
            if segs[1] in ("indices", "index"):
                pattern = segs[2] if count >= 3 else None
                rows = cat_indices(mock, pattern, params)
                return 200, rows, "cat-indices %s" % (pattern or "*")
            if segs[1] == "health":
                stats = cluster_health(mock)
                row = {
                    "cluster": CLUSTER_NAME,
                    "status": stats["status"],
                    "node.total": "1",
                    "node.data": "1",
                    "shards": stats["active_shards"],
                    "pri": stats["active_primary_shards"],
                    "relo": "0",
                    "init": "0",
                    "unassign": "0",
                    "pending_tasks": "0",
                    "max_task_wait_time": "-",
                    "active_shards_percent": "100.0%",
                }
                return 200, [row] if param(params, "format", "text") == "json" else row, "cat-health"
            if segs[1] == "aliases":
                rows = []
                for name, index in sorted(mock.indices.items()):
                    for alias_name, config in sorted((index.get("aliases") or {}).items()):
                        rows.append(
                            {
                                "alias": alias_name,
                                "index": name,
                                "filter": "*",
                                "routing.index": "-",
                                "routing.search": "-",
                                "is_write_index": bool(config.get("is_write_index")),
                            }
                        )
                return 200, rows, "cat-aliases"
            return 404, no_handler(path, method), "unknown-path"

        if head == "_aliases" and method == "POST":
            return self._op_alias_actions(body)

        if head == "_alias" and method in ("GET", "HEAD"):
            return self._op_get_alias(segs[1] if count >= 2 else None, path)

        if head == "_ilm" and count >= 2 and segs[1] == "policy":
            return self._op_ilm(method, segs[2] if count >= 3 else None, body, path)

        if head == "_index_template" and count >= 2:
            return self._op_template(
                method, mock.index_templates, "index_template", segs[1], body, path
            )

        if head == "_template" and count >= 2:
            return self._op_template(
                method, mock.legacy_templates, "template", segs[1], body, path, legacy=True
            )

        if head == "_nodes" and method in ("GET", "HEAD"):
            return 200, nodes_info(self.server), "nodes-info"

        if head == "_bulk" and method in ("POST", "PUT"):
            return self._op_bulk(mock, None, raw_body)

        if head == "_search" and method in ("GET", "POST"):
            return self._op_search(mock, None, body, params)

        if head == "_count" and method in ("GET", "POST"):
            return self._op_count(mock, None, body, params)

        if head == "_mget" and method in ("GET", "POST"):
            return self._op_mget(mock, None, body)

        if head == "_refresh" and method in ("GET", "POST"):
            return 200, {"_shards": {"total": len(mock.indices), "successful": len(mock.indices), "failed": 0}}, "refresh-all"

        if head in ("_security", "_license", "_xpack", "_snapshot", "_cluster"):
            return 404, no_handler(path, method), "unknown-path"

        # -- index scoped --------------------------------------------------
        index_name = segs[0]
        rest = segs[1:]
        if not rest:
            if method == "HEAD":
                if mock.index_exists(index_name):
                    return 200, None, "index-exists %s" % index_name
                return 404, None, "index-missing %s" % index_name
            if method == "GET":
                if index_name in mock.indices:
                    index = mock.indices[index_name]
                    return (
                        200,
                        {
                            index_name: {
                                "aliases": clone(index.get("aliases") or {}),
                                "mappings": clone(index.get("mappings") or {}),
                                "settings": {
                                    "index": {
                                        "creation_date": str(int(index.get("creation_date") or 0)),
                                        "number_of_shards": "1",
                                        "number_of_replicas": "0",
                                        "provided_name": index_name,
                                        "uuid": index.get("uuid"),
                                        "version": {"created": "8130000"},
                                    }
                                },
                            }
                        },
                        "index-info %s" % index_name,
                    )
                return 404, index_not_found(index_name), "index-missing %s" % index_name
            if method == "PUT":
                if "*" in index_name or "," in index_name:
                    return (
                        400,
                        error_payload(
                            "invalid_index_name_exception",
                            "Invalid index name [%s]" % index_name,
                            400,
                            {"index_uuid": "_na_", "index": index_name},
                        ),
                        "create-index %s" % index_name,
                    )
                if not mock.create_index(index_name, body):
                    return (
                        400,
                        error_payload(
                            "resource_already_exists_exception",
                            "index [%s/%s] already exists" % (index_name, index_uuid(index_name)),
                            400,
                            {"index_uuid": index_uuid(index_name), "index": index_name},
                        ),
                        "create-index %s" % index_name,
                    )
                return (
                    200,
                    {"acknowledged": True, "shards_acknowledged": True, "index": index_name},
                    "create-index %s" % index_name,
                )
            if method == "DELETE":
                targets = mock.resolve(index_name)
                if not targets:
                    return 404, index_not_found(index_name), "delete-index %s" % index_name
                for name in targets:
                    mock.delete_index(name)
                return 200, {"acknowledged": True}, "delete-index %s" % index_name
            return 404, no_handler(path, method), "unknown-path"

        action = rest[0]

        if action == "_bulk" and method in ("POST", "PUT"):
            return self._op_bulk(mock, index_name, raw_body)
        if action == "_search" and method in ("GET", "POST"):
            return self._op_search(mock, index_name, body, params)
        if action == "_count" and method in ("GET", "POST"):
            return self._op_count(mock, index_name, body, params)
        if action == "_delete_by_query" and method in ("POST", "GET"):
            return self._op_delete_by_query(mock, index_name, body)
        if action == "_mget" and method in ("GET", "POST"):
            return self._op_mget(mock, index_name, body)
        if action == "_refresh" and method in ("GET", "POST"):
            targets = mock.resolve(index_name)
            return 200, {"_shards": {"total": len(targets), "successful": len(targets), "failed": 0}}, "refresh %s" % index_name
        if action == "_flush" and method in ("GET", "POST"):
            targets = mock.resolve(index_name)
            return 200, {"_shards": {"total": len(targets), "successful": len(targets), "failed": 0}}, "flush %s" % index_name
        if action == "_alias":
            if method in ("GET", "HEAD"):
                if index_name not in mock.indices:
                    return 404, index_not_found(index_name), "get-alias %s" % index_name
                alias_name = rest[1] if len(rest) > 1 else None
                if alias_name and not pattern_matches(alias_name, alias_name):
                    return 404, self._alias_not_found(alias_name), "alias-missing %s" % alias_name
                index = mock.indices[index_name]
                aliases = {
                    name: clone(config)
                    for name, config in (index.get("aliases") or {}).items()
                    if pattern_matches(alias_name or "*", name)
                }
                if alias_name and not aliases:
                    return 404, self._alias_not_found(alias_name), "alias-missing %s" % alias_name
                return (
                    200,
                    {index_name: {"aliases": aliases}},
                    "get-alias %s->%s" % (alias_name or "*", index_name),
                )
            if method in ("PUT", "POST"):
                alias_name = rest[1] if len(rest) > 1 else None
                if not alias_name:
                    return 404, no_handler(path, method), "unknown-path"
                if index_name not in mock.indices:
                    return 404, index_not_found(index_name), "put-alias %s" % alias_name
                mock.set_alias(index_name, alias_name, body)
                return 200, {"acknowledged": True}, "put-alias %s->%s" % (index_name, alias_name)
            if method == "DELETE":
                alias_name = rest[1] if len(rest) > 1 else None
                if index_name not in mock.indices:
                    return 404, index_not_found(index_name), "delete-alias %s" % alias_name
                removed = mock.remove_alias(index_name, alias_name) if alias_name else False
                if not removed:
                    return 404, self._alias_not_found(alias_name), "alias-missing %s" % alias_name
                return 200, {"acknowledged": True}, "delete-alias %s->%s" % (index_name, alias_name)
        if action == "_mapping" and method in ("GET", "PUT", "POST"):
            if index_name not in mock.indices:
                return 404, index_not_found(index_name), "mapping %s" % index_name
            index = mock.indices[index_name]
            if method == "GET":
                return 200, {index_name: {"mappings": clone(index.get("mappings") or {})}}, "get-mapping %s" % index_name
            index["mappings"] = clone(body) if body else index.get("mappings")
            mock.save_locked()
            return 200, {"acknowledged": True}, "put-mapping %s" % index_name
        if action == "_settings" and method in ("GET", "PUT"):
            if index_name not in mock.indices:
                return 404, index_not_found(index_name), "settings %s" % index_name
            if method == "GET":
                return 200, {index_name: {"settings": {"index": {"number_of_shards": "1", "number_of_replicas": "0"}}}}, "get-settings %s" % index_name
            mock.indices[index_name]["settings"] = clone(body) if body else {}
            mock.save_locked()
            return 200, {"acknowledged": True}, "put-settings %s" % index_name

        if action in ("_doc", "_create", "_source", "_update"):
            return self._op_doc(mock, method, index_name, rest, body)

        if action == "_ilm" and len(rest) >= 2 and rest[1] == "explain":
            index = mock.indices.get(index_name)
            if index is None:
                return 404, index_not_found(index_name), "ilm-explain %s" % index_name
            return (
                200,
                {
                    "indices": {
                        index_name: {
                            "index": index_name,
                            "managed": False,
                            "policy": None,
                            "lifecycle_date_millis": int(index.get("creation_date") or 0),
                            "phase": "hot",
                            "action": "complete",
                            "step": "complete",
                        }
                    }
                },
                "ilm-explain %s" % index_name,
            )

        return 404, no_handler(path, method), "unknown-path"

    # -- operations -------------------------------------------------------

    def _alias_not_found(self, alias_name):
        return error_payload(
            "alias_not_found_exception",
            "alias [%s] is missing" % alias_name,
            404,
            {"resource.type": "alias", "resource.id": str(alias_name)},
        )

    def _op_alias_actions(self, body):
        mock = self.server.mock
        actions = (body or {}).get("actions")
        if not isinstance(actions, list) or not actions:
            return (
                400,
                error_payload(
                    "action_request_validation_exception",
                    "Validation Failed: 1: actions is mandatory;",
                    400,
                ),
                "alias-actions-invalid",
            )
        applied = 0
        for action in actions:
            if not isinstance(action, dict) or len(action) != 1:
                return 400, error_payload("illegal_argument_exception", "malformed alias action", 400), "alias-actions-invalid"
            kind, spec = next(iter(action.items()))
            spec = spec or {}
            if kind not in ("add", "remove", "remove_index"):
                return (
                    400,
                    error_payload(
                        "illegal_argument_exception",
                        "unknown alias action [%s]" % kind,
                        400,
                    ),
                    "alias-actions-invalid",
                )
            names = index_names_for(spec.get("index"), mock.indices)
            if not names:
                return 404, index_not_found(spec.get("index")), "alias-actions-missing-index"
            if kind == "remove_index":
                for name in names:
                    mock.delete_index(name)
                applied += 1
                continue
            alias_name = spec.get("alias")
            if not alias_name:
                return (
                    400,
                    error_payload("action_request_validation_exception", "Validation Failed: 1: alias is mandatory;", 400),
                    "alias-actions-invalid",
                )
            for name in names:
                if kind == "add":
                    mock.set_alias(name, alias_name, spec)
                else:
                    mock.remove_alias(name, alias_name)
            applied += 1
        mock.save_locked()
        return 200, {"acknowledged": True}, "alias-actions %d action(s)" % applied

    def _op_get_alias(self, alias_pattern, path):
        mock = self.server.mock
        names = mock.alias_index_names(alias_pattern if alias_pattern else "*")
        if not names:
            if alias_pattern:
                return 404, self._alias_not_found(alias_pattern), "alias-missing %s" % alias_pattern
            return 200, {}, "get-alias *"
        out = {}
        for name in names:
            index = mock.indices[name]
            aliases = {}
            for alias_name, config in (index.get("aliases") or {}).items():
                if pattern_matches(alias_pattern if alias_pattern else "*", alias_name):
                    aliases[alias_name] = clone(config)
            out[name] = {"aliases": aliases}
        return 200, out, "get-alias %s" % (alias_pattern or "*")

    def _op_ilm(self, method, name, body, path):
        mock = self.server.mock
        if name is None:
            if method in ("GET", "HEAD"):
                return 200, {key: clone(value) for key, value in mock.ilm_policies.items()}, "list-ilm-policies"
            return 404, no_handler(path, method), "unknown-path"
        if method == "PUT":
            mock.ilm_policies[name] = clone(body or {})
            mock.save_locked()
            return 200, {"acknowledged": True}, "put-ilm-policy %s" % name
        if method == "DELETE":
            existed = mock.ilm_policies.pop(name, None) is not None
            mock.save_locked()
            if not existed:
                return 404, self._policy_not_found(name), "delete-ilm-policy %s" % name
            return 200, {"acknowledged": True}, "delete-ilm-policy %s" % name
        if method in ("GET", "HEAD"):
            stored = mock.ilm_policies.get(name)
            if stored is None:
                return 404, self._policy_not_found(name), "ilm-missing %s" % name
            return 200, {name: clone(stored)}, "get-ilm-policy %s" % name
        return 404, no_handler(path, method), "unknown-path"

    def _policy_not_found(self, name):
        return error_payload(
            "resource_not_found_exception",
            "Lifecycle policy not found: %s" % name,
            404,
            {"resource.type": "lifecycle_policy", "resource.id": str(name)},
        )

    def _op_template(self, method, store, label, name, body, path, legacy=False):
        mock = self.server.mock
        if name is None:
            if method in ("GET", "HEAD"):
                if legacy:
                    return 200, {key: clone(value) for key, value in store.items()}, "list-templates"
                return (
                    200,
                    {"index_templates": [{"name": key, "index_template": clone(value)} for key, value in sorted(store.items())]},
                    "list-index-templates",
                )
            return 404, no_handler(path, method), "unknown-path"
        if method == "PUT":
            store[name] = clone(body or {})
            mock.save_locked()
            return 200, {"acknowledged": True}, "put-%s %s" % (label, name)
        if method == "DELETE":
            existed = store.pop(name, None) is not None
            mock.save_locked()
            if not existed:
                return 404, self._template_not_found(label, name), "delete-%s %s" % (label, name)
            return 200, {"acknowledged": True}, "delete-%s %s" % (label, name)
        if method in ("GET", "HEAD"):
            stored = store.get(name)
            if stored is None:
                return 404, self._template_not_found(label, name), "%s-missing %s" % (label, name)
            if legacy:
                return 200, {name: clone(stored)}, "get-%s %s" % (label, name)
            return (
                200,
                {"index_templates": [{"name": name, "index_template": clone(stored)}]},
                "get-%s %s" % (label, name),
            )
        return 404, no_handler(path, method), "unknown-path"

    def _template_not_found(self, label, name):
        return error_payload(
            "resource_not_found_exception",
            "%s [%s] missing" % (label, name),
            404,
            {"resource.type": label, "resource.id": str(name)},
        )

    def _op_bulk(self, mock, default_index, raw_body):
        actions = mock.parse_bulk(raw_body)
        items = []
        errors = False
        started = time.time()
        for op, meta, document in actions:
            index_name = meta.get("_index") or default_index
            if not index_name:
                return (
                    400,
                    error_payload(
                        "action_request_validation_exception",
                        "Validation Failed: 1: index is missing;",
                        400,
                    ),
                    "bulk-invalid",
                )
            index_name = str(index_name)
            doc_id = meta.get("_id")
            index_name = mock.resolve_write_target(index_name)
            if index_name not in mock.indices:
                if op in ("create", "index", "update") and mock.auto_create_index:
                    mock.create_index(index_name, {}, auto=True)
                else:
                    errors = True
                    items.append(
                        {
                            op: {
                                "_index": index_name,
                                "_id": str_id(doc_id),
                                "status": 404,
                                "error": index_not_found(index_name)["error"],
                            }
                        }
                    )
                    continue
            if op == "delete":
                result = mock.delete_doc(index_name, doc_id)
                entry = {
                    "_index": index_name,
                    "_id": result.get("doc_id"),
                    "_version": result.get("version", 1),
                    "result": "deleted" if result.get("found") else "not_found",
                    "_shards": {"total": 1, "successful": 1, "failed": 0},
                    "_seq_no": 0,
                    "_primary_term": 1,
                    "status": 200 if result.get("found") else 404,
                }
                if not result.get("found"):
                    errors = True
                items.append({"delete": entry})
                continue
            if op == "update":
                payload = document if isinstance(document, dict) else {}
                partial = payload.get("doc") or {}
                upsert = payload.get("upsert")
                result = mock.update_doc(
                    index_name,
                    doc_id,
                    partial,
                    upsert=upsert,
                    doc_as_upsert=bool(payload.get("doc_as_upsert")),
                )
                if result.get("missing_index"):
                    errors = True
                    items.append(
                        {
                            "update": {
                                "_index": index_name,
                                "_id": str_id(doc_id),
                                "status": 404,
                                "error": index_not_found(index_name)["error"],
                            }
                        }
                    )
                    continue
                if not result.get("found") and not result.get("updated"):
                    errors = True
                    items.append(
                        {
                            "update": {
                                "_index": index_name,
                                "_id": result.get("doc_id"),
                                "status": 404,
                                "error": {
                                    "type": "document_missing_exception",
                                    "reason": "[%s]: document missing" % result.get("doc_id"),
                                    "index_uuid": index_uuid(index_name),
                                    "shard": "0",
                                    "index": index_name,
                                },
                            }
                        }
                    )
                    continue
                items.append(
                    {
                        "update": {
                            "_index": index_name,
                            "_id": result.get("doc_id"),
                            "_version": result.get("version", 1),
                            "result": "updated",
                            "_shards": {"total": 1, "successful": 1, "failed": 0},
                            "_seq_no": result.get("seq") or 0,
                            "_primary_term": 1,
                            "status": 200,
                        }
                    }
                )
                continue
            result = mock.put_doc(index_name, doc_id, document, create_only=(op == "create"))
            if result.get("missing_index"):
                errors = True
                items.append(
                    {
                        op: {
                            "_index": index_name,
                            "_id": str_id(doc_id),
                            "status": 404,
                            "error": index_not_found(index_name)["error"],
                        }
                    }
                )
                continue
            if result.get("conflict"):
                errors = True
                items.append(
                    {
                        "create": {
                            "_index": index_name,
                            "_id": result.get("doc_id"),
                            "status": 409,
                            "error": {
                                "type": "version_conflict_engine_exception",
                                "reason": "[%s]: version conflict, document already exists (current version [%s])"
                                % (result.get("doc_id"), result.get("current_version", 1)),
                                "index_uuid": index_uuid(index_name),
                                "shard": "0",
                                "index": index_name,
                            },
                        }
                    }
                )
                continue
            created = bool(result.get("created"))
            items.append(
                {
                    op: {
                        "_index": index_name,
                        "_id": result.get("doc_id"),
                        "_version": result.get("version", 1),
                        "result": "created" if created else "updated",
                        "_shards": {"total": 1, "successful": 1, "failed": 0},
                        "_seq_no": result.get("seq") or 0,
                        "_primary_term": 1,
                        "status": 201 if created else 200,
                    }
                }
            )
        mock.save_locked()
        took = max(1, int((time.time() - started) * 1000.0))
        summary = "bulk %d action(s)%s" % (len(actions), " with errors" if errors else "")
        return 200, {"took": took, "errors": errors, "items": items}, summary

    def _op_search(self, mock, pattern, body, params):
        started = time.time()
        body = body or {}
        inline = param(params, "q")
        if inline is not None:
            body = dict(body)
            body["query"] = {"query_string": {"query": inline}}
        validate_query(body.get("query"))
        validate_query(body.get("post_filter"))
        docs = mock.collect_docs(pattern)
        matched = self._filter_docs(docs, body.get("query"))
        total_hits = len(matched)
        sort_specs = normalize_sort(body.get("sort"))
        if not sort_specs:
            sort_specs = normalize_sort(param(params, "sort"))
        if sort_specs:
            matched = sort_docs(matched, sort_specs)
        size = to_int(body.get("size", param_int(params, "size", DEFAULT_SEARCH_SIZE)), DEFAULT_SEARCH_SIZE)
        from_ = to_int(body.get("from", param_int(params, "from", 0)), 0)
        if from_ < 0:
            from_ = 0
        if size < 0:
            page = matched[from_:]
        else:
            page = matched[from_:from_ + size]
        post_filter = body.get("post_filter")
        if post_filter:
            page = [doc for doc in page if q_match(doc, post_filter)]
        source_filter = body.get("_source")
        if source_filter is None:
            source_param = param(params, "_source")
            if source_param is not None:
                source_filter = source_param
        hits = []
        for doc in page:
            hit = {
                "_index": doc["_index"],
                "_id": doc["_id"],
                "_score": None,
                "_source": project_source(doc.get("source"), source_filter),
            }
            if sort_specs:
                hit["sort"] = [sort_value(doc, field) for field, _, _ in sort_specs]
            hits.append(hit)
        total = self._total_hits(body, params, total_hits, len(hits))
        response = {
            "took": max(0, int((time.time() - started) * 1000.0)),
            "timed_out": False,
            "_shards": {"total": 1, "successful": 1, "skipped": 0, "failed": 0},
            "hits": {
                "total": total,
                "max_score": None,
                "hits": hits,
            },
        }
        aggs_spec = body.get("aggs") or body.get("aggregations")
        if aggs_spec:
            response["aggregations"] = run_aggs(aggs_spec, matched)
        return 200, response, "search %s [%d hit(s)]" % (pattern or "_all", total_hits)

    def _total_hits(self, body, params, total_hits, returned):
        rest_as_int = param_bool(params, "rest_total_hits_as_int", False) or body.get(
            "rest_total_hits_as_int"
        )
        value = body.get("track_total_hits")
        if value is None:
            raw_param = param(params, "track_total_hits")
            if raw_param is not None:
                value = raw_param
        if isinstance(value, str):
            lowered = value.strip().lower()
            if lowered in ("true", ""):
                value = True
            elif lowered == "false":
                value = False
            else:
                value = to_int(lowered, None)
        if isinstance(value, bool):
            tracked, relation = total_hits, "eq"
        elif isinstance(value, int):
            if total_hits > value:
                tracked, relation = value, "gte"
            else:
                tracked, relation = total_hits, "eq"
        else:
            tracked, relation = total_hits, "eq"
        if rest_as_int:
            return tracked
        return {"value": tracked, "relation": relation}

    def _filter_docs(self, docs, query):
        return [doc for doc in docs if q_match(doc, query)]

    def _op_count(self, mock, pattern, body, params):
        body = body or {}
        inline = param(params, "q")
        query = body.get("query")
        if inline is not None:
            query = {"query_string": {"query": inline}}
        validate_query(query)
        docs = mock.collect_docs(pattern)
        matched = self._filter_docs(docs, query)
        return (
            200,
            {"count": len(matched), "_shards": {"total": 1, "successful": 1, "failed": 0}},
            "count %s [%d]" % (pattern or "_all", len(matched)),
        )

    def _op_delete_by_query(self, mock, pattern, body):
        started = time.time()
        body = body or {}
        validate_query(body.get("query"))
        docs = mock.collect_docs(pattern)
        matched = self._filter_docs(docs, body.get("query"))
        deleted = 0
        for doc in matched:
            index = mock.indices.get(doc["_index"])
            if index is None:
                continue
            if str_id(doc["_id"]) in (index.get("docs") or {}):
                del index["docs"][str_id(doc["_id"])]
                deleted += 1
        mock.save_locked()
        response = {
            "took": max(0, int((time.time() - started) * 1000.0)),
            "timed_out": False,
            "total": len(matched),
            "deleted": deleted,
            "batches": 1 if matched else 0,
            "version_conflicts": 0,
            "noops": 0,
            "retries": {"bulk": 0, "search": 0},
            "throttled_millis": 0,
            "requests_per_second": -1.0,
            "throttled_until_millis": 0,
            "failures": [],
        }
        return 200, response, "delete-by-query %s [%d]" % (pattern or "_all", deleted)

    def _op_mget(self, mock, pattern, body):
        body = body or {}
        docs = []
        if "docs" in body and body.get("docs") is not None:
            for item in as_list(body.get("docs")):
                if not isinstance(item, dict):
                    continue
                index_name = item.get("_index") or pattern
                doc_id = item.get("_id")
                docs.append(self._mget_entry(mock, index_name, doc_id))
        else:
            for doc_id in as_list(body.get("ids")):
                docs.append(self._mget_entry(mock, pattern, doc_id))
        return 200, {"docs": docs}, "mget %d doc(s)" % len(docs)

    def _mget_entry(self, mock, index_name, doc_id):
        if not index_name:
            return {
                "_index": None,
                "_id": str_id(doc_id),
                "error": {
                    "type": "illegal_argument_exception",
                    "reason": "index is missing",
                },
            }
        index_name = str(index_name)
        resolved = index_names_for(index_name, mock.indices)
        target = resolved[0] if resolved else index_name
        entry = mock.get_doc(target, doc_id)
        if entry is None:
            return {"_index": target, "_id": str_id(doc_id), "found": False}
        return {
            "_index": target,
            "_id": str_id(doc_id),
            "_version": entry.get("version", 1),
            "_seq_no": entry.get("seq", 0),
            "_primary_term": 1,
            "found": True,
            "_source": clone(entry.get("source")),
        }

    def _op_doc(self, mock, method, index_name, rest, body):
        action = rest[0]
        doc_id = rest[1] if len(rest) > 1 and rest[1] != "" else None
        # Writing through a write alias must land in its backing index, never in an index that
        # happens to be named after the alias.
        index_name = mock.resolve_write_target(index_name)
        if index_name not in mock.indices:
            if method in ("PUT", "POST") and mock.auto_create_index and action in ("_doc", "_create", "_update"):
                mock.create_index(index_name, {}, auto=True)
            else:
                return 404, index_not_found(index_name), "%s %s" % (action.lstrip("_"), index_name)
        if action == "_doc":
            if method in ("PUT", "POST"):
                result = mock.put_doc(index_name, doc_id, body, create_only=False)
                if result.get("missing_index"):
                    return 404, index_not_found(index_name), "index-doc %s" % index_name
                mock.save_locked()
                created = bool(result.get("created"))
                payload = {
                    "_index": index_name,
                    "_id": result.get("doc_id"),
                    "_version": result.get("version", 1),
                    "result": "created" if created else "updated",
                    "_shards": {"total": 1, "successful": 1, "failed": 0},
                    "_seq_no": result.get("seq") or 0,
                    "_primary_term": 1,
                }
                return (201 if created else 200), payload, "index-doc %s/%s" % (index_name, result.get("doc_id"))
            if method in ("GET", "HEAD"):
                entry = mock.get_doc(index_name, doc_id)
                if entry is None:
                    return 404, {"_index": index_name, "_id": str_id(doc_id), "found": False}, "get-doc %s/%s" % (index_name, doc_id)
                return (
                    200,
                    {
                        "_index": index_name,
                        "_id": str_id(doc_id),
                        "_version": entry.get("version", 1),
                        "_seq_no": entry.get("seq", 0),
                        "_primary_term": 1,
                        "found": True,
                        "_source": clone(entry.get("source")),
                    },
                    "get-doc %s/%s" % (index_name, doc_id),
                )
            if method == "DELETE":
                result = mock.delete_doc(index_name, doc_id)
                payload = {
                    "_index": index_name,
                    "_id": result.get("doc_id"),
                    "_version": result.get("version", 1),
                    "result": "deleted" if result.get("found") else "not_found",
                    "_shards": {"total": 1, "successful": 1, "failed": 0},
                    "_seq_no": 0,
                    "_primary_term": 1,
                }
                return (200 if result.get("found") else 404), payload, "delete-doc %s/%s" % (index_name, doc_id)
        if action == "_create" and method in ("PUT", "POST"):
            result = mock.put_doc(index_name, doc_id, body, create_only=True)
            if result.get("conflict"):
                return (
                    409,
                    error_payload(
                        "version_conflict_engine_exception",
                        "[%s]: version conflict, document already exists (current version [%s])"
                        % (result.get("doc_id"), result.get("current_version", 1)),
                        409,
                        {"index_uuid": index_uuid(index_name), "shard": "0", "index": index_name},
                    ),
                    "create-doc %s/%s" % (index_name, doc_id),
                )
            mock.save_locked()
            return (
                201,
                {
                    "_index": index_name,
                    "_id": result.get("doc_id"),
                    "_version": result.get("version", 1),
                    "result": "created",
                    "_shards": {"total": 1, "successful": 1, "failed": 0},
                    "_seq_no": result.get("seq") or 0,
                    "_primary_term": 1,
                },
                "create-doc %s/%s" % (index_name, result.get("doc_id")),
            )
        if action == "_update" and method in ("POST", "PUT"):
            payload = body or {}
            result = mock.update_doc(
                index_name,
                doc_id,
                payload.get("doc") or {},
                upsert=payload.get("upsert"),
                doc_as_upsert=bool(payload.get("doc_as_upsert")),
            )
            if not result.get("found") and not result.get("updated"):
                return (
                    404,
                    error_payload(
                        "document_missing_exception",
                        "[%s]: document missing" % result.get("doc_id"),
                        404,
                        {"index_uuid": index_uuid(index_name), "shard": "0", "index": index_name},
                    ),
                    "update-doc %s/%s" % (index_name, doc_id),
                )
            mock.save_locked()
            return (
                200,
                {
                    "_index": index_name,
                    "_id": result.get("doc_id"),
                    "_version": result.get("version", 1),
                    "result": "updated" if result.get("updated") else "created",
                    "_shards": {"total": 1, "successful": 1, "failed": 0},
                    "_seq_no": result.get("seq") or 0,
                    "_primary_term": 1,
                },
                "update-doc %s/%s" % (index_name, doc_id),
            )
        if action == "_source" and method in ("GET", "HEAD"):
            entry = mock.get_doc(index_name, doc_id)
            if entry is None:
                return 404, {"_index": index_name, "_id": str_id(doc_id), "found": False}, "get-source %s/%s" % (index_name, doc_id)
            return 200, clone(entry.get("source")), "get-source %s/%s" % (index_name, doc_id)
        return 404, no_handler("/%s/%s" % (index_name, "/".join(rest)), method), "unknown-path"


def project_source(source, source_filter):
    if source_filter is None or source_filter is True:
        return clone(source)
    if source_filter is False:
        return None
    if isinstance(source_filter, str):
        if source_filter.strip().lower() in ("true", ""):
            return clone(source)
        if source_filter.strip().lower() == "false":
            return None
        fields = [item.strip() for item in source_filter.split(",") if item.strip()]
    else:
        fields = [str(item) for item in as_list(source_filter)]
    if isinstance(source, dict):
        out = {}
        for field in fields:
            values = get_path(source, field)
            if values:
                out[field] = values[0] if len(values) == 1 else values
        return out
    return source


def try_json(raw_body):
    if raw_body is None:
        return {}
    if isinstance(raw_body, bytes):
        if not raw_body.strip():
            return {}
        try:
            text = raw_body.decode("utf-8")
        except UnicodeDecodeError:
            return None
    else:
        text = str(raw_body)
        if not text.strip():
            return {}
    try:
        return json.loads(text)
    except ValueError:
        return None


def body_is_interesting(method, path):
    """Paths whose full body is copied into the request log (harness assertions)."""
    segs = segments_of(path)
    if method == "POST" and segs == ["_aliases"]:
        return True
    if method == "PUT":
        if len(segs) == 2 and segs[0] in ("_index_template", "_template"):
            return True
        if len(segs) == 3 and segs[0] == "_ilm" and segs[1] == "policy":
            return True
        if len(segs) == 1 and not segs[0].startswith("_"):
            return True
    return False


def is_ndjson_path(path):
    """Bulk endpoints take newline-delimited JSON, not a single JSON document."""
    segs = segments_of(path)
    return bool(segs) and segs[-1] == "_bulk"


# ---------------------------------------------------------------------------
# startup / CLI
# ---------------------------------------------------------------------------

EXIT_BIND_FAILED = 3


def port_in_use(host, port):
    """True when something is already listening on host:port (fail fast check)."""
    probe = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            try:
                probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            except OSError:
                pass
        else:
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 0)
        probe.settimeout(3.0)
        probe.bind((host, port))
        return False
    except OSError as exc:
        code = getattr(exc, "errno", None)
        winerror = getattr(exc, "winerror", None)
        if code in (errno.EADDRINUSE, 10048, 10013) or winerror in (10048, 10013, 10049):
            return True
        raise
    finally:
        try:
            probe.close()
        except OSError:
            pass


def fail_bind(host, port, exc=None):
    detail = ""
    if exc is not None:
        winerror = getattr(exc, "winerror", None)
        detail = " (%s%s)" % (
            exc.strerror or exc.__class__.__name__,
            ", winerror %s" % winerror if winerror else "",
        )
    sys.stderr.write(
        "mock-elasticsearch: ERROR: %s:%s is already in use%s.\n"
        "  Something else is listening there (a previous mock-elasticsearch run, or another service).\n"
        "  Start this mock on a free port (e.g. --port %s), use --port 0 to let the OS pick one,\n"
        "  or stop the process that owns the port.\n"
        % (host, port, detail, port + 1 if port < 65535 else port - 1)
    )
    sys.stderr.flush()


def parse_args(argv):
    parser = argparse.ArgumentParser(
        prog="mock_elasticsearch.py",
        description="Mock Elasticsearch 8.x server (stdlib only) for fusetor.com end-to-end runs.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Examples:\n"
            "  python mock_elasticsearch.py --port 9210\n"
            "  python mock_elasticsearch.py --port 0 --log ./requests.jsonl   # prints the chosen port\n"
            "  python mock_elasticsearch.py --require-auth elastic:changeme\n"
            "  python mock_elasticsearch.py --state ./mock-es-state.json\n"
        ),
    )
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help="TCP port (default %(default)s, 0 = pick a free one)")
    parser.add_argument("--host", default=DEFAULT_HOST, help="bind address (default %(default)s)")
    parser.add_argument(
        "--log",
        default=DEFAULT_LOG_FILE,
        help="request log path, JSONL (default %(default)s, relative to the working directory)",
    )
    parser.add_argument(
        "--state",
        default=None,
        help="optional JSON state file: loaded on start, written on every state change and on exit",
    )
    parser.add_argument(
        "--require-auth",
        default=None,
        metavar="USER:PASS",
        help="require HTTP Basic credentials; anything else (including ApiKey) gets 401",
    )
    parser.add_argument("--quiet", action="store_true", help="no banner, no per-request stdout lines")
    parser.add_argument(
        "--no-auto-create-index",
        action="store_true",
        help="do not auto-create missing indices on index/bulk writes (default: create, like ES)",
    )
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(sys.argv[1:] if argv is None else argv)

    require_auth = None
    if args.require_auth:
        user, separator, password = args.require_auth.partition(":")
        if not separator or not user:
            sys.stderr.write(
                "mock-elasticsearch: ERROR: --require-auth expects USER:PASS (got %r)\n" % args.require_auth
            )
            return 2
        require_auth = (user, password)

    if args.port < 0 or args.port > 65535:
        sys.stderr.write("mock-elasticsearch: ERROR: --port must be between 0 and 65535\n")
        return 2

    if args.port != 0:
        try:
            in_use = port_in_use(args.host, args.port)
        except OSError as exc:
            sys.stderr.write(
                "mock-elasticsearch: ERROR: cannot bind %s:%s: %s\n" % (args.host, args.port, exc)
            )
            return EXIT_BIND_FAILED
        if in_use:
            fail_bind(args.host, args.port)
            return EXIT_BIND_FAILED

    mock = MockElasticsearch(state_path=args.state)
    if args.no_auto_create_index:
        mock.auto_create_index = False
    loaded = mock.load()

    log_path = os.path.abspath(args.log)

    try:
        server = EsHttpServer(
            (args.host, args.port),
            EsRequestHandler,
            mock,
            log_path,
            threading.Lock(),
            require_auth=require_auth,
            quiet=args.quiet,
        )
    except OSError as exc:
        if getattr(exc, "errno", None) in (errno.EADDRINUSE, 10048) or getattr(exc, "winerror", None) in (10048, 10013):
            fail_bind(args.host, args.port, exc)
            return EXIT_BIND_FAILED
        sys.stderr.write("mock-elasticsearch: ERROR: cannot listen on %s:%s: %s\n" % (args.host, args.port, exc))
        return EXIT_BIND_FAILED

    bound_host, bound_port = server.server_address[0], server.server_address[1]
    if not args.quiet:
        print("=" * 78)
        print("mock-elasticsearch %s (cluster %r) -- Elasticsearch 8.x stand-in" % (ES_VERSION, CLUSTER_NAME))
        print("  listening : http://%s:%s" % (bound_host, bound_port))
        print("  log       : %s" % log_path)
        print("  state     : %s" % (args.state or "(memory only)"))
        if require_auth is None:
            print("  auth      : not required (Basic/ApiKey headers accepted and recorded)")
        else:
            print("  auth      : HTTP Basic required (user %r); anything else -> 401" % require_auth[0])
        print("  auto-cre  : %s" % ("on (ES default)" if mock.auto_create_index else "off"))
        print("  loaded    : %s" % (", ".join(loaded) if loaded else "no persisted indices"))
        print("  headers   : %s: %s on every response" % (PRODUCT_HEADER, PRODUCT_VALUE))
        print("=" * 78)
    # Always printed (even with --quiet) so a harness can discover an ephemeral --port 0 bind.
    print("mock-elasticsearch listening on http://%s:%s" % (bound_host, bound_port))
    sys.stdout.flush()

    stopping = {"flag": False}

    def handle_signal(signum, frame):
        if stopping["flag"]:
            return
        stopping["flag"] = True
        raise SystemExit(0)

    for signal_name in ("SIGINT", "SIGTERM"):
        if hasattr(signal, signal_name):
            try:
                signal.signal(getattr(signal, signal_name), handle_signal)
            except (ValueError, OSError):
                pass

    try:
        server.serve_forever(poll_interval=0.2)
    except (KeyboardInterrupt, SystemExit):
        pass
    finally:
        with mock.lock:
            mock.save_locked()
        try:
            server.server_close()
        except OSError:
            pass
        if not args.quiet:
            print("mock-elasticsearch: stopped (port %s)" % bound_port)
    return 0


if __name__ == "__main__":
    sys.exit(main())
