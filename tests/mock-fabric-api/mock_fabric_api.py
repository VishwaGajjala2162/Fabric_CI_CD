#!/usr/bin/env python3
"""
Tiny in-memory mock of the Fabric REST API endpoints the framework uses.
Lets you exercise bootstrap / plan / deploy / history end-to-end without a tenant:

    python3 tests/mock-fabric-api/mock_fabric_api.py 5055
    export FABRIC_API_BASE_URL=http://localhost:5055/v1 FABRIC_ACCESS_TOKEN=fake
    dotnet run --project src/Fabric.Deployment.Cli -- deploy --config tests/mock-fabric-api/deploysettings.mock.json ...

Behaviour is deliberately simplified (not a faithful emulation of Fabric).
Failure injection: a deploy whose note contains "FAIL" deploys half the items, then fails
(use it to exercise automatic rollback).
"""
import base64, datetime, json, sys, uuid, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

lock = threading.Lock()
S = {
    "capacities": [
        {"id": "11111111-0000-0000-0000-000000000001", "displayName": "cap-dev-f4", "sku": "F4", "region": "West Europe", "state": "Active"},
        {"id": "11111111-0000-0000-0000-000000000002", "displayName": "cap-prod-f64", "sku": "F64", "region": "West Europe", "state": "Active"},
        {"id": "11111111-0000-0000-0000-000000000003", "displayName": "cap-us-f8", "sku": "F8", "region": "East US", "state": "Active"},
    ],
    "workspaces": {},   # id -> ws
    "items": {},        # id -> item
    "pipelines": {},    # id -> {pipeline, stages:[...]}
    "links": [],        # list of {stageId: itemId} dicts per pipeline (logical item across stages)
    "operations": {},   # id -> {state, polls, result, pipelineId, record}
}
FAIL_ONCE = {"429": True}

def gid(): return str(uuid.uuid4())

def make_def(name, content):
    b = lambda s: base64.b64encode(s.encode()).decode()
    return {"parts": [{"path": "content.txt", "payload": b(content), "payloadType": "InlineBase64"},
                      {"path": ".platform", "payload": b(json.dumps({"metadata": {"displayName": name}})), "payloadType": "InlineBase64"}]}

def seed():
    ws = {"id": gid(), "displayName": "Sales-DEV", "type": "Workspace", "capacityId": S["capacities"][0]["id"]}
    S["workspaces"][ws["id"]] = ws
    for name, t in [("Sales Lakehouse", "Lakehouse"), ("Load Sales", "Notebook"), ("Sales Model", "SemanticModel"),
                    ("Sales Report", "Report"), ("Scratch Notebook", "Notebook"), ("Daily ETL", "DataPipeline")]:
        i = {"id": gid(), "displayName": name, "type": t, "workspaceId": ws["id"], "definition": make_def(name, f"{name} v1")}
        S["items"][i["id"]] = i
seed()

class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    def log_message(self, *a): pass

    def send(self, code, body=None, headers=None):
        data = b"" if body is None else json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        for k, v in (headers or {}).items(): self.send_header(k, v)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def body(self):
        return json.loads(self._raw or b"{}")

    def err(self, code, ec, msg): self.send(code, {"errorCode": ec, "message": msg, "requestId": gid()})

    def do_GET(self):  self.route("GET")
    def do_POST(self): self.route("POST")
    def do_DELETE(self): self.route("DELETE")

    def route(self, m):
        # always consume the request body so keep-alive connections stay in sync
        self._raw = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        if not (self.headers.get("Authorization") or "").startswith("Bearer "):
            return self.err(401, "Unauthorized", "missing token")
        path, _, qs = self.path.partition("?")
        p = path.rstrip("/").split("/")[2:]  # strip '', 'v1'
        with lock:
            try:
                return self.dispatch(m, p, qs)
            except KeyError as e:
                return self.err(404, "EntityNotFound", f"not found {e}")

    def dispatch(self, m, p, qs):
        # simulate one throttle to exercise retry logic
        if m == "GET" and p == ["capacities"] and FAIL_ONCE["429"]:
            FAIL_ONCE["429"] = False
            return self.send(429, {"errorCode": "TooManyRequests", "message": "throttled"}, {"Retry-After": "1"})

        if p == ["capacities"]: return self.send(200, {"value": S["capacities"]})

        # --- test helpers (not part of Fabric): inspect / bump item versions by name
        if p == ["_test", "items"]:
            return self.send(200, [{"ws": S["workspaces"][i["workspaceId"]]["displayName"], "name": i["displayName"], "type": i["type"],
                                    "content": base64.b64decode(i["definition"]["parts"][0]["payload"]).decode()}
                                   for i in S["items"].values()])
        if p == ["_test", "add"] and m == "POST":
            b = self.body()
            ws = next(w for w in S["workspaces"].values() if w["displayName"] == b["workspace"])
            i = {"id": gid(), "displayName": b["name"], "type": b["type"], "workspaceId": ws["id"], "definition": make_def(b["name"], "v1")}
            S["items"][i["id"]] = i
            return self.send(200, {"id": i["id"]})
        if p == ["_test", "bump"] and m == "POST":
            b = self.body()
            for i in S["items"].values():
                if S["workspaces"][i["workspaceId"]]["displayName"] == b["workspace"]:
                    i["definition"] = make_def(i["displayName"], f"{i['displayName']} {b['version']}")
            return self.send(200)

        if p == ["workspaces"] and m == "GET":
            all_ws = list(S["workspaces"].values())
            # two pages to exercise continuation tokens
            if "continuationToken=page2" in qs: return self.send(200, {"value": all_ws[1:]})
            if len(all_ws) > 1: return self.send(200, {"value": all_ws[:1], "continuationToken": "page2"})
            return self.send(200, {"value": all_ws})
        if p == ["workspaces"] and m == "POST":
            b = self.body()
            if any(w["displayName"] == b["displayName"] for w in S["workspaces"].values()):
                return self.err(409, "WorkspaceNameAlreadyExists", "exists")
            ws = {"id": gid(), "displayName": b["displayName"], "type": "Workspace", "capacityId": b.get("capacityId"),
                  "description": b.get("description")}
            S["workspaces"][ws["id"]] = ws
            return self.send(201, ws)
        if len(p) == 2 and p[0] == "workspaces": return self.send(200, S["workspaces"][p[1]])
        if len(p) == 3 and p[0] == "workspaces" and p[2] == "assignToCapacity":
            S["workspaces"][p[1]]["capacityId"] = self.body()["capacityId"]; return self.send(202)
        if len(p) == 3 and p[0] == "workspaces" and p[2] == "items":
            S["workspaces"][p[1]]
            return self.send(200, {"value": [{k: v for k, v in i.items() if k != "definition"}
                                            for i in S["items"].values() if i["workspaceId"] == p[1]]})

        if len(p) == 5 and p[0] == "workspaces" and p[2] == "items" and p[4] == "getDefinition":
            it = S["items"][p[3]]
            if it["type"] == "Dashboard":
                return self.err(400, "OperationNotSupportedForItem", "Dashboard has no definition API")
            body = {"definition": it["definition"]}
            if it["type"] == "Report":           # exercise the long-running path
                op_id = gid(); S["operations"][op_id] = {"polls": 0, "result": body, "record": None, "fail": False}
                return self.send(202, None, {"Location": f"http://localhost/v1/operations/{op_id}", "x-ms-operation-id": op_id, "Retry-After": "1"})
            return self.send(200, body)
        if len(p) == 5 and p[0] == "workspaces" and p[2] == "items" and p[4] == "updateDefinition":
            it = S["items"][p[3]]
            it["definition"] = self.body()["definition"]
            return self.send(200)
        if len(p) == 4 and p[0] == "workspaces" and p[2] == "items" and m == "DELETE":
            it = S["items"].pop(p[3])
            for l in S["links"]:
                for k in [k for k, v in l.items() if v == it["id"]]: del l[k]
            return self.send(200)
        if len(p) == 4 and p[0] == "workspaces" and p[2] == "items" and m == "GET":
            return self.send(200, {k: v for k, v in S["items"][p[3]].items() if k != "definition"})

        if p == ["deploymentPipelines"] and m == "GET":
            return self.send(200, {"value": [x["pipeline"] for x in S["pipelines"].values()]})
        if p == ["deploymentPipelines"] and m == "POST":
            b = self.body()
            pl = {"id": gid(), "displayName": b["displayName"], "description": b.get("description")}
            stages = [{"id": gid(), "order": n, "displayName": s["displayName"], "description": s.get("description", ""),
                       "isPublic": s.get("isPublic", False)} for n, s in enumerate(b["stages"])]
            S["pipelines"][pl["id"]] = {"pipeline": pl, "stages": stages, "ops": []}
            return self.send(201, pl)

        if len(p) >= 2 and p[0] == "deploymentPipelines":
            P = S["pipelines"][p[1]]
            stages = P["stages"]
            if len(p) == 3 and p[2] == "stages":
                out = []
                for s in stages:
                    s2 = dict(s)
                    if s.get("workspaceId"): s2["workspaceName"] = S["workspaces"][s["workspaceId"]]["displayName"]
                    out.append(s2)
                return self.send(200, {"value": out})
            if len(p) == 5 and p[2] == "stages" and p[4] == "assignWorkspace":
                st = next(s for s in stages if s["id"] == p[3]); st["workspaceId"] = self.body()["workspaceId"]
                return self.send(200)
            if len(p) == 5 and p[2] == "stages" and p[4] == "items":
                st = next(s for s in stages if s["id"] == p[3])
                return self.send(200, {"value": self.stage_items(p[1], st)})
            if len(p) == 3 and p[2] == "operations":
                return self.send(200, {"value": P["ops"]})
            if len(p) == 4 and p[2] == "operations":
                op = next((o for o in S["operations"].values() if o["record"] and o["record"]["id"] == p[3]), None)
                if op is None: return self.err(404, "EntityNotFound", "operation")
                done = op["record"]["status"] != "Running"
                return self.send(200, op["result"] if done else dict(op["result"], status="Running"))
            if len(p) == 3 and p[2] == "deploy":
                return self.deploy(p[1], self.body())

        if len(p) == 2 and p[0] == "operations":
            op = S["operations"][p[1]]
            op["polls"] += 1
            if op["polls"] < 2:
                return self.send(200, {"status": "Running", "percentComplete": 50}, {"Retry-After": "1"})
            status = "Failed" if op.get("fail") else "Succeeded"
            if op["record"]: op["record"]["status"] = status
            if op.get("fail"):
                return self.send(200, {"status": "Failed", "percentComplete": 50,
                                       "error": {"errorCode": "DeploymentFailed", "message": "Injected failure (note contained FAIL)"}})
            return self.send(200, {"status": "Succeeded", "percentComplete": 100})
        if len(p) == 3 and p[0] == "operations" and p[2] == "result":
            return self.send(200, S["operations"][p[1]]["result"])

        return self.err(404, "NotFound", f"{m} {self.path}")

    def stage_items(self, pid, st):
        P = S["pipelines"][pid]
        if not st.get("workspaceId"): return []
        orders = {s["id"]: s["order"] for s in P["stages"]}
        by_order = {s["order"]: s["id"] for s in P["stages"]}
        out = []
        for it in [i for i in S["items"].values() if i["workspaceId"] == st["workspaceId"]]:
            link = next((l for l in S["links"] if l.get(st["id"]) == it["id"]), {})
            prev_s, next_s = by_order.get(orders[st["id"]] - 1), by_order.get(orders[st["id"]] + 1)
            r = {"itemId": it["id"], "itemDisplayName": it["displayName"], "itemType": it["type"]}
            if prev_s and link.get(prev_s): r["sourceItemId"] = link[prev_s]
            if next_s and link.get(next_s): r["targetItemId"] = link[next_s]
            out.append(r)
        return out

    def deploy(self, pid, b):
        P = S["pipelines"][pid]
        if any(o["status"] == "Running" for o in P["ops"]):
            return self.err(409, "DeploymentInProgress", "another deployment is running")
        src = next(s for s in P["stages"] if s["id"] == b["sourceStageId"])
        tgt = next(s for s in P["stages"] if s["id"] == b["targetStageId"])
        if not tgt.get("workspaceId"):
            d = b.get("createdWorkspaceDetails")
            if not d: return self.err(400, "InvalidRequest", "createdWorkspaceDetails required for empty stage")
            ws = {"id": gid(), "displayName": d["name"], "type": "Workspace", "capacityId": d.get("capacityId")}
            S["workspaces"][ws["id"]] = ws; tgt["workspaceId"] = ws["id"]
        src_items = [i for i in S["items"].values() if i["workspaceId"] == src["workspaceId"]]
        wanted = {x["sourceItemId"] for x in b.get("items") or []} or {i["id"] for i in src_items}
        steps = []
        fail = "FAIL" in (b.get("note") or "")
        chosen = [i for i in src_items if i["id"] in wanted]
        for n, it in enumerate(chosen):
            if fail and n >= len(chosen) // 2:
                steps.append({"index": n, "description": f"Deploy {it['displayName']}", "status": "Failed",
                              "preDeploymentDiffState": "Different",
                              "sourceAndTarget": {"sourceItemId": it["id"], "sourceItemDisplayName": it["displayName"], "itemType": it["type"]},
                              "error": {"errorCode": "InjectedFailure", "message": "simulated"}})
                continue
            link = next((l for l in S["links"] if l.get(src["id"]) == it["id"]), None)
            if link is None: link = {src["id"]: it["id"]}; S["links"].append(link)
            state = "Different" if link.get(tgt["id"]) else "New"
            if not link.get(tgt["id"]):
                new = {"id": gid(), "displayName": it["displayName"], "type": it["type"], "workspaceId": tgt["workspaceId"]}
                S["items"][new["id"]] = new; link[tgt["id"]] = new["id"]
            S["items"][link[tgt["id"]]]["definition"] = json.loads(json.dumps(it["definition"]))
            steps.append({"index": n, "description": f"Deploy {it['displayName']}", "status": "Succeeded",
                          "preDeploymentDiffState": state,
                          "sourceAndTarget": {"sourceItemId": it["id"], "sourceItemDisplayName": it["displayName"],
                                              "targetItemId": link[tgt["id"]], "targetItemDisplayName": it["displayName"],
                                              "itemType": it["type"]}})
        op_id, dep_id = gid(), gid()
        record = {"id": op_id, "type": "Deploy", "status": "Running", "sourceStageId": src["id"], "targetStageId": tgt["id"],
                  "executionStartTime": datetime.datetime.now(datetime.timezone.utc).isoformat(), "performedBy": {"displayName": "mock-spn", "type": "ServicePrincipal"},
                  "note": {"content": b.get("note", ""), "isTruncated": False}}
        P["ops"].append(record)
        result = dict(record, status="Failed" if fail else "Succeeded", executionPlan={"steps": steps},
                      preDeploymentDiffInformation={"newItemsCount": sum(s["preDeploymentDiffState"] == "New" for s in steps),
                                                    "differentItemsCount": sum(s["preDeploymentDiffState"] == "Different" for s in steps),
                                                    "noDifferenceItemsCount": 0})
        S["operations"][op_id] = {"polls": 0, "result": result, "record": record, "fail": fail}
        return self.send(202, None, {"Location": f"http://localhost/v1/operations/{op_id}", "x-ms-operation-id": op_id,
                                     "deployment-id": dep_id, "Retry-After": "1"})

if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 5055
    print(f"Mock Fabric API on http://localhost:{port}/v1", flush=True)
    ThreadingHTTPServer(("127.0.0.1", port), H).serve_forever()
