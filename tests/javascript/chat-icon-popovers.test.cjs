const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('manual icon dismissal reacts to outside pointer input, not file dialog focus changes', () => {
    const handlers = new Map();
    const fileInput = {};
    let hidden = 0;
    let closeClicks = 0;
    const card = {
        matches: () => true,
        contains: target => target === fileInput,
        hidePopover: () => { hidden++; },
        querySelector: () => ({ click: () => { closeClicks++; } }),
        addEventListener() {}, removeEventListener() {}
    };
    const window = {};
    vm.runInNewContext(fs.readFileSync(path.join(__dirname,
        '../../src/Confast.Web/wwwroot/chat-time.js'), 'utf8'), {
        window, Element: class {}, document: {
            getElementById: () => card,
            addEventListener: (name, handler) => handlers.set(name, handler),
            removeEventListener: name => handlers.delete(name)
        }
    });
    window.confastChatTime.watchManualIconCard('icon');
    assert.deepEqual([...handlers.keys()], ['pointerdown', 'keydown']);
    handlers.get('pointerdown')({ target: fileInput });
    assert.equal(hidden, 0);
    handlers.get('pointerdown')({ target: {} });
    assert.equal(hidden, 1);
    handlers.get('keydown')({ key: 'Escape', preventDefault() {}, stopPropagation() {} });
    assert.equal(closeClicks, 1);
    window.confastChatTime.stopManualIconCard('icon');
    assert.equal(handlers.size, 0);
});

test('closing the group editor closes nested manual cards first', () => {
    const hidden = [];
    const child = { matches: () => true, hidePopover: () => hidden.push('icon') };
    const root = {
        querySelectorAll: () => [child], matches: () => true,
        hidePopover: () => hidden.push('group')
    };
    const window = {};
    vm.runInNewContext(fs.readFileSync(path.join(__dirname,
        '../../src/Confast.Web/wwwroot/chat-time.js'), 'utf8'), {
        window, document: { getElementById: () => root }
    });
    window.confastChatTime.hidePopoverStack('group');
    assert.deepEqual(hidden, ['icon', 'group']);
});
