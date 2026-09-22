import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";

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
    const device = { id: "device", protocol, transferProtocol };
    const record = (method, result) => async (...args) => { calls.push([method, ...args]); return result; };
    const context = vm.createContext({
        devices: { value: [device] }, transferForm: { value: { deviceId: device.id, direction: "upload" } },
        transferRemotePath: { value: "/NC" }, transferRemotePathPickedKind: { value: "none" },
        transferBatchSelections: { value: [] }, transferSelectedFiles: { value: [{ name: "O0001.nc" }] },
        transferSubmitting: { value: false }, remotePathTreeData: { value: [] }, REMOTE_TREE_MAX_NODES: 2000,
        computed: (getter) => ({ get value() { return getter(); } }),
        syncRemotePathPickerCurrentNode() {}, notifyBatchDownloadResult() {}, async loadTransferHistory() {},
        getApiErrorMessage: (error) => error.message,
        ElMessage: { warning: (message) => warnings.push(message), error: (message) => { throw new Error(message); }, info() {}, success() {} },
        machineConnectionPointsApi: { browseAddressSpace: () => { throw new Error("File picker used collection address space"); } },
        machineConnectionProgramTransferApi: {
            files: record("files", items), upload: record("upload", { status: "Completed" }),
            uploadBatchWait: record("uploadBatch", { failedFiles: 0, totalFiles: 2 }),
            download: record("download"), downloadBatchZip: record("downloadBatch", { failedFiles: 0 }),
        },
    });
    vm.runInContext(executable, context);
    return { context, handlers: context.handlers, calls, warnings, device };
}

for (const protocol of ["OpcUa", "FOCAS"]) {
    for (const transferProtocol of ["FTP", "SMB"]) {
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

test("Empty FTP root remains selectable and a subdirectory contains only its own children", async () => {
    const empty = setup("OpcUa", "FTP");
    await empty.handlers.loadRemotePathChildren();
    assert.equal(empty.context.remotePathTreeData.value[0].path, "/");
    const fixture = setup("FOCAS", "FTP", [{ name: "O0001.nc", path: "/NC/O0001.nc", nodeType: "file" }]);
    const parent = { path: "/NC", nodeType: "folder" };
    await fixture.handlers.loadRemotePathChildren(parent);
    assert.equal(parent.children.length, 1);
    assert.equal(parent.children[0].path, "/NC/O0001.nc");
});

test("FTP rejects OPC UA NodeIds before making a transfer request", async () => {
    const fixture = setup("OpcUa", "FTP");
    for (const path of ["ns=2;s=Sinumerik", "i=2253", "nsu=urn:controller;s=Program"]) {
        fixture.context.transferRemotePath.value = path;
        await fixture.handlers.startTransfer();
        assert.equal(fixture.handlers.isDownloadableProgramPathForProtocol(path, fixture.device), false);
    }
    assert.equal(fixture.calls.length, 0);
    assert.equal(fixture.warnings.length, 3);
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
