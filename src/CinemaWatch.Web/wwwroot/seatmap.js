/*
  CinemaWatch hall map (wizard step 3, docs/SAAS.md §5.2). No libraries. Without this script the map still works
  through its form (check boxes, row numbers as buttons); with it:
  - a row number toggles the row at once, without a reload;
  - painting: press on a seat and drag - the first seat decides select or deselect;
  - rectangle: press on the background and drag - the seats whose centre lies inside are all selected, or all
    deselected when every one already was;
  - wheelchair spaces change only by a single click; Esc during a gesture (or a lost pointer) restores the state;
  - the counts follow live.
  The rules mirror CinemaWatch.Core SeatSelection (the reference implementation); both are tested against
  tests/Fixtures/seat-selection-cases.json (C#: tests/CinemaWatch.Tests, JavaScript: tests/web, `node --test`).
  All coordinates are plan units: seat centres, circles of the plan's seat radius.
*/
(function (global) {
    'use strict';

    // ----- Rules (mirror of SeatSelection; a plan is { radius, seats: [{ key, section, rowKey, x, y, wheelchair }] }) -----

    /** The nearest seat whose centre is at most the radius away; null for the background. Ties: the first in the plan. */
    function seatAt(plan, point) {
        const limit = plan.radius * plan.radius;
        let best = null;
        let bestDistance = Infinity;
        for (const seat of plan.seats) {
            const dx = seat.x - point.x;
            const dy = seat.y - point.y;
            const distance = dx * dx + dy * dy;
            if (distance <= limit && distance < bestDistance) {
                best = seat;
                bestDistance = distance;
            }
        }
        return best;
    }

    function distanceToSegmentSquared(p, a, b) {
        const abx = b.x - a.x;
        const aby = b.y - a.y;
        const lengthSquared = abx * abx + aby * aby;
        const t = lengthSquared === 0 ? 0 : Math.min(1, Math.max(0, ((p.x - a.x) * abx + (p.y - a.y) * aby) / lengthSquared));
        const dx = a.x + t * abx - p.x;
        const dy = a.y + t * aby - p.y;
        return dx * dx + dy * dy;
    }

    /** Regular seats the pointer passes over moving straight from one point to the next. */
    function regularSeatsAlong(plan, from, to) {
        const limit = plan.radius * plan.radius;
        return plan.seats.filter(s => !s.wheelchair && distanceToSegmentSquared(s, from, to) <= limit);
    }

    /** Regular seats whose centre lies inside the rectangle spanned by two corners (edges included). */
    function regularSeatsInside(plan, corner1, corner2) {
        const left = Math.min(corner1.x, corner2.x);
        const right = Math.max(corner1.x, corner2.x);
        const top = Math.min(corner1.y, corner2.y);
        const bottom = Math.max(corner1.y, corner2.y);
        return plan.seats.filter(s => !s.wheelchair && s.x >= left && s.x <= right && s.y >= top && s.y <= bottom);
    }

    function toggleSeat(selection, seat) {
        const result = new Set(selection);
        if (!result.delete(seat.key)) result.add(seat.key);
        return result;
    }

    /** Selects every regular seat, or deselects them all when every one already was; wheelchair spaces untouched. */
    function toggleGroup(selection, seats) {
        const regular = seats.filter(s => !s.wheelchair).map(s => s.key);
        const result = new Set(selection);
        if (regular.length === 0) return result;
        if (regular.every(key => result.has(key))) regular.forEach(key => result.delete(key));
        else regular.forEach(key => result.add(key));
        return result;
    }

    function toggleRow(selection, plan, section, rowKey) {
        return toggleGroup(selection, plan.seats.filter(s => s.section === section && s.rowKey === rowKey));
    }

    /** One gesture from the press to the release; result is always computed from before (a cancel restores it). */
    class Gesture {
        constructor(plan, before, start) {
            this.plan = plan;
            this.before = new Set(before);
            this.start = start;
            this.current = start;
            this.startSeat = seatAt(plan, start);
            this.painted = new Set();
            if (this.startSeat === null) {
                this.kind = 'rectangle';
                this.result = toggleGroup(this.before, regularSeatsInside(plan, start, start));
            } else if (this.startSeat.wheelchair) {
                this.kind = 'toggle';
                this.result = toggleSeat(this.before, this.startSeat);
            } else {
                this.kind = 'paint';
                this.paintSelects = !this.before.has(this.startSeat.key);
                this.painted.add(this.startSeat.key);
                this.result = this.paintResult();
            }
        }

        /** The pointer moved with the button pressed; true when the result changed. */
        moveTo(point) {
            const previous = this.current;
            this.current = point;
            if (this.kind === 'paint') {
                let added = false;
                for (const seat of regularSeatsAlong(this.plan, previous, point)) {
                    if (!this.painted.has(seat.key)) {
                        this.painted.add(seat.key);
                        added = true;
                    }
                }
                if (!added) return false;
                this.result = this.paintResult();
                return true;
            }
            if (this.kind === 'rectangle') {
                const result = toggleGroup(this.before, regularSeatsInside(this.plan, this.start, point));
                if (sameSet(result, this.result)) return false;
                this.result = result;
                return true;
            }
            return false;
        }

        paintResult() {
            const result = new Set(this.before);
            for (const key of this.painted) {
                if (this.paintSelects) result.add(key);
                else result.delete(key);
            }
            return result;
        }
    }

    function sameSet(a, b) {
        if (a.size !== b.size) return false;
        for (const key of a) if (!b.has(key)) return false;
        return true;
    }

    function applyGesture(plan, before, path) {
        const gesture = new Gesture(plan, before, path[0]);
        for (let i = 1; i < path.length; i++) gesture.moveTo(path[i]);
        return gesture.result;
    }

    /** Watched free seats in runs of at least minAdjacent neighbours in one row (mirror of SeatMatcher). */
    function availableCount(seats, watched, minAdjacent) {
        const rows = new Map();
        for (const seat of seats) {
            if (!watched.has(seat.key) || !seat.free) continue;
            const row = seat.section + '|' + seat.rowKey;
            if (!rows.has(row)) rows.set(row, []);
            rows.get(row).push(seat.position);
        }
        let count = 0;
        for (const positions of rows.values()) {
            positions.sort((a, b) => a - b);
            let run = 1;
            for (let i = 1; i <= positions.length; i++) {
                if (i < positions.length && positions[i] === positions[i - 1] + 1) {
                    run++;
                    continue;
                }
                if (run >= minAdjacent) count += run;
                run = 1;
            }
        }
        return count;
    }

    /** Polish (one, few, many) or English (one, other) plural form, as CinemaWatch.Core Localizer.Plural. */
    function plural(lang, count, forms) {
        if (count === 1) return forms[0];
        if (lang === 'en') return forms[1];
        const lastDigit = count % 10;
        const lastTwo = count % 100;
        return lastDigit >= 2 && lastDigit <= 4 && (lastTwo < 12 || lastTwo > 14) ? forms[1] : forms[2];
    }

    const rules = { seatAt, regularSeatsAlong, regularSeatsInside, toggleSeat, toggleGroup, toggleRow, Gesture, applyGesture, availableCount, plural };

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = rules;
        return;
    }

    // ----- The page -----

    function attach(map) {
        const number = name => parseFloat(map.dataset[name]);
        const width = number('width');
        const height = number('height');
        const offsetX = number('offsetX');
        const offsetY = number('offsetY');
        const inputs = Array.from(map.querySelectorAll('input[name="seat"]'));
        const seats = inputs.map(input => ({
            key: input.value,
            section: input.dataset.section,
            rowKey: input.dataset.row,
            x: parseFloat(input.dataset.x),
            y: parseFloat(input.dataset.y),
            position: parseInt(input.dataset.position, 10),
            wheelchair: input.dataset.wheelchair === '1',
            free: input.parentElement.classList.contains('free'),
            input,
        }));
        const plan = { radius: number('radius'), seats };
        const rowLabels = Array.from(map.querySelectorAll('.row-label'));
        const summary = document.querySelector('[data-seatmap-summary]');
        const watchedCount = document.querySelector('[data-seatmap-watched]');
        const adjacency = document.getElementById('adj');
        const occupancyKnown = map.dataset.occupancy === '1';
        let gesture = null;
        let rectangle = null;
        let swallowClick = false;

        const selection = () => new Set(seats.filter(s => s.input.checked).map(s => s.key));

        function show(keys) {
            for (const seat of seats) seat.input.checked = keys.has(seat.key);
            refresh();
        }

        /** Row numbers (full / partial), the summary line and the tab's count, as the server draws them. */
        function refresh() {
            const keys = selection();
            for (const label of rowLabels) {
                const row = seats.filter(s => s.section === label.dataset.section && s.rowKey === label.dataset.row && !s.wheelchair);
                const watched = row.filter(s => keys.has(s.key)).length;
                label.classList.toggle('full', watched > 0 && watched === row.length);
                label.classList.toggle('partial', watched > 0 && watched < row.length);
            }
            if (watchedCount) watchedCount.textContent = String(keys.size);
            if (!summary) return;
            if (keys.size === 0) {
                summary.textContent = summary.dataset.none;
                return;
            }
            const forms = summary.dataset.forms.split('|');
            const text = keys.size + ' ' + plural(summary.dataset.lang, keys.size, forms);
            const minAdjacent = adjacency ? parseInt(adjacency.value, 10) || 1 : 1;
            const template = !occupancyKnown ? summary.dataset.unknown
                : minAdjacent > 1 ? summary.dataset.adjacent : summary.dataset.template;
            summary.textContent = template
                .replace('{seats}', text)
                .replace('{adj}', String(minAdjacent))
                .replace('{free}', String(availableCount(seats, keys, minAdjacent)));
        }

        /** The pointer's position in plan units (the map scales with the page). */
        function planPoint(event) {
            const box = map.getBoundingClientRect();
            return {
                x: (event.clientX - box.left) / box.width * width + offsetX,
                y: (event.clientY - box.top) / box.height * height + offsetY,
            };
        }

        function drawRectangle() {
            if (!gesture || gesture.kind !== 'rectangle') return;
            if (!rectangle) {
                rectangle = document.createElement('div');
                rectangle.className = 'selection-rectangle';
                map.appendChild(rectangle);
            }
            const left = Math.min(gesture.start.x, gesture.current.x) - offsetX;
            const top = Math.min(gesture.start.y, gesture.current.y) - offsetY;
            rectangle.style.left = (left / width * 100) + '%';
            rectangle.style.top = (top / height * 100) + '%';
            rectangle.style.width = (Math.abs(gesture.current.x - gesture.start.x) / width * 100) + '%';
            rectangle.style.height = (Math.abs(gesture.current.y - gesture.start.y) / height * 100) + '%';
        }

        function end(cancel) {
            if (!gesture) return;
            if (cancel) show(gesture.before);
            gesture = null;
            if (rectangle) {
                rectangle.remove();
                rectangle = null;
            }
        }

        // Gestures with a mouse or a pen; a finger taps (the label toggles its box) and scrolls.
        map.addEventListener('pointerdown', event => {
            if (event.pointerType === 'touch' || event.button !== 0 || event.target.closest('.row-label')) return;
            event.preventDefault();
            end(true);
            gesture = new Gesture(plan, selection(), planPoint(event));
            swallowClick = true; // the click that follows must not toggle the box a second time
            map.setPointerCapture(event.pointerId);
            show(gesture.result);
            drawRectangle();
        });
        map.addEventListener('pointermove', event => {
            if (!gesture) return;
            if (gesture.moveTo(planPoint(event))) show(gesture.result);
            drawRectangle();
        });
        map.addEventListener('pointerup', () => end(false));
        map.addEventListener('pointercancel', () => end(true));
        map.addEventListener('lostpointercapture', () => end(true));
        map.addEventListener('click', event => {
            if (swallowClick && event.target.closest('.seat')) event.preventDefault();
            swallowClick = false;
        }, true);
        document.addEventListener('keydown', event => {
            if (event.key === 'Escape' && gesture) {
                event.preventDefault();
                end(true);
            }
        });

        // Row numbers without a reload (without the script they submit the form).
        for (const label of rowLabels) {
            label.addEventListener('click', event => {
                event.preventDefault();
                show(toggleRow(selection(), plan, label.dataset.section, label.dataset.row));
            });
        }

        // A tap, the keyboard (Space) or a click without the gesture code: the counts follow.
        for (const seat of seats) seat.input.addEventListener('change', refresh);
        if (adjacency) adjacency.addEventListener('change', refresh);
        map.classList.add('scripted');
    }

    function start() {
        document.querySelectorAll('[data-seatmap]').forEach(attach);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();
})(typeof globalThis !== 'undefined' ? globalThis : this);
