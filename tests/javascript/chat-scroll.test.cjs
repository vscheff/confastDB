// Run with: node --test tests/javascript/chat-scroll.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

// Model browser scroll clamping and message geometry so delayed media sizing is
// deterministic. The real browser checks cover layout and native scroll events.
function fixture() {
    let current;
    const observers = [];
    class Observer {
        constructor(callback) { this.callback = callback; observers.push(this); }
        observe() { this.connected = true; }
        disconnect() { this.connected = false; }
    }
    const context = { window: {}, document: {
        getElementById: () => current,
        querySelector: () => null
    }, ResizeObserver: Observer, MutationObserver: Observer };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/Confast.Web/wwwroot/chat-time.js'), 'utf8'), context);
    const chat = context.window.confastChatTime;
    chat.positionReactionPicker = () => {};
    function history(id, heights) {
        const listeners = new Map();
        let scrollTop = 0;
        const element = {
            id, heights, clientHeight: 200, clientTop: 0, isConnected: true,
            get scrollHeight() { return this.heights.reduce((sum, height) => sum + height, 0); },
            get scrollTop() { return scrollTop; },
            set scrollTop(value) { scrollTop = Math.max(0, Math.min(value, this.scrollHeight - this.clientHeight)); },
            getAttribute: () => element.id,
            getBoundingClientRect: () => ({ top: 0 }),
            addEventListener: (name, callback) => listeners.set(name, callback),
            removeEventListener: name => listeners.delete(name),
            fire: name => listeners.get(name)?.(),
            querySelectorAll: () => element.children
        };
        element.children = heights.map((_, index) => ({
            getAttribute: () => String(index + 1),
            getBoundingClientRect: () => {
                const top = element.heights.slice(0, index).reduce((sum, height) => sum + height, 0) - element.scrollTop;
                return { top, bottom: top + element.heights[index] };
            }
        }));
        current = element;
        return element;
    }
    return { chat, history, resize: () => observers.filter(o => o.connected).forEach(o => o.callback()) };
}

test('reopening at the bottom follows media that loads after restoration', () => {
    const { chat, history, resize } = fixture();
    const first = history('channel', [100, 600, 300]);
    chat.restoreConversationScrollPosition();
    assert.equal(first.scrollTop, 800);
    chat.stopConversationScrollTracking();
    first.isConnected = false;
    const reopened = history('channel', [100, 50, 50]);
    chat.restoreConversationScrollPosition();
    assert.equal(reopened.scrollTop, 0);
    reopened.heights = [100, 600, 300];
    resize();
    assert.equal(reopened.scrollTop, 800);
    reopened.fire('scroll');
    assert.equal(chat.conversationScrollPositions.get('channel').atBottom, true);
});

test('a saved message offset survives clamping and media growth above it', () => {
    const { chat, history, resize } = fixture();
    const first = history('channel', [300, 300, 300]);
    chat.restoreConversationScrollPosition();
    first.scrollTop = 350;
    first.fire('scroll');
    chat.stopConversationScrollTracking();
    first.isConnected = false;
    const reopened = history('channel', [10, 10, 10]);
    chat.restoreConversationScrollPosition();
    // An intermediate clamped scroll event must not replace the saved anchor.
    reopened.fire('scroll');
    reopened.heights = [500, 300, 300];
    resize();
    assert.equal(reopened.scrollTop, 550);
    assert.equal(reopened.children[1].getBoundingClientRect().top, -50);
    reopened.scrollTop = 600;
    reopened.fire('scroll');
    reopened.heights = [700, 300, 300];
    resize();
    assert.equal(reopened.scrollTop, 800);
});

test('switching conversations isolates positions and jump-to-latest resets the anchor', () => {
    const { chat, history, resize } = fixture();
    const first = history('first', [300, 300, 300]);
    chat.restoreConversationScrollPosition();
    first.scrollTop = 350;
    first.fire('scroll');
    first.id = 'second';
    chat.restoreConversationScrollPosition();
    assert.equal(first.scrollTop, 700);
    first.id = 'first';
    chat.restoreConversationScrollPosition();
    assert.equal(first.scrollTop, 350);
    chat.scrollToLatestMessage();
    first.heights = [500, 300, 300];
    resize();
    assert.equal(first.scrollTop, 900);
    chat.stopConversationScrollTracking();
    first.heights = [700, 300, 300];
    resize();
    assert.equal(first.scrollTop, 900);
});
