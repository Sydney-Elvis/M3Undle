window.scrollToBottom = (id) => {
    const el = document.getElementById(id);
    if (el) el.scrollTop = el.scrollHeight;
};

// Attaches a scroll listener to the log container.
// Calls dotnetRef.OnScrollPositionChanged(atBottom) whenever the user scrolls.
// Returns a cleanup function (stored by the caller if needed).
window.initLogScroll = (id, dotnetRef) => {
    const el = document.getElementById(id);
    if (!el) return;

    const onScroll = () => {
        const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 50;
        dotnetRef.invokeMethodAsync('OnScrollPositionChanged', atBottom);
    };

    el.addEventListener('scroll', onScroll, { passive: true });
};
