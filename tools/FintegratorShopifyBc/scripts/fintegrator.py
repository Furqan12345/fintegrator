#!/usr/bin/env python3
"""Small fintegrator.io API client plus the deploy routine shared by the deploy and E2E scripts."""
import json
import time
import urllib.error
import urllib.parse
import urllib.request


class ApiError(RuntimeError):
    def __init__(self, method, path, status, body):
        super().__init__("%s %s -> HTTP %s: %s" % (method, path, status, body))
        self.status = status
        self.body = body


class FintegratorClient:
    def __init__(self, base_url, api_key, timeout=60):
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.timeout = timeout

    def request(self, method, path, body=None, expect=(200, 201, 202, 204)):
        url = self.base_url + path
        data = None
        headers = {"X-Api-Key": self.api_key, "Accept": "application/json"}
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"

        request = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                payload = response.read().decode("utf-8")
                status = response.status
        except urllib.error.HTTPError as error:
            payload = error.read().decode("utf-8", "replace")
            raise ApiError(method, path, error.code, payload) from None
        except urllib.error.URLError as error:
            raise RuntimeError("cannot reach fintegrator.io at %s: %s" % (self.base_url, error)) from None

        if status not in expect:
            raise ApiError(method, path, status, payload)
        if not payload:
            return None
        try:
            return json.loads(payload)
        except ValueError:
            return payload

    # ---- connections -------------------------------------------------------
    def list_connections(self):
        return self.request("GET", "/api/connections")

    def find_connection(self, name):
        wanted = name.strip().lower()
        for connection in self.list_connections():
            if (connection.get("name") or "").strip().lower() == wanted:
                return connection
        return None

    def create_connection(self, name, base_url, auth_type, auth_config_json):
        return self.request(
            "POST",
            "/api/connections",
            {
                "name": name,
                "baseUrl": base_url,
                "authType": auth_type,
                "authConfigJson": auth_config_json or "",
                "status": "Active",
            },
        )

    def ensure_connection(self, name, base_url, auth_type, auth_config_json=None):
        existing = self.find_connection(name)
        if existing:
            return existing, False
        created = self.create_connection(name, base_url, auth_type, auth_config_json)
        return created, True

    # ---- integration grouping ---------------------------------------------
    def list_integrations(self):
        return self.request("GET", "/api/integrations")

    def find_integration(self, name):
        wanted = name.strip().lower()
        for integration in self.list_integrations():
            if (integration.get("name") or "").strip().lower() == wanted:
                return integration
        return None

    def ensure_integration(self, name, description):
        existing = self.find_integration(name)
        if existing:
            return existing, False
        created = self.request("POST", "/api/integrations", {"name": name, "description": description})
        return created, True

    # ---- flows ------------------------------------------------------------
    def list_flows(self):
        return self.request("GET", "/api/integrationflow")

    def find_flow(self, name):
        wanted = name.strip().lower()
        for flow in self.list_flows():
            if (flow.get("name") or "").strip().lower() == wanted:
                return flow
        return None

    def create_flow(self, flow):
        return self.request("POST", "/api/integrationflow", flow)

    def update_flow(self, flow_id, flow):
        body = dict(flow)
        body["id"] = flow_id
        return self.request("PUT", "/api/integrationflow/%s" % flow_id, body)

    def get_persisted_state(self, flow_id):
        return self.request("GET", "/api/integrationflow/%s/persisted-state" % flow_id)

    def put_persisted_state(self, flow_id, state):
        return self.request(
            "PUT",
            "/api/integrationflow/%s/persisted-state" % flow_id,
            {"flowId": flow_id, "persistedStateJson": json.dumps(state, separators=(",", ":"))},
        )

    def publish_flow(self, flow_id, note=""):
        return self.request("POST", "/api/integrationflow/%s/publish" % flow_id, {"changeNote": note})

    # ---- cross references -----------------------------------------------
    def list_cross_references(self):
        return self.request("GET", "/api/crossreferences")

    def cross_reference_entry_count(self, name):
        page = self.request("GET", "/api/crossreferences/%s/entries?pageSize=1" % urllib.parse.quote(name))
        return int((page or {}).get("totalCount") or 0)

    def clear_cross_reference_entries(self, name):
        return self.request("DELETE", "/api/crossreferences/%s/entries" % urllib.parse.quote(name))

    # ---- executions -------------------------------------------------------
    def run_flow(self, flow_id):
        return self.request("POST", "/api/integrationflow/%s/run" % flow_id)

    def list_executions(self, flow_id, page_size=200):
        query = urllib.parse.urlencode({"flowId": flow_id, "pageSize": page_size})
        return self.request("GET", "/api/executions?%s" % query)

    def get_execution(self, execution_id):
        return self.request("GET", "/api/executions/%s" % execution_id)

    def wait_for_execution(self, execution_id, timeout=180, poll=1.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            execution = self.get_execution(execution_id)
            status = execution.get("status")
            if status not in ("Queued", "InProgress"):
                return execution
            time.sleep(poll)
        raise TimeoutError("execution %s did not finish within %ss" % (execution_id, timeout))


def read_persisted_state(client, flow_id):
    try:
        current = client.get_persisted_state(flow_id)
        raw = (current or {}).get("persistedStateJson") or "{}"
        parsed = json.loads(raw)
        return parsed if isinstance(parsed, dict) else {}
    except (ApiError, ValueError):
        return {}


def deploy_flows(client, flows, integration_id, shopify_connection_id, bc_connection_id, state_defaults, reset_state=False, publish=False):
    """Creates or updates every flow, seeds its persisted state from state_defaults and optionally publishes it.

    Order matters: PUT /api/integrationflow/{id} replaces the whole flow DTO, and
    IntegrationFlowDto.PersistedStateJson defaults to "{}", so an update wipes the flow's cursors
    unless they are carried back. The state is therefore read *before* the update and restored after.
    """
    deployed = []
    for flow in flows:
        body = dict(flow)
        body["integrationId"] = integration_id
        for node in body.get("nodes", []):
            if node.get("connectionId") == "__SHOPIFY_CONNECTION__":
                node["connectionId"] = shopify_connection_id
            elif node.get("connectionId") == "__BC_CONNECTION__":
                node["connectionId"] = bc_connection_id

        existing = client.find_flow(body["name"])
        state = {} if reset_state else (read_persisted_state(client, existing["id"]) if existing else {})

        if existing:
            saved = client.update_flow(existing["id"], body)
            action = "updated"
        else:
            saved = client.create_flow(body)
            action = "created"

        flow_id = saved["id"]

        changed = False
        for key, value in (state_defaults or {}).items():
            if key not in state:
                state[key] = value
                changed = True

        # Always restore after an update, even when nothing was added: the update cleared it.
        if changed or reset_state or existing:
            client.put_persisted_state(flow_id, state)

        if publish:
            client.publish_flow(flow_id, "Deployed from tools/FintegratorShopifyBc")

        deployed.append({"id": flow_id, "name": saved["name"], "action": action, "state": state})
    return deployed
