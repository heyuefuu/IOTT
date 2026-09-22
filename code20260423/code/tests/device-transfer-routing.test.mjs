import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";

const transferModule = vm.createContext({ exports: {} });
vm.runInContext(ts.transpileModule(
    readFileSync(new URL("../src/views/industrial/deviceTransferConfig.ts", import.meta.url), "utf8"),
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } },
).outputText, transferModule);

const component = readFileSync(new URL("../src/views/industrial/DeviceView.vue", import.meta.url), "utf8");
const script = component.match(/<script[^>]*setup[^>]*>([\s\S]*?)<\/script>/)?.[1];
assert.ok(script, "DeviceView script setup exists");
const names = [
    "getDeviceTransferProtocol", "remotePathPickerUsesAddressSpace", "getDeviceTransferRoot",
    "normalizeRemotePath", "normalizeTransferPathByProtocol", "normalizeNCLinkApiFilePath",
    "getRemotePathParent", "createFolderNode", "pathToRemoteNodeKey", "ensureFolderNode",
    "mapTransferFileItemToNode", "buildRemotePathTree", "getPathDepth", "hasDeepDescendants", "fetchRemoteTreeItems",
    "loadRemotePathChildren", "startTransfer", "isDownloadableProgramPathForProtocol",
    "remotePathPickerShowCheckboxes",
    "BRAND_KEY_TO_FORM_LABEL", "inferBrandKey", "mapStatus", "formatSeenAt", "mapDtoToUi",
    "buildExtendedProps", "treeDefaultsForNewDevice", "openAddDeviceDialog", "editDevice", "saveDevice",
    "onDeviceProtocolChange", "onDeviceBrandChange", "onGskSchemeChange", "onTransferProtocolChange",
    "getDelimitedAddressSpaceRemainder", "isAddressSpaceChildPath", "isImmediateAddressSpaceChild",
    "sanitizeAddressSpaceLevelNodes", "testConnection",
];
const parsed = ts.createSourceFile("DeviceView.ts", script, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
const statements = parsed.statements.filter((statement) => {
    const name = ts.isFunctionDeclaration(statement) ? statement.name?.text
        : ts.isVariableStatement(statement) ? statement.declarationList.declarations[0]?.name.getText(parsed) : null;
    return names.includes(name);
});
assert.equal(statements.length, names.length, "Tests execute the component's actual transfer handlers");
const executable = ts.transpileModule(
    statements.map((statement) => statement.getText(parsed)).join("\n") + `\nglobalThis.handlers = { ${names.join(",")} };`,
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } },
).outputText;

function setup(protocol, transferProtocol = "", items = []) {
    const calls = [];
    const warnings = [];
    const successes = [];
    const connectionResult = { success: true, mode: "driver" };
    const device = { id: "device", protocol, transferProtocol };
    const record = (method, result) => async (...args) => { calls.push([method, ...args]); return result; };
    const context = vm.createContext({
        devices: { value: [device] }, transferForm: { value: { deviceId: device.id, direction: "upload" } },
        transferRemotePath: { value: "/NC" }, transferRemotePathPickedKind: { value: "none" },
        transferBatchSelections: { value: [] }, transferSelectedFiles: { value: [{ name: "O0001.nc" }] },
        transferSubmitting: { value: false }, remotePathTreeData: { value: [] }, REMOTE_TREE_MAX_NODES: 2000,
        deviceForm: { value: {} }, selectedTreeNodeId: { value: "" },
        dialogVisible: { value: false }, dialogTitle: { value: "" }, async loadDevices() {},
        buildProgramTransferConfig: transferModule.exports.buildProgramTransferConfig,
        machineConnectionDevicesApi: { create: record("create", {}), update: record("update", {}), testConnection: record("testConnection", connectionResult) },
        computed: (getter) => ({ get value() { return getter(); } }),
        syncRemotePathPickerCurrentNode() {}, notifyBatchDownloadResult() {}, async loadTransferHistory() {},
        getApiErrorMessage: (error) => error.message,
        ElMessage: { warning: (message) => warnings.push(message), error: (message) => { throw new Error(message); }, info() {}, success: (message) => successes.push(message) },
        machineConnectionPointsApi: { browseAddressSpace: () => { throw new Error("File picker used collection address space"); } },
        machineConnectionProgramTransferApi: {
            files: record("files", items), upload: record("upload", { status: "Completed" }),
            uploadBatchWait: record("uploadBatch", { failedFiles: 0, totalFiles: 2 }),
            download: record("download"), downloadBatchZip: record("downloadBatch", { failedFiles: 0 }),
        },
    });
    vm.runInContext(executable, context);
    return { context, handlers: context.handlers, calls, warnings, successes, connectionResult, device };
}

for (const protocol of ["OpcUa", "FOCAS", "NCLinkApi", "GskWebServer", "Gskrm"]) {
    for (const transferProtocol of ["FTP", "SMB", "NFS"]) {
        test(`${protocol} + ${transferProtocol} browses files and permits batch transfers`, async () => {
            const fixture = setup(protocol, transferProtocol, [
                { name: "NC", path: "/NC", nodeType: "folder" },
                { name: "O0001.nc", path: "/NC/O0001.nc", nodeType: "file" },
            ]);
            const { handlers, context, calls, device } = fixture;
            assert.equal(handlers.remotePathPickerUsesAddressSpace(device), false);
            await handlers.loadRemotePathChildren();
            assert.equal(calls[0][0], "files");
            assert.equal(calls[0][2], "/");
            assert.equal(context.remotePathTreeData.value[0].path, "/");
            context.transferSelectedFiles.value.push({ name: "O0002.nc" });
            await handlers.startTransfer();
            assert.equal(calls.at(-1)[0], "uploadBatch");
            context.transferForm.value.direction = "download";
            assert.equal(handlers.remotePathPickerShowCheckboxes.value, true);
            context.transferRemotePathPickedKind.value = "batch";
            context.transferBatchSelections.value = [{ path: "/NC", nodeType: "folder" }];
            await handlers.startTransfer();
            assert.equal(calls.at(-1)[0], "downloadBatch");
            assert.equal(calls.at(-1)[2][0], "/NC/O0001.nc");
        });
    }
}

for (const transferProtocol of ["FTP", "SMB", "NFS"]) {
    test(`Empty ${transferProtocol} root remains selectable and a subdirectory contains only its own children`, async () => {
        const empty = setup("OpcUa", transferProtocol);
        await empty.handlers.loadRemotePathChildren();
        assert.equal(empty.context.remotePathTreeData.value[0].path, "/");
        const fixture = setup("FOCAS", transferProtocol, [{ name: "O0001.nc", path: "/NC/O0001.nc", nodeType: "file" }]);
        const parent = { path: "/NC", nodeType: "folder" };
        await fixture.handlers.loadRemotePathChildren(parent);
        assert.equal(parent.children.length, 1);
        assert.equal(parent.children[0].path, "/NC/O0001.nc");
    });

    test(`${transferProtocol} rejects OPC UA NodeIds before making a transfer request`, async () => {
        const fixture = setup("OpcUa", transferProtocol);
        for (const path of ["ns=2;s=Sinumerik", "i=2253", "nsu=urn:controller;s=Program"]) {
            fixture.context.transferRemotePath.value = path;
            await fixture.handlers.startTransfer();
            assert.equal(fixture.handlers.isDownloadableProgramPathForProtocol(path, fixture.device), false);
        }
        assert.equal(fixture.calls.length, 0);
        assert.equal(fixture.warnings.length, 3);
    });
}

for (const editing of [false, true]) {
    test(`OPC UA can ${editing ? "update" : "create"} without a separate transfer channel`, async () => {
        const fixture = setup("OpcUa");
        fixture.context.selectedTreeNodeId.value = "brand-siemens";
        fixture.handlers.openAddDeviceDialog();
        const form = fixture.context.deviceForm.value;
        assert.equal(form.transferProtocol, "");
        Object.assign(form, { id: editing ? "device" : "", name: "CNC", code: "CNC-1" });
        await fixture.handlers.saveDevice();
        assert.equal(fixture.warnings.length, 0);
        assert.equal(fixture.calls[0][0], editing ? "update" : "create");
        assert.equal(fixture.calls[0].at(-1).protocol, "OpcUa");
        assert.equal(fixture.calls[0].at(-1).transfer, undefined);
    });
}

test("NFS configuration is submitted on create and retains MountPoint when edited", async () => {
    const fixture = setup("OpcUa", "NFS");
    fixture.handlers.openAddDeviceDialog();
    Object.assign(fixture.context.deviceForm.value, {
        name: "CNC", code: "CNC-1", protocol: "OpcUa", port: 4840,
        transferProtocol: "NFS", transferHost: "192.0.2.10", transferPort: 2049,
        transferMountPoint: " /mnt/cnc ",
    });
    await fixture.handlers.saveDevice();
    const payload = fixture.calls[0].at(-1);
    assert.equal(payload.transfer?.protocol, "NFS");
    assert.equal(payload.transfer.extendedProperties.MountPoint, "/mnt/cnc");
    const device = fixture.handlers.mapDtoToUi({ ...payload, id: "device" });
    fixture.handlers.editDevice(device);
    assert.equal(fixture.context.deviceForm.value.transferMountPoint, "/mnt/cnc");
    await fixture.handlers.saveDevice();
    assert.equal(fixture.calls[1][0], "update");
    assert.equal(fixture.calls[1].at(-1).transfer.extendedProperties.MountPoint, "/mnt/cnc");
});

test("NFS requires its mount directory before submitting a device", async () => {
    const fixture = setup("OpcUa", "NFS");
    fixture.handlers.openAddDeviceDialog();
    Object.assign(fixture.context.deviceForm.value, {
        name: "CNC", code: "CNC-1", transferProtocol: "NFS", transferHost: "192.0.2.10",
        transferPort: 2049, transferMountPoint: "   ",
    });
    await fixture.handlers.saveDevice();
    assert.equal(fixture.calls.length, 0);
    assert.equal(fixture.warnings.length, 1);
    assert.match(fixture.warnings[0], /MountPoint/);
});

test("Native FOCAS keeps its single-file flow and accepts CNC memory paths", async () => {
    const fixture = setup("FOCAS");
    assert.equal(fixture.handlers.remotePathPickerUsesAddressSpace(fixture.device), true);
    fixture.context.transferForm.value.direction = "download";
    assert.equal(fixture.handlers.remotePathPickerShowCheckboxes.value, false);
    fixture.context.transferRemotePath.value = "//CNC_MEM/USER/PATH1/O0001";
    await fixture.handlers.startTransfer();
    assert.equal(fixture.calls[0][0], "download");
    assert.equal(fixture.calls[0][2], "//CNC_MEM/USER/PATH1/O0001");
});

test("Native NCLinkApi retains flat keys and its existing file endpoint", async () => {
    const fixture = setup("NCLinkApi");
    assert.equal(fixture.handlers.getDeviceTransferRoot(fixture.device), undefined);
    assert.equal(fixture.handlers.remotePathPickerUsesAddressSpace(fixture.device), false);
    fixture.context.transferRemotePath.value = "/O0001";
    await fixture.handlers.startTransfer();
    assert.equal(fixture.calls[0][0], "upload");
    assert.equal(fixture.calls[0][3], "O0001");
});

for (const [brandKey, protocol, port] of [["huazhong", "NCLinkApi", 19001], ["guangzhou", "GskWebServer", 11520]]) {
    test(`${brandKey} creates the matching driver and port`, async () => {
        const fixture = setup(protocol);
        fixture.context.selectedTreeNodeId.value = `brand-${brandKey}`;
        fixture.handlers.openAddDeviceDialog();
        Object.assign(fixture.context.deviceForm.value, { name: "CNC", code: "CNC-1", ncLinkApiDeviceId: "SN1", gskDeviceSn: "cnc" });
        await fixture.handlers.saveDevice();
        assert.equal(fixture.calls[0].at(-1).protocol, protocol);
        assert.equal(fixture.calls[0].at(-1).port, port);
    });
}

test("User changes switch protocol ports while custom GSK ports remain intact on scheme changes", () => {
    const { context, handlers } = setup("FOCAS");
    handlers.openAddDeviceDialog();
    handlers.onDeviceBrandChange("华中数控");
    assert.equal(context.deviceForm.value.protocol, "NCLinkApi");
    assert.equal(context.deviceForm.value.port, 19001);
    handlers.onDeviceBrandChange("广州数控");
    assert.equal(context.deviceForm.value.protocol, "GskWebServer");
    assert.equal(context.deviceForm.value.port, 11520);
    handlers.onGskSchemeChange("https");
    assert.equal(context.deviceForm.value.port, 443);
    context.deviceForm.value.port = 23456;
    handlers.onGskSchemeChange("http");
    assert.equal(context.deviceForm.value.port, 23456);
    for (const [protocol, port] of [["NCLink", 1883], ["OpcUa", 4840], ["FOCAS", 8193], ["Gskrm", 0]]) {
        handlers.onDeviceProtocolChange(protocol);
        assert.equal(context.deviceForm.value.port, port);
    }
    for (const [protocol, port] of [["FTP", 21], ["SMB", 445], ["NFS", 2049], ["GskrmFileTransfer", 0]]) {
        handlers.onTransferProtocolChange(protocol);
        assert.equal(context.deviceForm.value.transferPort, port);
    }
});

for (const [protocol, brand, original] of [
    ["NCLinkApi", "华中数控", { DeviceId: "SN1", ApiBaseUrl: "http://127.0.0.1:19002", ApiTimeoutMs: "15000", DefaultRequestTimeoutMs: "450" }],
    ["GskWebServer", "广州数控", { DeviceSn: "cnc", BaseUrl: "http://127.0.0.1:11521", RealtimeWebSocketBaseUrl: "ws://127.0.0.1:11522", HealthPath: "/health", AuthToken: "test-token" }],
]) {
    test(`${protocol} edits retain custom ports and invisible driver settings`, async () => {
        const fixture = setup(protocol);
        fixture.handlers.editDevice(fixture.handlers.mapDtoToUi({
            id: "device", name: "CNC", type: "CNC", brand, model: "fixture", protocol,
            host: "127.0.0.1", port: 23456, extendedProperties: { DeviceCode: "CNC-1", ...original },
        }));
        await fixture.handlers.saveDevice();
        assert.equal(fixture.calls[0][0], "update");
        const saved = fixture.calls[0].at(-1);
        assert.equal(saved.port, 23456);
        for (const [key, value] of Object.entries(original)) {
            assert.equal(saved.extendedProperties[key === "AuthToken" ? "WorkshopAuthToken" : key], value);
        }
        if (protocol === "NCLinkApi") {
            fixture.context.deviceForm.value.ncLinkApiBaseUrl = "";
            await fixture.handlers.saveDevice();
            assert.equal(fixture.calls[1].at(-1).extendedProperties.ApiBaseUrl, undefined);
            assert.equal(fixture.calls[1].at(-1).extendedProperties.ApiTimeoutMs, "15000");
        }
        fixture.handlers.onDeviceProtocolChange("OpcUa");
        await fixture.handlers.saveDevice();
        const changed = fixture.calls.at(-1).at(-1).extendedProperties;
        for (const key of Object.keys(original)) assert.equal(changed[key], undefined);
        assert.equal(changed.DeviceCode, "CNC-1");
    });
}

test("GSK SDK acquisition and file channels save without a user-supplied port", async () => {
    const fixture = setup("Gskrm", "GskrmFileTransfer");
    fixture.handlers.openAddDeviceDialog();
    fixture.handlers.onDeviceProtocolChange("Gskrm");
    Object.assign(fixture.context.deviceForm.value, { name: "CNC", code: "GSK-1", transferProtocol: "GskrmFileTransfer", transferHost: "192.0.2.20", transferPort: 0 });
    await fixture.handlers.saveDevice();
    assert.equal(fixture.warnings.length, 0);
    assert.equal(fixture.calls[0].at(-1).port, 0);
    assert.equal(fixture.calls[0].at(-1).transfer.port, 0);
    assert.equal(fixture.calls[0].at(-1).transfer.protocol, "GskrmFileTransfer");
});

test("NCLink VARIABLE@ nodes survive filtering without siblings, descendants or duplicates", () => {
    const { handlers } = setup("NCLinkApi");
    const nodes = ["/VARIABLE@SYS", "/VARIABLE@REG_X", "/VARIABLE@SYS", "/VARIABLE",
        "/VARIABLE2@SYS", "/VARIABLE@SYS/child", "/CHANNEL@0"].map((path) => ({ path }));
    const filtered = handlers.sanitizeAddressSpaceLevelNodes("/VARIABLE", nodes);
    assert.deepEqual(Array.from(filtered, (node) => node.path), ["/VARIABLE@SYS", "/VARIABLE@REG_X"]);
});

for (const mode of ["driver", "tcp", undefined]) {
    test(`Connection feedback distinguishes ${mode ?? "unspecified"} verification`, async () => {
        const fixture = setup("NCLinkApi");
        fixture.connectionResult.mode = mode;
        await fixture.handlers.testConnection("device");
        assert.equal(fixture.successes.length, mode === "driver" ? 1 : 0);
        assert.equal(fixture.warnings.length, mode === "driver" ? 0 : 1);
        assert.match((fixture.successes[0] ?? fixture.warnings[0]), /协议/);
    });
}

for (const items of [[], [{ name: "O0001.nc", path: "O0001.nc", nodeType: "file" },
    { name: "O0002.nc", path: "jobs/O0002.nc", nodeType: "file" }]]) {
    test(`NCLink root stays selectable with ${items.length} files and retains original keys`, async () => {
        const fixture = setup("NCLinkApi", "", items);
        await fixture.handlers.loadRemotePathChildren();
        const root = fixture.context.remotePathTreeData.value[0];
        assert.equal(root.path, "/");
        assert.equal(root._loaded, true);
        if (items.length) {
            assert.equal(root.children.find((node) => node.nodeType === "file").path, "O0001.nc");
            assert.equal(root.children.find((node) => node.path === "jobs").children[0].path, "jobs/O0002.nc");
        }
        fixture.context.transferRemotePath.value = root.path;
        fixture.context.transferRemotePathPickedKind.value = "folder";
        await fixture.handlers.startTransfer();
        assert.equal(fixture.calls.at(-1)[3], "/");
    });
}

for (const [path, kind, count, expected] of [
    ["/jobs/target.nc", "none", 1, "jobs/target.nc"], ["/jobs", "folder", 1, "jobs/"],
    ["jobs/", "none", 1, "jobs/"], ["/jobs", "none", 2, "jobs/"], ["/", "folder", 2, "/"],
]) {
    test(`NCLink upload preserves ${path} as a ${kind} target for ${count} files`, async () => {
        const fixture = setup("NCLinkApi");
        fixture.context.transferRemotePath.value = path;
        fixture.context.transferRemotePathPickedKind.value = kind;
        if (count === 2) fixture.context.transferSelectedFiles.value.push({ name: "O0002.nc" });
        await fixture.handlers.startTransfer();
        const request = fixture.calls[0];
        assert.equal(request[0], count === 2 ? "uploadBatch" : "upload");
        assert.equal(request[count === 2 ? 2 : 3], expected);
    });
}

test("NCLink still rejects an empty upload target", async () => {
    const fixture = setup("NCLinkApi");
    fixture.context.transferRemotePath.value = "   ";
    await fixture.handlers.startTransfer();
    assert.equal(fixture.calls.length, 0);
    assert.equal(fixture.warnings.length, 1);
});

test("NCLink folder selection downloads the exact flat keys as a batch", async () => {
    const fixture = setup("NCLinkApi", "", [{ name: "O0001", path: "jobs/O0001", nodeType: "file" }]);
    fixture.context.transferForm.value.direction = "download";
    fixture.context.transferRemotePathPickedKind.value = "batch";
    fixture.context.transferBatchSelections.value = [{ path: "jobs", nodeType: "folder" }];
    assert.equal(fixture.handlers.remotePathPickerShowCheckboxes.value, true);
    await fixture.handlers.startTransfer();
    assert.equal(fixture.calls.at(-1)[0], "downloadBatch");
    assert.equal(fixture.calls.at(-1)[2][0], "jobs/O0001");
});

test("Editing a transfer channel keeps hidden settings and switching protocols drops them", async () => {
    const fixture = setup("NCLinkApi", "SMB");
    fixture.handlers.editDevice(fixture.handlers.mapDtoToUi({
        id: "device", name: "CNC", type: "CNC", brand: "华中数控", model: "fixture", protocol: "NCLinkApi",
        host: "127.0.0.1", port: 19001, extendedProperties: { DeviceCode: "CNC-1", DeviceId: "SN1", transferDeviceId: "old-file-device" },
        transfer: { protocol: "SMB", host: "192.0.2.10", port: 1445, extendedProperties: { ShareName: "NC", RootPath: "/jobs", Domain: "WORKSHOP" } },
    }));
    fixture.context.deviceForm.value.transferShareName = "PROGRAMS";
    await fixture.handlers.saveDevice();
    const transfer = fixture.calls[0].at(-1).transfer;
    assert.equal(fixture.calls[0].at(-1).extendedProperties.transferDeviceId, "old-file-device");
    assert.equal(fixture.calls[0].at(-1).clearTransfer, false);
    assert.equal(transfer.port, 1445);
    assert.equal(transfer.extendedProperties.ShareName, "PROGRAMS");
    assert.equal(transfer.extendedProperties.RootPath, "/jobs");
    assert.equal(transfer.extendedProperties.Domain, "WORKSHOP");
    fixture.context.deviceForm.value.transferProtocol = "FTP";
    fixture.handlers.onTransferProtocolChange("FTP");
    await fixture.handlers.saveDevice();
    const switched = fixture.calls[1].at(-1).transfer;
    assert.equal(switched.port, 21);
    assert.deepEqual(Object.keys(switched.extendedProperties), []);
    fixture.context.deviceForm.value.transferProtocol = "";
    await fixture.handlers.saveDevice();
    assert.equal(fixture.calls[2].at(-1).transfer, undefined);
    assert.equal(fixture.calls[2].at(-1).clearTransfer, true);
    assert.equal(fixture.calls[2].at(-1).extendedProperties.transferDeviceId, undefined);
    assert.equal(fixture.calls[2].at(-1).extendedProperties.DeviceId, "SN1");
});

for (const [protocol, channel] of [["GskWebServer", ""], ["Gskrm", "GskrmFileTransfer"]]) {
    test(`${protocol} exposes an upload root for its flat program list`, async () => {
        const fixture = setup(protocol, channel, [{ name: "O0001", path: "O0001", nodeType: "file" }]);
        await fixture.handlers.loadRemotePathChildren();
        const root = fixture.context.remotePathTreeData.value[0];
        assert.equal(root.path, "/");
        assert.equal(root.children[0].path, "O0001");
        fixture.context.transferRemotePath.value = root.path;
        fixture.context.transferRemotePathPickedKind.value = "folder";
        await fixture.handlers.startTransfer();
        assert.equal(fixture.calls.at(-1)[0], "upload");
        assert.equal(fixture.calls.at(-1)[3], "/");
    });
}
