window.scrollToBottom = (id) => {
    const el = document.getElementById(id);
    if (el) el.scrollTop = el.scrollHeight;
};

// Attaches a scroll listener to the log container.
// Calls dotnetRef.OnScrollPositionChanged(atBottom) whenever the user scrolls.
// Returns an object with a dispose() method that removes the listener.
window.initLogScroll = (id, dotnetRef) => {
    const el = document.getElementById(id);
    if (!el) return { dispose: () => {} };

    const onScroll = () => {
        const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 50;
        dotnetRef.invokeMethodAsync('OnScrollPositionChanged', atBottom);
    };

    el.addEventListener('scroll', onScroll, { passive: true });

    return { dispose: () => el.removeEventListener('scroll', onScroll) };
};
