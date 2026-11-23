#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Self-test for the mock vendor server (Shopify Admin GraphQL + Business Central).

Starts ``server.py`` on a free port as a child process, drives every endpoint
with urllib, prints ``PASS``/``FAIL`` per case and exits non-zero if anything
failed.

    python tools/FintegratorShopifyBc/mock-vendors/selftest.py

Standard library only.
"""

import json
import os
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_PATH = os.path.join(HERE, "server.py")
LOG_DIR = os.path.join(HERE, "logs", "selftest")
LOG_FILE = os.path.join(LOG_DIR, "requests.jsonl")

SHOPIFY_PATH = "/admin/api/2026-10/graphql.json"
BC = "/api/fintegrator/integration/v1.0/companies(abc-123)"
WATERMARK = "2026-10-15T09:15:00Z"
# seed shipment feed rows are stamped 09:40 and 10:05, inventory changes 09:45 and 10:10
SHIPMENT_WATERMARK = "2026-10-15T09:45:00Z"

LOG_ENTRY_KEYS = ("ts", "method", "path", "query", "body", "responseStatus")

_results = []


def enc(query):
    """Percent-encode an OData query string (spaces are illegal in a URL).

    Keeps the OData syntax characters readable; the leading ``?`` stays a real
    query separator."""
    return urllib.parse.quote(query, safe="$=,()'&?")


def check(name, ok, detail=""):
    ok = bool(ok)
    _results.append((name, ok, detail))
    print("%s %s%s" % ("PASS" if ok else "FAIL", name, ("  -- " + str(detail)) if detail and not ok else ""))
    sys.stdout.flush()
    return ok


def case(name, fn):
    """Run a check function; an exception inside becomes a FAIL, never an abort."""
    try:
        ok, detail = fn()
    except Exception as exc:  # noqa: BLE001 - a self-test must never crash
        ok, detail = False, "exception %s: %s" % (type(exc).__name__, exc)
    return check(name, ok, detail)


# ---------------------------------------------------------------------------
# tiny HTTP client
# ---------------------------------------------------------------------------

class Http(object):
    def __init__(self, base):
        self.base = base

    def request(self, method, path, body=None, raw=None, timeout=20):
        data = None
        headers = {}
        if raw is not None:
            data = raw.encode("utf-8") if isinstance(raw, str) else raw
            headers["Content-Type"] = "application/json"
        elif body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"
        req = urllib.request.Request(self.base + path, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                text = resp.read().decode("utf-8", "replace")
                status = resp.status
        except urllib.error.HTTPError as exc:
            text = exc.read().decode("utf-8", "replace")
            status = exc.code
        except Exception as exc:  # noqa: BLE001
            return 0, {"_transportError": "%s: %s" % (type(exc).__name__, exc)}
        try:
            parsed = json.loads(text) if text.strip() else None
        except ValueError:
            parsed = {"_raw": text}
        return status, parsed

    def get(self, path, **kw):
        return self.request("GET", path, **kw)

    def post(self, path, body=None, **kw):
        return self.request("POST", path, body, **kw)

    def patch(self, path, body=None, **kw):
        return self.request("PATCH", path, body, **kw)

    def gql(self, query, variables=None):
        return self.post(SHOPIFY_PATH, {"query": query, "variables": variables or {}})


def free_port():
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    return port


def start_server():
    """Start server.py on a free port; return (proc, port, stdout_lines)."""
    lines = []
    proc = None
    port = None
    for _ in range(5):
        port = free_port()
        proc = subprocess.Popen(
            [sys.executable, SERVER_PATH, "--port", str(port), "--log-dir", LOG_DIR],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            cwd=HERE,
            universal_newlines=True,
        )
        reader = threading.Thread(target=_drain, args=(proc, lines), daemon=True)
        reader.start()
        deadline = time.time() + 15
        while time.time() < deadline:
            if any(line.startswith("LISTENING") for line in lines):
                return proc, port, lines
            if proc.poll() is not None:
                break
            time.sleep(0.05)
        try:
            proc.kill()
        except OSError:
            pass
    raise RuntimeError("server did not start; output:\n" + "\n".join(lines))


def _drain(proc, lines):
    try:
        for line in iter(proc.stdout.readline, ""):
            lines.append(line.rstrip("\r\n"))
    except (ValueError, OSError):
        pass


def wait_http(client, deadline_s=15.0):
    deadline = time.time() + deadline_s
    while time.time() < deadline:
        status, _ = client.get("/__control/health", timeout=2)
        if status == 200:
            return True
        time.sleep(0.1)
    return False


# ---------------------------------------------------------------------------
# Shopify cases
# ---------------------------------------------------------------------------

ORDERS_QUERY = (
    "query MockOrders($first: Int, $after: String, $query: String) {\n"
    "  orders(first: $first, after: $after, query: $query) {\n"
    "    pageInfo { hasNextPage endCursor }\n"
    "    edges { node { id name updatedAt createdAt currencyCode displayFinancialStatus\n"
    "                   displayFulfillmentStatus email note tags\n"
    "                   currentTotalPriceSet { shopMoney { amount currencyCode } }\n"
    "                   shippingAddress { name address1 city zip }\n"
    "                   customer { id email firstName lastName }\n"
    "                   lineItems { edges { node { id name sku quantity currentQuantity\n"
    "                                            variant { id }\n"
    "                                            originalUnitPriceSet { shopMoney { amount currencyCode } } } } } } }\n"
    "  }\n"
    "}"
)


def shopify_orders(client, variables, query=ORDERS_QUERY):
    status, body = client.gql(query, variables)
    orders = ((body or {}).get("data") or {}).get("orders") or {}
    return status, orders


def shopify_cases(http, state):
    case("shopify.orders.page1", lambda: _orders_page1(http, state))
    case("shopify.orders.page2", lambda: _orders_page2(http, state))
    case("shopify.orders.deltaFilterVars", lambda: _orders_delta(http, state))
    case("shopify.orders.deltaFilterInline", lambda: _orders_delta_inline(http, state))
    case("shopify.orders.strictlyGreater", lambda: _orders_strict(http, state))
    case("shopify.orders.noFilter", lambda: _orders_all(http, state))
    case("shopify.fulfillmentOrders.hit", lambda: _fo_hit(http, state))
    case("shopify.fulfillmentOrders.inlineOrderId", lambda: _fo_inline(http))
    case("shopify.fulfillmentOrders.unknownOrderNull", lambda: _fo_null(http))
    case("shopify.fulfillmentCreateV2.success", lambda: _fc_success(http))
    case("shopify.fulfillmentCreateV2.userErrors", lambda: _fc_user_error(http))
    case("shopify.productVariants.hit", lambda: _pv_hit(http))
    case("shopify.productVariants.miss", lambda: _pv_miss(http))
    case("shopify.locations", lambda: _locations(http))
    case("shopify.inventorySetQuantities", lambda: _isq(http))
    case("shopify.inventorySetQuantities.userErrors", lambda: _isq_errors(http))
    case("shopify.unknownOperation", lambda: _unknown_op(http))
    case("shopify.badJsonBodySurvives", lambda: _bad_json(http))


def _orders_page1(http, state):
    status, orders = shopify_orders(http, {"first": 2, "query": "updated_at:>'2026-10-15T09:00:00Z'"})
    edges = orders.get("edges") or []
    state["page1_ids"] = [edge["node"]["id"] for edge in edges]
    state["cursor"] = (orders.get("pageInfo") or {}).get("endCursor")
    node = edges[0]["node"] if edges else {}
    shape_ok = (
        node.get("id") == "gid://shopify/Order/5100000001"
        and node.get("name") == "#1001"
        and node.get("updatedAt") == "2026-10-15T09:10:00Z"
        and node.get("currencyCode") == "USD"
        and (node.get("currentTotalPriceSet") or {}).get("shopMoney", {}).get("amount") == "129.90"
        and (node.get("lineItems") or {}).get("edges", [{}])[0].get("node", {}).get("sku") == "TSHIRT-BLK-M"
        and (node.get("lineItems") or {}).get("edges", [{}])[0].get("node", {}).get("variant", {}).get("id", "").endswith("/8001")
        and (node.get("shippingAddress") or {}).get("city") == "Springfield"
        and (node.get("customer") or {}).get("firstName") == "Jane"
    )
    ok = (
        status == 200
        and len(edges) == 2
        and (orders.get("pageInfo") or {}).get("hasNextPage") is True
        and state["cursor"] == "cursor-2"
        and shape_ok
    )
    return ok, "status=%s edges=%s cursor=%s nodeOk=%s" % (status, len(edges), state["cursor"], shape_ok)


def _orders_page2(http, state):
    status, orders = shopify_orders(http, {"first": 2, "after": state.get("cursor")})
    edges = orders.get("edges") or []
    ids = [edge["node"]["id"] for edge in edges]
    page_info = orders.get("pageInfo") or {}
    ok = status == 200 and len(edges) == 1 and page_info.get("hasNextPage") is False and ids == ["gid://shopify/Order/5100000003"]
    return ok, "status=%s ids=%s pageInfo=%s" % (status, ids, page_info)


def _orders_delta(http, state):
    status, orders = shopify_orders(http, {"first": 50, "query": "updated_at:>'%s'" % WATERMARK})
    stamps = [edge["node"]["updatedAt"] for edge in (orders.get("edges") or [])]
    ok = status == 200 and stamps == ["2026-10-15T09:20:00Z", "2026-10-15T09:30:00Z"]
    return ok, "status=%s stamps=%s" % (status, stamps)


def _orders_delta_inline(http, state):
    inline = (
        "{ orders(first: 50, query: \"updated_at:>'2026-10-15T09:25:00Z'\") "
        "{ edges { node { id updatedAt } } pageInfo { hasNextPage } } }"
    )
    status, body = http.gql(inline, {})
    edges = (((body or {}).get("data") or {}).get("orders") or {}).get("edges") or []
    stamps = [edge["node"]["updatedAt"] for edge in edges]
    ok = status == 200 and stamps == ["2026-10-15T09:30:00Z"]
    return ok, "status=%s stamps=%s body=%s" % (status, stamps, str(body)[:200])


def _orders_strict(http, state):
    status, orders = shopify_orders(http, {"first": 50, "query": "updated_at:>'2026-10-15T09:30:00Z'"})
    edges = orders.get("edges") or []
    ok = status == 200 and edges == []
    return ok, "status=%s edges=%s" % (status, len(edges))


def _orders_all(http, state):
    status, orders = shopify_orders(http, {})
    edges = orders.get("edges") or []
    ok = status == 200 and len(edges) == 3
    return ok, "status=%s edges=%s" % (status, len(edges))


def _fo_hit(http, state):
    query = (
        "query MockFulfillmentOrders($id: ID!) { order(id: $id) { id name "
        "fulfillmentOrders(first: 10) { nodes { id status "
        "lineItems { nodes { id remainingQuantity lineItem { id sku } } } } } } }"
    )
    status, body = http.gql(query, {"id": "gid://shopify/Order/5100000001"})
    order = ((body or {}).get("data") or {}).get("order") or {}
    nodes = (order.get("fulfillmentOrders") or {}).get("nodes") or []
    first = nodes[0] if nodes else {}
    line = ((first.get("lineItems") or {}).get("nodes") or [{}])[0]
    ok = (
        status == 200
        and order.get("id") == "gid://shopify/Order/5100000001"
        and order.get("name") == "#1001"
        and first.get("id") == "gid://shopify/FulfillmentOrder/6001"
        and first.get("status") == "OPEN"
        and line.get("remainingQuantity") == 2
        and (line.get("lineItem") or {}).get("sku") == "TSHIRT-BLK-M"
    )
    return ok, "status=%s order=%s" % (status, str(order)[:200])


def _fo_inline(http):
    query = ('{ order(id: "gid://shopify/Order/5100000002") { id name fulfillmentOrders { '
             'nodes { id lineItems { nodes { remainingQuantity lineItem { sku } } } } } } }')
    status, body = http.gql(query, {})
    order = ((body or {}).get("data") or {}).get("order") or {}
    nodes = (order.get("fulfillmentOrders") or {}).get("nodes") or []
    line = ((nodes[0].get("lineItems") or {}).get("nodes") or [{}])[0] if nodes else {}
    ok = (
        status == 200
        and order.get("name") == "#1002"
        and len(nodes) == 1
        and nodes[0].get("id") == "gid://shopify/FulfillmentOrder/6002"
        and (line.get("lineItem") or {}).get("sku") == "MUG-WHITE"
    )
    return ok, "status=%s body=%s" % (status, json.dumps(body)[:250])


def _fo_null(http):
    query = "query { order(id: \"gid://shopify/Order/999999\") { id fulfillmentOrders { nodes { id } } } }"
    status, body = http.gql(query, {})
    data = (body or {}).get("data") or {}
    ok = status == 200 and "order" in data and data["order"] is None
    return ok, "status=%s body=%s" % (status, str(body)[:200])


def _fc_success(http):
    query = (
        "mutation MockFulfill($fulfillment: FulfillmentV2Input!) { "
        "fulfillmentCreateV2(fulfillment: $fulfillment) { "
        "fulfillment { id status trackingInfo { number company url } } userErrors { field message } } }"
    )
    variables = {
        "fulfillment": {
            "lineItemsByFulfillmentOrder": [{
                "fulfillmentOrderId": "gid://shopify/FulfillmentOrder/6001",
                "fulfillmentOrderLineItems": [{"id": "gid://shopify/FulfillmentOrderLineItem/6501", "quantity": 2}],
            }],
            "trackingInfo": {"number": "1Z999AA10123456784", "company": "UPS"},
            "notifyCustomer": True,
        }
    }
    status, body = http.gql(query, variables)
    result = ((body or {}).get("data") or {}).get("fulfillmentCreateV2") or {}
    fulfillment = result.get("fulfillment") or {}
    tracking = (fulfillment.get("trackingInfo") or [{}])[0]
    ok = (
        status == 200
        and result.get("userErrors") == []
        and fulfillment.get("status") == "SUCCESS"
        and str(fulfillment.get("id", "")).startswith("gid://shopify/Fulfillment/")
        and tracking.get("number") == "1Z999AA10123456784"
        and tracking.get("company") == "UPS"
    )
    return ok, "status=%s result=%s" % (status, str(result)[:250])


def _fc_user_error(http):
    query = (
        "mutation MockFulfill($fulfillment: FulfillmentV2Input!) { "
        "fulfillmentCreateV2(fulfillment: $fulfillment) { fulfillment { id } userErrors { field message } } }"
    )
    status, body = http.gql(query, {"fulfillment": {"lineItemsByFulfillmentOrder": [
        {"fulfillmentOrderId": "gid://shopify/FulfillmentOrder/424242"},
    ]}})
    result = ((body or {}).get("data") or {}).get("fulfillmentCreateV2") or {}
    errors = result.get("userErrors") or []
    ok = status == 200 and len(errors) == 1 and errors[0].get("field") == "lineItemsByFulfillmentOrder" and errors[0].get("message")

    status2, body2 = http.gql(query, {"fulfillment": {"lineItemsByFulfillmentOrder": []}})
    result2 = ((body2 or {}).get("data") or {}).get("fulfillmentCreateV2") or {}
    ok = ok and len(result2.get("userErrors") or []) == 1
    return ok, "unknownFO=%s empty=%s" % (str(errors)[:160], str(result2)[:160])


def _pv_hit(http):
    query = ("query MockVariants($query: String!) { productVariants(first: 1, query: $query) "
             "{ nodes { id sku inventoryItem { id tracked } } } }")
    status, body = http.gql(query, {"query": "sku:TSHIRT-BLK-M"})
    nodes = (((body or {}).get("data") or {}).get("productVariants") or {}).get("nodes") or []
    node = nodes[0] if nodes else {}
    ok = (
        status == 200 and len(nodes) == 1 and node.get("sku") == "TSHIRT-BLK-M"
        and node.get("id") == "gid://shopify/ProductVariant/8001"
        and (node.get("inventoryItem") or {}).get("id") == "gid://shopify/InventoryItem/8101"
        and (node.get("inventoryItem") or {}).get("tracked") is True
    )
    return ok, "status=%s nodes=%s" % (status, str(nodes)[:200])


def _pv_miss(http):
    query = ("query MockVariants($query: String!) { productVariants(first: 1, query: $query) "
             "{ nodes { id sku } } }")
    status, body = http.gql(query, {"query": "sku:DOES-NOT-EXIST"})
    nodes = (((body or {}).get("data") or {}).get("productVariants") or {}).get("nodes")
    ok = status == 200 and nodes == []
    return ok, "status=%s nodes=%s" % (status, nodes)


def _locations(http):
    status, body = http.gql("query { locations(first: 10) { nodes { id name isActive } } }", {})
    nodes = (((body or {}).get("data") or {}).get("locations") or {}).get("nodes") or []
    ok = status == 200 and len(nodes) == 1 and nodes[0].get("name") == "Main Warehouse" and nodes[0].get("isActive") is True
    return ok, "status=%s nodes=%s" % (status, str(nodes)[:160])


def _isq(http):
    query = ("mutation MockInventory($input: InventorySetQuantitiesInput!) { "
             "inventorySetQuantities(input: $input) { inventoryAdjustmentGroup { reason "
             "changes { name delta quantityAfterChange } } userErrors { field message } } }")
    variables = {"input": {"reason": "correction", "name": "available", "quantities": [{
        "inventoryItemId": "gid://shopify/InventoryItem/8101",
        "locationId": "gid://shopify/Location/9001",
        "quantity": 40,
    }]}}
    status, body = http.gql(query, variables)
    result = ((body or {}).get("data") or {}).get("inventorySetQuantities") or {}
    group = result.get("inventoryAdjustmentGroup") or {}
    changes = group.get("changes") or []
    change = changes[0] if changes else {}
    ok = (
        status == 200
        and result.get("userErrors") == []
        and group.get("reason") == "correction"
        and len(changes) == 1
        and change.get("name") == "available"
        and change.get("quantityAfterChange") == 40
        and change.get("delta") == 40
    )

    state_status, snapshot = http.get("/__control/state")
    sets = (snapshot or {}).get("inventorySets") or []
    last = sets[-1] if sets else {}
    ok = ok and sorted(last.keys()) == sorted(["inventoryItemId", "locationId", "quantity", "name", "reason"])
    return ok, "status=%s result=%s recorded=%s" % (status, str(result)[:200], str(last)[:200])


def _isq_errors(http):
    query = ("mutation MockInventory($input: InventorySetQuantitiesInput!) { "
             "inventorySetQuantities(input: $input) { inventoryAdjustmentGroup { changes { name } } "
             "userErrors { field message } } }")
    status, body = http.gql(query, {"input": {"reason": "correction", "quantities": [{"quantity": 5}]}})
    result = ((body or {}).get("data") or {}).get("inventorySetQuantities") or {}
    errors = result.get("userErrors") or []
    ok = status == 200 and len(errors) == 1 and bool(errors[0].get("message"))
    return ok, "status=%s result=%s" % (status, str(result)[:200])


def _unknown_op(http):
    status, body = http.gql("query { somethingWeDoNotMock { id } }", {})
    errors = (body or {}).get("errors") or []
    ok = status == 200 and len(errors) == 1 and "no Shopify operation" in errors[0].get("message", "")
    return ok, "status=%s body=%s" % (status, str(body)[:200])


def _bad_json(http):
    status, body = http.post(SHOPIFY_PATH, raw="{not json at all")
    errors = (body or {}).get("errors") or []
    ok = status == 200 and len(errors) == 1
    status2, body2 = http.gql("query { locations(first: 1) { nodes { id } } }", {})
    ok = ok and status2 == 200 and bool(((body2 or {}).get("data") or {}).get("locations"))
    return ok, "badJson=%s stillAlive=%s" % (str(body)[:120], status2)


# ---------------------------------------------------------------------------
# failure injection + control cases
# ---------------------------------------------------------------------------

def control_cases(http, state):
    case("control.fail.graphqlError", lambda: _fail_graphql(http))
    case("control.fail.transportStatus", lambda: _fail_status(http))
    case("control.orders.replace", lambda: _orders_replace(http))
    case("control.items.set", lambda: _items_set(http))
    case("control.state", lambda: _state_shape(http))
    case("control.unknownRoute404", lambda: _control_404(http))
    case("http.unknownPath404", lambda: _unknown_path(http))
    case("http.concurrentFanOut", lambda: _concurrency(http))


def _fail_graphql(http):
    status, body = http.post("/__control/fail", {"path": SHOPIFY_PATH, "graphqlError": "Mock injected GraphQL failure", "times": 1})
    ok = status == 200 and (body or {}).get("ok") is True
    first_status, first = http.gql("query { locations(first: 1) { nodes { id } } }", {})
    second_status, second = http.gql("query { locations(first: 1) { nodes { id } } }", {})
    first_errors = (first or {}).get("errors") or []
    second_data = (second or {}).get("data") or {}
    ok = ok and first_status == 200 and first_errors and first_errors[0]["message"] == "Mock injected GraphQL failure"
    ok = ok and second_status == 200 and bool(second_data.get("locations"))
    return ok, "inject=%s first=%s second=%s" % (str(body)[:120], str(first)[:120], str(second)[:120])


def _fail_status(http):
    status, body = http.post("/__control/fail", {"path": SHOPIFY_PATH, "status": 500, "times": 1})
    ok = status == 200 and (body or {}).get("ok") is True
    fail_status, fail_body = http.gql("query { locations(first: 1) { nodes { id } } }", {})
    ok_status, ok_body = http.gql("query { locations(first: 1) { nodes { id } } }", {})
    ok = ok and fail_status == 500 and ok_status == 200 and bool(((ok_body or {}).get("data") or {}).get("locations"))
    return ok, "inject=%s failStatus=%s failBody=%s nextStatus=%s" % (str(body)[:120], fail_status, str(fail_body)[:120], ok_status)


def _orders_replace(http):
    http.post("/__control/reset")
    minimal = [{
        "id": "5100000099",
        "name": "#1099",
        "updatedAt": "2026-11-01T12:00:00Z",
        "currencyCode": "EUR",
        "lineItems": [{"sku": "MUG-WHITE", "quantity": 3, "unitPrice": 12.5}],
    }]
    status, body = http.post("/__control/orders", {"orders": minimal})
    ok = status == 200 and (body or {}).get("count") == 1

    order_status, orders = shopify_orders(http, {})
    edges = orders.get("edges") or []
    node = edges[0]["node"] if edges else {}
    line = ((node.get("lineItems") or {}).get("edges") or [{}])[0].get("node", {})
    ok = (
        ok and order_status == 200 and len(edges) == 1
        and node.get("id") == "gid://shopify/Order/5100000099"
        and node.get("currencyCode") == "EUR"
        and node.get("updatedAt") == "2026-11-01T12:00:00Z"
        and line.get("sku") == "MUG-WHITE"
        and line.get("quantity") == 3
        and (line.get("originalUnitPriceSet") or {}).get("shopMoney", {}).get("amount") == "12.5"
        and (node.get("currentTotalPriceSet") or {}).get("shopMoney", {}).get("currencyCode") == "EUR"
    )
    # the replaced order must drive fulfillmentOrders too
    fo_query = ("query { order(id: \"gid://shopify/Order/5100000099\") { id name "
                "fulfillmentOrders { nodes { id lineItems { nodes { remainingQuantity lineItem { sku } } } } } } }")
    fo_status, fo_body = http.gql(fo_query, {})
    fo_order = ((fo_body or {}).get("data") or {}).get("order") or {}
    ok = ok and fo_status == 200 and (fo_order.get("fulfillmentOrders") or {}).get("nodes")
    return ok, "replace=%s order=%s" % (str(body)[:120], str(node)[:220])


def _items_set(http):
    http.post("/__control/reset")
    status, body = http.post("/__control/items", {"items": [{"sku": "NEW-SKU"}, {"sku": "MUG-WHITE"}]})
    ok = status == 200 and (body or {}).get("items") == ["NEW-SKU", "MUG-WHITE"]

    created_status, created = http.post(BC + "/fintegratorSalesOrders", {
        "shopifyOrderId": "gid://shopify/Order/5100000100",
        "fintegratorSalesOrderLines": [{"sku": "NEW-SKU", "quantity": 1, "unitPrice": 9.99}],
    })
    rejected_status, rejected = http.post(BC + "/fintegratorSalesOrders", {
        "shopifyOrderId": "gid://shopify/Order/5100000101",
        "fintegratorSalesOrderLines": [{"sku": "TSHIRT-BLK-M", "quantity": 1}],
    })
    variant_status, variant_body = http.gql(
        "query { productVariants(first: 1, query: \"sku:NEW-SKU\") { nodes { id sku } } }", {})
    nodes = (((variant_body or {}).get("data") or {}).get("productVariants") or {}).get("nodes") or []
    ok = (
        ok and created_status == 201 and (created or {}).get("status") == "Created"
        and rejected_status == 400 and "Application_DialogException" in json.dumps(rejected)
        and variant_status == 200 and len(nodes) == 1
    )
    http.post("/__control/reset")
    return ok, "items=%s created=%s rejected=%s variants=%s" % (
        str(body)[:120], created_status, rejected_status, str(nodes)[:120])


def _state_shape(http):
    http.post("/__control/reset")
    status, body = http.get("/__control/state")
    body = body or {}
    required = ("orders", "salesOrders", "fulfillments", "inventorySets", "shipmentFeed", "inventoryFeed", "requests")
    missing = [key for key in required if key not in body]
    ok = (
        status == 200 and not missing
        and len(body.get("orders") or []) == 3
        and len(body.get("shipmentFeed") or []) == 2
        and len(body.get("inventoryFeed") or []) == 2
        and len(body.get("salesOrders") or []) == 0
        and len(body.get("fulfillments") or []) == 0
        and len(body.get("inventorySets") or []) == 0
        and isinstance(body.get("requests"), int) and body["requests"] > 0
        and all("fintegratorShippedOrderLines" in row for row in body.get("shipmentFeed") or [])
    )
    counts = {key: (len(body.get(key)) if isinstance(body.get(key), list) else body.get(key)) for key in required if key in body}
    return ok, "status=%s missing=%s counts=%s" % (status, missing, counts)


def _control_404(http):
    status, body = http.get("/__control/not-a-route")
    ok = status == 404 and (body or {}).get("ok") is False
    return ok, "status=%s body=%s" % (status, str(body)[:160])


def _unknown_path(http):
    status, body = http.get("/definitely/not/a/route")
    ok = status == 404 and (body or {}).get("path") == "/definitely/not/a/route"
    return ok, "status=%s body=%s" % (status, str(body)[:200])


def _concurrency(http):
    results = []
    lock = threading.Lock()

    def worker():
        status, body = http.gql("query { locations(first: 1) { nodes { id } } }", {})
        with lock:
            results.append(status)

    threads = [threading.Thread(target=worker) for _ in range(8)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join(20)
    ok = len(results) == 8 and all(status == 200 for status in results)
    return ok, "statuses=%s" % results


# ---------------------------------------------------------------------------
# Business Central cases
# ---------------------------------------------------------------------------

SHIPMENT_QUERY = ("?$filter=lastModifiedDateTime gt {ts}"
                  "&$orderby=lastModifiedDateTime asc,entryNo asc&$top=200&$expand=fintegratorShippedOrderLines")
INVENTORY_QUERY = "?$filter=lastModifiedDateTime gt {ts}&$orderby=lastModifiedDateTime asc,entryNo asc&$top=500"


def bc_cases(http, state):
    case("bc.salesOrder.create", lambda: _bc_create(http, state))
    case("bc.salesOrder.duplicate", lambda: _bc_duplicate(http, state))
    case("bc.salesOrder.unknownSku400", lambda: _bc_unknown_sku(http))
    case("bc.salesOrder.missingQuantity400", lambda: _bc_missing_quantity(http))
    case("bc.salesOrder.getCollection", lambda: _bc_list(http, state))
    case("bc.salesOrder.getKeyed", lambda: _bc_keyed(http, state))
    case("bc.salesOrder.getKeyed404", lambda: _bc_keyed_404(http))
    case("bc.salesOrder.patch", lambda: _bc_patch_sales_order(http, state))
    case("bc.shipments.filter.and.expand", lambda: _bc_shipments(http, state))
    case("bc.shipments.noExpand", lambda: _bc_shipments_no_expand(http))
    case("bc.shipments.top", lambda: _bc_shipments_top(http))
    case("bc.shipments.compoundFilter", lambda: _bc_shipments_compound(http))
    case("bc.shipments.patch.shopifyPushed", lambda: _bc_patch_shipment(http))
    case("bc.inventory.filter", lambda: _bc_inventory(http))
    case("bc.inventory.top", lambda: _bc_inventory_top(http))
    case("bc.unknownEntity404", lambda: _bc_unknown_entity(http))
    case("bc.unexpectedPath404", lambda: _bc_unexpected_path(http))


def _bc_create(http, state):
    http.post("/__control/reset")
    payload = {
        "shopifyOrderId": "gid://shopify/Order/5100000001",
        "shopifyOrderName": "#1001",
        "customerNo": "",
        "orderDate": "2026-10-15",
        "currencyCode": "USD",
        "note": "Shopify order #1001",
        "shippingName": "Jane Doe",
        "shippingAddress1": "1 Main Street",
        "shippingAddress2": "Apt 4",
        "shippingCity": "Springfield",
        "shippingPostCode": "12345",
        "shippingCountryCode": "US",
        "shippingPhone": "+15550100",
        "totalAmount": 129.90,
        "fintegratorSalesOrderLines": [
            {"sku": "TSHIRT-BLK-M", "description": "T-Shirt Black M", "quantity": 2, "unitPrice": 29.95},
            {"sku": "MUG-WHITE", "description": "Mug White", "quantity": 1, "unitPrice": 12.50},
        ],
    }
    status, body = http.post(BC + "/fintegratorSalesOrders", payload)
    body = body or {}
    lines = body.get("fintegratorSalesOrderLines") or []
    state["sales_order_id"] = body.get("id")
    state["sales_order_no"] = body.get("salesOrderNo")
    ok = (
        status == 201
        and body.get("status") == "Created"
        and body.get("createdSalesOrder") is True
        and body.get("salesOrderNo") == "101001"
        and bool(body.get("id"))
        and body.get("errorMessage") == ""
        and body.get("shopifyOrderId") == "gid://shopify/Order/5100000001"
        and body.get("shopifyOrderName") == "#1001"
        and body.get("shippingName") == "Jane Doe"
        and [line.get("lineNo") for line in lines] == [10000, 20000]
        and lines[0].get("sku") == "TSHIRT-BLK-M"
        and lines[1].get("quantity") == 1
    )
    return ok, "status=%s body=%s" % (status, json.dumps(body)[:400])


def _bc_duplicate(http, state):
    payload = {
        "shopifyOrderId": "gid://shopify/Order/5100000001",
        "shopifyOrderName": "#1001",
        "fintegratorSalesOrderLines": [{"sku": "TSHIRT-BLK-M", "quantity": 2, "unitPrice": 29.95}],
    }
    status, body = http.post(BC + "/fintegratorSalesOrders", payload)
    body = body or {}
    ok = (
        status == 201
        and body.get("status") == "Duplicate"
        and body.get("createdSalesOrder") is False
        and body.get("salesOrderNo") == state.get("sales_order_no") == "101001"
    )
    status2, listing = http.get(BC + "/fintegratorSalesOrders")
    ok = ok and len((listing or {}).get("value") or []) == 1
    return ok, "status=%s body=%s listCount=%s" % (status, json.dumps(body)[:250], len((listing or {}).get("value") or []))


def _bc_unknown_sku(http):
    status, body = http.post(BC + "/fintegratorSalesOrders", {
        "shopifyOrderId": "gid://shopify/Order/5100000002",
        "fintegratorSalesOrderLines": [{"sku": "NO-SUCH-SKU", "quantity": 1}],
    })
    error = (body or {}).get("error") or {}
    message = error.get("message") or ""
    ok = (
        status == 400
        and error.get("code") == "Application_DialogException"
        and "NO-SUCH-SKU" in message
        and "does not match any Business Central item" in message
    )
    status2, listing = http.get(BC + "/fintegratorSalesOrders")
    ok = ok and len((listing or {}).get("value") or []) == 1
    return ok, "status=%s body=%s listCount=%s" % (status, json.dumps(body)[:250], len((listing or {}).get("value") or []))


def _bc_missing_quantity(http):
    status, body = http.post(BC + "/fintegratorSalesOrders", {
        "shopifyOrderId": "gid://shopify/Order/5100000003",
        "fintegratorSalesOrderLines": [{"sku": "MUG-WHITE"}],
    })
    status2, body2 = http.post(BC + "/fintegratorSalesOrders", {
        "shopifyOrderId": "",
        "fintegratorSalesOrderLines": [{"sku": "MUG-WHITE", "quantity": 1}],
    })
    ok = (
        status == 400 and "quantity" in json.dumps(body)
        and status2 == 400 and "shopifyOrderId" in json.dumps(body2)
    )
    return ok, "missingQty=%s emptyOrderId=%s" % (str(body)[:160], str(body2)[:160])


def _bc_list(http, state):
    status, body = http.get(BC + "/fintegratorSalesOrders")
    values = (body or {}).get("value") or []
    ok = (
        status == 200
        and len(values) == 1
        and bool(body.get("@odata.context"))
        and "$metadata#companies(abc-123)/fintegratorSalesOrders" in body.get("@odata.context", "")
        and values[0].get("salesOrderNo") == "101001"
    )
    filter_status, filtered = http.get(BC + "/fintegratorSalesOrders" + enc("?$filter=shopifyOrderId eq 'gid://shopify/Order/5100000001'"))
    ok = ok and filter_status == 200 and len((filtered or {}).get("value") or []) == 1
    empty_status, empty = http.get(BC + "/fintegratorSalesOrders" + enc("?$filter=shopifyOrderId eq 'gid://shopify/Order/nope'"))
    ok = ok and empty_status == 200 and ((empty or {}).get("value") or []) == []
    return ok, "status=%s values=%s" % (status, json.dumps(body)[:220])


def _bc_keyed(http, state):
    order_id = state.get("sales_order_id")
    status, body = http.get(BC + "/fintegratorSalesOrders(%s)" % order_id)
    ok = status == 200 and (body or {}).get("id") == order_id and (body or {}).get("salesOrderNo") == "101001"
    ok = ok and (body or {}).get("status") == "Created"
    return ok, "status=%s body=%s" % (status, json.dumps(body)[:220])


def _bc_keyed_404(http):
    status, body = http.get(BC + "/fintegratorSalesOrders(00000000-0000-0000-0000-000000000000)")
    error = (body or {}).get("error") or {}
    ok = status == 404 and error.get("code") == "NotFound" and bool(error.get("message"))
    return ok, "status=%s body=%s" % (status, json.dumps(body)[:200])


def _bc_patch_sales_order(http, state):
    order_id = state.get("sales_order_id")
    status, body = http.patch(BC + "/fintegratorSalesOrders(%s)" % order_id, {"note": "patched by selftest"})
    ok = status == 200 and (body or {}).get("note") == "patched by selftest" and (body or {}).get("salesOrderNo") == "101001"
    read_status, read = http.get(BC + "/fintegratorSalesOrders(%s)" % order_id)
    ok = ok and read_status == 200 and (read or {}).get("note") == "patched by selftest"
    return ok, "status=%s body=%s" % (status, json.dumps(body)[:200])


def _bc_shipments(http, state):
    status, body = http.get(BC + "/fintegratorShippedOrders" + enc(SHIPMENT_QUERY.format(ts=SHIPMENT_WATERMARK)))
    values = (body or {}).get("value") or []
    first = values[0] if values else {}
    lines = first.get("fintegratorShippedOrderLines") or []
    ok = (
        status == 200
        and len(values) == 1
        and first.get("entryNo") == 2
        and first.get("lastModifiedDateTime") == "2026-10-15T10:05:00Z"
        and first.get("shopifyOrderName") == "#1002"
        and first.get("trackingNo") == "JD0146000034567890"
        and len(lines) == 1
        and lines[0].get("sku") == "MUG-WHITE"
        and lines[0].get("quantity") == 1
        and "@odata.context" in (body or {})
    )
    return ok, "status=%s body=%s" % (status, json.dumps(body)[:400])


def _bc_shipments_no_expand(http):
    query = "?$filter=lastModifiedDateTime gt {ts}&$top=200".format(ts=SHIPMENT_WATERMARK)
    status, body = http.get(BC + "/fintegratorShippedOrders" + enc(query))
    values = (body or {}).get("value") or []
    ok = (
        status == 200 and len(values) == 1
        and "fintegratorShippedOrderLines" not in values[0]
        and values[0].get("entryNo") == 2
    )
    return ok, "status=%s values=%s" % (status, json.dumps(values)[:250])


def _bc_shipments_top(http):
    status, body = http.get(BC + "/fintegratorShippedOrders" + enc("?$orderby=lastModifiedDateTime asc,entryNo asc&$top=1"))
    values = (body or {}).get("value") or []
    ok = status == 200 and len(values) == 1 and values[0].get("entryNo") == 1
    status_desc, body_desc = http.get(BC + "/fintegratorShippedOrders" + enc("?$orderby=lastModifiedDateTime desc,entryNo desc&$top=1"))
    desc = ((body_desc or {}).get("value") or [])
    ok = ok and status_desc == 200 and len(desc) == 1 and desc[0].get("entryNo") == 2
    return ok, "asc=%s desc=%s" % (json.dumps(values)[:120], json.dumps(desc)[:120])


def _bc_shipments_compound(http):
    http.post("/__control/bc-shipments", {"shipments": [
        {"salesShipmentNo": "S-100", "salesOrderNo": "101001", "shopifyOrderId": "gid://shopify/Order/5100000001",
         "shopifyOrderName": "#1001", "lastModifiedDateTime": "2026-10-16T08:00:00Z", "shippedQuantity": 1,
         "fintegratorShippedOrderLines": [{"itemNo": "TSHIRT-BLK-M", "sku": "TSHIRT-BLK-M", "quantity": 1}]},
        {"salesShipmentNo": "S-101", "salesOrderNo": "101002", "shopifyOrderId": "gid://shopify/Order/5100000002",
         "shopifyOrderName": "#1002", "lastModifiedDateTime": "2026-10-16T08:00:00Z", "shippedQuantity": 1,
         "fintegratorShippedOrderLines": [{"itemNo": "MUG-WHITE", "sku": "MUG-WHITE", "quantity": 1}]},
    ]})
    # both new rows share the timestamp; entryNo (3, 4) must break the tie
    query = ("?$filter=lastModifiedDateTime gt 2026-10-16T08:00:00Z or "
             "(lastModifiedDateTime eq 2026-10-16T08:00:00Z and entryNo gt 3)"
             "&$orderby=lastModifiedDateTime asc,entryNo asc&$top=200&$expand=fintegratorShippedOrderLines")
    status, body = http.get(BC + "/fintegratorShippedOrders" + enc(query))
    values = (body or {}).get("value") or []
    entries = [row.get("entryNo") for row in values]
    ok = status == 200 and entries == [4]
    status2, body2 = http.get(BC + "/fintegratorShippedOrders" + enc("?$filter=lastModifiedDateTime gt 2026-10-16T07:00:00Z&$top=200"))
    entries2 = [row.get("entryNo") for row in ((body2 or {}).get("value") or [])]
    ok = ok and status2 == 200 and entries2 == [3, 4]
    return ok, "compounded=%s broader=%s" % (entries, entries2)


def _bc_patch_shipment(http):
    status, body = http.patch(BC + "/fintegratorShippedOrders(1)", {"shopifyPushed": True})
    ok = status == 200 and (body or {}).get("shopifyPushed") is True and (body or {}).get("entryNo") == 1
    read_status, read = http.get(BC + "/fintegratorShippedOrders(1)")
    ok = ok and read_status == 200 and (read or {}).get("shopifyPushed") is True
    missing_status, missing = http.patch(BC + "/fintegratorShippedOrders(99999)", {"shopifyPushed": True})
    ok = ok and missing_status == 404 and ((missing or {}).get("error") or {}).get("code") == "NotFound"
    return ok, "status=%s body=%s missing=%s" % (status, json.dumps(body)[:200], missing_status)


def _bc_inventory(http):
    status, body = http.get(BC + "/fintegratorInventoryChanges" + enc(INVENTORY_QUERY.format(ts=WATERMARK)))
    values = (body or {}).get("value") or []
    entry_nos = [row.get("entryNo") for row in values]
    ok = (
        status == 200
        and entry_nos == [1, 2]
        and values[1].get("sku") == "MUG-WHITE"
        and values[1].get("inventory") == 75
        and values[1].get("changeQuantity") == -1
        and "fintegratorShippedOrderLines" not in values[1]
    )
    status2, body2 = http.get(BC + "/fintegratorInventoryChanges" + enc("?$filter=lastModifiedDateTime gt 2026-10-15T10:00:00Z&$top=500"))
    entries2 = [row.get("entryNo") for row in ((body2 or {}).get("value") or [])]
    ok = ok and status2 == 200 and entries2 == [2]

    append_status, appended = http.post("/__control/bc-inventory", {"changes": [{
        "itemNo": "TSHIRT-BLK-M", "sku": "TSHIRT-BLK-M", "locationCode": "MAIN",
        "inventory": 41, "changeQuantity": -7, "lastModifiedDateTime": "2026-10-16T09:00:00Z",
    }]})
    status3, body3 = http.get(BC + "/fintegratorInventoryChanges" + enc("?$filter=lastModifiedDateTime gt 2026-10-15T23:00:00Z&$top=500"))
    values3 = (body3 or {}).get("value") or []
    ok = ok and append_status == 200 and len(values3) == 1 and values3[0].get("inventory") == 41 and bool(values3[0].get("lastModifiedDateTime"))
    return ok, "status=%s entries=%s appended=%s" % (status, entry_nos, json.dumps(values3)[:180])


def _bc_inventory_top(http):
    status, body = http.get(BC + "/fintegratorInventoryChanges?$top=1")
    values = (body or {}).get("value") or []
    ok = status == 200 and len(values) == 1 and values[0].get("entryNo") == 1
    return ok, "status=%s values=%s" % (status, json.dumps(values)[:200])


def _bc_unknown_entity(http):
    status, body = http.get(BC + "/somethingElse")
    error = (body or {}).get("error") or {}
    ok = status == 404 and error.get("code") == "NotFound" and "somethingElse" in (error.get("message") or "")
    return ok, "status=%s body=%s" % (status, json.dumps(body)[:200])


def _bc_unexpected_path(http):
    status, body = http.get("/api/fintegrator/integration/v1.0/nonsense")
    error = (body or {}).get("error") or {}
    ok = status == 404 and bool(error.get("code")) and bool(error.get("message"))
    return ok, "status=%s body=%s" % (status, json.dumps(body)[:200])


# ---------------------------------------------------------------------------
# request log + shutdown
# ---------------------------------------------------------------------------

def log_cases(http, state):
    case("control.requests", lambda: _requests_endpoint(http))
    case("log.fileWritten", lambda: _log_file(http))
    case("server.cleanShutdown", lambda: _shutdown(http))


def _requests_endpoint(http):
    status, body = http.get("/__control/requests?limit=5")
    entries = body if isinstance(body, list) else []
    # requests are logged AFTER the response, so the newest entry is the request
    # that preceded this one.
    ok = (
        status == 200
        and len(entries) == 5
        and all(set(LOG_ENTRY_KEYS) <= set(entry.keys()) for entry in entries)
        and all(entries[i]["ts"] <= entries[i + 1]["ts"] for i in range(len(entries) - 1))
        and entries[-1]["path"].startswith("/api/fintegrator/integration/")
        and entries[-1]["responseStatus"] == 404
    )
    limited_status, limited = http.get("/__control/requests?limit=1")
    ok = ok and limited_status == 200 and isinstance(limited, list) and len(limited) == 1
    return ok, "status=%s entries=%s" % (status, json.dumps(entries)[:250])


def _log_file(http):
    exists = os.path.isfile(LOG_FILE)
    if not exists:
        return False, "missing %s" % LOG_FILE
    with open(LOG_FILE, "r", encoding="utf-8") as handle:
        lines = [line for line in handle.read().splitlines() if line.strip()]
    parsed = []
    for line in lines:
        try:
            parsed.append(json.loads(line))
        except ValueError:
            return False, "line is not JSON: %s" % line[:120]
    paths = [entry.get("path") for entry in parsed]
    ok = (
        len(parsed) > 20
        and all(set(LOG_ENTRY_KEYS) <= set(entry.keys()) for entry in parsed)
        and "/__control/state" in paths
        and any(path.startswith("/admin/api/") for path in paths)
        and any(path.startswith("/api/fintegrator/integration/") for path in paths)
        and any(entry.get("responseStatus") == 400 for entry in parsed)
        and any(entry.get("responseStatus") == 404 for entry in parsed)
        and all(len(entry.get("body") or "") <= 8000 for entry in parsed)
    )
    return ok, "file=%s lines=%s" % (LOG_FILE, len(parsed))


def _shutdown(http):
    status, body = http.post("/__control/shutdown", {})
    ok = status == 200 and (body or {}).get("ok") is True
    return ok, "status=%s body=%s" % (status, json.dumps(body)[:120])


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------

def main():
    print("mock-vendors selftest -- server: %s" % SERVER_PATH)
    if os.path.isdir(LOG_DIR) and os.path.isfile(LOG_FILE):
        try:
            os.remove(LOG_FILE)
        except OSError:
            pass

    proc, port, lines = start_server()
    http = Http("http://127.0.0.1:%d" % port)
    state = {}
    if not check("server.started", any(line.startswith("LISTENING %d" % port) for line in lines),
                 "stdout=%s" % lines):
        proc.kill()
        return 1
    if not check("server.ready", wait_http(http), "port=%s" % port):
        proc.kill()
        return 1

    http.post("/__control/reset")
    shopify_cases(http, state)
    control_cases(http, state)
    bc_cases(http, state)
    log_cases(http, state)

    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=10)
    check("server.exitedCleanly", proc.returncode == 0 and any(line.startswith("STOPPED") for line in lines),
          "returncode=%s tail=%s" % (proc.returncode, lines[-3:]))

    failed = [name for name, ok, _ in _results if not ok]
    print("")
    print("%d/%d checks passed%s" % (len(_results) - len(failed), len(_results),
                                     "" if not failed else "; failed: " + ", ".join(failed)))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
