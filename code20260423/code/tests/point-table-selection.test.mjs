import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";
import { createSSRApp, getCurrentInstance } from "vue";
import { renderToString } from "@vue/server-renderer";
import useWatcher from "element-plus/es/components/table/src/store/watcher.mjs";

const component = readFileSync(new URL("../src/views/industrial/DeviceView.vue", import.meta.url), "utf8");
const script = component.match(/<script[^>]*setup[^>]*>([\s\S]*?)<\/script>/)?.[1];
assert.ok(script);
const parsed = ts.createSourceFile("DeviceView.ts", script, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
const names = ["mapVariableNodeToRow", "normalizeDataType"];
const functions = parsed.statements.filter((statement) => ts.isFunctionDeclaration(statement)
    && names.includes(statement.name?.text));
assert.equal(functions.length, names.length);
const context = vm.createContext({});
vm.runInContext(ts.transpileModule(functions.map((statement) => statement.getText(parsed)).join("\n"), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
}).outputText, context);

for (const children of [undefined, [], [{ path: "property" }]]) {
    test(`Mapped points with ${children?.length ?? "no"} tree children select as flat table rows`, async () => {
        const events = [];
        let selectedPaths;
        let allSelected;
        const rows = ["Zhh", "Sjwz", "Fzdl"].map((label) => context.mapVariableNodeToRow({
            id: label, path: `ns=1;s=ID_HNC.X_${label}`, label,
            nodeType: "Variable", dataType: "Double", isReadable: true, children,
        }));
        await renderToString(createSSRApp({
            emits: ["selection-change", "select"],
            setup() {
                const store = useWatcher();
                getCurrentInstance().store = store;
                store.states.rowKey.value = "id";
                store.states.data.value = rows;
                store.clearSelection();
                for (const row of store.states.data.value) {
                    store.toggleRowSelection(row, true, false, true);
                    store.updateAllSelected();
                }
                selectedPaths = store.getSelectionRows().map((row) => row.path);
                allSelected = store.states.isAllSelected.value;
                return () => null;
            },
        }, { onSelectionChange: (selection) => events.push(selection) }));
        assert.deepEqual(selectedPaths, rows.map((row) => row.path));
        assert.equal(events.at(-1).filter((row) => row.nodeType === "Variable").length, 3);
        assert.equal(allSelected, true, "All visible variables must set the header checkbox to checked");
    });
}
