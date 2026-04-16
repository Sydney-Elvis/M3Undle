window.scrollToBottom = (id) => {
    const el = document.getElementById(id);
    if (el) el.scrollTop = el.scrollHeight;
};

window.m3undleCopyText = async (text) => {
    const normalized = typeof text === "string" ? text : String(text ?? "");

    if (navigator.clipboard && window.isSecureContext) {
        try {
            await navigator.clipboard.writeText(normalized);
            return true;
        } catch {
            // Fallback below for browsers/environments that reject clipboard writes.
        }
    }

    try {
        const textarea = document.createElement("textarea");
        textarea.value = normalized;
        textarea.setAttribute("readonly", "");
        textarea.style.position = "fixed";
        textarea.style.top = "-9999px";
        textarea.style.left = "-9999px";
        document.body.appendChild(textarea);
        textarea.focus();
        textarea.select();
        textarea.setSelectionRange(0, textarea.value.length);

        const copied = document.execCommand("copy");
        document.body.removeChild(textarea);
        return copied;
    } catch {
        return false;
    }
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
