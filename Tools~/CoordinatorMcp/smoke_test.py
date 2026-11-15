"""Starts the published server against a minimal project and checks it over stdio, then checks its command line:
reads from the files without an Editor, a linked git worktree, and the wait for an Editor that does not answer yet.

Usage: python smoke_test.py <server binary>
"""
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time

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
reply = json.loads(text)
assert not failed and reply["success"] and reply["source"] == "files" and reply["data"]["columns"] == [], text
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
    result = subprocess.run([server, *arguments], cwd=cwd, capture_output=True, text=True, encoding="utf-8", timeout=90)
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
reply = json.loads(text)
assert code == 0 and reply["source"] == "files" and "no Unity Editor" in reply["source_reason"], text
code, text = cli("query", "tasks", "--source", "files")
assert code == 0 and json.loads(text)["data"]["tasks"] == [], text
code, text = cli("query", "task", "--task", "NOPE")
assert code == 6 and json.loads(text)["source"] == "files", text
code, text = cli("query", "board", "--source", "editor")
assert code == 7 and json.loads(text)["code"] == "editor_unreachable", text
code, text = cli("edit", "approve_task", "--task", "X")
assert code == 7 and json.loads(text)["code"] == "editor_unreachable", text
code, text = cli("edit", "generate_cards", "--source", "files")
assert code == 2, text
code, text = cli("query", "board", "--wait", "soon")
assert code == 2, text
code, text = cli("edit", "create_task", "--task", "{not json")
assert code == 2 and json.loads(text)["code"] == "usage", text
code, text = cli("query")
assert code == 2, text
code, text = cli("frobnicate")
assert code == 2 and json.loads(text)["code"] == "unknown_command", text
code, text = cli("status", cwd=tempfile.mkdtemp())
assert code == 4 and json.loads(text)["code"] == "no_project", text

# A linked git worktree answers for its main checkout and names itself as the checkout.
if shutil.which("git"):
    def git(*arguments, cwd=project):
        subprocess.run(["git", "-c", "user.name=smoke", "-c", "user.email=smoke@example.com", *arguments], cwd=cwd, check=True, capture_output=True)
    for folder in ("Assets", "ProjectSettings", "Coordinator"):
        open(os.path.join(project, folder, ".keep"), "w").close()
    git("init", "-q")
    git("add", ".")
    git("commit", "-q", "-m", "start")
    worktree = os.path.join(tempfile.mkdtemp(), "executor")
    git("worktree", "add", "-q", "-b", "executor", worktree)
    code, text = cli("status", cwd=worktree)
    status = json.loads(text)
    assert code == 0 and os.path.realpath(status["project"]) == os.path.realpath(project), text
    assert os.path.realpath(status["checkout"]) == os.path.realpath(worktree), text

# Unity runs for the project, but its bridge answers only after a while, as after a recompile: the call waits for it.
library = os.path.join(project, "Library")
os.makedirs(os.path.join(library, "Coordinator"), exist_ok=True)
windows = os.name == "nt"
editor = os.path.join(tempfile.mkdtemp(), "Unity" + (".exe" if windows else ""))
shutil.copy(os.path.join(os.environ.get("SystemRoot", "C:/Windows"), "System32", "PING.EXE") if windows else shutil.which("sleep"), editor)
unity = subprocess.Popen([editor, "-n", "60", "127.0.0.1"] if windows else [editor, "60"], stdout=subprocess.DEVNULL)
try:
    with open(os.path.join(library, "EditorInstance.json"), "w") as file:
        json.dump({"process_id": unity.pid}, file)
    code, text = cli("edit", "generate_cards", "--wait", "1")
    assert code == 7 and "runs for this project" in json.loads(text)["message"], text

    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)

    def bridge():
        time.sleep(2)
        with open(os.path.join(library, "Coordinator", "agent-bridge.json"), "w") as file:
            json.dump({"port": listener.getsockname()[1], "token": "t"}, file)
        connection, _ = listener.accept()
        with connection:
            request = json.loads(connection.makefile(encoding="utf-8").readline())
            answer = {"success": True, "message": "late editor", "data": {"tool": request["tool"]}}
            connection.sendall((json.dumps(answer) + "\n").encode("utf-8"))

    thread = threading.Thread(target=bridge)
    thread.start()
    started = time.time()
    code, text = cli("query", "board")
    reply = json.loads(text)
    assert code == 0 and reply["source"] == "editor" and reply["message"] == "late editor" and reply["waited_seconds"] >= 1.5, text
    assert time.time() - started < 20, text
    thread.join()
finally:
    unity.kill()
    unity.wait()

print("smoke test passed")
