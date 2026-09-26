import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";
import { computed, nextTick, reactive, ref, watch } from "vue";
import TreeStore from "element-plus/es/components/tree/src/model/tree-store.mjs";

const component = readFileSync(new URL("../src/views/plc/AddressBrowserView.vue", import.meta.url), "utf8");
const probeExports = {};
vm.runInNewContext(ts.transpileModule(
    readFileSync(new URL("../src/utils/plcProbe.ts", import.meta.url), "utf8"),
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } },
).outputText, { exports: probeExports });
const { getProbeProfile, createProbeTags } = probeExports;
const script = component.match(/<script[^>]*setup[^>]*>([\s\S]*?)<\/script>/)?.[1];
assert.ok(script, "AddressBrowserView script setup exists");
const names = ["mapNode", "getErr", "handleDeviceChange", "loadAddressSpace", "browseNotice", "browseNoticeType",
    "loadAddressChildren", "searchAddress", "form", "searchForm", "devices", "addressSpace",
    "addressSpaceVersion", "expandedKeys", "selectedAddress", "selectedAddresses", "searchResults",
    "loading", "addressTreeProps", "readExactAddress", "clearExactAddressRead", "directRead",
    "directReadDataTypes", "resetSearch", "selectedDevice", "selectedProtocol", "probeProfile", "probeArea",
    "probe", "probeController", "stopProbe", "startProbe", "probeSelection", "probeCollection",
    "probeSaveVersion", "handleProbeSelection", "saveProbeCollection", "addSelectedToCollection"];
const parsed = ts.createSourceFile("AddressBrowserView.ts", script, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
const statements = parsed.statements.filter((statement) => {
    if (ts.isExpressionStatement(statement) && ts.isCallExpression(statement.expression)
        && statement.expression.expression.getText(parsed) === "watch") return true;
    const name = ts.isFunctionDeclaration(statement) ? statement.name?.text
        : ts.isVariableStatement(statement) ? statement.declarationList.declarations[0]?.name.getText(parsed) : null;
    return names.includes(name);
});
assert.equal(statements.length, names.length + 1, "Tests execute the component's actual state, handlers and watcher");
const executable = ts.transpileModule(
    statements.map((statement) => statement.getText(parsed)).join("\n")
        + `\nglobalThis.fixture = { ${names.join(",")} };`,
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } },
).outputText;

function nodes(path = "I", nodeType = "Folder") {
    return [{ path, displayName: path, nodeType, dataType: "Bool" }];
}

function readResult(address = "I5", dataType = "Int32", value = 10, quality = "Good", errorMessage = null) {
    return { deviceId: "A", tags: [{ address, dataType, value, quality, errorMessage,
        timestamp: "2026-09-25T00:00:00Z" }] };
}

function deferred() {
    let resolve;
    let reject;
    const promise = new Promise((complete, fail) => { resolve = complete; reject = fail; });
    return { promise, resolve, reject };
}

async function flush() {
    await Promise.resolve();
    await nextTick();
}

function setup(browse = async (_deviceId, parentPath) => parentPath ? nodes("I5", "Variable") : nodes(), read = async () => ({ tags: [] }), create = async () => ({})) {
    const calls = [];
    const readCalls = [];
    const errors = [];
    const infos = [];
    const warnings = [];
    const successes = [];
    const creates = [];
    const routes = [];
    const context = vm.createContext({
        ref, reactive, watch, computed, AbortController, setTimeout, createProbeTags, getProbeProfile,
        machineConnectionPointsApi: {
            browseAddressSpace(...args) { calls.push(args); return browse(...args); },
            readTags(...args) { readCalls.push(args); return read(...args); },
        },
        machineConnectionCollectionApi: { createProfile(...args) { creates.push(args); return create(...args); } },
        router: { async push(route) { routes.push(route); } },
        ElMessage: { error: (message) => errors.push(message), info: (message) => infos.push(message),
            warning: (message) => warnings.push(message), success: (message) => successes.push(message) },
    });
    vm.runInContext(executable, context);
    const state = context.fixture;
    state.form.deviceId = "A";
    state.devices.value = [{ id: "A", protocol: "OpcUa" }, { id: "B", protocol: "OpcUa" }];
    return { ...state, calls, readCalls, errors, infos, warnings, successes, creates, routes, mount() {
        const store = reactive(new TreeStore({
            data: state.addressSpace.value, key: "address", props: state.addressTreeProps,
            lazy: true, load: state.loadAddressChildren, defaultCheckedKeys: [], defaultExpandedKeys: [],
        }));
        store.initialize();
        const stop = watch(() => state.addressSpace.value, (value) => store.setData(value), { deep: true });
        return { store, stop };
    } };
}

test("Probe addresses follow protocol numbering, bit boundaries and explicit DB selection", () => {
    const cases = [
        ["ModbusTCP", {}, "HR", 0, 1, ["HR0", "HR1"], "UInt16"],
        ["ModbusRTU", {}, "DI", 0, 1, ["DI0", "DI1"], "Bool"],
        ["Profibus", {}, "IR", 0, 1, ["IR0", "IR1"], "UInt16"],
        ["FINS", {}, "DM", 15, 16, ["DM15", "DM16"], "UInt16"],
        ["OmronHostLink", {}, "CIO", 15, 16, ["CIO15", "CIO16"], "UInt16"],
        ["Mewtocol", {}, "X", 15, 16, ["X0.F", "X1.0"], "Bool"],
        ["MewtocolSerial", {}, "DT", 9, 10, ["DT9", "DT10"], "UInt16"],
        ["SiemensS7", {}, "DB", 9, 10, ["DB12.DBB9", "DB12.DBB10"], "UInt8"],
        ["SiemensS7", {}, "AIW", 0, 1, ["AIW0", "AIW2"], "UInt16"],
        ["Inovance", { Series: "H3U" }, "X", 7, 8, ["X7", "X10"], "Bool"],
        ["InovanceSerial", { series: "H5U" }, "Y", 63, 64, ["Y77", "Y100"], "Bool"],
        ["InovanceSerialOverTcp", { Series: "AM600" }, "MX", 7, 8, ["MX0.7", "MX1.0"], "Bool"],
    ];
    for (const [protocol, properties, id, start, end, addresses, type] of cases) {
        const area = getProbeProfile(protocol, properties).areas.find((item) => item.id === id);
        const tags = createProbeTags(area, start, end, 12);
        assert.deepEqual(Array.from(tags, (tag) => tag.address), addresses, protocol);
        assert.ok(tags.every((tag) => tag.dataType === type), protocol);
    }
    assert.equal(getProbeProfile("OpcUa"), undefined);
    assert.equal(getProbeProfile("Inovance", { Series: "unknown" }).areas.length, 0);
    assert.equal(getProbeProfile("Inovance", {}).areas.length, 0);
});

test("Probe input bounds reject invalid ranges and DB numbers before any read", () => {
    const area = getProbeProfile("SiemensS7").areas.find((item) => item.id === "DB");
    for (const args of [[-1, 1, 1], [0, 64, 1], [2, 1, 1], [0.5, 1, 1], [65535, 65536, 1], [0, 1, 0], [0, 1, 1.5]]) {
        assert.throws(() => createProbeTags(area, ...args));
    }
    assert.equal(createProbeTags(area, 0, 63, 65535).length, 64);
});

test("Probe retains only successful real reads and reports missing or bad replies", async () => {
    const fixture = setup(undefined, async (_device, { tags }) => ({ tags: tags[0].address === "DM1" ? [] : [
        { ...tags[0], quality: tags[0].address === "DM2" ? "Bad" : "Good", value: 0, errorMessage: "rejected" },
    ] }));
    fixture.devices.value[0].protocol = "FINS";
    fixture.handleDeviceChange();
    fixture.probe.end = 2;
    await fixture.startProbe();
    assert.equal(fixture.probe.checked, 3);
    assert.equal(fixture.probe.failed, 2);
    assert.deepEqual(Array.from(fixture.probe.results, (tag) => tag.address), ["DM0"]);
    assert.equal(fixture.probe.results[0].value, "0");
    assert.match(fixture.probe.lastReadError, /DM2.*rejected/);
    assert.equal(fixture.addressSpace.value.length, 0);
    assert.ok(fixture.readCalls.every((call) => call[1].tags.length === 1 && call[2] instanceof AbortSignal));
});

test("Changing devices cancels the probe and discards a late reply", async () => {
    const waiting = deferred();
    const fixture = setup(undefined, () => waiting.promise);
    fixture.devices.value[0].protocol = "SiemensS7";
    fixture.handleDeviceChange();
    fixture.probe.end = 1;
    const run = fixture.startProbe();
    await fixture.startProbe();
    assert.equal(fixture.readCalls.length, 1);
    fixture.form.deviceId = "B";
    fixture.handleDeviceChange();
    waiting.resolve(readResult("IB0", "UInt8", 7));
    await run;
    assert.equal(fixture.probe.running, false);
    assert.equal(fixture.probe.results.length, 0);
    assert.equal(fixture.probe.checked, 0);
    assert.equal(fixture.readCalls[0][2].aborted, true);
});

test("Selected probe points save with per-point types, frequency groups and the correct device", async () => {
    const fixture = setup();
    fixture.probe.results = [
        { address: "HR0", dataType: "UInt16", collectionDataType: "Float", displayName: "温度", intervalMs: 1000 },
        { address: "HR2", dataType: "UInt16", collectionDataType: "UInt16", displayName: "", intervalMs: 5000 },
        { address: "HR3", dataType: "UInt16", collectionDataType: "UInt16", displayName: "未选中", intervalMs: 1000 },
    ];
    fixture.probeCollection.name = "产线采集";
    fixture.handleProbeSelection(fixture.probe.results.slice(0, 2));
    await fixture.saveProbeCollection();
    const [deviceId, profile] = fixture.creates[0];
    assert.equal(deviceId, "A");
    assert.deepEqual(Array.from(profile.groups, (group) => group.intervalMs), [1000, 5000]);
    assert.equal(profile.groups[0].tags[0].dataType, "Float");
    assert.equal(profile.groups[1].tags[0].displayName, "HR2");
    assert.equal(fixture.routes[0].query.deviceId, "A");
    assert.equal(fixture.routes[0].path, "/collection/manage");
});

test("Saving a probe profile rejects invalid periods and ignores navigation after a device change", async () => {
    const waiting = deferred();
    const fixture = setup(undefined, undefined, () => waiting.promise);
    fixture.probe.results = [{ address: "HR0", collectionDataType: "UInt16", displayName: "A", intervalMs: 0 }];
    fixture.handleProbeSelection(fixture.probe.results);
    fixture.probeCollection.name = "A";
    await fixture.saveProbeCollection();
    assert.equal(fixture.creates.length, 0);
    fixture.probe.results[0].intervalMs = 5000;
    const saving = fixture.saveProbeCollection();
    await fixture.saveProbeCollection();
    assert.equal(fixture.creates.length, 1);
    fixture.form.deviceId = "B";
    fixture.handleDeviceChange();
    waiting.resolve({});
    await saving;
    assert.equal(fixture.routes.length, 0);
    assert.equal(fixture.probeCollection.saving, false);
});

test("OPC UA selected variables enter the same editable collection flow", () => {
    const fixture = setup();
    fixture.addressSpace.value = [{ address: "folder", name: "folder", type: "folder", children: [
        { address: "ns=2;s=Temperature", name: "Temperature", type: "point", dataType: "Float" },
        { address: "ns=2;s=Other", name: "Other", type: "point", dataType: "Bool" },
    ] }];
    fixture.selectedAddresses.value = ["folder", "ns=2;s=Temperature"];
    fixture.addSelectedToCollection();
    assert.equal(fixture.probe.results.length, 1);
    assert.equal(fixture.probe.results[0].collectionDataType, "Float");
    assert.equal(fixture.probe.results[0].address, "ns=2;s=Temperature");
    fixture.addressSpace.value[0].children[0].dataType = "Structure";
    fixture.addSelectedToCollection();
    assert.equal(fixture.warnings.length, 1);
});

test("Initial lazy roots and loaded descendants remain visible and searchable with the device protocol", async () => {
    const fixture = setup();
    assert.equal(await fixture.loadAddressSpace(), true);
    const tree = fixture.mount();
    assert.equal(tree.store.root.childNodes.length, 1);
    const folder = tree.store.root.childNodes[0];
    folder.expand();
    await flush();
    fixture.searchForm.searchAddress = "I5";
    fixture.searchAddress();
    assert.equal(folder.childNodes.length, 1);
    assert.equal(fixture.addressSpace.value[0].children.length, 1);
    assert.equal(fixture.searchResults.value[0]?.address, "I5");
    assert.deepEqual(fixture.calls, [["A", undefined, "OpcUa"], ["A", "I", "OpcUa"]]);
    tree.stop();
});

test("A failed lazy request remains expandable and retries successfully", async () => {
    let fail = true;
    const fixture = setup(async (_deviceId, parentPath) => {
        if (!parentPath) return nodes();
        if (fail) throw new Error("temporary failure");
        return nodes("I5", "Variable");
    });
    await fixture.loadAddressSpace();
    const tree = fixture.mount();
    const folder = tree.store.root.childNodes[0];
    folder.expand();
    await flush();
    assert.equal(folder.loaded, false);
    assert.equal(folder.loading, false);
    assert.equal(folder.isLeaf, false);
    fail = false;
    folder.expand();
    await flush();
    assert.equal(folder.loaded, true);
    assert.equal(folder.childNodes.length, 1);
    assert.equal(fixture.calls.filter((args) => args[1]).length, 2);
    tree.stop();
});

test("An unsupported browser clears old points and displays the capability limitation without claiming success", async () => {
    const message = "当前 SiemensS7 驱动未实现在线地址枚举，不会生成预设点位。";
    const fixture = setup(async () => {
        throw { response: { status: 422, data: { code: "ADDRESS_SPACE_BROWSING_NOT_SUPPORTED", error: message } } };
    });
    fixture.devices.value[0].protocol = "SiemensS7";
    fixture.addressSpace.value = [fixture.mapNode(nodes("OLD", "Variable")[0])];
    fixture.selectedAddress.value = fixture.addressSpace.value[0];
    fixture.selectedAddresses.value = ["OLD"];
    assert.equal(await fixture.loadAddressSpace(), false);
    assert.equal(fixture.addressSpace.value.length, 0);
    assert.equal(fixture.selectedAddress.value, null);
    assert.equal(fixture.selectedAddresses.value.length, 0);
    assert.equal(fixture.browseNotice.value, message);
    assert.equal(fixture.browseNoticeType.value, "warning");
    assert.equal(fixture.errors.length, 0);
    assert.equal(fixture.successes.length, 0);
    assert.equal(fixture.loading.value, false);
    fixture.handleDeviceChange();
    assert.equal(fixture.browseNotice.value, "");
});

test("An empty device response stays empty rather than generating sample addresses", async () => {
    const fixture = setup(async () => []);
    assert.equal(await fixture.loadAddressSpace(), true);
    assert.equal(fixture.addressSpace.value.length, 0);
    assert.equal(fixture.browseNoticeType.value, "info");
    assert.match(fixture.browseNotice.value, /设备未返回.*未生成/);
    assert.doesNotMatch(component, /扫描地址|地址扫描完成/);
    assert.match(component, /\$router\.push\('\/plc\/rw'\)/);
});

test("A failed browse stays visibly failed until a successful retry clears the notice", async () => {
    let fail = true;
    const fixture = setup(async () => {
        if (fail) throw new Error("device unavailable");
        return nodes("OnlinePoint", "Variable");
    });
    assert.equal(await fixture.loadAddressSpace(), false);
    assert.equal(fixture.browseNoticeType.value, "error");
    assert.equal(fixture.browseNotice.value, "device unavailable");
    assert.equal(fixture.addressSpace.value.length, 0);
    fail = false;
    assert.equal(await fixture.loadAddressSpace(), true);
    assert.equal(fixture.addressSpace.value[0].address, "OnlinePoint");
    assert.equal(fixture.browseNotice.value, "");
});

test("Directory search without a loaded catalog explains the scope and makes no device request", () => {
    const fixture = setup();
    fixture.searchForm.searchAddress = "I5";
    fixture.searchAddress();
    assert.equal(fixture.calls.length, 0);
    assert.equal(fixture.readCalls.length, 0);
    assert.equal(fixture.searchResults.value.length, 0);
    assert.match(fixture.infos[0], /没有已加载.*按地址读取/);
});

test("An exact read requires an explicit device, address and data type", async () => {
    const fixture = setup();
    fixture.searchForm.searchAddress = "I5";
    await fixture.readExactAddress();
    fixture.searchForm.dataType = "Int32";
    fixture.form.deviceId = "";
    await fixture.readExactAddress();
    fixture.form.deviceId = "A";
    fixture.searchForm.searchAddress = " ";
    await fixture.readExactAddress();
    assert.equal(fixture.readCalls.length, 0);
    assert.equal(fixture.warnings.length, 3);
    assert.equal(fixture.directRead.result, null);
});

for (const sample of [{ dataType: "Int32", value: 0 }, { dataType: "Bool", value: false }]) {
    test(`Exact reads use the requested ${sample.dataType} and preserve ${sample.value} without inventing catalog nodes`, async () => {
        const fixture = setup(undefined, async () => readResult("I5", sample.dataType, sample.value));
        fixture.searchForm.searchAddress = " I5 ";
        fixture.searchForm.dataType = sample.dataType;
        await fixture.readExactAddress();
        assert.deepEqual(JSON.parse(JSON.stringify(fixture.readCalls)), [["A", { tags: [{ address: "I5", dataType: sample.dataType }] }]]);
        assert.equal(fixture.directRead.result.value, sample.value);
        assert.equal(fixture.directRead.result.quality, "Good");
        assert.equal(fixture.directRead.error, "");
        assert.equal(fixture.directRead.loading, false);
        assert.equal(fixture.calls.length, 0);
        assert.equal(fixture.addressSpace.value.length, 0);
        assert.equal(fixture.searchResults.value.length, 0);
        fixture.resetSearch();
        assert.equal(fixture.directRead.result, null);
        assert.equal(fixture.searchForm.dataType, "");
    });
}

for (const failure of ["bad quality", "empty response", "network error"]) {
    test(`Exact read reports ${failure} rather than keeping a previous valid value`, async () => {
        let fail = false;
        const fixture = setup(undefined, async () => {
            if (!fail) return readResult();
            if (failure === "network error") throw new Error("read failed");
            if (failure === "empty response") return { tags: [] };
            return readResult("I5", "Int32", 0, "Bad", "address rejected");
        });
        fixture.searchForm.searchAddress = "I5";
        fixture.searchForm.dataType = "Int32";
        await fixture.readExactAddress();
        fail = true;
        await fixture.readExactAddress();
        assert.notEqual(fixture.directRead.error, "");
        assert.equal(fixture.directRead.loading, false);
        assert.notEqual(fixture.directRead.result?.quality, "Good");
        assert.equal(fixture.addressSpace.value.length, 0);
    });
}

for (const changedField of ["device", "address", "type"]) {
    for (const failed of [false, true]) {
        test(`Changing ${changedField} ignores an old exact-read ${failed ? "failure" : "value"}`, async () => {
            const pending = deferred();
            let readCount = 0;
            const fixture = setup(undefined, async (_deviceId, body) => {
                if (++readCount === 1) return pending.promise;
                return readResult(body.tags[0].address, body.tags[0].dataType, 25);
            });
            fixture.searchForm.searchAddress = "I5";
            fixture.searchForm.dataType = "Int32";
            const previous = fixture.readExactAddress();
            if (changedField === "device") fixture.form.deviceId = "B";
            if (changedField === "address") fixture.searchForm.searchAddress = "I10";
            if (changedField === "type") fixture.searchForm.dataType = "Float";
            assert.equal(fixture.directRead.loading, false);
            assert.equal(fixture.directRead.result, null);
            await fixture.readExactAddress();
            if (failed) pending.reject(new Error("old failure"));
            else pending.resolve(readResult());
            await previous;
            assert.equal(fixture.directRead.result.value, 25);
            assert.equal(fixture.directRead.result.address, fixture.searchForm.searchAddress);
            assert.equal(fixture.directRead.result.dataType, fixture.searchForm.dataType);
            assert.equal(fixture.directRead.error, "");
            assert.equal(fixture.directRead.loading, false);
        });
    }
}

for (const stage of ["root", "child"]) {
    for (const switchDevice of [false, true]) {
        test(`${switchDevice ? "Switching devices" : "Refreshing"} ignores an old ${stage} response`, async () => {
            const pending = deferred();
            let rootCount = 0;
            const fixture = setup(async (deviceId, parentPath) => {
                if (!parentPath && ++rootCount === 1 && stage === "root") return pending.promise;
                if (parentPath && stage === "child") return pending.promise;
                return nodes(deviceId === "B" ? "BROOT" : "I");
            });
            let previousRequest;
            let previousTree;
            let previousFolder;
            if (stage === "root") previousRequest = fixture.loadAddressSpace();
            else {
                await fixture.loadAddressSpace();
                previousTree = fixture.mount();
                previousFolder = previousTree.store.root.childNodes[0];
                previousFolder.expand();
            }
            if (switchDevice) {
                fixture.form.deviceId = "B";
                fixture.handleDeviceChange();
            }
            const latest = fixture.loadAddressSpace();
            previousTree?.stop();
            assert.equal(await latest, true);
            pending.resolve(nodes("OLD", stage === "child" ? "Variable" : "Folder"));
            if (previousRequest) assert.equal(await previousRequest, false);
            await flush();
            assert.equal(fixture.addressSpace.value[0].address, switchDevice ? "BROOT" : "I");
            assert.equal(fixture.addressSpace.value[0].children.length, 0);
            assert.equal(fixture.loading.value, false);
            assert.equal(fixture.errors.length, 0);
            if (previousFolder) {
                assert.equal(previousFolder.data.children.length, 0);
                assert.equal(previousFolder.loading, false);
            }
        });
    }
}
