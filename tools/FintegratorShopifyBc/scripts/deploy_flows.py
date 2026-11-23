#!/usr/bin/env python3
"""Deploys the Shopify <-> Business Central integration into a running fintegrator.io instance.

  python deploy_flows.py --base-url http://localhost:5000 --api-key dev-api-key \
      --shopify-connection "shopify ate clothdiaper" \
      --bc-connection "business central" \
      --bc-base-url "https://api.businesscentral.dynamics.com/v2.0/<tenant>/<env>/api/fintegrator/integration/v1.0" \
      --bc-username "<user>" --bc-password "<web service access key>" \
      --bc-company-id "<company guid>" --publish

Connections are resolved by name and only created when they are missing. Credentials are never
printed: an existing connection keeps its stored (encrypted) secrets.
"""
import argparse
import json
import os
import sys

import build_flows
import fintegrator

SHOPIFY_BASE_URL_DEFAULT = "https://clothdiaper-com.myshopify.com"
BC_BASE_URL_HINT = "https://api.businesscentral.dynamics.com/v2.0/<tenantId>/<environment>/api/fintegrator/integration/v1.0"


def parse_args():
    parser = argparse.ArgumentParser(description="Deploy the Shopify <-> Business Central flows.")
    parser.add_argument("--base-url", default="http://localhost:5000", help="fintegrator.io API base URL")
    parser.add_argument("--api-key", default="dev-api-key", help="X-Api-Key value")
    parser.add_argument("--integration", default="shopify business central", help="integration grouping name")
    parser.add_argument("--integration-description", default="Shopify and Business Central order, shipment and inventory sync.")
    parser.add_argument("--shopify-connection", default="shopify ate clothdiaper", help="existing Shopify connection name")
    parser.add_argument("--shopify-base-url", default=SHOPIFY_BASE_URL_DEFAULT, help="base URL used when the Shopify connection has to be created")
    parser.add_argument("--shopify-token", default="", help="Shopify admin API access token used only when creating the connection")
    parser.add_argument("--bc-connection", default="business central", help="Business Central connection name")
    parser.add_argument("--bc-base-url", default="", help="connection base URL, e.g. " + BC_BASE_URL_HINT)
    parser.add_argument("--bc-auth-type", default="Basic", choices=["Basic", "OAuth2ClientCredentials", "Bearer", "None"])
    parser.add_argument("--bc-username", default="", help="Business Central user (Basic auth)")
    parser.add_argument("--bc-password", default="", help="Business Central web service access key (Basic auth)")
    parser.add_argument("--bc-company-id", default="", help="Business Central company GUID stored in the flows' persisted state")
    parser.add_argument("--shopify-api-version", default="2026-07", help="Shopify Admin API version used by the flows")
    parser.add_argument("--reset-state", action="store_true", help="reset every flow's persisted state before seeding defaults")
    parser.add_argument("--publish", action="store_true", help="publish (activate) the deployed flows")
    parser.add_argument("--dry-run", action="store_true", help="only rebuild flows/build/*.json")
    return parser.parse_args()


def main():
    args = parse_args()

    if build_flows.build_all() != 0:
        return 1
    if args.dry_run:
        return 0

    client = fintegrator.FintegratorClient(args.base_url, args.api_key)

    # --- Shopify connection -------------------------------------------------
    shopify_connection = client.find_connection(args.shopify_connection)
    if shopify_connection:
        print("connection   : %s (existing, %s)" % (shopify_connection["name"], shopify_connection["id"]))
    else:
        auth_config = ""
        if args.shopify_token:
            auth_config = json.dumps({"headers": [{"name": "X-Shopify-Access-Token", "value": args.shopify_token}]})
        shopify_connection = client.create_connection(args.shopify_connection, args.shopify_base_url, "Custom", auth_config)
        print("connection   : %s (created, %s)" % (shopify_connection["name"], shopify_connection["id"]))
        if not args.shopify_token:
            print("               WARNING: no --shopify-token supplied, add the X-Shopify-Access-Token header in the UI before running.")

    # --- Business Central connection ---------------------------------------
    bc_connection = client.find_connection(args.bc_connection)
    if bc_connection:
        print("connection   : %s (existing, %s)" % (bc_connection["name"], bc_connection["id"]))
    else:
        if not args.bc_base_url:
            print("error: Business Central connection '%s' does not exist; pass --bc-base-url (for example %s)"
                  % (args.bc_connection, BC_BASE_URL_HINT), file=sys.stderr)
            return 1

        auth_config = ""
        if args.bc_auth_type == "Basic":
            auth_config = json.dumps({"username": args.bc_username, "password": args.bc_password})
        bc_connection = client.create_connection(args.bc_connection, args.bc_base_url, args.bc_auth_type, auth_config)
        print("connection   : %s (created, %s)" % (bc_connection["name"], bc_connection["id"]))
        if args.bc_auth_type == "Basic" and (not args.bc_username or not args.bc_password):
            print("               WARNING: no Business Central credentials supplied, fill them in the UI before running.")

    # --- integration grouping ---------------------------------------------
    integration, _ = client.ensure_integration(args.integration, args.integration_description)
    print("integration  : %s (%s)" % (integration["name"], integration["id"]))

    # --- flows -------------------------------------------------------------
    state_defaults = {"shopifyApiVersion": args.shopify_api_version}
    if args.bc_company_id:
        state_defaults["bcCompanyId"] = args.bc_company_id

    flows = []
    for name in sorted(os.listdir(build_flows.BUILD_DIR)):
        if not name.endswith(".json"):
            continue
        with open(os.path.join(build_flows.BUILD_DIR, name), encoding="utf-8") as handle:
            flows.append(json.load(handle))

    deployed = fintegrator.deploy_flows(
        client,
        flows,
        integration["id"],
        shopify_connection["id"],
        bc_connection["id"],
        state_defaults,
        reset_state=args.reset_state,
        publish=args.publish,
    )

    for flow in deployed:
        print("flow         : %-55s %s %s%s" % (flow["name"], flow["action"], flow["id"], " (published)" if args.publish else ""))
        print("               state keys: %s" % ", ".join(sorted(flow["state"].keys())) or "               state keys: (none)")

    if not args.bc_company_id:
        print("\nNOTE: no --bc-company-id supplied. Set bcCompanyId in each flow's persisted state (designer -> State) or the Business Central calls will fail with an actionable error.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
