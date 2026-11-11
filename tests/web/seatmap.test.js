// The hall map's script (src/CinemaWatch.Web/wwwroot/seatmap.js) against the case file shared with the C# tests
// (tests/Fixtures/seat-selection-cases.json, run there by tests/CinemaWatch.Tests). Run: node --test tests/web
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const rules = require('../../src/CinemaWatch.Web/wwwroot/seatmap.js');
const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'Fixtures', 'seat-selection-cases.json'), 'utf8'));

const plan = {
    radius: fixture.seatRadius,
    seats: fixture.seats.map(s => ({ key: s.key, section: s.section, rowKey: s.rowKey, x: s.x, y: s.y, wheelchair: s.wheelchair === true })),
};
const point = ([x, y]) => ({ x, y });
const sorted = set => Array.from(set).sort();

test('the case file has cases', () => {
    assert.ok(fixture.cases.length >= 20);
});

for (const c of fixture.cases) {
    test(c.name, () => {
        const before = new Set(c.before);
        let after;
        if (c.row) {
            after = rules.toggleRow(before, plan, c.row.section, c.row.rowKey);
        } else if (c.cancel) {
            // Esc or a lost pointer: the live result is dropped, the selection from before the press comes back.
            const gesture = new rules.Gesture(plan, before, point(c.path[0]));
            c.path.slice(1).forEach(p => gesture.moveTo(point(p)));
            after = gesture.before;
        } else {
            after = rules.applyGesture(plan, before, c.path.map(point));
        }
        assert.deepEqual(sorted(after), [...c.after].sort());
    });
}

test('a gesture keeps the selection from before the press untouched', () => {
    const before = new Set(['1_1_1']);
    const gesture = new rules.Gesture(plan, before, { x: 100, y: 100 });
    gesture.moveTo({ x: 380, y: 100 });
    assert.deepEqual(sorted(before), ['1_1_1']);
    assert.deepEqual(sorted(gesture.before), ['1_1_1']);
});

test('free now: watched free seats in runs of at least the minimum, row by row (as SeatMatcher)', () => {
    const seats = [
        { key: 'a1', section: '1', rowKey: '1', position: 1, free: true },
        { key: 'a2', section: '1', rowKey: '1', position: 2, free: true },
        { key: 'a3', section: '1', rowKey: '1', position: 3, free: false },
        { key: 'a4', section: '1', rowKey: '1', position: 4, free: true },
        { key: 'b1', section: '1', rowKey: '2', position: 1, free: true },
        { key: 'b2', section: '1', rowKey: '2', position: 2, free: true },
        { key: 'b3', section: '1', rowKey: '2', position: 3, free: true },
    ];
    const all = new Set(seats.map(s => s.key));
    assert.equal(rules.availableCount(seats, all, 1), 6);
    assert.equal(rules.availableCount(seats, all, 2), 5);
    assert.equal(rules.availableCount(seats, all, 3), 3);
    assert.equal(rules.availableCount(seats, new Set(['a1', 'b3']), 1), 2);
    assert.equal(rules.availableCount(seats, new Set(['a1', 'b3']), 2), 0);
});

test('plural forms as Localizer.Plural (Polish one/few/many, English one/other)', () => {
    const pl = ['miejsce', 'miejsca', 'miejsc'];
    assert.deepEqual([1, 2, 4, 5, 12, 14, 22, 25, 112, 122].map(n => rules.plural('pl', n, pl)),
        ['miejsce', 'miejsca', 'miejsca', 'miejsc', 'miejsc', 'miejsc', 'miejsca', 'miejsc', 'miejsc', 'miejsca']);
    assert.deepEqual([1, 2, 5].map(n => rules.plural('en', n, ['seat', 'seats'])), ['seat', 'seats', 'seats']);
});
