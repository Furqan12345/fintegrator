#!/usr/bin/env python3
"""End-to-end test for the Shopify <-> Business Central integration.

Starts the mock vendor API, deploys the three flows against it, then drives real runs through the
fintegrator.io engine and asserts what actually happened:

  * order create   - delta pull by updatedAt watermark, one fan-out child per order, BC sales order
                     creation, second run picks up only new orders, re-run is idempotent, empty run
                     creates no children
  * order shipment - BC shipment feed -> Shopify fulfillment with tracking, delta on the second run
  * inventory      - BC inventory feed -> absolute Shopify quantity, unmapped SKU is skipped, delta

Every assertion is made from evidence: the mock's recorded requests and state, plus the
fintegrator.io execution/transaction rows.

  python run_e2e.py --api-base http://localhost:5000 --api-key dev-api-key
"""
import argparse
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL_ROOT = os.path.dirname(HERE)
MOCK_DIR = os.path.join(TOOL_ROOT, "mock-vendors")
BUILD_DIR = os.path.join(TOOL_ROOT, "flows", "build")
ARTIFACT_DIR = os.path.join(TOOL_ROOT, "artifacts")

MOCK_COMPANY_ID = "11111111-2222-3333-4444-555555555555"
MOCK_SHOPIFY_CONNECTION = "e2e mock shopify"
MOCK_BC_CONNECTION = "e2e mock business central"
MOCK_INTEGRATION = "e2e shopify business central"
MOCK_FLOW_SUFFIX = " (e2e mock)"
LEDGER_LIST = "shopify-orders-imported"
TRACKING_NUMBER = "1Z999AA10123456784"
KNOWN_SKUS = ["TSHIRT-BLK-M", "MUG-WHITE"]
UNKNOWN_SKU = "GHOST-SKU"

sys.path.insert(0, HERE)
import build_flows  # noqa: E402
import fintegrator  # noqa: E402


# --------------------------------------------------------------------------- reporting
class Report:
    def __init__(self):
        self.checks = []

    def check(self, name, ok, detail=""):
        self.checks.append({"name": name, "ok": bool(ok), "detail": str(detail)})
        print("  %s %s%s" % ("PASS" if ok else "FAIL", name, (" - " + str(detail)) if detail else ""))
        return bool(ok)

    @property
    def failed(self):
        return [check for check in self.checks if not check["ok"]]

    def summary(self):
        print("\n%d checks, %d failed" % (len(self.checks), len(self.failed)))
        for check in self.failed:
            print("  FAILED: %s - %s" % (check["name"], check["detail"]))


# --------------------------------------------------------------------------- http helper
def http_json(method, url, body=None, timeout=30):
    data = None
    headers = {"Accept": "application/json"}
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            payload = response.read().decode("utf-8")
            if not payload:
                return response.status, None
            try:
                return response.status, json.loads(payload)
            except ValueError:
                return response.status, payload
    except urllib.error.HTTPError as error:
        payload = error.read().decode("utf-8", "replace")
        try:
            return error.code, json.loads(payload)
        except ValueError:
            return error.code, payload


class Mock:
    """Control client for the local mock vendor API."""

    def __init__(self, port):
        self.base = "http://127.0.0.1:%d" % port
        self.shopify_url = self.base + "/admin/api/2026-07/graphql.json"

    def control(self, path, body=None):
        status, payload = http_json("POST" if body is not None else "GET", self.base + path, body)
        if status >= 300 and status != 400:
            raise RuntimeError("mock control %s failed: HTTP %s %s" % (path, status, payload))
        return payload

    def graphql(self, query, variables=None):
        status, payload = http_json("POST", self.shopify_url, {"query": query, "variables": variables or {}})
        if status >= 300:
            raise RuntimeError("mock graphql failed: HTTP %s %s" % (status, payload))
        return payload

    def reset(self):
        return self.control("/__control/reset", {})

    def set_items(self, items):
        return self.control("/__control/items", {"items": items})

    def set_orders(self, orders):
        return self.control("/__control/orders", {"orders": orders})

    def add_shipments(self, shipments, replace=False):
        return self.control("/__control/bc-shipments", {"shipments": shipments, "replace": bool(replace)})

    def add_inventory(self, changes, replace=False):
        return self.control("/__control/bc-inventory", {"changes": changes, "replace": bool(replace)})

    def state(self):
        return self.control("/__control/state")

    def requests(self, limit=400):
        payload = self.control("/__control/requests?limit=%d" % limit)
        if isinstance(payload, dict):
            return payload.get("requests", [])
        return payload or []


# --------------------------------------------------------------------------- engine helpers
ACTIVE_STATUSES = ("Queued", "InProgress")


def flow_runs(client, flow_id, timeout=240):
    """Runs a flow and waits until the whole run tree (parent + fan-out children) is finished."""
    started = client.run_flow(flow_id)
    execution_id = started.get("executionId")
    deadline = time.time() + timeout
    executions = []
    while time.time() < deadline:
        executions = client.list_executions(flow_id)
        active = [item for item in executions if item.get("status") in ACTIVE_STATUSES]
        if not active:
            break
        time.sleep(1.5)
    else:
        raise TimeoutError("flow %s still had active executions after %ss" % (flow_id, timeout))

    parent = next((item for item in executions if item.get("id") == execution_id), None)
    children = [item for item in executions if item.get("parentExecutionId") == execution_id]
    return parent, children


def node_messages(client, execution_id):
    """Returns {nodeName: message-ish text} using the Success card's message plus step payloads."""
    messages = []
    try:
        steps = client.request("GET", "/api/executions/%s/steps" % execution_id)
    except fintegrator.ApiError:
        return messages
    for step in steps or []:
        payload = step.get("responsePayload") or ""
        messages.append({"nodeName": step.get("nodeName"), "status": step.get("status"), "payload": payload})
    return messages


def run_ids_of(executions):
    return [item["id"] for item in executions]


def run_detail(execution):
    """Status plus whatever the run told us about itself, so a failed check is actionable."""
    if not execution:
        return "no execution row"
    detail = str(execution.get("status"))
    error = execution.get("errorMessage") or ""
    message = execution.get("message") or ""
    if error:
        detail += " / error: " + error[:200]
    if message:
        detail += " / " + message[:140]
    return detail


# --------------------------------------------------------------------------- scenarios
def seed_shopify_orders(updated):
    orders = []
    for index, timestamp in enumerate(updated):
        number = 1001 + index
        orders.append(
            {
                "id": "gid://shopify/Order/510000%04d" % number,
                "name": "#%d" % number,
                "updatedAt": timestamp,
                "createdAt": "2026-10-14T18:00:00Z",
                "currencyCode": "USD",
                "displayFinancialStatus": "PAID",
                "displayFulfillmentStatus": "UNFULFILLED",
                "email": "customer%d@example.com" % number,
                "note": "",
                "tags": ["fintegrator"],
                "currentTotalPriceSet": {"shopMoney": {"amount": "129.90", "currencyCode": "USD"}},
                "shippingAddress": {
                    "name": "Customer %d" % number,
                    "address1": "1 Main Street",
                    "address2": "",
                    "city": "Springfield",
                    "provinceCode": "IL",
                    "countryCodeV2": "US",
                    "zip": "62701",
                    "phone": "+15550100",
                },
                "customer": {"id": "gid://shopify/Customer/700%d" % index, "email": "customer%d@example.com" % number, "firstName": "Customer", "lastName": str(number)},
                "lineItems": [{"sku": KNOWN_SKUS[0], "quantity": 2, "unitPrice": "29.95", "name": "T-Shirt Black M"}],
            }
        )
    return orders


def discover_fulfillment_lines(mock, order_id):
    """Asks the mock what Shopify reports for an order, so the BC seed matches reality."""
    query = (
        "query FintegratorFulfillmentOrders($id: ID!) { order(id: $id) { id name fulfillmentOrders(first: 10) "
        "{ nodes { id status lineItems(first: 100) { nodes { id remainingQuantity lineItem { sku } } } } } } }"
    )
    payload = mock.graphql(query, {"id": order_id})
    lines = []
    order = (payload.get("data") or {}).get("order") or {}
    for fulfillment_order in (order.get("fulfillmentOrders") or {}).get("nodes") or []:
        for line in (fulfillment_order.get("lineItems") or {}).get("nodes") or []:
            lines.append({"sku": ((line.get("lineItem") or {}).get("sku") or ""), "quantity": line.get("remainingQuantity") or 0})
    return order, lines


def main():
    parser = argparse.ArgumentParser(description="End-to-end test of the Shopify <-> BC integration.")
    parser.add_argument("--api-base", default="http://localhost:5000")
    parser.add_argument("--api-key", default="dev-api-key")
    parser.add_argument("--port", type=int, default=5099)
    parser.add_argument("--keep-server", action="store_true")
    parser.add_argument("--shopify-api-version", default="2026-07")
    args = parser.parse_args()

    report = Report()
    os.makedirs(ARTIFACT_DIR, exist_ok=True)

    if build_flows.build_all() != 0:
        return 1

    server = None
    try:
        # Fail fast when the port is already served: a stale mock (for example from an earlier
        # --keep-server run) would answer every call with old code and all the assertions below
        # would be measured against the wrong server.
        try:
            status, _ = http_json("GET", "http://127.0.0.1:%d/__control/health" % args.port, timeout=3)
            print("error: port %d already answers /__control/health (HTTP %s). Stop that process or pass --port."
                  % (args.port, status), file=sys.stderr)
            return 1
        except Exception:
            pass

        server = subprocess.Popen(
            [sys.executable, os.path.join(MOCK_DIR, "server.py"), "--port", str(args.port)],
            cwd=MOCK_DIR,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
        )
        ready = False
        started = time.time()
        while time.time() - started < 30:
            line = server.stdout.readline()
            if not line:
                break
            print("mock: " + line.strip())
            if "LISTENING" in line.upper():
                ready = True
                break
        if not ready:
            print("error: mock server did not report LISTENING", file=sys.stderr)
            return 1

        mock = Mock(args.port)
        client = fintegrator.FintegratorClient(args.api_base, args.api_key)

        status, health = http_json("GET", args.api_base + "/health")
        report.check("fintegrator.io API reachable", status == 200, "HTTP %s %s" % (status, health))

        # ---- seed the mock --------------------------------------------------
        mock.reset()
        mock.set_items([{"sku": sku} for sku in KNOWN_SKUS])

        # The order pull must ignore orders that are not paid yet, so one unpaid order is seeded in the
        # middle of the timeline: if the financial_status filter were dropped it would be imported.
        def seed_orders(paid_wave):
            unpaid = dict(seed_shopify_orders(["2026-10-15T09:15:00Z"])[0])
            unpaid["id"] = "gid://shopify/Order/5100001999"
            unpaid["name"] = "#1999"
            unpaid["displayFinancialStatus"] = "UNPAID"
            return seed_shopify_orders(paid_wave) + [unpaid]

        first_wave = ["2026-10-15T09:10:00Z", "2026-10-15T09:20:00Z", "2026-10-15T09:30:00Z"]
        mock.set_orders(seed_orders(first_wave))
        # A fresh ledger: the previous scenario's imports must not leak into this one.
        client.clear_cross_reference_entries(LEDGER_LIST)
        order_one_id = "gid://shopify/Order/5100001001"
        order_two_id = "gid://shopify/Order/5100001002"
        order_one, fulfillment_lines = discover_fulfillment_lines(mock, order_one_id)
        report.check(
            "mock Shopify returns open fulfillment orders for the seeded order",
            bool(fulfillment_lines),
            "order %s, %d line(s)" % (order_one.get("name"), len(fulfillment_lines)),
        )
        if not fulfillment_lines:
            fulfillment_lines = [{"sku": KNOWN_SKUS[0], "quantity": 2}]

        mock.add_shipments(
            [
                {
                    "salesShipmentNo": "102301",
                    "salesOrderNo": "101001",
                    "shopifyOrderId": order_one_id,
                    "shopifyOrderName": "#1001",
                    "customerNo": "10000",
                    "shipmentDate": "2026-10-15",
                    "shippingAgentCode": "UPS",
                    "shippingAgentName": "UPS",
                    "trackingNo": TRACKING_NUMBER,
                    "trackingUrl": "https://www.ups.com/track?tracknum=" + TRACKING_NUMBER,
                    "shippedQuantity": sum(float(line["quantity"]) for line in fulfillment_lines),
                    "fullyShipped": True,
                    "lastModifiedDateTime": "2026-10-15T09:40:00Z",
                    "fintegratorShippedOrderLines": [
                        {"lineNo": 10000 * (index + 1), "itemNo": line["sku"], "sku": line["sku"], "quantity": line["quantity"]}
                        for index, line in enumerate(fulfillment_lines)
                    ],
                }
            ],
            replace=True,
        )
        mock.add_inventory(
            [
                {
                    "itemNo": KNOWN_SKUS[0],
                    "variantCode": "",
                    "locationCode": "WH1",
                    "sku": KNOWN_SKUS[0],
                    "inventory": 42,
                    "changeQuantity": -3,
                    "lastModifiedDateTime": "2026-10-15T09:45:00Z",
                },
                {
                    "itemNo": "GHOST",
                    "variantCode": "",
                    "locationCode": "WH1",
                    "sku": UNKNOWN_SKU,
                    "inventory": 5,
                    "changeQuantity": 1,
                    "lastModifiedDateTime": "2026-10-15T09:46:00Z",
                },
            ],
            replace=True,
        )

        # ---- connections + deployment --------------------------------------
        shopify_connection, _ = client.ensure_connection(MOCK_SHOPIFY_CONNECTION, "http://127.0.0.1:%d" % args.port, "None", "")
        bc_connection, _ = client.ensure_connection(
            MOCK_BC_CONNECTION, "http://127.0.0.1:%d/api/fintegrator/integration/v1.0" % args.port, "None", ""
        )
        integration, _ = client.ensure_integration(MOCK_INTEGRATION, "Mock-backed end-to-end integration.")

        flows = []
        for name in sorted(os.listdir(BUILD_DIR)):
            if name.endswith(".json"):
                with open(os.path.join(BUILD_DIR, name), encoding="utf-8") as handle:
                    flow = json.load(handle)
                # Keep the mock-bound deployment separate from any real deployment: same graph, own name.
                flow["name"] = flow["name"] + MOCK_FLOW_SUFFIX
                flows.append(flow)

        deployed = fintegrator.deploy_flows(
            client,
            flows,
            integration["id"],
            shopify_connection["id"],
            bc_connection["id"],
            {"shopifyApiVersion": args.shopify_api_version, "bcCompanyId": MOCK_COMPANY_ID},
            reset_state=True,
            publish=True,
        )
        deployed_by_name = {item["name"]: item for item in deployed}
        order_flow = deployed_by_name["Shopify to Business Central - Order Create" + MOCK_FLOW_SUFFIX]
        shipment_flow = deployed_by_name["Business Central to Shopify - Order Shipment" + MOCK_FLOW_SUFFIX]
        inventory_flow = deployed_by_name["Business Central to Shopify - Inventory Update" + MOCK_FLOW_SUFFIX]
        print("\ndeployed: order=%s shipment=%s inventory=%s\n" % (order_flow["id"], shipment_flow["id"], inventory_flow["id"]))

        # ================================================================= ORDER CREATE
        print("== order create ==")
        parent, children = flow_runs(client, order_flow["id"])
        state = json.loads(client.get_persisted_state(order_flow["id"])["persistedStateJson"])
        mock_state = mock.state()
        sales_orders = mock_state.get("salesOrders") or []

        report.check("order run 1 parent succeeded", (parent or {}).get("status") == "Success", run_detail(parent))
        report.check("order run 1 fanned out one child per order", len(children) == 3, "%d children" % len(children))
        report.check(
            "order run 1 children all succeeded",
            all(child.get("status") == "Success" for child in children) and len(children) == 3,
            ", ".join(run_detail(child) for child in children),
        )
        report.check("order run 1 created 3 BC sales orders", len(sales_orders) == 3, "%d sales orders" % len(sales_orders))
        report.check(
            "BC sales orders carry the Shopify order ids",
            sorted(order.get("shopifyOrderId", "") for order in sales_orders) == sorted("gid://shopify/Order/510000100%d" % index for index in (1, 2, 3)),
            str([order.get("shopifyOrderId") for order in sales_orders]),
        )
        report.check(
            "BC sales orders carry the Shopify lines",
            all(order.get("fintegratorSalesOrderLines") for order in sales_orders),
            "line counts: %s" % [len(order.get("fintegratorSalesOrderLines") or []) for order in sales_orders],
        )
        watermark_after_first = state.get("ordersWatermark")
        report.check("order watermark advanced to the newest updatedAt", watermark_after_first == max(first_wave), str(watermark_after_first))
        report.check("order state keeps bcCompanyId", state.get("bcCompanyId") == MOCK_COMPANY_ID, str(state.get("bcCompanyId")))

        seeded_ids = [order.get("id") for order in (mock.state().get("orders") or [])]
        report.check(
            "the unpaid seeded order exists in Shopify but was not imported",
            "gid://shopify/Order/5100001999" in seeded_ids
            and "gid://shopify/Order/5100001999" not in [order.get("shopifyOrderId") for order in sales_orders],
            str(seeded_ids),
        )
        ledger_after_first = client.cross_reference_entry_count(LEDGER_LIST)
        report.check("the import ledger recorded every imported order", ledger_after_first == 3, "%d ledger entries" % ledger_after_first)

        # second run: one new paid order appears; the boundary order is now in the ledger
        second_wave = first_wave + ["2026-10-15T10:00:00Z"]
        mock.set_orders(seed_orders(second_wave))
        requests_before = len(mock.requests())
        parent, children = flow_runs(client, order_flow["id"])
        state = json.loads(client.get_persisted_state(order_flow["id"])["persistedStateJson"])
        sales_orders = mock.state().get("salesOrders") or []
        new_requests = mock.requests()[requests_before:]
        order_queries = [item for item in new_requests if "Orders(" in json.dumps(item.get("body") or {})]
        second_query_body = json.dumps(order_queries[0].get("body") if order_queries else {})

        report.check("order run 2 parent succeeded", (parent or {}).get("status") == "Success", run_detail(parent))
        report.check(
            "order run 2 fanned out only the genuinely new order (the rest is filtered by the ledger)",
            len(children) == 1,
            "%d children" % len(children),
        )
        report.check("order run 2 created exactly one more BC sales order", len(sales_orders) == 4, "%d sales orders" % len(sales_orders))
        report.check(
            "order run 2 asked Shopify for orders newer than the watermark minus the overlap",
            "updated_at:>'2026-10-15T09:15:00Z'" in second_query_body,
            second_query_body[:200],
        )
        report.check(
            "order run 2 restricted the pull to paid orders",
            "financial_status:paid" in second_query_body,
            second_query_body[:200],
        )
        report.check("order watermark advanced to the new order", state.get("ordersWatermark") == "2026-10-15T10:00:00Z", str(state.get("ordersWatermark")))

        # third run: nothing new, and the whole overlap window is already in the ledger
        parent, children = flow_runs(client, order_flow["id"])
        report.check("order run 3 with no new orders succeeded", (parent or {}).get("status") == "Success", run_detail(parent))
        report.check(
            "order run 3 called Business Central for nothing",
            len(children) == 0,
            "%d children" % len(children),
        )
        report.check("order run 3 created no new sales orders", len(mock.state().get("salesOrders") or []) == 4, "still 4")

        # fourth run: rewind the watermark; the ledger still shields Business Central
        rewound = dict(state)
        rewound["ordersWatermark"] = max(first_wave)
        client.put_persisted_state(order_flow["id"], rewound)
        parent, children = flow_runs(client, order_flow["id"])
        report.check(
            "order run 4 (rewound window) was fully absorbed by the ledger",
            len(children) == 0,
            "%d children" % len(children),
        )
        report.check(
            "no duplicate sales orders after the rewind",
            len(mock.state().get("salesOrders") or []) == 4,
            "%d sales orders" % len(mock.state().get("salesOrders") or []),
        )
        ledger_after_all = client.cross_reference_entry_count(LEDGER_LIST)
        report.check("the ledger still holds one entry per imported order", ledger_after_all == 4, "%d ledger entries" % ledger_after_all)

        # ================================================================= ORDER SHIPMENT
        print("\n== order shipment ==")
        parent, children = flow_runs(client, shipment_flow["id"])
        state = json.loads(client.get_persisted_state(shipment_flow["id"])["persistedStateJson"])
        mock_state = mock.state()
        fulfillments = mock_state.get("fulfillments") or []

        report.check("shipment run 1 parent succeeded", (parent or {}).get("status") == "Success", run_detail(parent))
        report.check("shipment run 1 fanned out one child", len(children) == 1, "%d children" % len(children))
        report.check(
            "shipment run 1 child succeeded",
            all(child.get("status") == "Success" for child in children) and len(children) == 1,
            ", ".join(run_detail(child) for child in children),
        )
        report.check("Shopify received a fulfillment", len(fulfillments) == 1, "%d fulfillments" % len(fulfillments))
        report.check(
            "fulfillment carries the Business Central tracking number",
            bool(fulfillments) and TRACKING_NUMBER in json.dumps(fulfillments[0]),
            json.dumps(fulfillments[0])[:200] if fulfillments else "none",
        )
        report.check("shipment child tagged the transaction", bool(children) and bool(children[0].get("identifier")), (children[0].get("identifier") if children else ""))
        pushed_rows = [row for row in (mock.state().get("shipmentFeed") or []) if row.get("shopifyPushed")]
        report.check(
            "Business Central feed row was flagged as pushed (replay guard)",
            len(pushed_rows) == 1,
            "%d flagged feed row(s)" % len(pushed_rows),
        )
        report.check("shipment watermark advanced", state.get("shipmentsWatermark") == "2026-10-15T09:40:00Z", str(state.get("shipmentsWatermark")))

        # second run: one more shipment
        mock.add_shipments(
            [
                {
                    "salesShipmentNo": "102302",
                    "salesOrderNo": "101002",
                    "shopifyOrderId": order_two_id,
                    "shopifyOrderName": "#1002",
                    "customerNo": "10000",
                    "shipmentDate": "2026-10-15",
                    "shippingAgentCode": "DHL",
                    "shippingAgentName": "DHL Express",
                    "trackingNo": "JD014600003RU",
                    "shippedQuantity": 2,
                    "fullyShipped": True,
                    "lastModifiedDateTime": "2026-10-15T10:05:00Z",
                    "fintegratorShippedOrderLines": [{"lineNo": 10000, "itemNo": KNOWN_SKUS[0], "sku": KNOWN_SKUS[0], "quantity": 2}],
                }
            ]
        )
        parent, children = flow_runs(client, shipment_flow["id"])
        state = json.loads(client.get_persisted_state(shipment_flow["id"])["persistedStateJson"])
        report.check("shipment run 2 picked up exactly the new shipment", len(children) == 1, "%d children" % len(children))
        report.check(
            "shipment run 2 child succeeded (fulfilled or explicitly skipped)",
            bool(children) and children[0].get("status") == "Success",
            (children[0].get("message") if children else ""),
        )
        report.check("shipment watermark advanced to the new row", state.get("shipmentsWatermark") == "2026-10-15T10:05:00Z", str(state.get("shipmentsWatermark")))

        # ================================================================= INVENTORY
        print("\n== inventory ==")
        parent, children = flow_runs(client, inventory_flow["id"])
        state = json.loads(client.get_persisted_state(inventory_flow["id"])["persistedStateJson"])
        mock_state = mock.state()
        sets = mock_state.get("inventorySets") or []

        report.check("inventory run 1 parent succeeded", (parent or {}).get("status") == "Success", run_detail(parent))
        report.check("inventory run 1 fanned out two children", len(children) == 2, "%d children" % len(children))
        report.check(
            "inventory run 1 children all succeeded",
            len(children) == 2 and all(child.get("status") == "Success" for child in children),
            ", ".join(run_detail(child) for child in children),
        )
        report.check("Shopify received one inventory set", len(sets) == 1, "%d inventory sets" % len(sets))
        report.check(
            "inventory set used the Business Central quantity",
            bool(sets) and float(sets[0].get("quantity", -1)) == 42.0,
            json.dumps(sets[0])[:200] if sets else "none",
        )
        report.check(
            "unmapped SKU was skipped with a clear reason",
            any("no shopify variant matches sku" in (child.get("message") or "").lower() for child in children),
            "; ".join(run_detail(child) for child in children),
        )
        report.check("inventory watermark advanced", state.get("inventoryWatermark") == "2026-10-15T09:46:00Z", str(state.get("inventoryWatermark")))

        mock.add_inventory(
            [
                {
                    "itemNo": KNOWN_SKUS[1],
                    "variantCode": "",
                    "locationCode": "WH1",
                    "sku": KNOWN_SKUS[1],
                    "inventory": 7,
                    "changeQuantity": -1,
                    "lastModifiedDateTime": "2026-10-15T10:10:00Z",
                }
            ]
        )
        parent, children = flow_runs(client, inventory_flow["id"])
        state = json.loads(client.get_persisted_state(inventory_flow["id"])["persistedStateJson"])
        report.check("inventory run 2 picked up exactly the new change", len(children) == 1, "%d children" % len(children))
        report.check("inventory watermark advanced to the new row", state.get("inventoryWatermark") == "2026-10-15T10:10:00Z", str(state.get("inventoryWatermark")))

        # ================================================================= report
        report.summary()
        report_path = os.path.join(ARTIFACT_DIR, "e2e-report.json")
        with open(report_path, "w", encoding="utf-8") as handle:
            json.dump(
                {
                    "generatedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                    "flows": {"order": order_flow["id"], "shipment": shipment_flow["id"], "inventory": inventory_flow["id"]},
                    "checks": report.checks,
                    "mockState": mock.state(),
                },
                handle,
                indent=2,
            )
        print("report written to %s" % report_path)
        return 1 if report.failed else 0

    finally:
        if server and not args.keep_server:
            server.terminate()
            try:
                server.wait(timeout=10)
            except subprocess.TimeoutExpired:
                server.kill()
            print("mock server stopped")


if __name__ == "__main__":
    sys.exit(main())
