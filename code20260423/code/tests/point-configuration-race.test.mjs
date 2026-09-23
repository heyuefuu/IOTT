import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";
import { computed, nextTick, ref, watch } from "vue";

const component = readFileSync(new URL("../src/views/industrial/DeviceView.vue", import.meta.url), "utf8");
const script = component.match(/<script[^>]*setup[^>]*>([\s\S]*?)<\/script>/)?.[1];
assert.ok(script, "DeviceView script setup exists");
const handlers = ["openPointDialog", "refreshSavedPathsFromDb", "applySavedPathsToTable",
    "handlePointTreeNodeClick", "loadAddressChildren", "expandPointTreeNodes",
    "collectFirstLevelExpandedKeys", "mapVariableNodeToRow", "normalizeDataType"];
const names = [...handlers, "pointRequestVersion", "pointDialogVersion"];
const parsed = ts.createSourceFile("DeviceView.ts", script, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
const statementName = (statement) => ts.isFunctionDeclaration(statement) ? statement.name?.text
    : ts.isVariableStatement(statement) ? statement.declarationList.declarations[0]?.name.getText(parsed) : null;
const statements = parsed.statements.filter((statement) => names.includes(statementName(statement))
    || (ts.isExpressionStatement(statement) && ts.isCallExpression(statement.expression)
        && statement.expression.expression.getText(parsed) === "watch"
        && statement.expression.arguments[0]?.getText(parsed) === "[pointDialogVisible, pointDialogDeviceId]"));
for (const name of handlers) assert.ok(statements.some((statement) => statementName(statement) === name), name);
assert.ok(statements.some(ts.isExpressionStatement), "Tests execute the component's actual session watcher");
const executable = ts.transpileModule(
    statements.map((statement) => statement.getText(parsed)).join("\n")
        + `\nglobalThis.handlers = { ${handlers.join(",")} };`,
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } },
).outputText;

function deferred() {
    let resolve;
    const promise = new Promise((complete) => { resolve = complete; });
    return { promise, resolve };
}

function variable(path = "i=2259") {
    return { id: path, path, label: "State", dataType: "Int32", nodeType: "Variable",
        isReadable: true, isWritable: false, _loaded: true, children: [] };
}

function setup(options = {}) {
    const databaseReads = Array.from({ length: 2 }, () => ({ started: deferred(), response: deferred() }));
    const flattenCalls = [];
    const errors = [];
    let databaseIndex = 0;
    const context = vm.createContext({
        watch, nextTick: options.nextTick ?? nextTick,
        pointDialogVisible: ref(false), pointDialogDeviceId: ref("device-a"),
        pointDialogDeviceName: ref(""), pointDialogDeviceProtocol: ref("OpcUa"),
        selectedPointTreeNodeId: ref("/"), pointTableFlattenLoading: ref(false),
        pointTableData: ref([]), selectedPointRows: ref([]), pointExpandedKeys: ref(["/"]),
        pointTreeRenderKey: ref(0), pointTreeRef: ref({ getNode() {} }),
        pointTreeData: ref([{ id: "/", path: "/", label: "Root", nodeType: "Folder", children: [], _loaded: false }]),
        savedPathsInDb: ref(new Set()), savedPointConfigByPath: ref(new Map()),
        datacollectionApi: { list(deviceId) {
            const request = databaseReads[databaseIndex++];
            request.deviceId = deviceId;
            request.started.resolve();
            return request.response.promise;
        } },
        machineConnectionPointsApi: { browseAddressSpace: options.browse ?? (async () => [variable()]) },
        sanitizeAddressSpaceLevelNodes: (_parent, nodes) => nodes,
        mapAddressNodeToTreeNode: (node) => node,
        async flattenAllDescendantVariableNodes(deviceId, path) {
            flattenCalls.push({ deviceId, path });
            return [variable()];
        },
        POINT_FLATTEN_MAX_VARIABLES: 5000,
        console: { error: (error) => errors.push(error) }, ElMessage: { warning() {}, info() {} },
    });
    context.filteredPointTableData = computed(() => context.pointTableData.value);
    context.pointTableRef = ref({
        clearSelection() { context.selectedPointRows.value = []; },
        toggleRowSelection(row) { context.selectedPointRows.value.push(row); },
    });
    vm.runInContext(executable, context);
    return { context, handlers: context.handlers, databaseReads, flattenCalls, errors };
}

function assertSavedPoint(fixture, frequency) {
    assert.equal(fixture.context.savedPathsInDb.value.has("i=2259"), true);
    assert.equal(fixture.context.savedPointConfigByPath.value.get("i=2259")?.collectionFrequency, frequency);
    assert.equal(fixture.context.pointTableData.value[0]?.frequency, String(frequency));
    assert.equal(fixture.context.selectedPointRows.value[0]?.path, "i=2259");
    assert.equal(fixture.errors.length, 0);
}

test("Clicking a point while its device configuration is loading retains saved selection and frequency", async () => {
    const fixture = setup();
    fixture.context.pointDialogVisible.value = true;
    const loading = fixture.handlers.refreshSavedPathsFromDb({ silent: true });
    await fixture.handlers.handlePointTreeNodeClick(variable());
    fixture.databaseReads[0].response.resolve([{ path: "i=2259", collectionFrequency: 2500 }]);
    await loading;
    assertSavedPoint(fixture, 2500);
    assert.equal(fixture.context.selectedPointTreeNodeId.value, "i=2259");
});

for (const reopenSameDevice of [false, true]) {
    test(`${reopenSameDevice ? "Closing and reopening" : "Switching devices"} ignores the previous configuration response`, async () => {
        const fixture = setup();
        const previousOpen = fixture.handlers.openPointDialog({ id: "device-a", name: "A", protocol: "OpcUa" });
        await fixture.databaseReads[0].started.promise;
        if (reopenSameDevice) fixture.context.pointDialogVisible.value = false;
        const deviceId = reopenSameDevice ? "device-a" : "device-b";
        const currentOpen = fixture.handlers.openPointDialog({ id: deviceId, name: "Current", protocol: "OpcUa" });
        await fixture.databaseReads[1].started.promise;
        await fixture.handlers.handlePointTreeNodeClick(variable());
        fixture.databaseReads[1].response.resolve([{ path: "i=2259", collectionFrequency: 3000 }]);
        await currentOpen;
        fixture.databaseReads[0].response.resolve([{ path: "i=2259", collectionFrequency: 125 }]);
        await previousOpen;
        assert.equal(fixture.databaseReads[1].deviceId, deviceId);
        assertSavedPoint(fixture, 3000);
        assert.equal(fixture.context.selectedPointTreeNodeId.value, "i=2259");
        assert.equal(fixture.flattenCalls.length, 0, "Initialization must not select the root after a user click");
    });
}

for (const stage of ["browse", "expand"]) {
    test(`A click during initial ${stage} still loads configuration and preserves the user's node`, async () => {
        const started = deferred();
        const finish = deferred();
        let calls = 0;
        const pauseFirst = (fallback) => (...args) => {
            if (calls++ === 0) {
                started.resolve();
                return finish.promise;
            }
            return fallback(...args);
        };
        const fixture = setup(stage === "browse"
            ? { browse: pauseFirst(async () => [variable()]) }
            : { nextTick: pauseFirst(nextTick) });
        const opening = fixture.handlers.openPointDialog({ id: "device-a", name: "A", protocol: "OpcUa" });
        await started.promise;
        const clicked = stage === "browse" ? fixture.context.pointTreeData.value[0] : variable();
        await fixture.handlers.handlePointTreeNodeClick(clicked);
        assert.equal(fixture.databaseReads[0].deviceId, undefined, "The click precedes the configuration request");
        finish.resolve(stage === "browse" ? [variable()] : undefined);
        const outcome = await Promise.race([
            fixture.databaseReads[0].started.promise.then(() => "requested"),
            opening.then(() => "skipped"),
        ]);
        assert.equal(outcome, "requested", "A node click must not skip configuration loading");
        fixture.databaseReads[0].response.resolve([{ path: "i=2259", collectionFrequency: 4000 }]);
        await opening;
        assertSavedPoint(fixture, 4000);
        assert.equal(fixture.context.selectedPointTreeNodeId.value, clicked.id);
        assert.equal(fixture.flattenCalls.length, stage === "browse" ? 1 : 0);
        assert.equal(fixture.context.pointTreeData.value[0]._loaded, true);
    });
}
