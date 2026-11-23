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
    # SDK tool-name conventions can differ (PascalCase vs snake_case).
    # Match actual discovered names rather than assuming a convention.
    normalize = lambda value: "".join(ch for ch in value.lower() if ch.isalnum())
    by_normalized = {normalize(name): name for name in names}
    required = {"familyoverview", "listdemopeople", "inspectdemoperson",
                "proposedemoperson", "listdemodrafts"}
    if not required.issubset(by_normalized):
        raise AssertionError(f"Expected fictional MCP tools missing; found: {sorted(names)}")
    overview_name = by_normalized["familyoverview"]
    send({"jsonrpc": "2.0", "id": 3, "method": "tools/call",
          "params": {"name": overview_name, "arguments": {}}})
    result = response(3)
    rendered = json.dumps(result)
    if "syntheticOnly" not in rendered or "true" not in rendered:
        raise AssertionError("MCP overview did not confirm synthetic-only scope")
    # Demonstrate the proposal workflow, without approving or persisting data.
    send({"jsonrpc": "2.0", "id": 4, "method": "tools/call",
          "params": {"name": by_normalized["proposedemoperson"],
                     "arguments": {"displayName": "Fictional Proposal"}}})
    staged = response(4)
    if "pending" not in json.dumps(staged):
        raise AssertionError("MCP proposal was not staged as pending")
    send({"jsonrpc": "2.0", "id": 5, "method": "tools/call",
          "params": {"name": by_normalized["listdemodrafts"], "arguments": {}}})
    listed = response(5)
    if "Fictional Proposal" not in json.dumps(listed):
        raise AssertionError("In-memory staged proposal was not discoverable")
    print("PASS MCP stdio handshake, fictional discovery and in-memory proposal call")
finally:
    selector.close()
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)
