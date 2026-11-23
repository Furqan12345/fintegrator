#!/usr/bin/env python3
"""Expands the flow templates in flows/*.flow.json into deployable flow JSON in flows/build/.

Template tokens
---------------
{"$script": "<path relative to flows/scripts>"}  contents of the C# script file, prefixed with the
                                               shared prelude (flows/scripts/shared/values.cs)
{"$json":   <any JSON value>}                   that value serialised as a JSON string
{"$guid":   "<seed>"}                           a stable UUID derived from the seed

Stable GUIDs are what make redeploying safe: the same template always produces the same node and
edge ids, so an update (PUT) keeps node identity instead of orphaning history.
"""
import json
import os
import sys
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
FLOWS_DIR = os.path.join(ROOT, "flows")
SCRIPTS_DIR = os.path.join(FLOWS_DIR, "scripts")
BUILD_DIR = os.path.join(FLOWS_DIR, "build")
GUID_NAMESPACE = uuid.uuid5(uuid.NAMESPACE_DNS, "fintegrator.io/shopify-business-central")
PRELUDE_PATH = os.path.join(SCRIPTS_DIR, "shared", "values.cs")


def guid_for(seed: str) -> str:
    return str(uuid.uuid5(GUID_NAMESPACE, seed))


def read_text(path):
    with open(path, encoding="utf-8") as handle:
        return handle.read().strip()


def expand(value, base_dir):
    if isinstance(value, dict):
        if "$script" in value:
            script_path = os.path.join(base_dir, value["$script"])
            if not os.path.isfile(script_path):
                raise FileNotFoundError("script not found: " + script_path)
            script = read_text(script_path)
            # Only pay for the shared prelude where it is used: every byte is compiled by Roslyn on
            # the engine's first run, and the platform caps the total generated script size.
            if "FinValues" in script:
                return read_text(PRELUDE_PATH) + "\n\n" + script
            return script
        if "$json" in value:
            return json.dumps(value["$json"], separators=(",", ":"), ensure_ascii=False)
        if "$guid" in value:
            return guid_for(value["$guid"])
        return {key: expand(item, base_dir) for key, item in value.items()}
    if isinstance(value, list):
        return [expand(item, base_dir) for item in value]
    return value


def build_all():
    os.makedirs(BUILD_DIR, exist_ok=True)
    built = []
    for name in sorted(os.listdir(FLOWS_DIR)):
        if not name.endswith(".flow.json"):
            continue
        with open(os.path.join(FLOWS_DIR, name), encoding="utf-8") as handle:
            template = json.load(handle)
        flow = expand(template, SCRIPTS_DIR)
        output_path = os.path.join(BUILD_DIR, name.replace(".flow.json", ".json"))
        with open(output_path, "w", encoding="utf-8") as handle:
            json.dump(flow, handle, indent=2, ensure_ascii=False)
            handle.write("\n")
        built.append(output_path)
        print("built " + os.path.relpath(output_path, ROOT))
    if not built:
        print("no *.flow.json templates found in " + FLOWS_DIR, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(build_all())
