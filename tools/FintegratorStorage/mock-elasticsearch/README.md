# mock-elasticsearch

A **mock Elasticsearch 8.x server** (Python standard library only, no `pip install`) that stands in
for a real cluster while verifying the optional Elasticsearch backend of fusetor.com end to end.

It answers the subset of the Elasticsearch REST API the product uses, behaves like ES 8.13 on those
paths, and **records every request to a JSONL log** so the harness can assert exactly what the
product asked for (endpoint, query string, status, auth scheme, body bytes, and the full body of
template / ILM policy / alias / index-creation requests).

```
mock-elasticsearch/
  mock_elasticsearch.py   the server (single file, stdlib only)
  selftest.py             end-to-end self-test over real HTTP (starts/stops the mock itself)
  README.md               this file
```

---

## 1. Run it

```powershell
# default: http://127.0.0.1:9210, log written to <cwd>\mock-elasticsearch-requests.jsonl
python tools\FintegratorStorage\mock-elasticsearch\mock_elasticsearch.py

# pick a log file, keep the state, require Basic auth
python tools\FintegratorStorage\mock-elasticsearch\mock_elasticsearch.py `
    --port 9210 --log C:\tmp\es-requests.jsonl --state C:\tmp\es-state.json `
    --require-auth elastic:changeme

# let the OS choose a free port: the banner prints the real one, the selftest greps it
python tools\FintegratorStorage\mock-elasticsearch\mock_elasticsearch.py --port 0
```

Startup banner (always on unless `--quiet`; the one machine-readable `listening on ...` line is
printed even with `--quiet`, so `--port 0` is usable from scripts):

```
==============================================================================
mock-elasticsearch 8.13.0 (cluster 'es-mock-cluster') -- Elasticsearch 8.x stand-in
  listening : http://127.0.0.1:9210
  log       : C:\...\mock-elasticsearch-requests.jsonl
  state     : (memory only)
  auth      : not required (Basic/ApiKey headers accepted and recorded)
  auto-cre  : on (ES default)
  loaded    : no persisted indices
  headers   : X-Elastic-Product: Elasticsearch on every response
==============================================================================
mock-elasticsearch listening on http://127.0.0.1:9210
```

Every request is also printed to stdout (one line per request) unless `--quiet`:

```
[mock-es] PUT     200 /ft-index-a  -> create-index ft-index-a (0.412 ms)
[mock-es] POST    200 /_bulk  -> bulk 42 action(s) (3.1 ms)
```

### CLI

| flag | default | meaning |
|---|---|---|
| `--port` | `9210` | TCP port; `0` = pick a free one (the banner prints it) |
| `--host` | `127.0.0.1` | bind address |
| `--log` | `mock-elasticsearch-requests.jsonl` | request log (JSONL, relative to the working directory) |
| `--state` | *(none)* | persist indices/docs/aliases/policies/templates to this JSON file; loaded at start, rewritten on every state change and on exit |
| `--require-auth USER:PASS` | *(none)* | require HTTP Basic credentials; wrong/missing (and ApiKey) get `401` |
| `--quiet` | off | no banner, no per-request stdout lines |
| `--no-auto-create-index` | off | do not create missing indices on writes (see §7) |

**Fail fast on a busy port.** If something already listens on `--host:--port` the process prints a
clear message to stderr and exits with code `3` *before* touching the log file:

```
mock-elasticsearch: ERROR: 127.0.0.1:9210 is already in use (Address already in use, winerror 10048).
  Something else is listening there (a previous mock-elasticsearch run, or another service).
  Start this mock on a free port (e.g. --port 9211), use --port 0 to let the OS pick one,
  or stop the process that owns the port.
```

The pre-flight probe uses `SO_EXCLUSIVEADDRUSE` on Windows (and a plain bind elsewhere), so a
second mock on the same port can never silently steal traffic.

### Self-test

```powershell
python tools\FintegratorStorage\mock-elasticsearch\selftest.py
```

It starts the mock on free ports, exercises every endpoint below over real HTTP (`urllib`, no
in-process shortcuts), asserts responses **and** the request log, restarts the server with
`--require-auth` (401 without credentials, 200 with them) and with `--state` (state survives a
restart), checks the busy-port failure, and prints `N/M checks passed`, exiting non-zero if
anything failed.

---

## 2. What it implements

`X-Elastic-Product: Elasticsearch` is sent on **every** response, including `404`s and `401`s —
the official 8.x clients refuse to talk to a server that does not send it.

### Required surface (the contract the harness asserts)

| Method | Path | Behaviour |
|---|---|---|
| `GET` | `/` | cluster info: `name=es-mock`, `cluster_name=es-mock-cluster`, `version.number=8.13.0`, `version.build_flavor=default`, `version.lucene_version=9.10.0`, `tagline=You Know, for Search` |
| `GET`/`HEAD` | `/_cluster/health` | `status=green`, 1 node, `active_primary_shards == active_shards == number of indices` (one shard per index) |
| `HEAD` | `/<index>` | `200` if the index (or an alias with that name) exists, `404` otherwise, no body |
| `PUT` | `/<index>` | create index; `settings`/`mappings`/`aliases` stored verbatim (alias config may carry `is_write_index`); `{"acknowledged":true,"shards_acknowledged":true,"index":"<index>"}`; `400 resource_already_exists_exception` when it exists |
| `DELETE` | `/<index>` | deletes the index and every alias pointing at it, `{"acknowledged":true}`; `404 index_not_found_exception` when missing; wildcards/`a,b` work |
| `POST` | `/_aliases` | `{"actions":[{"add":{...}},{"remove":{...}}]}`; maintains alias → indices plus which index is the alias' write index; `{"acknowledged":true}` |
| `GET` | `/_alias/<name>` | `{"<index>":{"aliases":{"<name>":{...}}}}` for the indices carrying the alias (`is_write_index` reported per index); `404` (`alias_not_found_exception`) when nothing carries it; `/_alias` returns all |
| `PUT` | `/_ilm/policy/<name>` | stores `{"policy":{...}}` verbatim, `{"acknowledged":true}` |
| `GET`/`HEAD` | `/_ilm/policy/<name>` | `{"<name>": <body>}` / `200`; `404 resource_not_found_exception` otherwise |
| `PUT` | `/_index_template/<name>` | stores the composable template verbatim (`index_patterns`, `priority`, `template`, `_meta`), `{"acknowledged":true}` |
| `GET`/`HEAD` | `/_index_template/<name>` | `{"index_templates":[{"name":"<name>","index_template":<body>}]}` / `200`; `404 resource_not_found_exception` otherwise |
| `GET` | `/_cat/indices/<pattern>?format=json&bytes=b&h=...` | JSON rows with `index`, `health`, `status`, `docs.count`, `store.size`, `creation.date` (epoch millis); `*`/`?` and `a,b` patterns; `h=` selects/orders columns |
| `POST` | `/_bulk`, `/<index>/_bulk` | NDJSON `create`/`index`/`delete` (+ `update`); target index from the meta line or the URL; missing `_id` is generated; `create` on an existing `_id` → **per-item** `409 version_conflict_engine_exception`; `index` overwrites; response is `{"took","errors","items":[...]}`, always HTTP `200` |
| `POST`/`GET` | `/<pattern>/_search` | query DSL: `match_all`, `bool` (`must`/`filter`/`should`/`must_not`, `minimum_should_match`), `term`, `terms` (array or `{"value":[...]}`), `range` (`gte`/`gt`/`lte`/`lt` on numbers, ISO dates and epoch millis), `exists`, `query_string` (`*`/`?` wildcards, plain terms, `field:value`, `fields`, `default_operator`, `AND`/`OR`/`NOT`), `ids`; plus `sort`, `size`, `from`, `track_total_hits`, `_source`, `post_filter` and a `terms` aggregation; missing indices behave like `allow_no_indices` (empty result, no error) |
| `POST`/`GET` | `/<pattern>/_count` | optional `{"query":{...}}`, `{"count":N,"_shards":{"total":1,"successful":1,"failed":0}}` |
| `POST` | `/<pattern>/_delete_by_query` | `{"deleted":N}` plus the full ES envelope (`total`, `batches`, `failures`, …); `?refresh=true` accepted |
| `POST` | `/<pattern>/_mget`, `POST`/`GET` `/_mget` | `{"ids":[...]}` or `{"docs":[{"_index","_id"}]}` → `{"docs":[found+_source | found:false]}` |

Unknown paths answer `404` with `{"error":{"type":"...","reason":"..."},"status":404}`.

### Extensions (not required by the product contract, present because real clients ask for them)

* single-document CRUD: `PUT|POST /<index>/_doc/<id>`, `POST /<index>/_doc`, `PUT|POST /<index>/_create/<id>`,
  `GET|HEAD /<index>/_doc/<id>`, `DELETE /<index>/_doc/<id>`, `POST /<index>/_update/<id>`, `GET /<index>/_source/<id>`
* `GET /<index>` (settings/mappings/aliases), `GET|PUT /<index>/_mapping`, `GET|PUT /<index>/_settings`
* `POST|GET /<index>/_refresh`, `POST|GET /<index>/_flush`, `POST /_refresh`
* `GET|HEAD /_nodes` (single-node info, useful for client sniffing)
* `GET /_cat/aliases`, `GET /_cat/health`
* legacy `/_template/<name>` (PUT/GET/HEAD/DELETE), `GET|DELETE /_ilm/policy`, `GET|DELETE /_index_template`
* `PUT|POST|DELETE /<index>/_alias/<name>`, `POST|GET /<index>/_ilm/explain` (always `managed:false`)
* aggregations beyond `terms`: `value_count`, `cardinality`, `sum`, `avg`, `min`, `max`, `filter`,
  nested sub-aggregations, `size`/`order`/`missing`/`min_doc_count` on terms
* query clauses beyond the contract: `match`, `match_phrase`, `match_phrase_prefix`, `multi_match`,
  `prefix`, `wildcard`, `regexp`, `constant_score`, `boosting`, `match_none`, `simple_query_string`
* `q=` URI search, `sort=field:desc` and `size`/`from`/`track_total_hits` query parameters,
  `rest_total_hits_as_int=true`, `_source=false|<field list>`

---

## 3. Request log (how the harness should read it)

One JSON object per line, appended (and flushed) per request, in completion order:

```json
{"at":"2026-10-16T09:12:44.812Z","method":"PUT","path":"/_index_template/fusetor-logs","query":"","status":200,"auth":"basic","body_bytes":612,"summary":"put-index_template fusetor-logs","duration_ms":0.44,"body":"{\"index_patterns\":[\"fusetor-logs-*\"],...}","body_json":{"index_patterns":["fusetor-logs-*"]}}
```

| field | meaning |
|---|---|
| `at` | UTC ISO-8601 with milliseconds (`...Z`) |
| `method` | HTTP method as sent (`GET`, `HEAD`, `POST`, …) |
| `path` | request path, percent-decoded, **without** the query string |
| `query` | raw query string as received (no leading `?`); `""` when absent |
| `status` | HTTP status the mock answered with (after auth) |
| `auth` | `basic` \| `apikey` \| `none` — which `Authorization` scheme the product used (`none` for missing or any other scheme, e.g. `Bearer`) |
| `body_bytes` | size of the raw request body in bytes (`0` for `GET`/`HEAD`) |
| `summary` | short human/tool-friendly description, see below |
| `duration_ms` | time spent producing the response (ms, 3 decimals) |
| `body` | **only** for the interesting paths below: the raw request body as a string |
| `body_json` | same request body parsed (`null` omitted when it is not valid JSON) |
| `body_truncated` | `true` when a body exceeded 32 KiB and `body` was cut (very unusual) |

Bodies **are** recorded for (everything else stores only `body_bytes`):

* `PUT /_index_template/<name>` and `PUT /_template/<name>`
* `PUT /_ilm/policy/<name>`
* `POST /_aliases`
* `PUT /<index>` (index creation, incl. `settings`/`mappings`/`aliases`)

`body` is the verbatim text (`json.loads(entry["body"])` round-trips) while `body_json` saves you
one parse — use either:

```powershell
# what template did the product send?
python -c "import json;print([json.loads(l)['body_json'] for l in open('C:/tmp/es-requests.jsonl',encoding='utf-8') if json.loads(l)['path']=='/_index_template/fusetor-logs'][-1])"
```

```python
# pytest-ish assertions
import json
log = [json.loads(line) for line in open("mock-elasticsearch-requests.jsonl", encoding="utf-8")]
assert any(e["method"] == "PUT" and e["path"] == "/_index_template/fusetor-logs" for e in log)
bulk = [e for e in log if e["path"].endswith("/_bulk")][0]
assert bulk["status"] == 200 and bulk["body_bytes"] > 0 and "body" not in bulk
assert [e["auth"] for e in log if e["path"] == "/"] == ["basic"]
```

`summary` values the harness can match on:

| summary | request |
|---|---|
| `cluster-info`, `cluster-health`, `nodes-info` | `GET /`, `GET /_cluster/health`, `GET /_nodes` |
| `create-index <name>`, `delete-index <name>`, `index-info <name>`, `index-exists <name>`, `index-missing <name>` | `PUT`/`DELETE`/`GET`/`HEAD /<index>` |
| `alias-actions N action(s)`, `get-alias <name>`, `put-alias <index>-><alias>` | `/_aliases`, `/_alias/*` |
| `put-ilm-policy <name>`, `get-ilm-policy <name>`, `ilm-missing <name>`, `list-ilm-policies` | `/_ilm/policy/*` |
| `put-index_template <name>`, `get-index_template <name>`, `index_template-missing <name>` | `/_index_template/*` |
| `cat-indices <pattern>` | `/_cat/indices/<pattern>` |
| `bulk N action(s)` / `bulk N action(s) with errors` | `_bulk` |
| `search <pattern> [N hit(s)]` | `_search` |
| `count <pattern> [N]` | `_count` |
| `delete-by-query <pattern> [N]` | `_delete_by_query` |
| `mget N doc(s)` | `_mget` |
| `index-doc <index>/<id>`, `get-doc <index>/<id>`, `refresh <index>`, … | the extension endpoints |
| `unauthorized`, `unknown-path`, `bad-json`, `bad-body`, `internal-error` | failures (the harness can fail on these) |

---

## 4. Auth

* Without `--require-auth`: `Authorization: Basic ...` and `Authorization: ApiKey ...` are accepted,
  ignored and **recorded per request** in the log (`"auth":"basic"` / `"apikey"`, `"none"` when the
  product sent nothing or another scheme). This is how the harness proves the product authenticated.
* With `--require-auth user:pass`: only a matching `Basic` credential is accepted. Anything else —
  missing header, wrong user, wrong password, `ApiKey`, `Bearer` — gets `401` with
  `{"error":{"type":"security_exception","reason":"missing authentication credentials ..."}}`,
  `WWW-Authenticate: Basic realm="security" charset="UTF-8"`, and still the
  `X-Elastic-Product` header. The request log still records what was attempted.

---

## 5. State, persistence, concurrency

* All state is in memory: indices (settings/mappings/aliases/documents), ILM policies, index
  templates, plus a monotonically increasing per-index sequence so documents keep insertion order.
* `--state <path>` mirrors that state to JSON. It is written **on every state change** (atomic
  temp-file + rename, so it is safe to kill the process) and loaded at startup; deleting the file
  resets the cluster.
* Concurrency: `ThreadingHTTPServer` with daemon threads and one re-entrant lock around all state
  (and around the request-log append), so an API host and an Engine worker can hammer it at the same
  time. `HTTP/1.1` keep-alive, chunked request bodies and `HEAD` are handled.

### 5.1 Resetting the cluster (clearing residue from older runs)

A long-lived mock accumulates test residue, and a mock started **before the write-alias fix** can
carry a *literal* index whose name is a write alias (`fusetor-logs-write`). That residue shows
up on the product's Storage page as an extra backing index with an inflated document count, and its
name has no period, so index-level retention can never expire it. Reset it like this:

```powershell
# 1. Memory-only mock (no --state): stopping the process IS the reset — the next start begins empty.
Stop-Process -Id (Get-NetTCPConnection -LocalPort 9210 -State Listen).OwningProcess
python tools\FintegratorStorage\mock-elasticsearch\mock_elasticsearch.py --port 9210 `
    --log tools\FintegratorStorage\mock-elasticsearch\es-requests.jsonl --quiet

# 2. Keep the process but drop only the stray index (the alias itself lives on the dated index):
Invoke-RestMethod -Method Delete http://127.0.0.1:9210/fusetor-logs-write
#    List candidates first if you are unsure:
Invoke-RestMethod 'http://127.0.0.1:9210/_cat/indices/*-write?format=json&h=index,docs.count'

# 3. Re-create templates, ILM policies and write aliases from the product (also repairs a wiped state):
Invoke-RestMethod -Method Post -Headers @{ 'X-Api-Key' = 'dev-api-key' } http://localhost:5000/api/storage/apply

# 4. Prove the alias resolves and no literal alias-named index remains:
Invoke-RestMethod 'http://127.0.0.1:9210/_cat/aliases?format=json&h=alias,index,is_write_index'
```

Since the alias fix, every write path (`_bulk`, single-document writes, `_mget`, `_search`) resolves
a write alias to its backing index (`resolve_write_target`), so a freshly started mock never
re-creates the literal `-write` index. When the mock runs with `--state`, deleting that file before
startup is an equivalent reset.

---

## 6. What it deliberately does **not** do

This is a behaviour-compatible stub, not a search engine:

* **No analyzers, tokenizers, mappings or scoring.** `_score` is always `null`; `term` is an exact
  match with a case-insensitive fallback, `match`/`match_phrase` are case-insensitive substrings, and
  `query_string` is case-insensitive with `*`/`?` wildcards. Mapping definitions are stored verbatim
  but never enforced, so a `text` field and a `keyword` field behave identically.
* **No shards, replicas or routing.** Every index reports one primary shard, health is always
  `green`, `number_of_shards`/`number_of_replicas` sent by the product are stored but ignored.
* **No ILM execution.** Policies are stored and reported (`GET /_ilm/policy/<name>`) and never acted
  upon: no rollover, no delete phase, no `_ilm/explain` lifecycle (`managed:false`). Index templates
  are stored verbatim; they are applied *only* when an index is auto-created on a write (§7).
* **No refresh/translog semantics.** Documents are visible immediately to search/count/mget; the
  `refresh`, `wait_for_active_shards`, `timeout`, `pipeline`, `routing` and `version` parameters are
  accepted and ignored (no `refresh=false` staleness, no optimistic-concurrency `version` checks in
  `_bulk`).
* **No `_all`/`_close`/`_open`, scroll, PIT, async search, `_reindex`, `_update_by_query`,
  aggregations beyond §2, `filter_path`, `typed_keys`, `human`/`v` cat formatting, `search_after`,
  `collapse`, `highlight`, `suggest`, `script_fields`, `runtime_mappings`, `terms` lookups, `nested`
  queries, SQL, security realms/roles, TLS or snapshots.** Unsupported *query clauses* fail loudly
  with `400 parsing_exception` (better than silently returning the wrong rows); unsupported *paths*
  `404`.
* Not durable, not performant: a single process with an in-memory dict, no result-window limits
  (`from + size > 10000` is allowed), no bulk back-pressure.

## 7. Auto-created indices

Real Elasticsearch creates a missing index on a write when `action.auto_create_index` is on (the
default) and applies the highest-priority matching composable index template. A logging product that
writes `fusetor-logs-2026.10.16` through a `fusetor-logs-*` template depends on that, so the
mock does the same for `index`/`create`/`update` bulk actions and single-document writes: the index
appears in `_cat/indices`, is marked `"auto_created": true` in the `--state` file, and gets the
template's `settings`, `mappings` and `aliases` (so the write alias shows up in `GET /_alias/<name>`
as it would on a real cluster). Pass `--no-auto-create-index` to get strict per-item
`404 index_not_found_exception` errors instead.

## 8. Deviations from real Elasticsearch (and things that were guessed)

Everything below is intentional; the harness should not depend on real-ES behaviour here:

1. **Unknown paths.** Real ES replies `{"error":"no handler found for uri [...] and method [...]","status":404}`
   with `error` as a *string*. The task asked for an object, so the mock returns
   `{"error":{"root_cause":[...],"type":"no_handler_found_exception","reason":"no handler found for uri [...] and method [...]"},"status":404}`.
2. **`_cat/indices` value types.** The mock returns JSON **numbers** for `docs.count`, `store.size`
   (raw bytes with `bytes=b`) and `creation.date` (epoch millis). Real ES returns the human-formatted
   *strings* (`"4.4kb"`, and quoted counts) and ignores `bytes` for JSON output. `int(row["docs.count"])`
   therefore works, `row["docs.count"] == "5"` does not. `_cat` always keeps `index` as the first
   column, like ES does when `h=` omits it.
3. **Error bodies** always carry ES's `root_cause` + a top-level `status`, i.e. a superset of the
   documented shape. Missing-alias errors use `alias_not_found_exception`
   (`"alias [x] is missing"` — wording inferred from ES 8).
4. **Search response extras**: `_shards` is included and, when a `sort` was requested, each hit gets
   its `sort` values — both like ES, both beyond the specified shape.
5. **`track_total_hits: false`** still returns the exact total (`{"value":N,"relation":"eq"}`) instead
   of omitting `hits.total`; a mock has nothing to gain by skipping the count. An integer value
   behaves like ES (`{"value":min(N,t),"relation":"gte"}`), and `rest_total_hits_as_int=true` returns
   a plain number.
6. **Missing indices never error**: the mock always applies `allow_no_indices` semantics for
   `_search`/`_count`/`_mget`/`_delete_by_query`, with or without `ignore_unavailable=true`.
7. **No scoring/ordering subtleties**: hits come back in insertion order unless `sort` is given;
   missing sort values always sort last; ties keep insertion order.
8. **`_bulk` specifics**: a `delete` for a missing document reports per-item `status:404`,
   `result:"not_found"` and sets the top-level `errors:true` (ES does this too — a harness that
   bulk-deletes unknown ids must expect `errors:true`). A malformed NDJSON body (odd number of lines,
   unusable action line) is rejected with `400 parse_exception` instead of ES's mixing of 400/413.
   `update` actions are supported even though the product contract does not require them.
9. **`_delete_by_query`** returns the full ES envelope (`took`,`timed_out`,`total`,`deleted`,`batches`,
   `version_conflicts`,`noops`,`retries`,`throttled_millis`,`requests_per_second`,`failures`) — the
   contract's `{"deleted":N}` is one field inside it.
10. **`_alias` / write-index bookkeeping**: an explicit `is_write_index:true` demotes the previous
    write index to `false` (ES behaviour). An alias covering exactly one index exposes no
    `is_write_index` key (implicit write index), exactly like ES; `GET /_alias/<name>` returns `404`
    when no index carries the alias.
11. **Auth recording** deliberately collapses `Bearer`/unknown schemes to `"none"`, and
    `--require-auth` accepts **only** Basic (an `ApiKey` header is rejected, which real ES would
    accept with a valid key — there is no key here).
12. **Durations/`took`** are wall-clock milliseconds of the mock's own work, typically `0`/`1`;
    `took` is at least `1` for bulk. Do not assert on these values.
13. **`_cat/health`, `_nodes`, `_ilm/explain`, `_refresh`, `_flush`** return small, plausible
    stand-ins (see §2) rather than the full ES payloads.

## 9. Quick manual smoke test

```powershell
$m = Start-Process -PassThru python -ArgumentList "tools\FintegratorStorage\mock-elasticsearch\mock_elasticsearch.py --port 9210"
try {
  Invoke-RestMethod http://127.0.0.1:9210/ | ConvertTo-Json -Depth 4
  Invoke-RestMethod -Method Put http://127.0.0.1:9210/fusetor-logs-2026.10.16 -ContentType application/json -Body '{}'
  Invoke-RestMethod -Method Put 'http://127.0.0.1:9210/_bulk' -ContentType application/x-ndjson -Body "`n{`"index`":{`"_index`":`"fusetor-logs-2026.10.16`",`"_id`":`"1`"}}`n{`"message`":`"hello`"}`n"
  Invoke-RestMethod 'http://127.0.0.1:9210/_cat/indices/fusetor-*?format=json&h=index,docs.count,creation.date'
  Get-Content mock-elasticsearch-requests.jsonl -Tail 3
} finally { Stop-Process -Id $m.Id }
```
