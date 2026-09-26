import assert from "node:assert/strict";
import { Blob, File } from "node:buffer";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";
import { reactive, ref, watch } from "vue";
import { compileScript, compileTemplate, parse } from "@vue/compiler-sfc";

const source = readFileSync(new URL("../src/utils/tiaSymbolTable.ts", import.meta.url), "utf8");
const executable = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext },
}).outputText;
const { parseTiaSymbolTableCsv, readImportFileText, toCollectionImportCsvFile } =
    await import(`data:text/javascript;base64,${Buffer.from(executable).toString("base64")}`);
const gbkSample = Buffer.from("w/uzxizK/b7dwODQzSy12Na3CrLiytQsQnl0ZSwlTUIxMA==", "base64");

test("TIA scalar types preserve signedness and storage width", () => {
    const cases = [
        ["Bool", "Bool"], ["Byte", "UInt8"], ["SInt", "Int8"], ["USInt", "UInt8"],
        ["Char", "UInt8"], ["WChar", "UInt16"], ["Date", "UInt16"], ["Word", "UInt16"],
        ["Int", "Int16"], ["DInt", "Int32"], ["UDInt", "UInt32"], ["DWord", "UInt32"],
        ["Time", "Int32"], ["Time_Of_Day", "UInt32"], ["TOD", "UInt32"],
        ["LInt", "Int64"], ["ULInt", "UInt64"], ["LWord", "UInt64"],
        ["Real", "Float"], ["LReal", "Double"],
    ];
    for (const [type, expected] of cases) {
        const [row] = parseTiaSymbolTableCsv(`Name,Data Type,Address\nTag,${type},%MB10`);
        assert.equal(row.dataType, expected, type);
        assert.equal(row.address, "MB10");
    }
});

test("TIA export retains quoted metadata and normalized bit addresses", async () => {
    const [row] = parseTiaSymbolTableCsv('\uFEFFName;Data Type;Address;Group;Unit\n"Valve, A";Bool;%I0.0;Inputs;state', {
        defaultIntervalMs: 5000,
    });
    const output = toCollectionImportCsvFile([row], "collection.csv");
    assert.equal(await output.text(), 'Address,DataType,GroupName,IntervalMs,DisplayName,Unit\nI0.0,Bool,Inputs,5000,"Valve, A",state');
});

test("Unsupported TIA types fail with the tag name instead of importing String", () => {
    for (const type of ["UnknownType", "Array[0..10] of Byte", "WString[20]"]) {
        assert.throws(() => parseTiaSymbolTableCsv(`Name,Data Type,Address\nBroken,${type},%MB10`), /Broken \(MB10\)/);
    }
});

test("Import decoding accepts UTF-8, GBK and UTF-16 BOMs", async () => {
    const csv = "名称,数据类型,地址\n测试,Byte,%MB10";
    for (const bytes of [Buffer.from(csv), gbkSample, Buffer.from("\uFEFF" + csv, "utf16le"),
        Buffer.from("\uFEFF" + csv, "utf16le").swap16()]) {
        const [row] = parseTiaSymbolTableCsv(await readImportFileText(new Blob([bytes])));
        assert.equal(row.displayName, "测试");
        assert.equal(row.address, "MB10");
        assert.equal(row.dataType, "UInt8");
    }
    await assert.rejects(readImportFileText(new Blob([new Uint8Array([0xff])])), /编码/);
});

const component = readFileSync(new URL("../src/views/plc/CollectionImportView.vue", import.meta.url), "utf8");
const script = component.match(/<script[^>]*setup[^>]*>([\s\S]*?)<\/script>/)?.[1];
assert.ok(script);
const names = ["fileConfig", "manualConfig", "uploadedFile", "clearUploadedFile", "handleFileChange",
    "handleFileRemove", "parseTiaFile", "getImportFile", "importMethod", "devices", "devicesLoading",
    "getImportError", "loadDevices", "normalizeDataType", "importManualConfig", "importConfig", "openCollectionManage"];
const parsed = ts.createSourceFile("CollectionImportView.ts", script, ts.ScriptTarget.Latest, true);
const statements = parsed.statements.filter(statement => ts.isVariableStatement(statement)
    && names.includes(statement.declarationList.declarations[0]?.name.getText(parsed)));
assert.equal(statements.length, names.length);
const handlersSource = ts.transpileModule(statements.map(statement => statement.getText(parsed)).join("\n")
    + `\nglobalThis.handlers = { ${names.join(",")} };`, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
}).outputText;

const registeredDevice = { id: "plc-line-77", name: "77", type: "PLC", protocol: "SiemensS7",
    host: "127.0.0.1", port: 108 };

function setup(options = {}) {
    const messages = [];
    const warnings = [];
    const successes = [];
    const requests = [];
    const deviceRequests = [];
    const confirmations = [];
    const routes = [];
    let clearCount = 0;
    const context = vm.createContext({
        reactive, ref, watch, File, parseTiaSymbolTableCsv, readImportFileText, toCollectionImportCsvFile,
        router: { push(route) { routes.push(route); } },
        fileType: ref("tia"), uploadRef: ref({ clearFiles() { clearCount++; } }),
        ElMessage: { error(message) { messages.push(message); }, warning(message) { warnings.push(message); },
            success(message) { successes.push(message); } },
        ElMessageBox: { confirm(...args) {
            confirmations.push(args);
            return options.confirm ? options.confirm() : Promise.resolve();
        } },
        machineConnectionDevicesApi: { async list(type) {
            deviceRequests.push(type);
            return options.list ? options.list(type) : [registeredDevice];
        } },
        machineConnectionCollectionApi: {
            async createProfile(deviceId, body) {
                requests.push({ method: "create", deviceId, body });
                return options.create ? options.create(deviceId, body) : { id: "profile-1", deviceId, ...body };
            },
            async importTags(deviceId, file) {
                requests.push({ method: "import", deviceId, file });
                return options.import ? options.import(deviceId, file) : { successCount: 1, errorCount: 0 };
            },
        },
    });
    vm.runInContext(handlersSource, context);
    return { ...context.handlers, context, messages, warnings, successes, requests, deviceRequests,
        confirmations, routes, get clearCount() { return clearCount; } };
}

test("TIA frequency and group are independent of the manual form", async () => {
    const fixture = setup();
    Object.assign(fixture.fileConfig, { frequency: 5000, groupName: "FileGroup" });
    Object.assign(fixture.manualConfig, { frequency: 25, name: "ManualGroup" });
    const file = new File(["Name,Data Type,Address\nTag,Bool,%I0.0"], "tags.csv");
    const [row] = await fixture.parseTiaFile(file);
    assert.equal(row.intervalMs, 5000);
    assert.equal(row.groupName, "FileGroup");
    assert.equal(fixture.manualConfig.frequency, 25);
    fixture.fileConfig.frequency = 0;
    await assert.rejects(fixture.parseTiaFile(file), /100.*60000/);
});

test("Invalid replacement and removing the last file clear the selected upload", () => {
    const fixture = setup();
    const valid = { raw: new File(["data"], "a.csv") };
    fixture.handleFileChange(valid);
    fixture.handleFileChange({ raw: new File(["invalid"], "b.txt") });
    assert.equal(fixture.uploadedFile.value, null);
    assert.equal(fixture.clearCount, 1);
    fixture.handleFileChange(valid);
    fixture.handleFileRemove(valid, []);
    assert.equal(fixture.uploadedFile.value, null);
});

test("Standard GBK CSV is converted to UTF-8 before upload", async () => {
    const fixture = setup();
    fixture.context.fileType.value = "standard";
    const file = await fixture.getImportFile(new File([gbkSample], "tags.csv"));
    assert.equal(await file.text(), "名称,数据类型,地址\n测试,Byte,%MB10");
});

test("Both import forms select registered device IDs and compile with canonical data types", () => {
    const filename = "CollectionImportView.vue";
    const { descriptor, errors } = parse(component, { filename });
    assert.deepEqual(errors, []);
    const compiled = compileScript(descriptor, { id: "plc-collection-import" });
    const template = compileTemplate({ source: descriptor.template.content, filename,
        id: "plc-collection-import", compilerOptions: { bindingMetadata: compiled.bindings } });
    assert.deepEqual(template.errors, []);
    const selectors = component.match(/<el-select\b[\s\S]*?<\/el-select>/g);
    for (const field of ["fileConfig.deviceId", "manualConfig.deviceId"]) {
        const selector = selectors.find(element => element.includes(`v-model="${field}"`));
        assert.ok(selector, `${field} must use a device selector`);
        assert.match(selector, /:value="device\.id"/);
        assert.doesNotMatch(selector, /allow-create/);
    }
    assert.match(selectors.find(element => element.includes('v-model="manualConfig.dataType"')), /value="Int32"/);
});

test("Device options load only PLCs and recover after a failed request", async () => {
    let attempts = 0;
    const fixture = setup({ list() {
        if (++attempts === 1) throw { response: { data: { error: "设备服务暂时不可用" } } };
        return [registeredDevice];
    } });
    await fixture.loadDevices();
    assert.deepEqual(fixture.messages, ["设备服务暂时不可用"]);
    assert.equal(fixture.devicesLoading.value, false);
    await fixture.loadDevices();
    assert.deepEqual(fixture.deviceRequests, ["PLC", "PLC"]);
    assert.equal(fixture.devices.value[0].id, registeredDevice.id);
    assert.equal(fixture.manualConfig.deviceId, "");
    assert.equal(fixture.fileConfig.deviceId, "");
});

function fillManual(fixture, deviceId = registeredDevice.id) {
    fixture.importMethod.value = "manual";
    Object.assign(fixture.manualConfig, { name: "111", deviceId, address: "I5", dataType: "Int32", frequency: 1000 });
}

for (const method of ["manual", "file"]) {
    test(`${method} import rejects device names and unknown IDs before confirmation`, async () => {
        const fixture = setup();
        await fixture.loadDevices();
        fillManual(fixture);
        fixture.importMethod.value = method;
        fixture.uploadedFile.value = new File(["Name,Data Type,Address\nInput,DInt,%I5"], "tags.csv");
        for (const deviceId of ["222", registeredDevice.name, "missing-device"]) {
            fixture.manualConfig.deviceId = deviceId;
            fixture.fileConfig.deviceId = deviceId;
            await fixture.importConfig();
        }
        assert.equal(fixture.requests.length, 0);
        assert.equal(fixture.confirmations.length, 0);
        assert.equal(fixture.warnings.length, 3);
        assert.ok(fixture.warnings.every(message => message.includes("已注册的 PLC 设备")));
    });
}

test("Manual import submits the selected device ID and the requested I5 Int32 profile", async () => {
    const fixture = setup();
    await fixture.loadDevices();
    fillManual(fixture);
    await fixture.importConfig();
    assert.deepEqual(JSON.parse(JSON.stringify(fixture.requests)), [{
        method: "create", deviceId: registeredDevice.id,
        body: { name: "111", groups: [{ groupName: "111", intervalMs: 1000,
            tags: [{ address: "I5", dataType: "Int32", displayName: "111" }] }] },
    }]);
    assert.deepEqual(fixture.successes, ["配置导入成功"]);
    assert.equal(fixture.manualConfig.deviceId, registeredDevice.id);
});

test("Collection navigation uses the active import mode and retains its device after saving", async () => {
    const fixture = setup();
    await fixture.loadDevices();
    fillManual(fixture);
    fixture.fileConfig.deviceId = "another-device";
    fixture.openCollectionManage();
    assert.equal(fixture.routes[0].query.deviceId, registeredDevice.id);
    await fixture.importConfig();
    fixture.openCollectionManage();
    assert.equal(fixture.routes[1].query.deviceId, registeredDevice.id);
    fixture.importMethod.value = "file";
    fixture.openCollectionManage();
    assert.equal(fixture.routes[2].query.deviceId, "another-device");
});

test("Manual collection accepts slow sampling and rejects invalid periods", async () => {
    const fixture = setup();
    await fixture.loadDevices();
    fillManual(fixture);
    for (const period of [0, -1, 1.5, 60001, NaN]) {
        fixture.manualConfig.frequency = period;
        await fixture.importConfig();
    }
    assert.equal(fixture.requests.length, 0);
    fixture.manualConfig.frequency = 5000;
    await fixture.importConfig();
    assert.equal(fixture.requests[0].body.groups[0].intervalMs, 5000);
});

for (const method of ["manual", "file"]) {
    test(`${method} import shows the backend missing-device error and preserves the form`, async () => {
        const detail = "设备已删除，请重新选择目标设备";
        const fail = () => { throw { message: "Request failed with status code 404",
            response: { status: 404, data: method === "manual" ? { error: detail } : detail } }; };
        const fixture = setup({ create: fail, import: fail });
        await fixture.loadDevices();
        fillManual(fixture);
        fixture.importMethod.value = method;
        fixture.fileConfig.deviceId = registeredDevice.id;
        fixture.context.fileType.value = "standard";
        fixture.uploadedFile.value = new File(["Address,DataType,GroupName,IntervalMs\nI5,Int32,Input,1000"], "tags.csv");
        await fixture.importConfig();
        assert.equal(fixture.requests.length, 1);
        assert.deepEqual(fixture.messages, [detail]);
        assert.equal(fixture.successes.length, 0);
        assert.equal(fixture.manualConfig.address, "I5");
        assert.ok(fixture.uploadedFile.value);
    });
}

test("Cancelling import sends no create request", async () => {
    const fixture = setup({ confirm: () => Promise.reject("cancel") });
    await fixture.loadDevices();
    fillManual(fixture);
    await fixture.importConfig();
    assert.equal(fixture.requests.length, 0);
    assert.equal(fixture.messages.length, 0);
});

test("Opening a device dropdown while options load does not duplicate the request", async () => {
    let resolveDevices;
    const fixture = setup({ list: () => new Promise(resolve => { resolveDevices = resolve; }) });
    const loading = fixture.loadDevices();
    await fixture.loadDevices();
    assert.equal(fixture.deviceRequests.length, 1);
    assert.equal(fixture.devicesLoading.value, true);
    resolveDevices([registeredDevice]);
    await loading;
    assert.equal(fixture.devicesLoading.value, false);
});
