import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

test('only Escape inside an open provider menu cancels the browser default and still reaches Blazor', () => {
    const listeners = [];
    class Element {
        constructor(picker) { this.picker = picker; }
        closest(selector) { assert.equal(selector, '.provider-picker'); return this.picker; }
    }
    const document = { addEventListener: (...args) => listeners.push(args) };
    vm.runInNewContext(readFileSync(new URL('../SwarmCode.App/wwwroot/js/swarmcode.js', import.meta.url), 'utf8'),
        { window: {}, document, Element });
    const [name, keydown, capture] = listeners.find(([name]) => name === 'keydown');
    assert.equal(name, 'keydown');
    assert.equal(capture, true);
    const picker = open => ({ querySelector: selector => {
        assert.equal(selector, '.provider-picker__toggle[aria-expanded="true"]');
        return open ? {} : null;
    } });
    for (const [key, target, expected] of [
        ['Escape', new Element(picker(true)), true],
        ['Escape', new Element(picker(false)), false],
        ['Escape', new Element(null), false],
        ['Escape', {}, false],
        ['Enter', new Element(picker(true)), false],
        ['Tab', new Element(picker(true)), false],
    ]) {
        let prevented = false;
        keydown({ key, target, preventDefault: () => prevented = true,
            stopPropagation: () => assert.fail('The renderer must receive the key event') });
        assert.equal(prevented, expected);
    }
});
