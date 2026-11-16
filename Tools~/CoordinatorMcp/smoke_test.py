"""Starts the published server against a minimal project and checks it over stdio, then checks its command line:
reads and edits on the files without an Editor, the write lock, the undo entry for the Editor, a linked git worktree,
the wait with --source editor and the short claim time of a busy Editor.

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

if os.name != "nt":
    import fcntl


def hold_lock(path):
    # .NET takes the write lock with FileShare.None: on Windows any open handle blocks it, on Linux and macOS only an
    # advisory flock does, so the test takes one there.
    handle = open(path, "a")
    if os.name != "nt":
        fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
    return handle

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
assert failed and json.loads(text)["code"] == "needs_person" and json.loads(text)["source"] == "files", text
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
assert status["workflow_mode"] == "safe", text
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
assert code == 6 and json.loads(text)["code"] == "needs_person" and json.loads(text)["source"] == "files", text
code, text = cli("edit", "approve_task", "--task", "X", "--source", "editor", "--wait", "0")
assert code == 7 and json.loads(text)["code"] == "editor_unreachable", text
code, text = cli("edit", "generate_cards", "--source", "files")
assert code == 0 and json.loads(text)["data"]["written"] == 0, text

# An edit without the Editor writes the files, leaves one undo entry for the Editor, and rolls back what adds errors.
record = {"schemaVersion": 1, "id": "smoke", "title": "Smoke", "lead": "smoke", "targetBranch": "develop", "integrationBranch": "task/smoke", "base": {"branch": "develop"}}
code, text = cli("edit", "create_task", "--task", json.dumps(record))
reply = json.loads(text)
assert code == 0 and reply["success"] and reply["source"] == "files", text
task_file = os.path.join(project, "Coordinator", "Tasks", "smoke", "task.json")
assert json.load(open(task_file, encoding="utf-8"))["title"] == "Smoke", text
inbox = os.path.join(project, "Library", "Coordinator", "history-inbox")
entries = [name for name in os.listdir(inbox) if name.endswith(".json")]
assert len(entries) == 1, entries
entry = json.load(open(os.path.join(inbox, entries[0]), encoding="utf-8"))
assert entry["files"][0]["before"] is None and "Smoke" in entry["files"][0]["after"], entry
code, text = cli("edit", "create_task", "--task", json.dumps(record))
assert code == 6 and json.loads(text)["code"] == "task_exists", text
code, text = cli("query", "task", "--task", "smoke")
assert code == 0 and json.loads(text)["data"]["task"]["title"] == "Smoke", text

# One writer at a time: while another process holds the write lock, an edit waits, then answers busy and changes nothing.
lock = hold_lock(os.path.join(project, "Library", "Coordinator", "write.lock"))
try:
    started = time.time()
    code, text = cli("edit", "update_task", "--task", json.dumps(dict(record, title="Changed")))
    assert code == 6 and json.loads(text)["code"] == "busy" and time.time() - started >= 9, text
    assert json.load(open(task_file, encoding="utf-8"))["title"] == "Smoke"
finally:
    lock.close()
code, text = cli("edit", "update_task", "--task", json.dumps(dict(record, title="Changed")))
assert code == 0 and json.load(open(task_file, encoding="utf-8"))["title"] == "Changed", text

# Lazy mode, which the Editor mirrors to Library/Coordinator/agent-mode.json: the lead agent approves a clean draft and the
# approval names the agent and the person who instructed it. Safe and Free refuse.
mode_file = os.path.join(project, "Library", "Coordinator", "agent-mode.json")
with open(mode_file, "w", encoding="utf-8") as file:
    json.dump({"workflowMode": "free"}, file)
code, text = cli("edit", "approve_task", "--task", "smoke", "--by", "lead-agent", "--instructed_by", "owner")
assert code == 6 and json.loads(text)["code"] == "needs_person" and "Free" in json.loads(text)["message"], text
with open(mode_file, "w", encoding="utf-8") as file:
    json.dump({"workflowMode": "lazy"}, file)
code, text = cli("status")
assert json.loads(text)["workflow_mode"] == "lazy", text
code, text = cli("query", "capabilities")
assert code == 0 and json.loads(text)["data"]["workflow_mode"]["agent_may_approve"] is True, text
code, text = cli("edit", "approve_task", "--task", "smoke", "--by", "lead-agent")
assert code == 6 and json.loads(text)["code"] == "needs_person", text
code, text = cli("edit", "approve_task", "--task", "smoke", "--by", "lead-agent", "--instructed-by", "owner")
assert code == 0 and json.loads(text)["source"] == "files", text
approval = json.load(open(task_file, encoding="utf-8"))["approval"]
assert approval["by"] == "lead-agent" and approval["instructedBy"] == "owner", approval
code, text = cli("query", "validate", "--task", "smoke")
assert code == 0 and json.loads(text)["data"]["clean"] is True, text
os.remove(mode_file)
shutil.rmtree(os.path.join(project, "Coordinator", "Tasks", "smoke"))
shutil.rmtree(inbox)
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
    # A read with ref reads that commit's Coordinator files from git, also from the worktree.
    os.makedirs(os.path.join(project, "Coordinator", "Tasks", "smoke-ref"))
    with open(os.path.join(project, "Coordinator", "Tasks", "smoke-ref", "task.json"), "w", encoding="utf-8") as file:
        json.dump(dict(record, id="smoke-ref", integrationBranch="task/smoke-ref"), file)
    git("add", ".")
    git("commit", "-q", "-m", "smoke-ref task")
    shutil.rmtree(os.path.join(project, "Coordinator", "Tasks", "smoke-ref"))
    code, text = cli("query", "tasks")
    assert code == 0 and json.loads(text)["data"]["tasks"] == [], text
    code, text = cli("query", "tasks", "--ref", "HEAD", cwd=worktree)
    reply = json.loads(text)
    assert code == 0 and [task["id"] for task in reply["data"]["tasks"]] == ["smoke-ref"] and reply["data"]["at"]["ref"] == "HEAD", text
    code, text = cli("query", "board", "--ref", "no-such-branch")
    assert code == 6 and json.loads(text)["code"] == "ref_not_found", text
    code, text = cli("edit", "generate_cards", "--ref", "HEAD")
    assert code == 6 and json.loads(text)["code"] == "invalid_parameters", text

# Unity runs for the project, but its bridge answers only after a while, as after a recompile: by default the files answer
# at once; --source editor waits for the bridge.
library = os.path.join(project, "Library")
os.makedirs(os.path.join(library, "Coordinator"), exist_ok=True)
windows = os.name == "nt"
editor = os.path.join(tempfile.mkdtemp(), "Unity" + (".exe" if windows else ""))
shutil.copy(os.path.join(os.environ.get("SystemRoot", "C:/Windows"), "System32", "PING.EXE") if windows else shutil.which("sleep"), editor)
unity = subprocess.Popen([editor, "-n", "60", "127.0.0.1"] if windows else [editor, "60"], stdout=subprocess.DEVNULL)
try:
    with open(os.path.join(library, "EditorInstance.json"), "w") as file:
        json.dump({"process_id": unity.pid}, file)
    code, text = cli("edit", "generate_cards", "--source", "editor", "--wait", "1")
    assert code == 7 and "runs for this project" in json.loads(text)["message"], text
    started = time.time()
    code, text = cli("query", "board")
    reply = json.loads(text)
    assert code == 0 and reply["source"] == "files" and "does not answer now" in reply["source_reason"] and time.time() - started < 5, text

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
            assert "claim_seconds" not in request, request
            answer = {"success": True, "message": "late editor", "data": {"tool": request["tool"]}}
            connection.sendall((json.dumps(answer) + "\n").encode("utf-8"))
        # A busy Editor that does not start a call in its claim time cancels it; the command line then uses the files.
        connection, _ = listener.accept()
        with connection:
            request = json.loads(connection.makefile(encoding="utf-8").readline())
            assert request["claim_seconds"] == 5, request
            answer = {"success": False, "code": "editor_timeout", "message": "not started"}
            connection.sendall((json.dumps(answer) + "\n").encode("utf-8"))

    thread = threading.Thread(target=bridge)
    thread.start()
    started = time.time()
    code, text = cli("query", "board", "--source", "editor")
    reply = json.loads(text)
    assert code == 0 and reply["source"] == "editor" and reply["message"] == "late editor" and reply["waited_seconds"] >= 1.5, text
    assert time.time() - started < 20, text
    code, text = cli("query", "board")
    reply = json.loads(text)
    assert code == 0 and reply["source"] == "files" and "did not start" in reply["source_reason"], text
    thread.join()
    # A read with ref never asks the Editor, which may be older and ignore ref.
    if shutil.which("git"):
        code, text = cli("query", "tasks", "--ref", "HEAD")
        reply = json.loads(text)
        assert code == 0 and reply["source"] == "files" and reply["source_reason"] == "a read with ref comes from git", text
finally:
    unity.kill()
    unity.wait()

print("smoke test passed")
