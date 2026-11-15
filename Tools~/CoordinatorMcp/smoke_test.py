"""Starts the published server against a minimal project and checks it over stdio, then checks its command line.

Usage: python smoke_test.py <server binary>
"""
import json
import os
import subprocess
import sys
import tempfile

server = os.path.abspath(sys.argv[1])
project = tempfile.mkdtemp()
for folder in ("Assets", "ProjectSettings", "Coordinator/Tasks"):
    os.makedirs(os.path.join(project, folder))

process = subprocess.Popen([server], cwd=os.path.join(project, "Assets"), stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")
count = 0


def call(method, params=None):
    global count
    count += 1
    process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": count, "method": method, "params": params or {}}) + "\n")
    process.stdin.flush()
    return json.loads(process.stdout.readline())


def tool(name, arguments=None):
    result = call("tools/call", {"name": name, "arguments": arguments or {}})["result"]
    return result["isError"], result["content"][0]["text"]


assert call("initialize", {"protocolVersion": "2025-06-18"})["result"]["serverInfo"]["name"] == "coordinator"
names = [item["name"] for item in call("tools/list")["result"]["tools"]]
assert names == ["coordinator_status", "coordinator_query", "coordinator_edit"], names
failed, text = tool("coordinator_status")
status = json.loads(text)
assert not failed and status["has_coordinator"] and not status["editor_connected"], text
assert os.path.realpath(status["project"]) == os.path.realpath(project), text
failed, text = tool("coordinator_query", {"action": "board"})
assert failed and "not running" in text, text
failed, text = tool("coordinator_edit", {"action": "approve_task"})
assert failed and "not running" in text, text
assert call("no/such/method")["error"]["code"] == -32601
process.stdin.write("not json\n")
process.stdin.flush()
assert json.loads(process.stdout.readline())["error"]["code"] == -32700
process.stdin.close()
assert process.wait(timeout=10) == 0

# The registered forms "--project <path>" and a bare folder stay the MCP server.
for arguments in (["--project", project], [project]):
    server_process = subprocess.run([server] + arguments, input=json.dumps({"jsonrpc": "2.0", "id": 1, "method": "ping"}) + "\n",
                                    capture_output=True, text=True, encoding="utf-8", timeout=10)
    assert server_process.returncode == 0 and json.loads(server_process.stdout)["result"] == {}, server_process.stdout


def cli(*arguments, cwd=project):
    result = subprocess.run([server, *arguments], cwd=cwd, capture_output=True, text=True, encoding="utf-8", timeout=30)
    return result.returncode, result.stdout


code, text = cli("help")
assert code == 0 and "coordinator-mcp query <action>" in text, text
code, text = cli("status")
status = json.loads(text)
assert code == 0 and status["has_coordinator"] and not status["editor_connected"] and status["cli"], text
code, text = cli("--project", project, "status", cwd=tempfile.gettempdir())
assert code == 0 and os.path.realpath(json.loads(text)["project"]) == os.path.realpath(project), text
code, text = cli("commands")
assert code == 0 and [item["command"] for item in json.loads(text)["commands"]] == ["query", "edit", "status", "commands", "help"], text
code, text = cli("query", "board")
assert code == 7 and json.loads(text)["code"] == "editor_unreachable", text
code, text = cli("edit", "create_task", "--task", "{not json")
assert code == 2 and json.loads(text)["code"] == "usage", text
code, text = cli("query")
assert code == 2, text
code, text = cli("frobnicate")
assert code == 2 and json.loads(text)["code"] == "unknown_command", text
code, text = cli("status", cwd=tempfile.mkdtemp())
assert code == 4 and json.loads(text)["code"] == "no_project", text
print("smoke test passed")
