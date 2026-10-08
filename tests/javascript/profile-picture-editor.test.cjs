const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('group crop sessions cannot reset or dispose the profile crop session', () => {
    const revoked = [];
    const window = {};
    vm.runInNewContext(fs.readFileSync(path.join(__dirname,
        '../../src/Confast.Web/wwwroot/profile-picture-editor.js'), 'utf8'), {
        window, URL: { revokeObjectURL: url => revoked.push(url) }
    });
    const profile = window.confastProfilePictureEditor;
    const group = window.confastGroupIconEditor;
    profile.state = { zoom: 3, panX: 20, panY: 30, input: { value: 'profile' }, url: 'blob:profile' };
    group.state = { zoom: 4, panX: 40, panY: 50, input: { value: 'group' }, url: 'blob:group' };
    group.reset();
    assert.equal(group.state.zoom, 1);
    assert.equal(profile.state.zoom, 3);
    group.dispose();
    assert.equal(group.state, null);
    assert.equal(profile.state.input.value, 'profile');
    assert.deepEqual(revoked, ['blob:group']);
});

