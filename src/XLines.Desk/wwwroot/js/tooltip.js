// The Desk's tooltip without MudBlazor (ADR 0034, step 2).
//
// DeskTooltip draws its body as a popover element beside the trigger, inside one wrapper marked
// data-desk-tip. This file raises it: on hover and on keyboard focus, after a pause so a passing
// pointer does not flash it, and one at a time. It is delegated on the document, so it is loaded
// once (DeskScripts) and a grid of a thousand tooltips costs no listener and no call to the server.
//
// Where the tooltip sits is the stylesheet's business (controls.css, .desk-tooltip): the open
// trigger is named as the anchor here, and CSS anchor positioning does the rest.
(() => {
    const PAUSE = 300;
    const ANCHOR = '--desk-tip';

    let open = null;
    let pending = null;
    let timer = 0;

    const hostOf = node => node instanceof Element ? node.closest('[data-desk-tip]') : null;
    const tipOf = host => host.querySelector(':scope > .desk-tooltip');

    function hide() {
        clearTimeout(timer);
        pending = null;
        if (!open) return;

        const tip = tipOf(open);
        open.style.removeProperty('anchor-name');
        if (tip && tip.matches(':popover-open')) tip.hidePopover();
        open = null;
    }

    function show(host) {
        if (open === host) return;
        hide();

        const tip = tipOf(host);
        if (!tip || !host.isConnected) return;

        host.style.setProperty('anchor-name', ANCHOR);
        tip.showPopover();
        open = host;
    }

    function ask(host) {
        if (open === host || pending === host) return;
        clearTimeout(timer);
        pending = host;
        timer = setTimeout(() => { pending = null; show(host); }, PAUSE);
    }

    function leave(event) {
        const host = hostOf(event.target);
        if (!host || (pending !== host && open !== host)) return;
        // Moving between the trigger's own parts is not leaving it.
        if (event.relatedTarget instanceof Node && host.contains(event.relatedTarget)) return;
        hide();
    }

    document.addEventListener('pointerover', event => {
        // A finger has no hover: a touch that lands here is a press, and the press is the answer.
        if (event.pointerType === 'touch') return;
        const host = hostOf(event.target);
        if (host) ask(host);
    });
    document.addEventListener('pointerout', leave);

    document.addEventListener('focusin', event => {
        const host = hostOf(event.target);
        // The keyboard's focus only: a control focused by a click is already under the pointer.
        if (host && event.target.matches(':focus-visible')) ask(host);
    });
    document.addEventListener('focusout', leave);

    // A tooltip explains; it never stays in the way of the thing it explains.
    document.addEventListener('pointerdown', hide, true);
    document.addEventListener('scroll', hide, true);
    document.addEventListener('keydown', event => { if (event.key === 'Escape') hide(); }, true);
})();
