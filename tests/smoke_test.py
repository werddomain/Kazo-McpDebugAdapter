#!/usr/bin/env python3
"""
End-to-end smoke test: drives the MCP Debug Adapter over stdio exactly like an MCP client
(Claude, VS Code...) and debugs tests/SampleApp with netcoredbg.

Usage (from the repository root):
    python tests/smoke_test.py [--netcoredbg PATH] [--no-build]

Requires the .NET SDK and netcoredbg (in PATH, NETCOREDBG_PATH, or --netcoredbg).
"""

import argparse
import json
import os
import queue
import subprocess
import sys
import threading
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ADAPTER_PROJECT = os.path.join(ROOT, "src", "McpDebugAdapter", "McpDebugAdapter.csproj")
SAMPLE_PROJECT = os.path.join(ROOT, "tests", "SampleApp", "SampleApp.csproj")
SAMPLE_SOURCE = os.path.join(ROOT, "tests", "SampleApp", "Program.cs")
LINE_IN_ADD = 8    # var sum = a + b;
LINE_IN_LOOP = 19  # var r = Add(i, 10);


class McpClient:
    def __init__(self, command, env):
        self.proc = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=sys.stderr, env=env)
        self.messages = queue.Queue()
        self.next_id = 0
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        for raw in self.proc.stdout:
            line = raw.decode("utf-8").strip()
            if line:
                self.messages.put(json.loads(line))

    def send(self, payload):
        self.proc.stdin.write((json.dumps(payload) + "\n").encode("utf-8"))
        self.proc.stdin.flush()

    def request(self, method, params=None, timeout=120):
        self.next_id += 1
        request_id = self.next_id
        self.send({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params or {}})
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                message = self.messages.get(timeout=1)
            except queue.Empty:
                continue
            if message.get("id") == request_id:
                return message
            raise AssertionError(f"unexpected message while waiting for {method}: {message}")
        raise AssertionError(f"no response to {method}")

    def tool(self, name, **arguments):
        response = self.request("tools/call", {"name": name, "arguments": arguments})
        result = response["result"]
        text = "\n".join(c.get("text", "") for c in result["content"] if c["type"] == "text")
        print(f"--- {name} {arguments if arguments else ''}\n{text}\n")
        return result, text

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=15)
        except Exception:
            self.proc.kill()


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def build(project, configuration):
    subprocess.run(["dotnet", "build", project, "-c", configuration, "-nologo", "-v", "q"], check=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--netcoredbg", help="path to netcoredbg executable")
    parser.add_argument("--no-build", action="store_true")
    options = parser.parse_args()

    if not options.no_build:
        build(ADAPTER_PROJECT, "Debug")
        build(SAMPLE_PROJECT, "Debug")

    ext = ".exe" if os.name == "nt" else ""
    adapter_dll = os.path.join(ROOT, "src", "McpDebugAdapter", "bin", "Debug", "net8.0", "McpDebugAdapter.dll")
    sample_dll = os.path.join(ROOT, "tests", "SampleApp", "bin", "Debug", "net10.0", "SampleApp.dll")
    sample_exe = os.path.join(ROOT, "tests", "SampleApp", "bin", "Debug", "net10.0", "SampleApp" + ext)

    env = dict(os.environ)
    if options.netcoredbg:
        env["NETCOREDBG_PATH"] = options.netcoredbg

    client = McpClient(["dotnet", adapter_dll], env)
    try:
        init = client.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                             "clientInfo": {"name": "smoke", "version": "1"}})
        check(init["result"]["protocolVersion"] == "2025-06-18", "protocol version not negotiated")
        client.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        client.send({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": 99}})

        unknown = client.request("does/not/exist")
        check(unknown.get("error", {}).get("code") == -32601 and unknown["id"] is not None, "bad unknown-method error")

        tools = client.request("tools/list")["result"]["tools"]
        check(any(t["name"] == "debug_wait_for_stop" for t in tools), "tools/list incomplete")

        # 1. Launch with a breakpoint set beforehand
        _, text = client.tool("debug_set_breakpoint", file=SAMPLE_SOURCE, line=LINE_IN_ADD)
        check("queued" in text, "breakpoint should be queued before launch")
        result, text = client.tool("debug_launch", programPath=sample_dll)
        check(not result["isError"], "launch failed")

        _, text = client.tool("debug_wait_for_stop", waitSeconds=30)
        check(f"Program.cs:{LINE_IN_ADD}" in text, "did not stop at the breakpoint")

        _, text = client.tool("debug_get_variables")
        check("a: 0" in text and "b: 10" in text, "unexpected locals")

        _, text = client.tool("debug_evaluate", expression="a + b")
        check("= 10" in text, "evaluate failed")

        _, text = client.tool("debug_get_stack")
        check("Program.Add" in text and "Program.Main" in text, "unexpected stack")

        _, text = client.tool("debug_step_out")
        check("Program.Main" in text, "step out did not return to Main")

        _, text = client.tool("debug_step_next")
        check("Step over done" in text, "step over failed")

        # 2. Conditional breakpoint while running
        client.tool("debug_remove_breakpoint", file=SAMPLE_SOURCE, line=LINE_IN_ADD)
        _, text = client.tool("debug_set_breakpoint", file=SAMPLE_SOURCE, line=LINE_IN_LOOP, condition="i == 3")
        check("bound" in text, "breakpoint not bound in running session")
        _, text = client.tool("debug_continue", waitSeconds=30)
        check(f"Program.cs:{LINE_IN_LOOP}" in text, "conditional breakpoint not hit")
        _, text = client.tool("debug_evaluate", expression="i")
        check("= 3" in text, "condition was not honoured")

        _, text = client.tool("debug_get_output")
        check("Bonjour Benoît – démarrage" in text, "non-ASCII output lost")

        client.tool("debug_remove_breakpoint", file=SAMPLE_SOURCE, line=LINE_IN_LOOP)
        _, text = client.tool("debug_continue", waitSeconds=30)
        check("ended" in text.lower(), "program did not finish")
        _, text = client.tool("debug_get_status")
        check('"active": false' in text, "session still active after exit")

        # 3. Attach to a running process (apphost executable)
        target = subprocess.Popen([sample_exe, "long"], stdout=subprocess.DEVNULL)
        try:
            time.sleep(2)
            result, text = client.tool("debug_attach", processId=target.pid)
            check(not result["isError"], "attach failed")
            client.tool("debug_set_breakpoint", file=SAMPLE_SOURCE, line=LINE_IN_ADD)
            _, text = client.tool("debug_wait_for_stop", waitSeconds=30)
            check(f"Program.cs:{LINE_IN_ADD}" in text, "attach: breakpoint not hit")
            _, text = client.tool("debug_pause")
            check("already paused" in text, "pause while stopped")
            client.tool("debug_remove_breakpoint", file=SAMPLE_SOURCE, line=LINE_IN_ADD)
            client.tool("debug_continue")
            _, text = client.tool("debug_pause")
            check("Paused" in text, "pause failed")
            result, text = client.tool("debug_stop")
            check(not result["isError"] and "Detached" in text, "stop failed")
            time.sleep(1)
            check(target.poll() is None, "detaching killed the attached process")
        finally:
            target.kill()

        print("SMOKE TEST PASSED")
        return 0
    finally:
        client.close()


if __name__ == "__main__":
    sys.exit(main())
