import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";
import { computed, reactive, ref } from "vue";
import { compileScript, compileTemplate, parse } from "@vue/compiler-sfc";

const filename = "TransferDeviceView.vue";
const component = readFileSync(new URL("../src/views/transfer/DeviceView.vue", import.meta.url), "utf8");
const { descriptor, errors } = parse(component, { filename });
assert.equal(errors.length, 0);
const script = compileScript(descriptor, { id: "serial-transfer" });
assert.deepEqual(compileTemplate({ source: descriptor.template.content, filename, id: "serial-transfer",
    compilerOptions: { bindingMetadata: script.bindings } }).errors, []);
const parsed = ts.createSourceFile(filename, descriptor.scriptSetup.content, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
const executable = ts.transpileModule(parsed.statements.filter((statement) => !ts.isImportDeclaration(statement))
    .map((statement) => statement.getText(parsed)).join("\n")
    + "\nglobalThis.handlers = { currentDevice, mapToDevice, openAddDeviceDialog, openEditDeviceDialog, buildDeviceRequest, saveDevice, testConnection, testResult };",
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } }).outputText;

function setup(sync = true) {
    const writes = [];
    const warnings = [];
    const capture = (method) => async (...args) => {
        writes.push({ method, body: JSON.parse(JSON.stringify(args.at(-1))) });
        return { upstreamSynced: sync, upstreamError: sync ? null : "sync failed", success: true, mode: "driver" };
    };
    const sandbox = vm.createContext({ computed, reactive, ref, onMounted() {},
        ElMessage: { error() {}, success() {}, warning: (message) => warnings.push(message) },
        machineConnectionDevicesApi: { list: async () => [], create: capture("create"), update: capture("update"), testConnection: capture("test") },
        TRANSFER_PROTOCOLS: ["FTP", "SMB", "NFS", "Serial"],
    });
    vm.runInContext(executable, sandbox);
    return { handlers: sandbox.handlers, writes, warnings };
}

const settings = { PortName: "COM7", BaudRate: "19200", DataBits: "7", Parity: "Even", StopBits: "Two" };
const fixture = { id: "serial-1", name: "Serial fixture", host: "localhost", port: 0, model: "SERIAL-1",
    protocol: "Serial", status: "Offline", extendedProperties: { ...settings, Custom: "preserved" } };

test("Serial create persists localhost:0 and serial settings", async () => {
    const { handlers, writes } = setup();
    handlers.openAddDeviceDialog();
    Object.assign(handlers.currentDevice, { name: "Serial", deviceCode: "SERIAL-1", protocol: "Serial", serialSettings: { ...settings } });
    await handlers.saveDevice();
    assert.equal(writes.length, 1);
    assert.equal(writes[0].body.host, "localhost");
    assert.equal(writes[0].body.port, 0);
    for (const [key, value] of Object.entries(settings)) assert.equal(writes[0].body.extendedProperties[key], value);
});

test("Serial edit reads and preserves settings; protocol change drops only serial keys", async () => {
    const { handlers, writes } = setup();
    const mapped = handlers.mapToDevice(fixture);
    handlers.openEditDeviceDialog(mapped);
    handlers.currentDevice.serialSettings.BaudRate = "38400";
    assert.equal(mapped.serialSettings.BaudRate, "19200", "Form edit mutated list entry");
    await handlers.saveDevice();
    assert.equal(writes[0].body.extendedProperties.BaudRate, "38400");
    assert.equal(writes[0].body.extendedProperties.Custom, "preserved");
    Object.assign(handlers.currentDevice, { protocol: "FTP", ip: "192.0.2.10", port: 21 });
    const body = handlers.buildDeviceRequest();
    for (const key of Object.keys(settings)) assert.equal(body.extendedProperties[key], undefined);
    assert.equal(body.extendedProperties.Custom, "preserved");
});

test("Connection test saves edited parameters before driver test", async () => {
    const { handlers, writes } = setup();
    handlers.openEditDeviceDialog(handlers.mapToDevice(fixture));
    handlers.currentDevice.serialSettings.PortName = "COM8";
    await handlers.testConnection();
    assert.deepEqual(writes.map((entry) => entry.method), ["update", "test"]);
    assert.equal(writes[0].body.extendedProperties.PortName, "COM8");
    assert.match(handlers.testResult.value.message, /COM8.*已打开/);
});

test("Connection test stops when updated configuration failed upstream sync", async () => {
    const { handlers, writes } = setup(false);
    handlers.openEditDeviceDialog(handlers.mapToDevice(fixture));
    await handlers.testConnection();
    assert.deepEqual(writes.map((entry) => entry.method), ["update"]);
    assert.equal(handlers.testResult.value.success, false);
});

test("Serial save rejects empty PortName and fills driver defaults", async () => {
    const { handlers, writes, warnings } = setup();
    handlers.openEditDeviceDialog(handlers.mapToDevice(fixture));
    handlers.currentDevice.serialSettings = { PortName: " " };
    await handlers.saveDevice();
    assert.equal(writes.length, 0);
    assert.equal(warnings.length, 1);
    handlers.currentDevice.serialSettings.PortName = "COM3";
    await handlers.saveDevice();
    assert.equal(writes[0].body.extendedProperties.BaudRate, "9600");
    assert.equal(writes[0].body.extendedProperties.StopBits, "One");
});

test("Transfer list recognizes the exact Serial enum name", () => {
    const source = readFileSync(new URL("../src/views/transfer/transferRecordMetrics.ts", import.meta.url), "utf8");
    const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
    const sandbox = vm.createContext({ exports: {} });
    vm.runInContext(code, sandbox);
    assert.ok(sandbox.exports.TRANSFER_PROTOCOLS.includes("Serial"));
});

test("Transfer history lists both primary and independent Serial channels", async () => {
    const metrics = vm.createContext({ exports: {} });
    vm.runInContext(ts.transpileModule(
        readFileSync(new URL("../src/views/transfer/transferRecordMetrics.ts", import.meta.url), "utf8"),
        { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText, metrics);
    const pageSource = readFileSync(new URL("../src/views/transfer/TransferRecordView.vue", import.meta.url), "utf8");
    const { descriptor: pageDescriptor } = parse(pageSource);
    const pageScript = ts.createSourceFile("Records.ts", pageDescriptor.scriptSetup.content, ts.ScriptTarget.Latest, true);
    const selectedNames = ["transferProtocols", "transferProtocol", "loadDevices"];
    const selected = pageScript.statements.filter(statement => selectedNames.includes(
        ts.isFunctionDeclaration(statement) ? statement.name?.text :
            ts.isVariableStatement(statement) ? statement.declarationList.declarations[0]?.name.getText(pageScript) : ""));
    const sandbox = vm.createContext({ devices: ref([]), selectedDeviceId: ref(""),
        TRANSFER_PROTOCOLS: metrics.exports.TRANSFER_PROTOCOLS,
        ElMessage: { error(error) { throw new Error(error); } },
        machineConnectionDevicesApi: { list: async () => [
            { id: "primary", protocol: "Serial" }, { id: "nested", protocol: "HaasMdc", transfer: { protocol: "Serial" } },
            { id: "read-only", protocol: "MTConnect" },
        ] },
    });
    vm.runInContext(ts.transpileModule(selected.map(statement => statement.getText(pageScript)).join("\n")
        + "\nglobalThis.load = loadDevices;", { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText, sandbox);
    await sandbox.load();
    assert.deepEqual(Array.from(sandbox.devices.value, device => device.id), ["primary", "nested"]);
});
