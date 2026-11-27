// Brings what a press chose into view (DriveChart.razor: a drive chosen on the chart, its plays
// in the drive log below).
//
// The target is looked for near the control that chose it, not on the whole page: from the
// control's parent outward, the first ancestor holding a match, never past the window (.win) it
// sits in. Two windows of the same kind on the desk each hold their own group for the same drive,
// and the one to scroll is the one in the window that was pressed. Scrolled to its start, so the
// opened group's plays fall below it, and at once under reduced motion — a smooth scroll is motion
// the machine was asked to leave out. A frame later, so the render that opened the group has landed.

export function revealNear(element, selector) {
    requestAnimationFrame(() => {
        let scope = element instanceof Element ? element.parentElement : null;
        let target = null;

        while (scope && !target) {
            target = scope.querySelector(selector);
            if (scope.classList.contains('win')) {
                break;
            }
            scope = scope.parentElement;
        }

        if (!target) {
            return;
        }

        const reduce = window.matchMedia
            ? window.matchMedia('(prefers-reduced-motion: reduce)').matches
            : false;

        target.scrollIntoView({ block: 'start', inline: 'nearest', behavior: reduce ? 'auto' : 'smooth' });
    });
}
