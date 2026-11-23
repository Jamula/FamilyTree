#!/usr/bin/env python3
"""Local CI handshake and tool smoke for the fictional stdio MCP server.

No family data, remote services, secrets, or network connections involved.
"""
from __future__ import annotations

import json
from pathlib import Path
import selectors
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
command = [
    "dotnet", "run",
    "--project", "src/FamilyTree.Mcp/FamilyTree.Mcp.csproj",
    "--configuration", "Release",
    "--no-build", "--no-restore"
]

process = subprocess.Popen(
    command, cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
    stderr=subprocess.DEVNULL, text=True, bufsize=1
)
assert process.stdin is not None and process.stdout is not None
selector = selectors.DefaultSelector()
selector.register(process.stdout, selectors.EVENT_READ)


def send(message: dict) -> None:
    process.stdin.write(json.dumps(message, separators=(",", ":")) + "\n")
    process.stdin.flush()


def response(request_id: int, timeout: float = 30.0) -> dict:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError("MCP server exited before completing JSON-RPC handshake")
        if not selector.select(timeout=0.5):
            continue
        line = process.stdout.readline()
        if not line:
            raise RuntimeError("MCP stdout closed before the expected response")
        item = json.loads(line)
        if item.get("id") == request_id:
            if "error" in item:
                raise RuntimeError(f"MCP request {request_id} failed")
            return item["result"]
    raise TimeoutError(f"MCP response {request_id} timed out")


try:
    send({
        "jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "familytree-ci", "version": "0.1.0"}
        }
    })
    initialized = response(1)
    if "serverInfo" not in initialized:
        raise AssertionError("MCP initialize response missing serverInfo")
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})
    send({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
    tools = response(2)["tools"]
    names = {tool["name"] for tool in tools}
    required = {"FamilyOverview", "ListDemoPeople", "InspectDemoPerson",
                "ProposeDemoPerson", "ListDemoDrafts"}
    if not required.issubset(names):
        raise AssertionError("Expected fictional MCP tools are missing")
    send({"jsonrpc": "2.0", "id": 3, "method": "tools/call",
          "params": {"name": "FamilyOverview", "arguments": {}}})
    result = response(3)
    rendered = json.dumps(result)
    if '"syntheticOnly"' not in rendered or 'true' not in rendered:
        raise AssertionError("MCP overview did not confirm synthetic-only scope")
    print("PASS MCP stdio handshake, tool discovery and fictional overview call")
finally:
    selector.close()
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)
