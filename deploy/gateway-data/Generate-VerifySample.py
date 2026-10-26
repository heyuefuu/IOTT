#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""为「验证管理」模块生成样例数据（评价知识记录 + 验证任务及其运行结果）。

- 评价知识记录（evaluation-records.json）：当前无任何种子，本脚本按指标配置
  （SeedData/evaluation-indicators.json）构造合法快照，并按 KnowledgeRecord.Prepare
  的规则填充权重、评分、测试结果、备注与总分（权重每级合计 100；评分 0-100 或留空；
  若存在 factor>0 的指标无评分则 totalScore 置空）。
- 验证任务（verify-tasks.json）：生成 pending / 通过 / 不通过 三类任务，并写入
  lastRunJson（含 7 项自动指标的运行指标与结论），用于任务列表与结果可视化。

写入门网关的 App_Data 目录（工程目录与 bin 下所有 App_Data），覆盖前备份为 .bak。

用法：
  python .\deploy\gateway-data\Generate-VerifySample.py --root D:\Company\IOTT\IOTT
"""
from __future__ import annotations

import argparse
import copy
import json
import os
import sys
import uuid
from datetime import datetime, timezone, timedelta

CST = timezone(timedelta(hours=8))

METRICS = [
    ("industrial-protocol", "5.2.1", "工控协议覆盖率"),
    ("communication-stability", "5.2.2", "通讯稳定性"),
    ("max-connections", "5.2.3", "最大并发连接数"),
    ("transfer-protocol", "5.2.4", "传输协议覆盖率"),
    ("file-integrity", "5.2.5", "文件完整性"),
    ("transfer-speed", "5.2.6", "传输速度"),
    ("file-size", "5.2.7", "文件大小"),
]


def load_json(path: str):
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def app_data_dirs(root: str):
    gw = os.path.join(root, "MachineConnectionApi20260423", "MachineConnectionApi", "MachineConnectionApi")
    dirs = [os.path.join(gw, "App_Data")]
    bin_dir = os.path.join(gw, "bin")
    if os.path.isdir(bin_dir):
        for cur, sub, _ in os.walk(bin_dir):
            if os.path.basename(cur) == "App_Data":
                dirs.append(cur)
    return dirs


def find_first(*candidates):
    for c in candidates:
        if c and os.path.isfile(c):
            return c
    return None


def write_all(dirs, name, obj):
    text = json.dumps(obj, ensure_ascii=False, indent=2)
    for d in dirs:
        os.makedirs(d, exist_ok=True)
        path = os.path.join(d, name)
        if os.path.isfile(path):
            with open(path, "r", encoding="utf-8-sig") as s:
                existing = s.read()
            if existing == text:
                print(f"  跳过（内容一致）: {path}")
                continue
            with open(path + ".bak", "w", encoding="utf-8") as s:
                s.write(existing)
        with open(path, "w", encoding="utf-8") as s:
            s.write(text)
        print(f"  已写入: {path}")


# ---------------- 评价知识记录 ----------------

def flatten(config):
    cat_w, sub_w, item_w = {}, {}, {}
    for sec in config["indicators"]:
        cat_w[sec["id"]] = sec["weight"]
        for child in sec["children"]:
            sub_w[child["id"]] = child["weight"]
            for item in child["items"]:
                item_w[item["id"]] = item["weight"]
    return cat_w, sub_w, item_w


def score_for(profile, index):
    if profile == "excellent":
        return float(95 + index % 5)
    if profile == "good":
        return float(80 + index % 10)
    if profile == "partial":
        return float(45 + index % 25)
    return float(80 + index % 10)


def compute_total(config, cat_w, sub_w, item_w, scores):
    total, complete = 0.0, True
    for sec in config["indicators"]:
        for child in sec["children"]:
            sibling = sum(i["weight"] for i in child["items"]) or 1.0
            for item in child["items"]:
                factor = cat_w[sec["id"]] * sub_w[child["id"]] * item_w[item["id"]] / (10000.0 * sibling)
                score = scores.get(item["id"])
                if factor > 0 and score is None:
                    complete = False
                total += (score or 0.0) * factor
    return round(total, 1) if complete else None


def build_record(config, profile, seq):
    cat_w, sub_w, item_w = flatten(config)
    scores, test_results, remarks = {}, {}, {}
    index = 0
    for sec in config["indicators"]:
        for child in sec["children"]:
            for item in child["items"]:
                index += 1
                if profile == "incomplete" and index % 7 == 0:
                    scores[item["id"]] = None
                else:
                    scores[item["id"]] = score_for(profile, index)
                test_results[item["id"]] = (
                    "符合要求" if (scores[item["id"]] or 0) >= 80
                    else "部分符合" if (scores[item["id"]] or 0) >= 50
                    else "不适用" if scores[item["id"]] is None
                    else "不符合"
                )
                remarks[item["id"]] = ""

    total = compute_total(config, cat_w, sub_w, item_w, scores)
    now = datetime.now(CST).isoformat()
    label = {"excellent": "优", "good": "良", "partial": "中", "incomplete": "待补全"}[profile]
    category = config.get("category", "machine")
    return {
        "id": uuid.uuid4().hex,
        "machineName": f"样例机床-{label}{seq}",
        "machineNo": f"MC-{category.upper()}-{seq:03d}",
        "machineModel": "0i-MF",
        "controlSystem": "华中数控 8型",
        "partName": "演示零件",
        "partFeature": "演示特征",
        "testLocation": "验证实验室",
        "testDate": "2026-10-10",
        "tester": "system",
        "dataSource": "manual",
        "category": category,
        "snapshot": copy.deepcopy(config),
        "version": 1,
        "categoryWeights": cat_w,
        "subCategoryWeights": sub_w,
        "scores": scores,
        "weights": item_w,
        "testResults": test_results,
        "remarks": remarks,
        "conclusion": f"综合评分 {total}" if total is not None else "存在未评分项，无法计算总分",
        "suggestion": "样例数据，用于验证知识库/评价记录的完整性与评分逻辑。",
        "syncTaskId": None,
        "syncRunId": None,
        "createdAt": now,
        "updatedAt": now,
        "totalScore": total,
    }


def validate_record(rec):
    # 复核：必填、日期、权重同级合计 100、评分范围、总分一致性
    for field in ("machineName", "machineNo", "machineModel", "controlSystem", "partName", "testDate"):
        assert rec[field], f"缺少必填字段 {field}"
    datetime.strptime(rec["testDate"], "%Y-%m-%d")
    for key in ("categoryWeights", "subCategoryWeights", "weights", "scores", "testResults", "remarks"):
        assert rec[key] is not None, f"{key} 不能为空"
    assert abs(sum(rec["categoryWeights"].values()) - 100) < 0.1
    for sec in rec["snapshot"]["indicators"]:
        assert abs(sum(rec["subCategoryWeights"][c["id"]] for c in sec["children"]) - 100) < 0.1
        for child in sec["children"]:
            assert abs(sum(rec["weights"][i["id"]] for i in child["items"]) - 100) < 0.1
    for v in rec["scores"].values():
        assert v is None or 0 <= v <= 100
    cat_w, sub_w, item_w = flatten(rec["snapshot"])
    assert rec["totalScore"] == compute_total(rec["snapshot"], cat_w, sub_w, item_w, rec["scores"])


# ---------------- 验证任务 ----------------

def run_result(task_id, task_name, device_id, pass_map, category, snapshot=None):
    now = datetime.now(CST)
    metrics = []
    for mid, code, name in METRICS:
        ok = pass_map.get(mid, True)
        metrics.append({
            "metricId": mid, "code": code, "name": name + "指标",
            "status": "passed" if ok else "failed",
            "result": "达标" if ok else "未达标",
            "value": "样例值",
            "reference": f"指标库标准（{name}）",
            "detail": "样例运行结果，用于结果可视化验证",
            "evidence": [f"sample metric={mid}"],
        })
    passed = sum(1 for m in metrics if m["status"] == "passed")
    overall = passed == len(metrics)
    result = "通过" if overall else "不通过"
    started = now - timedelta(seconds=12)
    payload = {
        "runId": uuid.uuid4().hex,
        "taskId": task_id,
        "taskName": task_name,
        "status": "completed" if overall else "failed",
        "result": result,
        "detail": f"自动验证完成：达标 {passed}/{len(metrics)} 项",
        "startedAt": started.strftime("%Y-%m-%d %H:%M:%S"),
        "completedAt": now.strftime("%Y-%m-%d %H:%M:%S"),
        "metrics": metrics,
        "deviceId": device_id,
    }
    if snapshot is not None:
        payload["evaluationSnapshot"] = snapshot
    return payload, result, f"自动验证完成：达标 {passed}/{len(metrics)} 项"


def build_tasks(device_id, snapshots):
    now = datetime.now(CST)
    all_true = {m[0]: True for m in METRICS}
    mixed = {m[0]: (i % 2 == 0) for i, m in enumerate(METRICS)}

    def options():
        return {
            "communicationRounds": 3, "probeTimeoutMs": 3000, "maxParallelTargets": 50,
            "requiredMinConcurrentSuccess": 0,
            "concurrentDeviceIds": [device_id], "allowFileWrites": True,
        }

    def task(tid, name, status, priority, metrics, last_run):
        return {
            "id": tid, "name": name, "type": "automation", "status": status, "priority": priority,
            "deviceId": device_id, "machineId": device_id,
            "evaluationCategory": "machine", "metricIds": metrics, "options": options(),
            "currentRunJson": "", "activeRunId": None, "params": "",
            "description": "样例验证任务（Generate-VerifySample.py）",
            "createdAt": now.isoformat(),
            "completedAt": (now.isoformat() if status in ("completed", "failed") else None),
            "executionTime": ("12.5 s" if status in ("completed", "failed") else ""),
            "result": (last_run[1] if last_run else ""),
            "detail": (last_run[2] if last_run else ""),
            "scheduleType": "none", "scheduleTime": "", "lastAutoRunAt": None,
            "lastRunJson": (json.dumps(last_run[0], ensure_ascii=False) if last_run else ""),
        }

    pass_run = run_result("sample-task-pass", "样例-全部通过", device_id, all_true, "machine",
                          snapshots.get("machine") if snapshots else None)
    fail_run = run_result("sample-task-fail", "样例-部分不通过", device_id, mixed, "machine",
                          snapshots.get("machine") if snapshots else None)
    return [
        task("sample-task-pending", "样例-待运行任务", "pending", "中", [m[0] for m in METRICS], None),
        task("sample-task-pass", "样例-全部通过", "completed", "高", [m[0] for m in METRICS], pass_run),
        task("sample-task-fail", "样例-部分不通过", "failed", "低", [m[0] for m in METRICS], fail_run),
    ]


# ---------------- 测试附件（文件类指标 file-integrity/transfer-speed/file-size） ----------------

FILE_METRIC_SIZES = {
    "file-integrity": [0.1, 1, 10, 20],
    "transfer-speed": [10.0],
    "file-size": [1.0, 10.0, 200.0],
}
BINARY_SIZES = [0.1, 1, 10, 20]  # 默认生成真实二进制；200MiB 需 --large


def create_attachment(dirs, size_mib):
    size_bytes = int(round(size_mib * 1024 * 1024))
    fid = uuid.uuid4().hex
    name = f"verify-{size_mib:g}MiB.nc"
    display = f"{size_mib:g} MiB"
    chunk = bytes(8192)
    for d in dirs:
        adir = os.path.join(d, "evaluation-attachments")
        os.makedirs(adir, exist_ok=True)
        with open(os.path.join(adir, fid + ".bin"), "wb") as f:
            remaining = size_bytes
            while remaining > 0:
                n = min(len(chunk), remaining)
                f.write(chunk[:n])
                remaining -= n
        meta = {"id": fid, "name": name, "size": display, "sizeBytes": size_bytes}
        with open(os.path.join(adir, fid + ".json"), "w", encoding="utf-8") as f:
            json.dump(meta, f, ensure_ascii=False, indent=2)
    return {"id": fid, "name": name, "size": display, "sizeBytes": size_bytes, "mib": size_mib}


def patch_indicator_files(indicators_path, dirs, specs):
    arr = load_json(indicators_path)
    for cfg in arr:
        for sec in cfg.get("indicators", []):
            for child in sec.get("children", []):
                for item in child.get("items", []):
                    mid = item.get("metricId")
                    if mid in FILE_METRIC_SIZES:
                        want = FILE_METRIC_SIZES[mid]
                        item["files"] = [
                            {"id": s["id"], "name": s["name"], "size": s["size"], "sizeBytes": s["sizeBytes"]}
                            for s in specs if any(abs(s["mib"] - w) <= 0.001 for w in want)
                        ]
    write_all(dirs, "evaluation-indicators.json", arr)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=None, help="仓库根（含 Host/、MachineConnectionApi20260423/）")
    ap.add_argument("--large", action="store_true", help="额外生成 200MiB 附件（file-size 档位；默认仅 0.1/1/10/20）")
    args = ap.parse_args()

    root = args.root
    if not root:
        cur = os.path.abspath(os.path.dirname(__file__))
        while cur and not os.path.isdir(os.path.join(cur, "Host")):
            parent = os.path.dirname(cur)
            if parent == cur:
                cur = None
                break
            cur = parent
        root = cur
    if not root or not os.path.isdir(root):
        print("未找到仓库根，请用 --root 指定。", file=sys.stderr)
        return 1

    gw = os.path.join(root, "MachineConnectionApi20260423", "MachineConnectionApi", "MachineConnectionApi")
    indicators_path = find_first(
        os.path.join(gw, "App_Data", "evaluation-indicators.json"),
        os.path.join(gw, "SeedData", "evaluation-indicators.json"),
    )
    devices_path = find_first(
        os.path.join(gw, "App_Data", "devices.json"),
        os.path.join(gw, "SeedData", "devices.json"),
    )
    if not indicators_path:
        print("未找到 evaluation-indicators.json，无法生成评价记录。", file=sys.stderr)
        return 1

    configs = load_json(indicators_path)
    by_cat = {c.get("category", "machine"): c for c in configs}

    # 评价知识记录：machine 覆盖 优/良/中/待补全；machining 覆盖 良/中
    plan = [("machine", "excellent"), ("machine", "good"), ("machine", "partial"), ("machine", "incomplete"),
            ("machining", "good"), ("machining", "partial")]
    records = []
    for seq, (cat, profile) in enumerate(plan, 1):
        cfg = by_cat.get(cat)
        if not cfg:
            continue
        rec = build_record(cfg, profile, seq)
        validate_record(rec)
        records.append(rec)
    print(f"生成评价知识记录 {len(records)} 条（machine: 优/良/中/待补全；machining: 良/中）")

    device_id = "sample-device-001"
    if devices_path:
        try:
            devices = load_json(devices_path)
            if isinstance(devices, list) and devices:
                device_id = devices[0].get("id", device_id)
        except Exception as e:  # noqa
            print(f"  读取设备失败，使用默认设备ID：{e}")
    snapshots = {"machine": by_cat.get("machine")} if by_cat.get("machine") else {}
    tasks = build_tasks(device_id, snapshots)
    print(f"生成验证任务 {len(tasks)} 个（pending/completed/failed），设备 {device_id}")

    dirs = app_data_dirs(root)
    print("目标 App_Data：")
    for d in dirs:
        print("  - " + d)

    # 测试附件 + 指标项 files 补丁（解决"配置测试附件缺失、大小不符或仅有默认文件名"）
    binary_sizes = list(BINARY_SIZES) + ([200.0] if args.large else [])
    spec_sizes = sorted({s for v in FILE_METRIC_SIZES.values() for s in v})
    specs_by_size = {s: create_attachment(dirs, s) for s in binary_sizes}
    for s in spec_sizes:
        if s not in specs_by_size:
            specs_by_size[s] = {"id": uuid.uuid4().hex, "name": f"verify-{s:g}MiB.nc",
                                "size": f"{s:g} MiB", "sizeBytes": int(round(s * 1024 * 1024)), "mib": s}
    specs = [specs_by_size[s] for s in spec_sizes]
    patch_indicator_files(indicators_path, dirs, specs)
    print(f"生成测试附件 {len(binary_sizes)} 个（真实二进制）并补丁指标项 files，档位 {spec_sizes}"
          + ("" if args.large else "（200MiB 仅写元数据；如需真实文件用 --large）"))

    write_all(dirs, "evaluation-records.json", records)
    write_all(dirs, "verify-tasks.json", tasks)
    print("完成。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
