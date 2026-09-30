import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";
import { parse, compileScript, compileTemplate } from "@vue/compiler-sfc";

const source = readFileSync(new URL("../src/views/robot/ReadWriteView.vue", import.meta.url), "utf8");
const { descriptor } = parse(source);
const executable = ts.transpileModule(
    descriptor.scriptSetup.content.replace(/^import .*;$/gm, "")
        + "\nglobalThis.handlers = { batchRead, readData, batchItems, rwForm, rwResult, isConnected };",
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } },
).outputText;

function setup(response) {
    const messages = { success: [], warning: [], error: [] };
    const calls = [];
    const context = vm.createContext({
        ref: (value) => ({ value }), reactive: (value) => value, onMounted() {},
        ElMessage: Object.fromEntries(Object.entries(messages).map(([type, values]) => [type, (value) => values.push(value)])),
        machineConnectionPointsApi: { async readTags(...args) {
            calls.push(args);
            if (response instanceof Error) throw response;
            return response;
        } },
    });
    vm.runInContext(executable, context);
    const handlers = context.handlers;
    handlers.isConnected.value = true;
    return { ...handlers, messages, calls };
}

const row = (address, dataType = "joint") => ({ address, dataType, value: "old", errorMessage: "" });
const tag = (address, value, quality = "Good", errorMessage) => ({ address, value, quality, errorMessage });

test("Batch reads retain good values and expose failed quality, including absent error text", async () => {
    const fixture = setup({ tags: [tag("40001", 12.5), tag("40002", 0, "Bad", "connection closed"), tag("40003", 0, "Bad")] });
    const rows = [row("40001"), row("40002"), row("40003")];
    fixture.batchItems.value = rows;
    await fixture.batchRead();
    assert.equal(rows[0].value, "12.5");
    assert.equal(rows[0].errorMessage, "");
    assert.equal(rows[1].value, "");
    assert.match(rows[1].errorMessage, /connection closed/);
    assert.equal(rows[2].value, "");
    assert.match(rows[2].errorMessage, /Bad/);
    assert.equal(fixture.messages.success.length, 0);
    assert.match(fixture.messages.error[0], /1\/3.*成功/);
});

test("Batch reads match repeated addresses in request order and preserve valid zero and false", async () => {
    const fixture = setup({ tags: [tag("40001", 0), tag("40001", false), tag("40001", 2.5)] });
    const rows = [row("40001", "Int16"), row(""), row("40001", "Bool"), row("40001", "Float")];
    fixture.batchItems.value = rows;
    await fixture.batchRead();
    assert.deepEqual(rows.map((item) => item.value), ["0", "old", "false", "2.5"]);
    assert.equal(fixture.calls[0][1].tags.length, 3);
    assert.deepEqual(Array.from(fixture.calls[0][1].tags, (item) => item.dataType), ["Int16", "Bool", "Float"]);
    assert.equal(fixture.messages.error.length, 0);
    assert.equal(fixture.messages.success.length, 1);
});

test("Batch reads reject explicit errors even with Good quality and report missing results", async () => {
    const fixture = setup({ tags: [tag("40001", 0, "Good", "read failed")] });
    const rows = [row("40001"), row("40002")];
    fixture.batchItems.value = rows;
    await fixture.batchRead();
    assert.equal(rows[0].value, "");
    assert.equal(rows[0].errorMessage, "read failed");
    assert.equal(rows[1].value, "");
    assert.ok(rows[1].errorMessage);
    assert.equal(fixture.messages.success.length, 0);
});

test("Batch transport failures clear stale values and expose the failure per requested row", async () => {
    const fixture = setup(new Error("transport disconnected"));
    const rows = [row("40001"), row("40002")];
    fixture.batchItems.value = rows;
    await fixture.batchRead();
    for (const item of rows) {
        assert.equal(item.value, "");
        assert.equal(item.errorMessage, "transport disconnected");
    }
    assert.equal(fixture.messages.success.length, 0);
});

test("Single reads require Good quality without an error and clear unsuccessful values", async () => {
    for (const result of [tag("40001", 0, "Bad"), tag("40001", 0, "Good", "read failed"), undefined]) {
        const fixture = setup({ tags: result ? [result] : [] });
        Object.assign(fixture.rwForm, { address: "40001", value: "old" });
        await fixture.readData();
        assert.equal(fixture.rwResult.value.success, false);
        assert.equal(fixture.rwForm.value, "");
    }
    const fixture = setup({ tags: [tag("40001", 0)] });
    fixture.rwForm.address = "40001";
    await fixture.readData();
    assert.equal(fixture.rwResult.value.success, true);
    assert.equal(fixture.rwForm.value, "0");
});

test("Single transport failures clear the previous value", async () => {
    const fixture = setup(new Error("transport disconnected"));
    Object.assign(fixture.rwForm, { address: "40001", value: "old" });
    await fixture.readData();
    assert.equal(fixture.rwResult.value.success, false);
    assert.equal(fixture.rwForm.value, "");
    assert.equal(fixture.rwResult.value.message, "transport disconnected");
});

test("Batch row errors are rendered and the Vue component compiles", () => {
    assert.match(descriptor.template.content, /scope\.row\.errorMessage/);
    const script = compileScript(descriptor, { id: "robot-read-write" });
    const template = compileTemplate({ source: descriptor.template.content, filename: "ReadWriteView.vue", id: "robot-read-write",
        compilerOptions: { bindingMetadata: script.bindings } });
    assert.deepEqual(template.errors, []);
});
