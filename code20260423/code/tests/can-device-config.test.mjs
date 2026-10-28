import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";
import { ref } from "vue";
import { compileScript, compileTemplate, parse } from "@vue/compiler-sfc";

const filename = "DeviceView.vue";
const component = readFileSync(new URL("../src/views/industrial/DeviceView.vue", import.meta.url), "utf8");
const { descriptor, errors } = parse(component, { filename });
assert.deepEqual(errors, []);
const compiled = compileScript(descriptor, { id: "can-device" });
assert.deepEqual(compileTemplate({ source: descriptor.template.content, filename, id: "can-device",
    compilerOptions: { bindingMetadata: compiled.bindings } }).errors, []);
const parsed = ts.createSourceFile(filename, descriptor.scriptSetup.content, ts.ScriptTarget.Latest, true);
const names = ["deviceForm", "canBitRates", "inferBrandKey", "mapStatus", "formatSeenAt", "mapDtoToUi",
    "buildExtendedProps", "onDeviceProtocolChange", "openAddDeviceDialog", "editDevice", "saveDevice"];
const selected = parsed.statements.filter((statement) => ts.isFunctionDeclaration(statement)
    ? names.includes(statement.name?.text)
    : ts.isVariableStatement(statement) && statement.declarationList.declarations.some((item) => names.includes(item.name.getText(parsed))));
const executable = ts.transpileModule(selected.map((statement) => statement.getText(parsed)).join("\n")
    + "\nglobalThis.handlers = { " + names.join(",") + " };",
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } }).outputText;

function setup() {
    const writes = [];
    const warnings = [];
    const capture = (method) => async (...args) => {
        writes.push({ method, body: JSON.parse(JSON.stringify(args.at(-1))) });
        return { upstreamSynced: true };
    };
    const sandbox = vm.createContext({ ref, dialogTitle: ref(""), dialogVisible: ref(false),
        treeDefaultsForNewDevice: () => ({}), buildProgramTransferConfig: () => undefined,
        loadDevices: async () => {}, ElMessage: { success() {}, error(error) { throw Error(error); }, warning: (value) => warnings.push(value) },
        machineConnectionDevicesApi: { create: capture("create"), update: capture("update") },
    });
    vm.runInContext(executable, sandbox);
    return { handlers: sandbox.handlers, writes, warnings };
}

const fixture = { id: "can-1", name: "CAN fixture", type: "CNC", brand: "FANUC", model: "Adapter-1",
    protocol: "CAN", host: "COM3", port: 0, status: "Offline", extendedProperties: {
        CanTransport: "SLCAN", PortName: "COM7", BaudRate: "230400", CanBitRate: "250000",
        DeviceCode: "CAN-1", Custom: "preserved",
    } };

test("CAN create uses SLCAN serial settings and port zero", async () => {
    const { handlers, writes } = setup();
    handlers.openAddDeviceDialog();
    Object.assign(handlers.deviceForm.value, { name: "CAN", code: "CAN-1" });
    handlers.onDeviceProtocolChange("CAN");
    await handlers.saveDevice();
    assert.equal(writes.length, 1);
    assert.equal(writes[0].body.host, "COM3");
    assert.equal(writes[0].body.port, 0);
    assert.equal(writes[0].body.extendedProperties.CanTransport, "SLCAN");
    assert.equal(writes[0].body.extendedProperties.BaudRate, "115200");
    assert.equal(writes[0].body.extendedProperties.CanBitRate, "500000");
    assert.equal(writes[0].body.extendedProperties.Handshake, "None");
});

test("CAN edit restores actual serial settings and preserves custom properties", async () => {
    const { handlers, writes } = setup();
    handlers.editDevice(handlers.mapDtoToUi(fixture));
    assert.equal(handlers.deviceForm.value.ip, "COM7");
    assert.equal(handlers.deviceForm.value.baudRate, 230400);
    handlers.deviceForm.value.canBitRate = 1000000;
    await handlers.saveDevice();
    assert.equal(writes[0].method, "update");
    assert.equal(writes[0].body.extendedProperties.PortName, "COM7");
    assert.equal(writes[0].body.extendedProperties.CanBitRate, "1000000");
    assert.equal(writes[0].body.extendedProperties.Custom, "preserved");
    assert.equal(fixture.extendedProperties.CanBitRate, "250000");
});

test("Changing CAN to TCP restores a network host and drops CAN-only properties", async () => {
    const { handlers, writes } = setup();
    handlers.editDevice(handlers.mapDtoToUi(fixture));
    handlers.onDeviceProtocolChange("ModbusTCP");
    await handlers.saveDevice();
    assert.equal(writes[0].body.host, "127.0.0.1");
    assert.equal(writes[0].body.port, 502);
    assert.equal(writes[0].body.extendedProperties.PortName, undefined);
    assert.equal(writes[0].body.extendedProperties.CanBitRate, undefined);
});

test("Invalid CAN rates and empty serial ports fail before saving", async () => {
    const { handlers, writes, warnings } = setup();
    handlers.editDevice(handlers.mapDtoToUi(fixture));
    for (const settings of [{ ip: " " }, { ip: "COM3", baudRate: 0 }, { baudRate: 115200, canBitRate: 333333 }]) {
        Object.assign(handlers.deviceForm.value, settings);
        await handlers.saveDevice();
    }
    assert.equal(writes.length, 0);
    assert.equal(warnings.length, 3);
});
