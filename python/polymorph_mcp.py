#!/usr/bin/env python3
"""Polymorph MCP server (stdio).

Lets Claude (or any MCP client) drive a running Polymorph: every API method becomes a tool
(scene_list, object_create, modifier_add, uv_unwrap, gta_check, gta_export, ...), plus
view_screenshot which returns an image of the window.

Claude Desktop config (claude_desktop_config.json):

    {"mcpServers": {"polymorph": {"command": "python", "args": ["C:/path/to/Polymorph/python/polymorph_mcp.py"]}}}

Standard library only.
"""
import base64
import json
import os
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from polymorph import Polymorph, PolymorphError  # noqa: E402

PROTOCOL = "2025-06-18"
_client = None


def client():
    global _client
    if _client is None:
        _client = Polymorph()
    return _client


TYPE = {"int": {"type": "integer"}, "float": {"type": "number"}, "bool": {"type": "boolean"}, "string": {"type": "string"},
        "vec3": {"type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 3},
        "array": {"type": "array"}, "object": {"type": "object"}}


def tool_name(method):
    return method.replace(".", "_")


def list_tools():
    try:
        methods = client().methods()
    except PolymorphError as e:
        global _client
        _client = None
        return [{"name": "polymorph_status", "description": "Polymorph is not running: " + str(e), "inputSchema": {"type": "object", "properties": {}}}]
    tools = []
    for m in methods:
        props, req = {}, []
        for p in m["params"]:
            s = dict(TYPE.get(p["type"], {"type": "string"}))
            if p.get("description"):
                s["description"] = p["description"]
            props[p["name"]] = s
            if p.get("required"):
                req.append(p["name"])
        schema = {"type": "object", "properties": props}
        if req:
            schema["required"] = req
        desc = m["description"]
        if m["name"] == "view.screenshot":
            desc = "Picture of the Polymorph window (returned as an image)."
            schema = {"type": "object", "properties": {}}
        tools.append({"name": tool_name(m["name"]), "description": desc, "inputSchema": schema})
    tools.append({"name": "polymorph_status", "description": "Is Polymorph running? Version, open file, object count.", "inputSchema": {"type": "object", "properties": {}}})
    return tools


def call_tool(name, args):
    global _client
    try:
        if name == "polymorph_status":
            return [{"type": "text", "text": json.dumps(client().info(), indent=1)}], False
        method = next((m["name"] for m in client().methods() if tool_name(m["name"]) == name), None)
        if method is None:
            return [{"type": "text", "text": "Unknown tool " + name}], True
        if method == "view.screenshot":
            path = os.path.join(tempfile.gettempdir(), "polymorph_mcp_%d.png" % int(time.time() * 1000))
            client().call("view.screenshot", path=path)
            for _ in range(60):
                if os.path.exists(path) and os.path.getsize(path) > 0:
                    break
                time.sleep(0.05)
            time.sleep(0.05)
            with open(path, "rb") as f:
                data = base64.b64encode(f.read()).decode("ascii")
            try:
                os.remove(path)
            except OSError:
                pass
            return [{"type": "image", "data": data, "mimeType": "image/png"}], False
        result = client().call(method, **(args or {}))
        # splat.view / splat.section: the picture comes back as an image
        if isinstance(result, dict) and isinstance(result.get("png"), str):
            path = result["png"]
            for _ in range(100):
                if os.path.exists(path) and os.path.getsize(path) > 0:
                    break
                time.sleep(0.05)
            if os.path.exists(path):
                with open(path, "rb") as f:
                    data = base64.b64encode(f.read()).decode("ascii")
                return [{"type": "image", "data": data, "mimeType": "image/png"}, {"type": "text", "text": json.dumps(result, indent=1, default=str)}], False
        return [{"type": "text", "text": json.dumps(result, indent=1, default=str)}], False
    except PolymorphError as e:
        _client = None
        return [{"type": "text", "text": "Polymorph: " + str(e)}], True
    except Exception as e:  # keep the server alive
        return [{"type": "text", "text": "%s: %s" % (type(e).__name__, e)}], True


def handle(msg):
    mid, method, params = msg.get("id"), msg.get("method"), msg.get("params") or {}
    if method == "initialize":
        return {"jsonrpc": "2.0", "id": mid, "result": {
            "protocolVersion": params.get("protocolVersion", PROTOCOL),
            "capabilities": {"tools": {"listChanged": False}},
            "serverInfo": {"name": "polymorph", "version": "1.0.0"},
            "instructions": "Drives Polymorph, a 3D modeller for GTA V / FiveM. Units are metres, Z up. Call polymorph_status first, "
                            "scene_list to see objects, object_create / object_create_mesh to build, modifier_add ('Edit Poly', 'Unwrap UVW', 'Mirror'...), "
                            "uv_unwrap for UVs, gta_check then gta_export for FiveM, view_screenshot to look at the result."}}
    if method in ("notifications/initialized", "notifications/cancelled"):
        return None
    if method == "ping":
        return {"jsonrpc": "2.0", "id": mid, "result": {}}
    if method == "tools/list":
        return {"jsonrpc": "2.0", "id": mid, "result": {"tools": list_tools()}}
    if method == "tools/call":
        content, err = call_tool(params.get("name"), params.get("arguments"))
        return {"jsonrpc": "2.0", "id": mid, "result": {"content": content, "isError": err}}
    if mid is None:
        return None
    return {"jsonrpc": "2.0", "id": mid, "error": {"code": -32601, "message": "Method not found: %s" % method}}


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except ValueError:
            continue
        reply = handle(msg)
        if reply is not None:
            sys.stdout.write(json.dumps(reply) + "\n")
            sys.stdout.flush()


if __name__ == "__main__":
    main()
