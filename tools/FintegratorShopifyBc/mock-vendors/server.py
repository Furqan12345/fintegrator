#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Mock vendor API server for fintegrator.io end-to-end tests (local, no credentials).

One process, one port, two vendors:

  * Shopify Admin GraphQL  ->  POST /admin/api/{version}/graphql.json
  * Business Central OData ->  /api/fintegrator/integration/v1.0/companies({companyId})/{entitySet}
                               (see ../BC-EXTENSION-CONTRACT.md for the entity shapes)
  * Control API            ->  /__control/*  (reset, seeds, failure injection, request log)

Everything else answers 404 with a JSON body naming the path.

State lives in memory only. The request log is appended to
``logs/requests.jsonl`` (one JSON object per line, flushed per request) unless
``--log-dir`` points somewhere else.

Python 3 standard library only.

Run:
    python tools/FintegratorShopifyBc/mock-vendors/server.py --port 5099
"""

import argparse
import json
import os
import re
import signal
import sys
import threading
import uuid
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlparse

SERVER_DIR = os.path.dirname(os.path.abspath(__file__))
DEFAULT_LOG_DIR = os.path.join(SERVER_DIR, "logs")
DEFAULT_PORT = 5099
DEFAULT_HOST = "127.0.0.1"
DEFAULT_SKUS = ["TSHIRT-BLK-M", "MUG-WHITE"]
MAX_BODY_LOG_CHARS = 8000
EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)


# ---------------------------------------------------------------------------
# small helpers
# ---------------------------------------------------------------------------

def now_iso():
    """UTC now as ``2026-10-15T09:10:00Z`` (seconds precision)."""
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def now_ts():
    """UTC now with milliseconds, used for request-log timestamps."""
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def jdump(obj):
    return json.dumps(obj, separators=(",", ":"), ensure_ascii=False)


def json_clone(obj):
    """Deep copy through JSON (all state in this server is JSON-safe)."""
    return json.loads(json.dumps(obj))


def safe_json(raw):
    """Parse a request body defensively; anything unusable becomes ``{}``."""
    if raw is None:
        return {}
    if isinstance(raw, (bytes, bytearray)):
        try:
            raw = raw.decode("utf-8")
        except UnicodeDecodeError:
            raw = raw.decode("utf-8", "replace")
    if not isinstance(raw, str):
        return {}
    text = raw.strip()
    if not text:
        return {}
    try:
        parsed = json.loads(text)
    except (ValueError, TypeError):
        return {}
    if isinstance(parsed, dict):
        return parsed
    return {"_raw": parsed}


def coerce_iso(value, default=None):
    """Keep caller-supplied timestamps byte-for-byte (so echoes are exact)."""
    if value is None:
        return default
    text = str(value).strip().strip("'").strip('"')
    return text if text else default


def _num(value, default=0):
    if isinstance(value, bool):
        return default
    if isinstance(value, (int, float)):
        return value
    if value is None:
        return default
    try:
        text = str(value).strip()
        if not text:
            return default
        as_float = float(text)
    except (TypeError, ValueError):
        return default
    if as_float == int(as_float) and abs(as_float) < 1e15:
        return int(as_float)
    return as_float


def fmt_amount(value):
    """Money as a string, preserving caller strings (``"129.90"`` stays as-is)."""
    if isinstance(value, str) and value.strip():
        return value.strip()
    number = _num(value, 0)
    if isinstance(number, int):
        return str(number)
    text = ("%.2f" % number).rstrip("0").rstrip(".")
    return text or "0"


_TS_RE = re.compile(
    r"^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2}))?(?:\.(\d+))?(Z|z|[+-]\d{2}:?\d{2})?$"
)


def parse_ts(value):
    """Parse an ISO-ish timestamp to an aware UTC datetime, or None."""
    if value is None:
        return None
    text = str(value).strip().strip("'").strip('"')
    if not text:
        return None
    match = _TS_RE.match(text)
    if not match:
        return None
    year, month, day, hour, minute, second, frac, zone = match.groups()
    micro = int((frac or "0")[:6].ljust(6, "0"))
    try:
        stamp = datetime(
            int(year), int(month), int(day), int(hour), int(minute), int(second or 0),
            micro, tzinfo=timezone.utc,
        )
    except ValueError:
        return None
    if zone and zone not in ("Z", "z"):
        sign = 1 if zone[0] == "+" else -1
        offset = timedelta(hours=int(zone[1:3]), minutes=int(zone[-2:]))
        stamp = stamp - sign * offset
    return stamp


def canonical_order_gid(value):
    """Accept ``5100000001``, ``gid://shopify/Order/5100000001`` or ``#1001``."""
    text = str(value or "").strip()
    if not text:
        return ""
    if text.startswith("gid://"):
        return text
    if re.match(r"^\d+$", text):
        return "gid://shopify/Order/" + text
    return text


def gid_tail(value):
    text = str(value or "").strip()
    return text.rsplit("/", 1)[-1] if text else ""


def odata_error(code, message):
    return {"error": {"code": code, "message": message}}


def parse_orderby(text):
    specs = []
    for part in str(text or "").split(","):
        part = part.strip()
        if not part:
            continue
        bits = part.split()
        field = bits[0]
        descending = len(bits) > 1 and bits[1].lower().startswith("desc")
        specs.append((field, descending))
    return specs


def first_param(params, name, default=None):
    values = params.get(name)
    if not values:
        return default
    return values[0]


# ---------------------------------------------------------------------------
# Shopify GraphQL: query detection
# ---------------------------------------------------------------------------

_RE_ORDERS = re.compile(r"(?<![A-Za-z0-9_])orders\s*[({]", re.I)
_RE_FULFILLMENT_ORDERS = re.compile(r"(?<![A-Za-z0-9_])fulfillmentOrders\b\s*[({]", re.I)
_RE_PRODUCT_VARIANTS = re.compile(r"(?<![A-Za-z0-9_])productVariants\b\s*[({]", re.I)
_RE_LOCATIONS = re.compile(r"(?<![A-Za-z0-9_])locations\b\s*[({]", re.I)
_RE_SKU = re.compile(r"sku\s*:\s*'?([A-Za-z0-9._\-]+)'?", re.I)
_RE_INLINE_ORDER_ID = re.compile(r"order\s*\(\s*id\s*:\s*['\"]?([A-Za-z0-9:\/_\-#]+)['\"]?", re.I)
_RE_INLINE_FIRST = re.compile(r"first\s*:\s*(\d+)", re.I)
_RE_INLINE_AFTER = re.compile(r"after\s*:\s*['\"]?([A-Za-z0-9:_\-]+)['\"]?", re.I)
_RE_DELTA_STRICT = re.compile(
    r"updated_?at\s*:\s*(?:>=|>|<=|<|=)?\s*'?"
    r"(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?Z?)'?",
    re.I,
)
_RE_DELTA_LOOSE = re.compile(r"updated_?at\s*:\s*>?\s*'?([0-9T:\-\.Z]+)'?", re.I)
_RE_CURSOR = re.compile(r"^(?:cursor-)?(\d+)$", re.I)
_RE_FINANCIAL_STATUS = re.compile(r"financial_status\s*:\s*([a-z_,\s]+)", re.I)


def extract_updated_at_filter(variables, query_text):
    """Find ``updated_at:>'...'`` in variables.query and/or the inline query."""
    candidates = []
    if isinstance(variables, dict):
        for key in ("query", "queryFilter", "search", "filter"):
            value = variables.get(key)
            if isinstance(value, str) and value.strip():
                candidates.append(value)
    if isinstance(query_text, str) and query_text.strip():
        candidates.append(query_text)
    for text in candidates:
        match = _RE_DELTA_STRICT.search(text) or _RE_DELTA_LOOSE.search(text)
        if match:
            stamp = parse_ts(match.group(1))
            if stamp is not None:
                return stamp
    return None


def extract_financial_status_filter(variables, query_text):
    """Find ``financial_status:paid`` (optionally a comma separated list) in the search string.

    Returns the accepted display_financial_status values lowercased, or None when the query does not
    filter on financial status at all. The real API accepts lowercase values such as paid,
    partially_paid, refunded, voided; the flow relies on this filter to keep unpaid orders out.
    """
    candidates = []
    if isinstance(variables, dict):
        for key in ("query", "queryFilter", "search", "filter"):
            value = variables.get(key)
            if isinstance(value, str) and value.strip():
                candidates.append(value)
    if isinstance(query_text, str) and query_text.strip():
        candidates.append(query_text)

    for text in candidates:
        match = _RE_FINANCIAL_STATUS.search(text)
        if match:
            return {value.strip().lower() for value in match.group(1).split(",") if value.strip()}
    return None


def parse_cursor(after):
    if after is None:
        return 0
    text = str(after).strip()
    if not text:
        return 0
    match = _RE_CURSOR.match(text)
    if match:
        return int(match.group(1))
    tail = re.search(r"(\d+)\s*$", text)
    return int(tail.group(1)) if tail else 0


def contract_order_node(order):
    """The contractual Shopify order node (the flow's C# post-flight script reads it)."""
    node = {
        "id": order["id"],
        "name": order["name"],
        "updatedAt": order["updatedAt"],
        "createdAt": order["createdAt"],
        "currencyCode": order["currencyCode"],
        "displayFinancialStatus": order["displayFinancialStatus"],
        "displayFulfillmentStatus": order["displayFulfillmentStatus"],
        "email": order["email"],
        "note": order["note"],
        "tags": list(order["tags"]),
        "currentTotalPriceSet": json_clone(order["currentTotalPriceSet"]),
        "shippingAddress": json_clone(order["shippingAddress"]),
        "customer": json_clone(order["customer"]),
        "lineItems": {"edges": [{"node": contract_line_node(line)} for line in order["lineItems"]]},
    }
    return node


def contract_line_node(line):
    return {
        "id": line["id"],
        "name": line["name"],
        "sku": line["sku"],
        "quantity": line["quantity"],
        "currentQuantity": line["currentQuantity"],
        "variant": {"id": line["variant"]["id"]},
        "originalUnitPriceSet": json_clone(line["originalUnitPriceSet"]),
    }


# ---------------------------------------------------------------------------
# state
# ---------------------------------------------------------------------------

class Store(object):
    """All mock state (in memory) plus the append-only request log."""

    def __init__(self, log_path):
        self.lock = threading.RLock()
        self.log_lock = threading.Lock()
        self.log_path = log_path
        self.requests = []
        self.request_count = 0
        self.max_requests_in_memory = 5000
        self.reset()

    # -- reset / seed -------------------------------------------------------
    def reset(self):
        """Wipe vendor state, reset counters and failure rules. Keeps the request log."""
        with self.lock:
            self.orders = []
            self.sales_orders = []
            self.fulfillments = []
            self.inventory_sets = []
            self.shipment_feed = []
            self.inventory_feed = []
            self.skus = list(DEFAULT_SKUS)
            self.sku_registry = {}
            self.inventory_levels = {}
            self.failures = []
            self.counters = {
                "salesOrderNo": 101001,
                "orderSeq": 0,
                "lineSeq": 0,
                "variantSeq": 8000,
                "inventoryItemSeq": 8100,
                "fulfillmentSeq": 7000,
                "shipmentEntryNo": 1,
                "inventoryEntryNo": 1,
            }
            self._seed()

    def _seed(self):
        for sku in self.skus:
            self.variant_for_sku(sku)

        self.orders = [
            self.normalise_order({
                "id": "gid://shopify/Order/5100000001",
                "name": "#1001",
                "updatedAt": "2026-10-15T09:10:00Z",
                "createdAt": "2026-10-14T18:00:00Z",
                "currencyCode": "USD",
                "email": "jane@example.com",
                "note": "",
                "tags": ["fintegrator"],
                "totalAmount": "129.90",
                "shippingAddress": {
                    "name": "Jane Doe", "address1": "1 Main Street", "address2": "Apt 4",
                    "city": "Springfield", "provinceCode": "IL", "countryCodeV2": "US",
                    "zip": "62701", "phone": "+15550100",
                },
                "customer": {
                    "id": "gid://shopify/Customer/7001", "email": "jane@example.com",
                    "firstName": "Jane", "lastName": "Doe",
                },
                "lineItems": [
                    {"sku": "TSHIRT-BLK-M", "description": "T-Shirt Black M", "quantity": 2, "unitPrice": "29.95"},
                ],
            }),
            self.normalise_order({
                "id": "gid://shopify/Order/5100000002",
                "name": "#1002",
                "updatedAt": "2026-10-15T09:20:00Z",
                "createdAt": "2026-10-14T19:30:00Z",
                "currencyCode": "USD",
                "email": "sam@example.com",
                "note": "leave at door",
                "totalAmount": "12.50",
                "lineItems": [
                    {"sku": "MUG-WHITE", "description": "Mug White", "quantity": 1, "unitPrice": "12.50"},
                ],
            }),
            self.normalise_order({
                "id": "gid://shopify/Order/5100000003",
                "name": "#1003",
                "updatedAt": "2026-10-15T09:30:00Z",
                "createdAt": "2026-10-15T07:00:00Z",
                "currencyCode": "USD",
                "email": "lee@example.com",
                "totalAmount": "54.95",
                "lineItems": [
                    {"sku": "TSHIRT-BLK-M", "description": "T-Shirt Black M", "quantity": 1, "unitPrice": "29.95"},
                    {"sku": "MUG-WHITE", "description": "Mug White", "quantity": 2, "unitPrice": "12.50"},
                ],
            }),
        ]

        self.shipment_feed = []
        self.add_shipment({
            "entryNo": 1,
            "lastModifiedDateTime": "2026-10-15T09:40:00Z",
            "salesShipmentNo": "102001",
            "salesOrderNo": "101001",
            "shopifyOrderId": "gid://shopify/Order/5100000001",
            "shopifyOrderName": "#1001",
            "customerNo": "C00010",
            "shipmentDate": "2026-10-15",
            "shippingAgentCode": "UPS",
            "shippingAgentName": "UPS Ground",
            "trackingNo": "1Z999AA10123456784",
            "trackingUrl": "https://www.ups.com/track?tracknum=1Z999AA10123456784",
            "shippedQuantity": 2,
            "fullyShipped": True,
            "shopifyPushed": False,
            "fintegratorShippedOrderLines": [
                {"lineNo": 10000, "itemNo": "TSHIRT-BLK-M", "sku": "TSHIRT-BLK-M", "quantity": 2},
            ],
        })
        self.add_shipment({
            "entryNo": 2,
            "lastModifiedDateTime": "2026-10-15T10:05:00Z",
            "salesShipmentNo": "102002",
            "salesOrderNo": "101002",
            "shopifyOrderId": "gid://shopify/Order/5100000002",
            "shopifyOrderName": "#1002",
            "customerNo": "C00011",
            "shipmentDate": "2026-10-15",
            "shippingAgentCode": "DHL",
            "shippingAgentName": "DHL Express",
            "trackingNo": "JD0146000034567890",
            "trackingUrl": "https://www.dhl.com/track?id=JD0146000034567890",
            "shippedQuantity": 1,
            "fullyShipped": True,
            "shopifyPushed": False,
            "fintegratorShippedOrderLines": [
                {"lineNo": 10000, "itemNo": "MUG-WHITE", "sku": "MUG-WHITE", "quantity": 1},
            ],
        })

        self.inventory_feed = []
        self.add_inventory_change({
            "entryNo": 1,
            "lastModifiedDateTime": "2026-10-15T09:45:00Z",
            "itemNo": "TSHIRT-BLK-M",
            "variantCode": "",
            "locationCode": "MAIN",
            "sku": "TSHIRT-BLK-M",
            "inventory": 48,
            "changeQuantity": -2,
        })
        self.add_inventory_change({
            "entryNo": 2,
            "lastModifiedDateTime": "2026-10-15T10:10:00Z",
            "itemNo": "MUG-WHITE",
            "variantCode": "",
            "locationCode": "MAIN",
            "sku": "MUG-WHITE",
            "inventory": 75,
            "changeQuantity": -1,
        })

    # -- sku / variant registry --------------------------------------------
    def variant_for_sku(self, sku):
        """Stable ProductVariant / InventoryItem ids per SKU (8001/8101 for the first)."""
        with self.lock:
            record = self.sku_registry.get(sku)
            if record is None:
                self.counters["variantSeq"] += 1
                self.counters["inventoryItemSeq"] += 1
                record = {
                    "variantId": "gid://shopify/ProductVariant/%d" % self.counters["variantSeq"],
                    "inventoryItemId": "gid://shopify/InventoryItem/%d" % self.counters["inventoryItemSeq"],
                }
                self.sku_registry[sku] = record
            return record

    def set_skus(self, items):
        """Replace the known-SKU list (the single source of truth for SKU validity)."""
        with self.lock:
            skus = []
            for item in items or []:
                if isinstance(item, dict):
                    sku = item.get("sku") or item.get("itemNo") or item.get("ShopifySKU")
                else:
                    sku = item
                sku = str(sku or "").strip()
                if sku and sku not in skus:
                    skus.append(sku)
            self.skus = skus
            for sku in skus:
                self.variant_for_sku(sku)
            return len(skus)

    def known_sku(self, sku):
        with self.lock:
            return sku in self.skus

    # -- orders -------------------------------------------------------------
    def normalise_order(self, spec):
        """Turn a full node or the minimal ``{id,name,updatedAt,lineItems:[...]}`` shape
        into the internal order record (private ``_``-keys are stripped on output)."""
        spec = dict(spec or {})
        order_id = canonical_order_gid(spec.get("id") or spec.get("shopifyOrderId"))
        if not order_id:
            self.counters["orderSeq"] += 1
            order_id = "gid://shopify/Order/%d" % (5100000000 + self.counters["orderSeq"])
        self.counters["orderSeq"] += 1
        order_seq = self.counters["orderSeq"]

        updated = coerce_iso(spec.get("updatedAt") or spec.get("updated_at"), now_iso())
        created = coerce_iso(spec.get("createdAt") or spec.get("created_at"), updated)
        currency = str(spec.get("currencyCode") or "USD")

        raw_lines = spec.get("lineItems")
        if isinstance(raw_lines, dict):
            edges = raw_lines.get("edges") or []
            raw_lines = [edge.get("node", edge) for edge in edges if isinstance(edge, dict)]
        if not isinstance(raw_lines, list):
            raw_lines = []
        lines = [self._make_line(raw, currency, index) for index, raw in enumerate(raw_lines) if isinstance(raw, dict)]

        shipping = spec.get("shippingAddress")
        if not isinstance(shipping, dict):
            shipping = None
            if any(spec.get(key) for key in ("shippingName", "shippingAddress1", "shippingCity", "shippingPostCode")):
                shipping = {
                    "name": spec.get("shippingName") or "",
                    "address1": spec.get("shippingAddress1") or "",
                    "address2": spec.get("shippingAddress2") or "",
                    "city": spec.get("shippingCity") or "",
                    "provinceCode": spec.get("shippingProvinceCode") or "",
                    "countryCodeV2": spec.get("shippingCountryCode") or "",
                    "zip": spec.get("shippingPostCode") or spec.get("shippingZip") or "",
                    "phone": spec.get("shippingPhone") or "",
                }

        customer = spec.get("customer") if isinstance(spec.get("customer"), dict) else None

        total = spec.get("totalAmount")
        if total is None:
            total = _num(spec.get("currentTotalPriceSet", {}).get("shopMoney", {}).get("amount") if isinstance(spec.get("currentTotalPriceSet"), dict) else None, 0)
            if total == 0:
                total = sum(_num(line["quantity"], 0) * _num(line["originalUnitPriceSet"]["shopMoney"]["amount"], 0) for line in lines)

        if isinstance(spec.get("currentTotalPriceSet"), dict):
            total_price_set = json_clone(spec["currentTotalPriceSet"])
        else:
            total_price_set = {"shopMoney": {"amount": fmt_amount(total), "currencyCode": currency}}

        return {
            "id": order_id,
            "name": str(spec.get("name") or ("#" + gid_tail(order_id))),
            "updatedAt": updated,
            "createdAt": created,
            "currencyCode": currency,
            "displayFinancialStatus": str(spec.get("displayFinancialStatus") or "PAID"),
            "displayFulfillmentStatus": str(spec.get("displayFulfillmentStatus") or "UNFULFILLED"),
            "email": str(spec.get("email") or ""),
            "note": str(spec.get("note") or ""),
            "tags": [str(tag) for tag in (spec.get("tags") if isinstance(spec.get("tags"), list) else ["fintegrator"])],
            "currentTotalPriceSet": total_price_set,
            "shippingAddress": shipping,
            "customer": customer,
            "lineItems": lines,
            "_foId": "gid://shopify/FulfillmentOrder/%d" % (6000 + order_seq),
        }

    def _make_line(self, raw, currency, index):
        self.counters["lineSeq"] += 1
        seq = self.counters["lineSeq"]
        sku = str(raw.get("sku") or raw.get("itemNo") or "").strip()
        quantity = _num(raw.get("quantity"), 0)
        if raw.get("currentQuantity") is None:
            current = quantity
        else:
            current = _num(raw.get("currentQuantity"), quantity)

        price = raw.get("unitPrice")
        price_currency = currency
        if isinstance(raw.get("originalUnitPriceSet"), dict):
            money = raw["originalUnitPriceSet"].get("shopMoney") or {}
            if price is None:
                price = money.get("amount")
            price_currency = money.get("currencyCode") or currency
        price = fmt_amount(price)

        variant = raw.get("variant") if isinstance(raw.get("variant"), dict) else {}
        variant_id = variant.get("id") or self.variant_for_sku(sku)["variantId"]

        return {
            "id": raw.get("id") or ("gid://shopify/LineItem/%d" % (9000 + seq)),
            "name": str(raw.get("name") or raw.get("description") or sku or ("Line %d" % (index + 1))),
            "sku": sku,
            "quantity": quantity,
            "currentQuantity": current,
            "variant": {"id": variant_id},
            "originalUnitPriceSet": {"shopMoney": {"amount": price, "currencyCode": price_currency}},
            "_foLineId": "gid://shopify/FulfillmentOrderLineItem/%d" % (6500 + seq),
        }

    def replace_orders(self, specs):
        with self.lock:
            self.orders = []
            self.counters["orderSeq"] = 0
            self.counters["lineSeq"] = 0
            for spec in specs or []:
                if isinstance(spec, dict):
                    self.orders.append(self.normalise_order(spec))
            return len(self.orders)

    def find_order(self, order_id):
        """Resolve an order by GID, numeric id, name (``#1001``) or FulfillmentOrder id."""
        with self.lock:
            wanted = canonical_order_gid(order_id)
            tail = gid_tail(wanted)
            for order in self.orders:
                if order["id"] == wanted or order["name"] == str(order_id or ""):
                    return order
            if tail and re.match(r"^\d+$", tail):
                for order in self.orders:
                    if gid_tail(order["id"]) == tail:
                        return order
            for order in self.orders:
                if order["_foId"] == str(order_id or "") or gid_tail(order["_foId"]) == gid_tail(str(order_id or "")):
                    return order
            return None

    def find_order_by_fulfillment_order(self, fulfillment_order_id):
        text = str(fulfillment_order_id or "").strip()
        if not text:
            return None
        with self.lock:
            for order in self.orders:
                if order["_foId"] == text or gid_tail(order["_foId"]) == gid_tail(text):
                    return order
            return None

    # -- fulfillments / inventory sets --------------------------------------
    def add_fulfillment(self, record):
        with self.lock:
            self.counters["fulfillmentSeq"] += 1
            fulfillment_id = "gid://shopify/Fulfillment/%d" % self.counters["fulfillmentSeq"]
            record = json_clone(record)
            record["id"] = fulfillment_id
            self.fulfillments.append(record)
            return record

    def add_inventory_set(self, record):
        with self.lock:
            record = {
                "inventoryItemId": record.get("inventoryItemId"),
                "locationId": record.get("locationId"),
                "quantity": record.get("quantity"),
                "name": record.get("name") or "available",
                "reason": record.get("reason") or "correction",
            }
            self.inventory_sets.append(record)
            return record

    def inventory_level(self, inventory_item_id, location_id):
        with self.lock:
            key = "%s|%s" % (inventory_item_id, location_id)
            return _num(self.inventory_levels.get(key), 0)

    def set_inventory_level(self, inventory_item_id, location_id, quantity):
        with self.lock:
            key = "%s|%s" % (inventory_item_id, location_id)
            self.inventory_levels[key] = quantity

    # -- BC feeds -----------------------------------------------------------
    def clear_shipments(self):
        with self.lock:
            self.shipment_feed = []
            self.counters["shipmentEntryNo"] = 1

    def clear_inventory_changes(self):
        with self.lock:
            self.inventory_feed = []
            self.counters["inventoryEntryNo"] = 1

    def add_shipment(self, row):
        with self.lock:
            row = dict(row or {})
            entry = row.get("entryNo")
            if entry in (None, ""):
                entry = self.counters["shipmentEntryNo"]
            entry = _num(entry, self.counters["shipmentEntryNo"])
            self.counters["shipmentEntryNo"] = max(self.counters["shipmentEntryNo"], int(_num(entry, 0)) + 1)
            record = {
                "id": str(row.get("id") or uuid.uuid4()),
                "entryNo": entry,
                "lastModifiedDateTime": coerce_iso(row.get("lastModifiedDateTime"), now_iso()),
                "salesShipmentNo": str(row.get("salesShipmentNo") or ""),
                "salesOrderNo": str(row.get("salesOrderNo") or ""),
                "shopifyOrderId": str(row.get("shopifyOrderId") or ""),
                "shopifyOrderName": str(row.get("shopifyOrderName") or ""),
                "customerNo": str(row.get("customerNo") or ""),
                "shipmentDate": str(row.get("shipmentDate") or ""),
                "shippingAgentCode": str(row.get("shippingAgentCode") or ""),
                "shippingAgentName": str(row.get("shippingAgentName") or ""),
                "trackingNo": str(row.get("trackingNo") or ""),
                "trackingUrl": str(row.get("trackingUrl") or ""),
                "shippedQuantity": _num(row.get("shippedQuantity"), 0),
                "fullyShipped": bool(row.get("fullyShipped", False)),
                "shopifyPushed": bool(row.get("shopifyPushed", False)),
            }
            raw_lines = row.get("fintegratorShippedOrderLines")
            if not isinstance(raw_lines, list):
                raw_lines = []
            lines = []
            for raw in raw_lines:
                if not isinstance(raw, dict):
                    continue
                lines.append({
                    "lineNo": _num(raw.get("lineNo"), 10000 + 10000 * len(lines)),
                    "itemNo": str(raw.get("itemNo") or raw.get("sku") or ""),
                    "sku": str(raw.get("sku") or raw.get("itemNo") or ""),
                    "quantity": _num(raw.get("quantity"), 0),
                })
            record["fintegratorShippedOrderLines"] = lines
            self.shipment_feed.append(record)
            return record

    def add_inventory_change(self, row):
        with self.lock:
            row = dict(row or {})
            entry = row.get("entryNo")
            if entry in (None, ""):
                entry = self.counters["inventoryEntryNo"]
            entry = _num(entry, self.counters["inventoryEntryNo"])
            self.counters["inventoryEntryNo"] = max(self.counters["inventoryEntryNo"], int(_num(entry, 0)) + 1)
            record = {
                "id": str(row.get("id") or uuid.uuid4()),
                "entryNo": entry,
                "lastModifiedDateTime": coerce_iso(row.get("lastModifiedDateTime"), now_iso()),
                "itemNo": str(row.get("itemNo") or ""),
                "variantCode": str(row.get("variantCode") or ""),
                "locationCode": str(row.get("locationCode") or ""),
                "sku": str(row.get("sku") or row.get("itemNo") or ""),
                "inventory": _num(row.get("inventory"), 0),
                "changeQuantity": _num(row.get("changeQuantity"), 0),
            }
            self.inventory_feed.append(record)
            return record

    # -- sales orders -------------------------------------------------------
    def find_sales_order(self, shopify_order_id):
        with self.lock:
            for order in self.sales_orders:
                if order.get("shopifyOrderId") == shopify_order_id:
                    return order
            return None

    def add_sales_order(self, entity):
        with self.lock:
            stored = json_clone(entity)
            self.sales_orders.append(stored)
            return stored

    def find_sales_order_by_key(self, key):
        with self.lock:
            for order in self.sales_orders:
                if key in (order.get("id"), order.get("salesOrderNo"), order.get("shopifyOrderId")):
                    return order
            return None

    # -- failure injection --------------------------------------------------
    def add_failure_rule(self, rule):
        with self.lock:
            self.failures.append(rule)
            return json_clone(rule)

    def take_failure(self, path):
        """Pop one hit off the first matching failure rule (or None)."""
        with self.lock:
            for rule in list(self.failures):
                pattern = rule.get("path") or ""
                if not pattern:
                    continue
                if pattern.endswith("*"):
                    matched = path.startswith(pattern[:-1])
                else:
                    matched = path == pattern or path.startswith(pattern)
                if not matched:
                    continue
                times = _num(rule.get("times"), 1)
                if times > 0:
                    rule["times"] = times - 1
                    if rule["times"] <= 0:
                        self.failures.remove(rule)
                return {"status": rule.get("status"), "graphqlError": rule.get("graphqlError")}
            return None

    def failure_rules(self):
        with self.lock:
            return json_clone(self.failures)

    # -- request log --------------------------------------------------------
    def record_request(self, entry):
        with self.log_lock:
            self.request_count += 1
            self.requests.append(entry)
            if len(self.requests) > self.max_requests_in_memory:
                del self.requests[: len(self.requests) - self.max_requests_in_memory]
            try:
                with open(self.log_path, "a", encoding="utf-8") as handle:
                    handle.write(jdump(entry) + "\n")
                    handle.flush()
            except OSError:
                pass

    def requests_snapshot(self, limit):
        with self.log_lock:
            if limit is None or limit >= len(self.requests):
                return json_clone(self.requests)
            return json_clone(self.requests[-limit:])

    # -- snapshots ----------------------------------------------------------
    def state_snapshot(self):
        with self.lock:
            return {
                "orders": [contract_order_node(order) for order in self.orders],
                "salesOrders": json_clone(self.sales_orders),
                "fulfillments": json_clone(self.fulfillments),
                "inventorySets": json_clone(self.inventory_sets),
                "shipmentFeed": json_clone(self.shipment_feed),
                "inventoryFeed": json_clone(self.inventory_feed),
                "items": list(self.skus),
                "counters": json_clone(self.counters),
                "requests": self.request_count,
            }


# ---------------------------------------------------------------------------
# Shopify GraphQL handlers
# ---------------------------------------------------------------------------

def _gql_orders(store, variables, query_text=None):
    first = variables.get("first")
    if first in (None, "") and query_text:
        inline_first = _RE_INLINE_FIRST.search(query_text)
        if inline_first:
            first = inline_first.group(1)
    first = max(0, min(250, int(_num(first, 25))))
    after = variables.get("after")
    if after in (None, ""):
        after = variables.get("cursor")
    if after in (None, "") and query_text:
        inline_after = _RE_INLINE_AFTER.search(query_text)
        if inline_after:
            after = inline_after.group(1)
    offset = parse_cursor(after)
    since = extract_updated_at_filter(variables, query_text)
    accepted_statuses = extract_financial_status_filter(variables, query_text)

    with store.lock:
        eligible = []
        for index, order in enumerate(store.orders):
            if since is not None:
                stamp = parse_ts(order.get("updatedAt"))
                if stamp is None or not stamp > since:
                    continue
            if accepted_statuses is not None:
                status = str(order.get("displayFinancialStatus") or "").strip().lower()
                if status not in accepted_statuses:
                    continue
            eligible.append((parse_ts(order.get("updatedAt")) or EPOCH, index, order))
        eligible.sort(key=lambda item: (item[0], item[1]))
        ordered = [item[2] for item in eligible]

    page = ordered[offset:offset + first] if first > 0 else []
    has_next = (offset + len(page)) < len(ordered)
    end_cursor = ("cursor-%d" % (offset + len(page))) if page else None
    return {
        "data": {
            "orders": {
                "pageInfo": {"hasNextPage": has_next, "endCursor": end_cursor},
                "edges": [{"node": contract_order_node(order)} for order in page],
            }
        }
    }


def _gql_fulfillment_orders(store, variables, query_text=None):
    order_ref = None
    for key in ("id", "orderId", "order_id", "order"):
        value = variables.get(key)
        if value not in (None, ""):
            order_ref = value
            break
    if order_ref is None and query_text:
        inline = _RE_INLINE_ORDER_ID.search(query_text)
        if inline:
            order_ref = inline.group(1)
    order = store.find_order(order_ref) if order_ref is not None else None
    if order is None:
        return {"data": {"order": None}}
    nodes = []
    for line in order["lineItems"]:
        nodes.append({
            "id": order["_foId"],
            "status": "OPEN",
            "lineItems": {
                "nodes": [
                    {
                        "id": line["_foLineId"],
                        "remainingQuantity": line["currentQuantity"],
                        "lineItem": {"id": line["id"], "sku": line["sku"]},
                    }
                ]
            },
        })
    return {
        "data": {
            "order": {
                "id": order["id"],
                "name": order["name"],
                "fulfillmentOrders": {"nodes": nodes},
            }
        }
    }


def _user_error(field, message):
    return {"field": field, "message": message}


def _gql_fulfillment_create(store, variables):
    payload = variables.get("fulfillment")
    if not isinstance(payload, dict):
        payload = variables.get("input")
    if not isinstance(payload, dict):
        payload = {}

    entries = payload.get("lineItemsByFulfillmentOrder")
    if not isinstance(entries, list) or not entries:
        return {
            "data": {
                "fulfillmentCreateV2": {
                    "fulfillment": None,
                    "userErrors": [_user_error(
                        "lineItemsByFulfillmentOrder",
                        "Mock: lineItemsByFulfillmentOrder is required and must not be empty.",
                    )],
                }
            }
        }

    resolved = []
    for entry in entries:
        if not isinstance(entry, dict):
            continue
        order = store.find_order_by_fulfillment_order(entry.get("fulfillmentOrderId"))
        if order is not None:
            resolved.append((order, entry))

    if not resolved:
        return {
            "data": {
                "fulfillmentCreateV2": {
                    "fulfillment": None,
                    "userErrors": [_user_error(
                        "lineItemsByFulfillmentOrder",
                        "Mock: no known fulfillment order in lineItemsByFulfillmentOrder "
                        "(call fulfillmentOrders first, ids are regenerated by /__control/reset).",
                    )],
                }
            }
        }

    tracking = payload.get("trackingInfo") if isinstance(payload.get("trackingInfo"), dict) else {}
    number = tracking.get("number")
    company = tracking.get("company")
    order, entry = resolved[0]

    line_refs = []
    raw_lines = entry.get("fulfillmentOrderLineItems")
    if isinstance(raw_lines, list):
        for raw in raw_lines:
            if isinstance(raw, dict):
                line_refs.append({"id": raw.get("id"), "quantity": _num(raw.get("quantity"), 0)})

    record = store.add_fulfillment({
        "orderId": order["id"],
        "orderName": order["name"],
        "fulfillmentOrderId": entry.get("fulfillmentOrderId"),
        "trackingNumber": number,
        "trackingCompany": company,
        "lineItems": line_refs,
    })

    return {
        "data": {
            "fulfillmentCreateV2": {
                "fulfillment": {
                    "id": record["id"],
                    "status": "SUCCESS",
                    "trackingInfo": [{"number": number, "company": company, "url": None}],
                },
                "userErrors": [],
            }
        }
    }


def _gql_product_variants(store, variables, query_text=None):
    sku = None
    for candidate in [variables.get(key) for key in ("query", "search", "sku")] + [query_text]:
        if isinstance(candidate, str) and candidate.strip():
            match = _RE_SKU.search(candidate)
            if match:
                sku = match.group(1)
                break
    if sku is None:
        sku = str(variables.get("sku") or "").strip() or None

    nodes = []
    if sku and store.known_sku(sku):
        variant = store.variant_for_sku(sku)
        nodes.append({
            "id": variant["variantId"],
            "sku": sku,
            "inventoryItem": {"id": variant["inventoryItemId"], "tracked": True},
        })
    return {"data": {"productVariants": {"nodes": nodes}}}


def _gql_locations():
    return {
        "data": {
            "locations": {
                "nodes": [{"id": "gid://shopify/Location/9001", "name": "Main Warehouse", "isActive": True}]
            }
        }
    }


def _gql_inventory_set_quantities(store, variables):
    payload = variables.get("input")
    if not isinstance(payload, dict):
        payload = variables.get("inventorySetQuantities") if isinstance(variables.get("inventorySetQuantities"), dict) else {}
    reason = payload.get("reason") or "correction"
    quantities = payload.get("quantities")
    user_errors = []
    changes = []

    if not isinstance(quantities, list) or not quantities:
        user_errors.append(_user_error("quantities", "Mock: quantities is required and must not be empty."))
    else:
        for index, entry in enumerate(quantities):
            if not isinstance(entry, dict):
                user_errors.append(_user_error(
                    "quantities[%d]" % index, "Mock: every quantities entry must be an object."))
                continue
            inventory_item_id = entry.get("inventoryItemId")
            location_id = entry.get("locationId")
            name = entry.get("name") or "available"
            missing = []
            if not inventory_item_id:
                missing.append("inventoryItemId")
            if not location_id:
                missing.append("locationId")
            if missing:
                user_errors.append(_user_error(
                    missing[0],
                    "Mock: quantities[%d] is missing %s (both are required)." % (index, " and ".join(missing)),
                ))
                continue
            quantity = _num(entry.get("quantity"), 0)
            previous = store.inventory_level(inventory_item_id, location_id)
            store.set_inventory_level(inventory_item_id, location_id, quantity)
            store.add_inventory_set({
                "inventoryItemId": inventory_item_id,
                "locationId": location_id,
                "quantity": quantity,
                "name": name,
                "reason": reason,
            })
            changes.append({
                "name": name,
                "delta": _num(quantity, 0) - _num(previous, 0),
                "quantityAfterChange": quantity,
            })

    return {
        "data": {
            "inventorySetQuantities": {
                "inventoryAdjustmentGroup": {"reason": reason, "changes": changes},
                "userErrors": user_errors,
            }
        }
    }


def shopify_graphql(store, query_text, variables):
    """Dispatch a Shopify GraphQL request by inspecting the query text.

    A real Shopify payload contains every top-level field the query selected, so a query that asks
    for more than one connection (the inventory flow asks for ``productVariants`` *and*
    ``locations`` in a single call) gets the extra fields merged into the same ``data`` object.
    """
    query = query_text or ""
    lowered = query.lower()
    try:
        payload = None
        if "inventorysetquantities" in lowered:
            payload = _gql_inventory_set_quantities(store, variables)
        elif "fulfillmentcreatev2" in lowered:
            payload = _gql_fulfillment_create(store, variables)
        elif _RE_FULFILLMENT_ORDERS.search(query):
            payload = _gql_fulfillment_orders(store, variables, query)
        elif _RE_PRODUCT_VARIANTS.search(query):
            payload = _gql_product_variants(store, variables, query)
        elif _RE_LOCATIONS.search(query):
            payload = _gql_locations()
        elif _RE_ORDERS.search(query):
            payload = _gql_orders(store, variables, query)

        if payload is not None:
            data = payload.get("data")
            if isinstance(data, dict):
                if "locations" not in data and _RE_LOCATIONS.search(query):
                    data["locations"] = _gql_locations()["data"]["locations"]
            return payload, 200
    except Exception as exc:  # never kill the connection
        return {"errors": [{"message": "Mock server error: %s: %s" % (type(exc).__name__, exc)}]}, 200
    summary = " ".join(query.split())[:160] or "<empty query>"
    return {"errors": [{"message": "Mock: no Shopify operation matches this query: %s" % summary}]}, 200


# ---------------------------------------------------------------------------
# Business Central OData handlers
# ---------------------------------------------------------------------------

BC_PATH_RE = re.compile(
    r"^/api/fintegrator/integration/(?:v[^/]+/)?companies\((?P<company>[^)]*)\)/(?P<entity>[A-Za-z0-9_]+)"
    r"(?:\((?P<key>[^)]*)\))?/?$"
)
BC_ENTITIES = ("fintegratorSalesOrders", "fintegratorShippedOrders", "fintegratorInventoryChanges")


def bc_context(host, company, entity):
    return "http://%s/api/fintegrator/integration/v1.0/$metadata#companies(%s)/%s" % (host or "localhost", company, entity)


def entity_etag(entity, row):
    key = row.get("entryNo")
    if key in (None, ""):
        key = row.get("id") or row.get("salesOrderNo") or ""
    return 'W/"%s=%s"' % (entity, key)


def expand_row(row, expand):
    """Attach navigation properties only when explicitly expanded."""
    out = dict(row)
    if (expand or "").strip().lower() == "fintegratorshippedorderlines":
        lines = out.get("fintegratorShippedOrderLines")
        out["fintegratorShippedOrderLines"] = json_clone(lines) if isinstance(lines, list) else []
    else:
        out.pop("fintegratorShippedOrderLines", None)
    return out


def matches_feed_filter(filter_text, row):
    """Tolerant BC ``$filter`` evaluation for ``lastModifiedDateTime`` / ``entryNo``."""
    if not filter_text:
        return True
    gt_ts = re.search(r"(?<![A-Za-z0-9_])lastModifiedDateTime\s+gt\s+'?([^'\)\s,]+)'?", filter_text, re.I)
    ge_ts = re.search(r"(?<![A-Za-z0-9_])lastModifiedDateTime\s+ge\s+'?([^'\)\s,]+)'?", filter_text, re.I)
    eq_ts = re.search(r"(?<![A-Za-z0-9_])lastModifiedDateTime\s+eq\s+'?([^'\)\s,]+)'?", filter_text, re.I)
    entry_gt = re.search(r"(?<![A-Za-z0-9_])entryNo\s+gt\s+(-?\d+)", filter_text, re.I)

    gt_stamp = parse_ts(gt_ts.group(1)) if gt_ts else None
    ge_stamp = parse_ts(ge_ts.group(1)) if ge_ts else None
    eq_stamp = parse_ts(eq_ts.group(1)) if eq_ts else None
    entry_floor = int(entry_gt.group(1)) if entry_gt else None

    if gt_stamp is None and ge_stamp is None and eq_stamp is None and entry_floor is None:
        return True  # unrecognised filter: stay tolerant, filter nothing

    row_stamp = parse_ts(row.get("lastModifiedDateTime"))
    row_entry = _num(row.get("entryNo"), 0)

    if gt_stamp is not None:
        if row_stamp is None:
            return False
        if row_stamp > gt_stamp:
            return True
        # watermark tie-breaker: lastModifiedDateTime eq X and entryNo gt N
        if eq_stamp is not None and entry_floor is not None:
            return row_stamp == eq_stamp and row_entry > entry_floor
        return False
    if ge_stamp is not None:
        if row_stamp is None or row_stamp < ge_stamp:
            return False
        if eq_stamp is None:
            return True
    if eq_stamp is not None and (row_stamp is None or row_stamp != eq_stamp):
        return False
    if entry_floor is not None and row_entry <= entry_floor:
        return False
    return True


def query_feed(rows, query_string, expandable=False):
    """Apply $filter / $orderby / $top / $expand to feed rows."""
    params = parse_qs(query_string or "", keep_blank_values=True)
    filter_text = first_param(params, "$filter")
    orderby_text = first_param(params, "$orderby")
    top_text = first_param(params, "$top")
    expand = first_param(params, "$expand") or ""

    selected = [row for row in rows if matches_feed_filter(filter_text, row)]

    specs = parse_orderby(orderby_text)
    if not specs:
        specs = [("lastModifiedDateTime", False), ("entryNo", False)]
    for field, descending in reversed(specs):
        if field == "lastModifiedDateTime":
            selected.sort(key=lambda row: parse_ts(row.get(field)) or EPOCH, reverse=descending)
        elif field == "entryNo":
            selected.sort(key=lambda row: _num(row.get(field), 0), reverse=descending)
        else:
            selected.sort(key=lambda row: str(row.get(field) or ""), reverse=descending)

    top = 100
    if top_text not in (None, ""):
        parsed_top = _num(top_text, 100)
        if isinstance(parsed_top, (int, float)):
            top = int(parsed_top)
    top = max(0, min(1000, top))
    selected = selected[:top]

    return [expand_row(row, expand) if expandable else dict(row) for row in selected]


def bc_sales_order_list(store, company, query_string, host):
    params = parse_qs(query_string or "", keep_blank_values=True)
    filter_text = first_param(params, "$filter") or ""
    top_text = first_param(params, "$top")
    expand = (first_param(params, "$expand") or "").strip().lower()

    with store.lock:
        rows = json_clone(store.sales_orders)

    def keep(row):
        if not filter_text:
            return True
        for field in ("shopifyOrderId", "salesOrderNo", "status", "customerNo", "id"):
            # (?<![A-Za-z0-9_]) keeps "id" from matching inside "shopifyOrderId"
            match = re.search(r"(?<![A-Za-z0-9_])%s\s+eq\s+'?([^'\)\s,]+)'?" % field, filter_text, re.I)
            if match and str(row.get(field)) != match.group(1):
                return False
        return True

    rows = [row for row in rows if keep(row)]
    if top_text not in (None, ""):
        rows = rows[: max(0, min(1000, int(_num(top_text, 100))))]
    if expand in ("fintegratorsalesorderlines", "lines"):
        for row in rows:
            row.setdefault("fintegratorSalesOrderLines", [])
    return {
        "@odata.context": bc_context(host, company, "fintegratorSalesOrders"),
        "value": [dict(row, **{"@odata.etag": entity_etag("fintegratorSalesOrders", row)}) for row in rows],
    }


def bc_create_sales_order(store, body, company, host):
    """POST deep insert into fintegratorSalesOrders (creates or reports the duplicate)."""
    shopify_order_id = str(body.get("shopifyOrderId") or "").strip()
    if not shopify_order_id:
        return 400, odata_error(
            "Application_DialogException",
            "shopifyOrderId must not be empty (the extension keys the staging record on it).",
        ), {}

    raw_lines = body.get("fintegratorSalesOrderLines")
    if raw_lines is None:
        raw_lines = []
    if not isinstance(raw_lines, list):
        return 400, odata_error(
            "Application_DialogException",
            "fintegratorSalesOrderLines must be an array of lines.",
        ), {}

    prepared_lines = []
    for index, raw in enumerate(raw_lines):
        if not isinstance(raw, dict):
            return 400, odata_error(
                "Application_DialogException",
                "fintegratorSalesOrderLines[%d] must be an object." % index,
            ), {}
        sku = str(raw.get("sku") or "").strip()
        if not sku:
            return 400, odata_error(
                "Application_DialogException",
                "fintegratorSalesOrderLines[%d] is missing sku." % index,
            ), {}
        if raw.get("quantity") in (None, ""):
            return 400, odata_error(
                "Application_DialogException",
                "fintegratorSalesOrderLines[%d] (sku %s) is missing quantity." % (index, sku),
            ), {}
        quantity = _num(raw.get("quantity"), None)
        if quantity is None:
            return 400, odata_error(
                "Application_DialogException",
                "fintegratorSalesOrderLines[%d] (sku %s) has a non-numeric quantity." % (index, sku),
            ), {}
        if not store.known_sku(sku):
            return 400, odata_error(
                "Application_DialogException",
                "Shopify SKU %s does not match any Business Central item." % sku,
            ), {}
        prepared_lines.append((raw, sku, quantity))

    with store.lock:
        existing = store.find_sales_order(shopify_order_id)
        if existing is not None:
            duplicate = json_clone(existing)
            duplicate["status"] = "Duplicate"
            duplicate["createdSalesOrder"] = False
            duplicate["errorMessage"] = ""
            return 201, duplicate, {}

        entity = json_clone(body)
        entity["id"] = str(uuid.uuid4())
        entity["shopifyOrderId"] = shopify_order_id
        entity["shopifyOrderName"] = str(body.get("shopifyOrderName") or "")
        entity["customerNo"] = str(body.get("customerNo") or "")
        if not entity["customerNo"]:
            entity["customerNo"] = "C00010"  # FIN Integrator Setup."Default Customer No."
        entity["orderDate"] = str(body.get("orderDate") or datetime.now(timezone.utc).strftime("%Y-%m-%d"))
        entity["currencyCode"] = str(body.get("currencyCode") or "USD")
        entity["note"] = str(body.get("note") or "")
        entity["status"] = "Created"
        entity["salesOrderNo"] = str(store.counters["salesOrderNo"])
        store.counters["salesOrderNo"] += 1
        entity["errorMessage"] = ""
        entity["createdSalesOrder"] = True

        lines = []
        for position, (raw, sku, quantity) in enumerate(prepared_lines):
            line = json_clone(raw)
            line_no = raw.get("lineNo")
            if line_no in (None, ""):
                line_no = 10000 * (position + 1)
            line["lineNo"] = _num(line_no, 10000 * (position + 1))
            line["sku"] = sku
            line["quantity"] = quantity
            line["description"] = str(raw.get("description") or sku)
            if line.get("unitPrice") in (None, ""):
                line["unitPrice"] = 0
            lines.append(line)
        entity["fintegratorSalesOrderLines"] = lines
        if entity.get("totalAmount") in (None, ""):
            entity["totalAmount"] = sum(_num(line["quantity"], 0) * _num(line["unitPrice"], 0) for line in lines)

        stored = store.add_sales_order(entity)

    out = json_clone(stored)
    out["@odata.etag"] = entity_etag("fintegratorSalesOrders", out)
    return 201, out, {"Location": "%s/api/fintegrator/integration/v1.0/companies(%s)/fintegratorSalesOrders(%s)" % (
        "http://%s" % (host or "localhost"), company, out["id"])}


def bc_route(store, method, path, query_string, json_body, host):
    """Business Central routing. Returns (status, payload, headers)."""
    match = BC_PATH_RE.match(path)
    if not match:
        return 404, odata_error(
            "NotFound",
            "Mock: no Business Central route for %s (expected "
            "/api/fintegrator/integration/v1.0/companies({companyId})/{entitySet})." % path,
        ), {"OData-Version": "4.0"}

    company = match.group("company") or ""
    entity = match.group("entity")
    key = (match.group("key") or "").strip().strip("'\"") or None
    headers = {"OData-Version": "4.0"}

    if entity not in BC_ENTITIES:
        return 404, odata_error(
            "NotFound",
            "Mock: unknown entity set '%s' (known: %s)." % (entity, ", ".join(BC_ENTITIES)),
        ), headers

    # ---------------------------------------------------------------- sales orders
    if entity == "fintegratorSalesOrders":
        if method == "POST" and key is None:
            return bc_create_sales_order(store, json_body, company, host)
        if method == "GET" and key is None:
            return 200, bc_sales_order_list(store, company, query_string, host), headers
        if method == "GET" and key is not None:
            found = store.find_sales_order_by_key(key)
            if found is None:
                return 404, odata_error(
                    "NotFound",
                    "Mock: no fintegratorSalesOrders record with key '%s'." % key,
                ), headers
            out = json_clone(found)
            out["@odata.etag"] = entity_etag(entity, out)
            return 200, out, headers
        if method == "PATCH" and key is not None:
            with store.lock:
                found = store.find_sales_order_by_key(key)
                if found is None:
                    return 404, odata_error(
                        "NotFound",
                        "Mock: no fintegratorSalesOrders record with key '%s'." % key,
                    ), headers
                found.update(json_clone(json_body))
                out = json_clone(found)
            out["@odata.etag"] = entity_etag(entity, out)
            return 200, out, headers
        return 405, odata_error(
            "BadRequest",
            "Mock: %s is not supported on fintegratorSalesOrders." % method,
        ), headers

    # ------------------------------------------------------------- shipped orders
    if entity == "fintegratorShippedOrders":
        if method == "GET" and key is None:
            with store.lock:
                rows = json_clone(store.shipment_feed)
            selected = query_feed(rows, query_string, expandable=True)
            for row in selected:
                row["@odata.etag"] = entity_etag(entity, row)
            return 200, {
                "@odata.context": bc_context(host, company, "fintegratorShippedOrders"),
                "value": selected,
            }, headers
        if method in ("GET", "PATCH") and key is not None:
            with store.lock:
                found = None
                for row in store.shipment_feed:
                    if str(row.get("entryNo")) == key or row.get("salesShipmentNo") == key or str(row.get("id")) == key:
                        found = row
                        break
                if found is None:
                    return 404, odata_error(
                        "NotFound",
                        "Mock: no fintegratorShippedOrders record with entryNo '%s'." % key,
                    ), headers
                if method == "PATCH":
                    found.update(json_clone(json_body))
                out = expand_row(json_clone(found), (first_param(parse_qs(query_string or ""), "$expand") or ""))
            out["@odata.etag"] = entity_etag(entity, out)
            return 200, out, headers
        return 405, odata_error(
            "BadRequest",
            "Mock: %s is not supported on fintegratorShippedOrders (read-only feed, PATCH is allowed)." % method,
        ), headers

    # -------------------------------------------------------- inventory changes
    if entity == "fintegratorInventoryChanges":
        if method == "GET" and key is None:
            with store.lock:
                rows = json_clone(store.inventory_feed)
            selected = query_feed(rows, query_string, expandable=False)
            for row in selected:
                row["@odata.etag"] = entity_etag(entity, row)
            return 200, {
                "@odata.context": bc_context(host, company, "fintegratorInventoryChanges"),
                "value": selected,
            }, headers
        if method == "GET" and key is not None:
            with store.lock:
                for row in store.inventory_feed:
                    if str(row.get("entryNo")) == key:
                        out = json_clone(row)
                        out["@odata.etag"] = entity_etag(entity, out)
                        return 200, out, headers
            return 404, odata_error(
                "NotFound",
                "Mock: no fintegratorInventoryChanges record with entryNo '%s'." % key,
            ), headers
        return 405, odata_error(
            "BadRequest",
            "Mock: %s is not supported on fintegratorInventoryChanges (read-only feed)." % method,
        ), headers

    return 404, odata_error("NotFound", "Mock: no route for %s." % path), headers


# ---------------------------------------------------------------------------
# control API
# ---------------------------------------------------------------------------

def control_route(store, method, path, query_string, json_body, shutdown_hook=None):
    route = path[len("/__control"):].strip("/")
    params = parse_qs(query_string or "", keep_blank_values=True)

    if route in ("", "health") and method in ("GET", "HEAD"):
        return 200, {"ok": True, "listening": True}, {}

    if method == "POST" and route == "shutdown":
        if shutdown_hook is not None:
            shutdown_hook()
        return 200, {"ok": True, "shuttingDown": True}, {}

    if method == "POST" and route == "reset":
        store.reset()
        return 200, {"ok": True}, {}

    if method == "POST" and route == "orders":
        orders = json_body.get("orders")
        if not isinstance(orders, list):
            return 400, {"ok": False, "error": 'body must be {"orders":[...]}'}, {}
        count = store.replace_orders(orders)
        return 200, {"ok": True, "count": count}, {}

    if method == "POST" and route == "bc-shipments":
        shipments = json_body.get("shipments")
        if not isinstance(shipments, list):
            return 400, {"ok": False, "error": 'body must be {"shipments":[...]}'}, {}
        if json_body.get("replace"):
            store.clear_shipments()
        for row in shipments:
            if isinstance(row, dict):
                store.add_shipment(row)
        return 200, {"ok": True, "count": len(store.shipment_feed)}, {}

    if method == "POST" and route == "bc-inventory":
        changes = json_body.get("changes")
        if not isinstance(changes, list):
            return 400, {"ok": False, "error": 'body must be {"changes":[...]}'}, {}
        if json_body.get("replace"):
            store.clear_inventory_changes()
        for row in changes:
            if isinstance(row, dict):
                store.add_inventory_change(row)
        return 200, {"ok": True, "count": len(store.inventory_feed)}, {}

    if method == "POST" and route == "items":
        items = json_body.get("items")
        if not isinstance(items, list):
            return 400, {"ok": False, "error": 'body must be {"items":[{"sku":"..."}]}'}, {}
        count = store.set_skus(items)
        return 200, {"ok": True, "count": count, "items": list(store.skus)}, {}

    if method == "POST" and route == "fail":
        target = json_body.get("path")
        if not target:
            return 400, {"ok": False, "error": 'body must be {"path":"/admin/api/...","status":500|"graphqlError":"...","times":1}'}, {}
        times = json_body.get("times")
        times = 1 if times in (None, "") else _num(times, 1)
        rule = store.add_failure_rule({
            "path": str(target),
            "times": times,
            "status": json_body.get("status"),
            "graphqlError": json_body.get("graphqlError"),
        })
        return 200, {"ok": True, "failure": rule, "failures": store.failure_rules()}, {}

    if method in ("GET", "HEAD") and route == "state":
        return 200, store.state_snapshot(), {}

    if method in ("GET", "HEAD") and route == "requests":
        limit = first_param(params, "limit")
        limit = 200 if limit in (None, "") else max(1, min(5000, int(_num(limit, 200))))
        return 200, store.requests_snapshot(limit), {}

    return 404, {"ok": False, "error": "Mock: unknown control route '%s'." % path, "path": path}, {}


# ---------------------------------------------------------------------------
# HTTP plumbing
# ---------------------------------------------------------------------------

SHOPIFY_GRAPHQL_RE = re.compile(r"^/admin/api/(?:[^/]+/)?graphql\.json$")


class MockHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "FintegratorMockVendors/1.0"
    sys_version = ""
    store = None  # assigned by main()
    shutdown_hook = None  # assigned by main()

    # -- noise control ------------------------------------------------------
    def log_message(self, fmt, *args):  # keep stdout parseable for the harness
        return

    def log_error(self, fmt, *args):
        return

    # -- verbs --------------------------------------------------------------
    def do_GET(self):
        self._handle("GET")

    def do_HEAD(self):
        self._handle("HEAD")

    def do_POST(self):
        self._handle("POST")

    def do_PATCH(self):
        self._handle("PATCH")

    def do_PUT(self):
        self._handle("PUT")

    def do_DELETE(self):
        self._handle("DELETE")

    def do_OPTIONS(self):
        self._send(204, None, {"Allow": "GET, HEAD, POST, PATCH, OPTIONS"})
        self._log("OPTIONS", urlparse(self.path), "", 204)

    # -- core ---------------------------------------------------------------
    def _handle(self, method):
        store = self.store
        raw_body = self._read_body()
        parsed = urlparse(self.path)
        path = unquote(parsed.path or "/")
        query_string = parsed.query or ""
        body_text = raw_body.decode("utf-8", "replace") if isinstance(raw_body, bytes) else str(raw_body or "")
        body_json = safe_json(body_text)

        status, payload, extra_headers = 500, None, {}
        try:
            if path.startswith("/__control"):
                status, payload, extra_headers = control_route(
                    store, method, path, query_string, body_json, self.shutdown_hook
                )
            else:
                failure = store.take_failure(path)
                if failure is not None and failure.get("status"):
                    status = int(_num(failure["status"], 500))
                    payload = {
                        "error": "Mock: injected transport failure for %s." % path,
                        "status": status,
                    }
                    extra_headers = {}
                elif failure is not None and failure.get("graphqlError"):
                    status = 200
                    payload = {"errors": [{"message": str(failure["graphqlError"])}]}
                    extra_headers = {}
                elif path.startswith("/admin/api/"):
                    status, payload, extra_headers = self._shopify_route(method, path, body_json)
                elif path.startswith("/api/fintegrator/integration/"):
                    host = self.headers.get("Host") or "localhost"
                    status, payload, extra_headers = bc_route(
                        store, method, path, query_string, body_json, host
                    )
                else:
                    status = 404
                    payload = {
                        "error": "Mock: no route for this path.",
                        "path": path,
                        "hint": "Shopify: /admin/api/{version}/graphql.json, Business Central: "
                                "/api/fintegrator/integration/v1.0/companies({companyId})/{entitySet}, "
                                "control: /__control/*",
                    }
        except Exception as exc:
            status = 500
            payload = {"error": {"code": "MockServerError", "message": "%s: %s" % (type(exc).__name__, exc)}}
            extra_headers = {}

        self._send(status, payload, extra_headers)
        self._log(method, parsed, body_text, status)

    def _shopify_route(self, method, path, body_json):
        if not SHOPIFY_GRAPHQL_RE.match(path):
            return 404, {"errors": [{"message": "Mock: no Shopify route for %s." % path}]}, {}
        if method != "POST":
            return 405, {"errors": [{"message": "Mock: Shopify GraphQL requires POST, got %s." % method}]}, {}
        query_text = body_json.get("query")
        if not isinstance(query_text, str):
            query_text = ""
        variables = body_json.get("variables")
        if not isinstance(variables, dict):
            variables = {}
        payload, status = shopify_graphql(self.store, query_text, variables)
        return status, payload, {}

    def _read_body(self):
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except (TypeError, ValueError):
            length = 0
        if length <= 0:
            return b""
        try:
            return self.rfile.read(length)
        except (OSError, ValueError):
            return b""

    def _send(self, status, payload, extra_headers=None):
        body = b""
        if payload is not None and status != 204:
            body = jdump(payload).encode("utf-8")
        try:
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            for name, value in (extra_headers or {}).items():
                self.send_header(name, value)
            self.end_headers()
            if body and self.command != "HEAD":
                self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError):
            pass

    def _log(self, method, parsed, body_text, status):
        entry = {
            "ts": now_ts(),
            "method": method,
            "path": unquote(parsed.path or "/"),
            "query": parsed.query or "",
            "body": (body_text or "")[:MAX_BODY_LOG_CHARS],
            "responseStatus": status,
        }
        try:
            self.store.record_request(entry)
        except Exception:
            pass


# ---------------------------------------------------------------------------
# entry point
# ---------------------------------------------------------------------------

def parse_args(argv=None):
    parser = argparse.ArgumentParser(description="Mock Shopify Admin GraphQL + Business Central API server.")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help="port to listen on (default %d)" % DEFAULT_PORT)
    parser.add_argument("--host", default=DEFAULT_HOST, help="bind address (default %s)" % DEFAULT_HOST)
    parser.add_argument("--log-dir", default=DEFAULT_LOG_DIR,
                        help="directory for requests.jsonl (default <script>/logs)")
    return parser.parse_args(argv)


def _raise_keyboard_interrupt(signum, frame):  # noqa: ARG001 - signal signature
    raise KeyboardInterrupt()


def main(argv=None):
    args = parse_args(argv)

    log_dir = os.path.abspath(args.log_dir or DEFAULT_LOG_DIR)
    try:
        os.makedirs(log_dir, exist_ok=True)
    except OSError:
        pass
    log_path = os.path.join(log_dir, "requests.jsonl")

    store = Store(log_path)
    MockHandler.store = store

    # allow_reuse_address must be off on Windows: with it on, a second instance can silently bind a
    # port that is already served, and the harness would then talk to a stale process while its own
    # server looks healthy. Failing loudly is the whole point of a test double.
    class MockServer(ThreadingHTTPServer):
        allow_reuse_address = False

    try:
        server = MockServer((args.host, args.port), MockHandler)
    except OSError as exc:
        print("ERROR cannot bind %s:%s (%s). Stop the process that already listens there or pass a "
              "different --port." % (args.host, args.port, exc), file=sys.stderr, flush=True)
        return 2

    server.daemon_threads = True
    port = server.server_address[1]

    # /__control/shutdown: give the HTTP response time to flush, then stop the loop.
    # (staticmethod: a plain function stored on the class would bind as a method)
    MockHandler.shutdown_hook = staticmethod(lambda: threading.Timer(0.25, server.shutdown).start())

    for sig in (signal.SIGINT, signal.SIGTERM):
        try:
            signal.signal(sig, _raise_keyboard_interrupt)
        except (ValueError, OSError, AttributeError):
            pass

    print("LISTENING %d" % port, flush=True)
    print("LOG %s" % log_path, flush=True)
    sys.stdout.flush()
    try:
        server.serve_forever(poll_interval=0.2)
    except KeyboardInterrupt:
        print("SIGINT received, shutting down", flush=True)
    finally:
        try:
            server.shutdown()
        except Exception:
            pass
        server.server_close()
    print("STOPPED", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
