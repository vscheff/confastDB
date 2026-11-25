const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../../src/Confast.Web/wwwroot/factory-reset.js"), "utf8");

test("Factory Reset requires exact wording, acknowledgement and the final dialog", () => {
    const fields = { Password: { value: "" }, Phrase: { value: "" }, Acknowledged: { checked: false }, Confirmed: { value: "false" } };
    const submit = { disabled: true };
    const formEvents = new Map();
    const dialog = { open: false, showModal() { this.open = true; }, close() { this.open = false; } };
    const cancel = { addEventListener(_, callback) { this.click = callback; } };
    const confirm = { addEventListener(_, callback) { this.click = callback; } };
    const elements = new Map([["dialog", dialog], ['button[type="submit"]', submit], ["[data-reset-cancel]", cancel], ["[data-reset-confirm]", confirm]]);
    let submitted = 0;
    const form = {
        elements: { namedItem: name => fields[name] },
        querySelector: selector => elements.get(selector),
        addEventListener: (event, callback) => formEvents.set(event, callback),
        requestSubmit() {
            let prevented = false;
            formEvents.get("submit")({ preventDefault() { prevented = true; } });
            if (!prevented) submitted++;
        }
    };
    vm.runInNewContext(source, {
        document: { querySelector: selector => selector === "[data-factory-reset]" ? form : null },
        window: { addEventListener() {} }
    });

    fields.Password.value = "test password";
    fields.Phrase.value = "reset database";
    fields.Acknowledged.checked = true;
    formEvents.get("input")();
    assert.equal(submit.disabled, true);
    fields.Phrase.value = "RESET DATABASE";
    fields.Acknowledged.checked = false;
    formEvents.get("input")();
    assert.equal(submit.disabled, true);
    fields.Acknowledged.checked = true;
    formEvents.get("input")();
    assert.equal(submit.disabled, false);

    form.requestSubmit();
    assert.equal(dialog.open, true);
    assert.equal(submitted, 0);
    cancel.click();
    assert.equal(dialog.open, false);
    form.requestSubmit();
    confirm.click();
    assert.equal(fields.Confirmed.value, "true");
    assert.equal(submitted, 1);
    assert.equal(submit.disabled, true);
});

test("the maintenance page updates independently and returns to sign-in after completion", async () => {
    const phase = { textContent: "" };
    const callbacks = [];
    const destinations = [];
    let status = { phase: "CleaningStorage", message: "Clearing managed file storage." };
    vm.runInNewContext(source, {
        document: { querySelector: selector => selector === "[data-maintenance-page]" ? { querySelector: () => phase } : null },
        fetch: async () => ({ ok: true, json: async () => status }),
        window: { setTimeout: callback => callbacks.push(callback), location: { replace: url => destinations.push(url) } }
    });

    await callbacks.shift()();
    assert.equal(phase.textContent, status.message);
    assert.deepEqual(destinations, []);
    status = { phase: null, lastOutcome: "Completed" };
    await callbacks.shift()();
    assert.deepEqual(destinations, ["/login"]);
});
