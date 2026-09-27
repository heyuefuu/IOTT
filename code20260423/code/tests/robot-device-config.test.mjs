import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";
import { computed, effectScope, reactive, ref, watch } from "vue";
import { compileScript, compileTemplate, parse } from "@vue/compiler-sfc";

const filename = "RobotDeviceConfigView.vue";
const component = readFileSync(new URL("../src/views/robot/DeviceConfigView.vue", import.meta.url), "utf8");
const { descriptor, errors } = parse(component, { filename });
assert.equal(errors.length, 0);
const compiled = compileScript(descriptor, { id: "robot-config" });
const template = compileTemplate({ source: descriptor.template.content, filename, id: "robot-config",
    compilerOptions: { bindingMetadata: compiled.bindings, expressionPlugins: ["typescript"] } });
assert.deepEqual(template.errors, []);
const parsed = ts.createSourceFile(filename, descriptor.scriptSetup.content, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
const executable = ts.transpileModule(parsed.statements.filter(statement => !ts.isImportDeclaration(statement))
    .map(statement => statement.getText(parsed)).join("\n")
    + "\nglobalThis.handlers = { mapToRobot, currentDevice, openEditDeviceDialog, saveDevice, importRobotTags, importingDeviceId };",
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } }).outputText;

function setup(context, importResponse = { successCount: 1, errorCount: 0, errors: [] }) {
    const writes = [], imports = [], warnings = [], failures = [];
    let loads = 0;
    const scope = effectScope();
    const sandbox = vm.createContext({ computed, reactive, ref, watch, onMounted() {},
        useRouter: () => ({ push() {} }),
        ElMessage: { success() {}, warning: message => warnings.push(message), error: message => failures.push(message) },
        ElMessageBox: {},
        machineConnectionDevicesApi: { list: async () => { loads++; return []; },
            update: async (id, body) => { writes.push({ id, body }); return {}; } },
        machineConnectionCollectionApi: { importTags: async (id, file) => { imports.push({ id, file }); return importResponse; } },
    });
    scope.run(() => vm.runInContext(executable, sandbox));
    context.after(() => scope.stop());
    const handlers = sandbox.handlers;
    const device = handlers.mapToRobot({ id: "robot-7", name: "Robot", model: "HSR", brand: "HSR",
        protocol: "HuazhongRobot", host: "127.0.0.1", port: 502, status: "Offline",
        extendedProperties: { Station: "7", DataFormat: "ABCD", deviceCode: "R7", vendorSetting: "keep" } });
    handlers.openEditDeviceDialog(device);
    return { handlers, device, writes, imports, warnings, failures, loads: () => loads };
}

test("Robot configuration saves station, lengths and mapping while preserving device properties", async context => {
    const state = setup(context);
    const map = JSON.stringify([{ Path: "/Robot/Ready", DisplayName: "Ready", ModbusAddress: "240",
        DataType: "UInt16", IsWritable: true }]);
    Object.assign(state.handlers.currentDevice, { addressMap: map, stringLength: 15, byteArrayLength: 4 });
    await state.handlers.saveDevice();
    assert.equal(state.writes.length, 1);
    assert.equal(state.writes[0].id, "robot-7");
    assert.deepEqual(JSON.parse(JSON.stringify(state.writes[0].body.extendedProperties)), {
        Station: "7", DataFormat: "ABCD", deviceCode: "R7", vendorSetting: "keep", description: "",
        PingAddress: "", StringLength: "15", ByteArrayLength: "4", AddressMap: map,
    });
});

test("Invalid robot station, byte length and mapping fail before a device update", async context => {
    const state = setup(context);
    for (const change of [{ station: 0 }, { byteArrayLength: 3 }, { addressMap: "{}" },
        { addressMap: JSON.stringify([{ Path: "/Robot/Ready", DisplayName: "Ready", ModbusAddress: "240",
            DataType: "UInt8", IsWritable: false }]) }]) {
        state.handlers.openEditDeviceDialog(state.device);
        Object.assign(state.handlers.currentDevice, change);
        await state.handlers.saveDevice();
    }
    assert.equal(state.writes.length, 0);
    assert.equal(state.warnings.length, 4);
});

test("Robot tag upload retains selected device and refreshes saved mapping", async context => {
    const state = setup(context);
    const raw = { name: "points.csv" };
    await state.handlers.importRobotTags(state.device, { name: raw.name, raw });
    assert.equal(state.imports.length, 1);
    assert.equal(state.imports[0].id, "robot-7");
    assert.equal(state.imports[0].file, raw);
    assert.equal(state.loads(), 1);
    assert.equal(state.handlers.importingDeviceId.value, "");
});

test("Rejected robot import shows server validation without claiming a successful save", async context => {
    const state = setup(context, { successCount: 0, errorCount: 1, errors: ["ModbusAddress is required"] });
    await state.handlers.importRobotTags(state.device, { name: "points.json", raw: {} });
    assert.deepEqual(state.failures, ["ModbusAddress is required"]);
    assert.equal(state.loads(), 0);
    assert.equal(state.handlers.importingDeviceId.value, "");
});

test("Editing a mapped Float heartbeat preserves automatic and explicit types", async context => {
    const state = setup(context);
    const properties = { ...state.device.extendedProperties, PingAddress: "/Robot/Speed",
        AddressMap: JSON.stringify([{ Path: "/Robot/Speed", DisplayName: "Speed", ModbusAddress: "240",
            DataType: "Float", IsWritable: false }]) };
    for (const explicit of [undefined, "Float"]) {
        const device = state.handlers.mapToRobot({ id: "robot-7", name: "Robot", model: "HSR", brand: "HSR",
            protocol: "HuazhongRobot", host: "127.0.0.1", port: 502, status: "Offline",
            extendedProperties: { ...properties, ...(explicit ? { PingDataType: explicit } : {}) } });
        assert.equal(device.pingDataType, explicit ?? "");
        state.handlers.openEditDeviceDialog(device);
        await state.handlers.saveDevice();
        assert.equal(state.writes.at(-1).body.extendedProperties.PingDataType, explicit);
    }
    state.handlers.currentDevice.pingDataType = "";
    await state.handlers.saveDevice();
    assert.equal(Object.hasOwn(state.writes.at(-1).body.extendedProperties, "PingDataType"), false);
    assert.equal(state.warnings.length, 0);
});

test("ModbusTCP loads UnitId first and saves both station aliases", async context => {
    const state = setup(context);
    const device = state.handlers.mapToRobot({ id: "modbus-9", name: "Robot", model: "Robot",
        brand: "Robot", protocol: "ModbusTCP", host: "127.0.0.1", port: 502, status: "Offline",
        extendedProperties: { UnitId: "9", Station: "3", vendorSetting: "keep" } });
    assert.equal(device.station, 9);
    state.handlers.openEditDeviceDialog(device);
    state.handlers.currentDevice.station = 7;
    await state.handlers.saveDevice();
    const properties = state.writes[0].body.extendedProperties;
    assert.equal(properties.UnitId, "7");
    assert.equal(properties.Station, "7");
    assert.equal(properties.vendorSetting, "keep");
});
