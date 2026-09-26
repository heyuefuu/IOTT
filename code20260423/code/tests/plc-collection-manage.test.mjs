import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";
import { ref } from "vue";

const component = readFileSync(new URL("../src/views/collection/CollectionManageView.vue", import.meta.url), "utf8");
const script = component.match(/<script[^>]*setup[^>]*>([\s\S]*?)<\/script>/)[1];
const names = ["profiles", "loadingProfiles", "profilesRequestVersion", "loadProfiles", "handleDeviceChange",
    "startProfile", "startingProfileId", "countTags", "liveRows", "subscribedDeviceId", "MAX_LIVE_ROWS", "onBatch", "getErr"];
const parsed = ts.createSourceFile("CollectionManageView.ts", script, ts.ScriptTarget.Latest, true);
const statements = parsed.statements.filter(statement => {
    const name = ts.isFunctionDeclaration(statement) ? statement.name?.text
        : ts.isVariableStatement(statement) ? statement.declarationList.declarations[0]?.name.getText(parsed) : null;
    return names.includes(name);
});
assert.equal(statements.length, names.length);
const executable = ts.transpileModule(statements.map(statement => statement.getText(parsed)).join("\n")
    + `\nglobalThis.fixture = { ${names.join(",")} };`, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
}).outputText;

function deferred() {
    let resolve;
    const promise = new Promise(complete => { resolve = complete; });
    return { resolve, promise };
}

function setup(list = async () => []) {
    const selectedDeviceId = ref("A");
    const starts = [];
    const subscriptions = [];
    const context = vm.createContext({
        ref, selectedDeviceId,
        ElMessage: { error() {}, warning() {}, success() {} },
        machineConnectionCollectionApi: {
            listProfiles: list,
            async startCollection(...args) { starts.push(args); return { taskId: "running" }; },
        },
        async loadTasks() {},
        async subscribeDeviceById(id) { subscriptions.push(id); },
    });
    vm.runInContext(executable, context);
    return { ...context.fixture, selectedDeviceId, starts, subscriptions };
}

test("Switching PLC devices discards a previous profile response", async () => {
    const waiting = deferred();
    const fixture = setup(id => id === "A" ? waiting.promise : Promise.resolve([{ id: "B-profile" }]));
    const old = fixture.loadProfiles();
    fixture.selectedDeviceId.value = "B";
    await fixture.loadProfiles();
    waiting.resolve([{ id: "A-profile" }]);
    await old;
    assert.equal(fixture.profiles.value[0].id, "B-profile");
    assert.equal(fixture.loadingProfiles.value, false);
});

test("Collection only starts the current enabled profile and subscribes to its live data", async () => {
    const fixture = setup();
    const profile = { id: "profile", deviceId: "A", isEnabled: false,
        groups: [{ groupName: "state", intervalMs: 5000, tags: [{ address: "HR0", dataType: "UInt16" }] }] };
    await fixture.startProfile(profile);
    await fixture.startProfile({ ...profile, isEnabled: true, deviceId: "B" });
    assert.equal(fixture.starts.length, 0);
    await fixture.startProfile({ ...profile, isEnabled: true });
    assert.equal(fixture.starts[0][0], "A");
    assert.equal(fixture.starts[0][1][0].intervalMs, 5000);
    assert.deepEqual(fixture.subscriptions, ["A"]);
});

test("Live collection ignores late data from a different PLC", () => {
    const fixture = setup();
    fixture.subscribedDeviceId.value = "B";
    const batch = { deviceId: "A", groupName: "state",
        values: [{ address: "HR0", value: 123, quality: "Good", timestamp: "2026-09-26T00:00:00Z" }] };
    fixture.onBatch(batch);
    assert.equal(fixture.liveRows.value.length, 0);
    fixture.onBatch({ ...batch, deviceId: "B" });
    assert.equal(fixture.liveRows.value[0].value, 123);
});
