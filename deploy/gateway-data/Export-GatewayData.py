"""Export portable gateway business data without local credentials."""
import argparse
import collections
import datetime
import hashlib
import json
import pathlib
import re
import shutil
import subprocess
import urllib.request

SECRET = re.compile(r"password|passwd|pwd|token|secret|credential|authorization|private.?key|cookie|session", re.I)
REDACTIONS = collections.Counter()
CHANGES = []


def clean(value, location=""):
    if isinstance(value, dict):
        result = {}
        for key, child in value.items():
            child_location = location + "/" + key
            if SECRET.search(key):
                if child:
                    REDACTIONS[child_location] += 1
                result[key] = {} if isinstance(child, dict) else [] if isinstance(child, list) else None
            else:
                result[key] = clean(child, child_location)
        return result
    if isinstance(value, list):
        return [clean(child, location + "/" + str(index)) for index, child in enumerate(value)]
    if isinstance(value, str):
        if value.lstrip().startswith(("{", "[")):
            try:
                original = json.loads(value)
                cleaned = clean(original, location + "/embedded-json")
                return json.dumps(cleaned, ensure_ascii=False) if cleaned != original else value
            except json.JSONDecodeError:
                pass
        if re.search(r"(?:password|token|secret|authorization|\u521d\u59cb\u5bc6\u7801|\u53e3\u4ee4)\s*[:=\uff1a ]\s*\S+", value, re.I):
            REDACTIONS[location] += 1
            return "[credential text removed from distribution]"
    return value


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('w', encoding='utf-8', newline='\n') as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write('\n')


def query_mysql(arguments, query):
    result = subprocess.run([str(arguments.mysql_bin / "mysql.exe"),
        "--defaults-extra-file=" + str(arguments.client_config.resolve()), "--batch", "--raw",
        "--default-character-set=utf8mb4", "--skip-column-names", "--execute=" + query],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
    return [json.loads(line) for line in result.stdout.decode("utf-8").splitlines()]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app-data", required=True, type=pathlib.Path)
    parser.add_argument("--host-url", default="http://localhost:5173")
    parser.add_argument("--mysql-bin", type=pathlib.Path, default=pathlib.Path(r"C:\Program Files\MySQL\MySQL Server 8.4\bin"))
    parser.add_argument("--client-config", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    arguments = parser.parse_args()
    if arguments.output.exists() and any(arguments.output.iterdir()):
        raise RuntimeError("Output must be empty; existing snapshots are preserved.")
    snapshot = arguments.output / "App_Data"
    source_files = sorted(arguments.app_data.rglob("*"))
    documents = {}
    for source in source_files:
        if not source.is_file():
            continue
        relative = source.relative_to(arguments.app_data)
        if source.suffix.lower() == ".json":
            documents[relative.as_posix()] = clean(json.loads(source.read_text(encoding="utf-8-sig")), relative.as_posix())
        elif source.suffix.lower() != ".tmp":
            destination = snapshot / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination)
    with urllib.request.urlopen(arguments.host_url.rstrip("/") + "/api/Devices", timeout=30) as response:
        upstream = clean(json.load(response), "upstream-devices")
    devices = {row["id"]: row for row in upstream}
    local_devices = documents.get("devices.json", [])
    for row in local_devices:
        original = devices.get(row["id"], {})
        merged = dict(original, **row)
        merged["extendedProperties"] = dict(original.get("extendedProperties") or {}, **(row.get("extendedProperties") or {}))
        if original.get("transfer") and row.get("transfer"):
            merged["transfer"] = dict(original["transfer"], **row["transfer"])
        devices[row["id"]] = merged
    upstream_ids = {row["id"] for row in upstream}
    for row in devices.values():
        row.update(status="Offline", lastSeenAt=None, upstreamSynced=None, upstreamError=None,
                   restoredFromUpstream=row["id"] in upstream_ids)
    documents["devices.json"] = list(devices.values())
    for row in documents.get("verify-tasks.json", []):
        if row.get("scheduleType") != "none":
            CHANGES.append({"kind": "schedule-disabled", "id": row["id"], "previous": row.get("scheduleType")})
        row["scheduleType"] = "none"
        if row.get("status") == "running" or row.get("currentRunJson") or row.get("activeRunId"):
            raise RuntimeError("Active verification run found; export after it completes to preserve results.")
        if row.get("options", {}) and row["options"].get("allowFileWrites"):
            row["options"]["allowFileWrites"] = False
            CHANGES.append({"kind": "file-write-permission-reset", "id": row["id"]})
    if documents.get("verify-task-completions.json"):
        raise RuntimeError("Pending completion journal found; export after gateway recovery completes.")
    connectivity = documents.get("cs-connectivity.json", {})
    for group, status in (("gateways", "\u505c\u6b62"), ("dataSources", "\u7981\u7528"), ("servers", "\u505c\u6b62")):
        for row in connectivity.get(group, []):
            row["status"] = status
            if group == "servers":
                row["clientCount"] = 0
                if row.get("rootDirectory"):
                    CHANGES.append({"kind": "server-root-reset", "id": row["id"], "previous": row["rootDirectory"]})
                    row["rootDirectory"] = ""
    for name, document in documents.items():
        write_json(snapshot / name, document)
    programs = export_programs(arguments)
    manifest = {"formatVersion": 1, "createdAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "source": "Running gateway AppContext.BaseDirectory/App_Data and upstream device registry",
        "gatewayDeviceCount": len(local_devices), "upstreamDeviceCount": len(upstream), "mergedDeviceCount": len(devices),
        "recordCounts": {name: len(value) if isinstance(value, list) else 1 for name, value in documents.items()},
        "redactions": dict(REDACTIONS), "changes": CHANGES, "programs": programs, "files": []}
    for path in sorted(arguments.output.rglob("*")):
        if path.is_file():
            manifest["files"].append({"path": path.relative_to(arguments.output).as_posix(),
                "bytes": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    write_json(arguments.output / "manifest.json", manifest)
    print(json.dumps({"files": len(manifest["files"]), "records": manifest["recordCounts"],
        "programReferences": len(programs), "programFiles": sum(item["available"] for item in programs)}))


def export_programs(arguments):
    query = "SELECT JSON_OBJECT('id',Id,'fileName',FileName,'localFilePath',LocalFilePath,'remotePath',RemotePath) FROM IndustrialIoT.ncprograms ORDER BY Id"
    records = query_mysql(arguments, query)
    references = []
    for row in records:
        if not row["localFilePath"]:
            continue
        source = pathlib.Path(row["localFilePath"])
        item = {"id": row["id"], "fileName": row["fileName"], "available": source.is_file(), "path": None,
                "sourcePath": clean(row["localFilePath"], "programs/" + row["id"] + "/sourcePath")}
        if item["available"]:
            if source.stat().st_size >= 100 * 1024 * 1024:
                raise RuntimeError("Program file exceeds GitHub limit: " + row["id"])
            relative = pathlib.Path("program-files") / (row["id"] + source.suffix)
            destination = arguments.output / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination)
            item["path"] = relative.as_posix()
        references.append(item)
    return references


if __name__ == "__main__":
    main()
