window.scrollToId = (id) => {
    const el = document.getElementById(id);
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'center' });
};

window.scrollToClass = (className) => {
    const el = document.querySelector('.' + className);
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'center' });
};

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

// Mapped channels panel: attaches drag-to-resize on the left edge handle.
// Returns a handle with a dispose() method to remove document listeners.
window.initMappingPanelResize = (handleId, panelId, contentId, minWidth, maxWidth) => {
    const handle = document.getElementById(handleId);
    if (!handle) return { dispose: () => {} };

    let dragging = false;

    const onMouseDown = (e) => {
        dragging = true;
        e.preventDefault();
    };

    const onMouseMove = (e) => {
        if (!dragging) return;
        const width = Math.max(minWidth, Math.min(maxWidth, window.innerWidth - e.clientX));
        const panel = document.getElementById(panelId);
        const content = document.getElementById(contentId);
        if (panel) panel.style.width = width + 'px';
        if (content) content.style.marginRight = width + 'px';
    };

    const onMouseUp = (e) => {
        if (!dragging) return;
        dragging = false;
        const width = Math.max(minWidth, Math.min(maxWidth, window.innerWidth - e.clientX));
        try { localStorage.setItem('m3undle:mapping-panel-width', width.toString()); } catch {}
    };

    handle.addEventListener('mousedown', onMouseDown);
    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);

    return {
        dispose: () => {
            handle.removeEventListener('mousedown', onMouseDown);
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
        }
    };
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
