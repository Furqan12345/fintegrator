// Scroll helpers for Pages/Logs.razor. Loaded as an ES module via dynamic import, so it
// needs no <script> tag in index.html.

const PIN_THRESHOLD_PX = 48;

// True while the viewport is parked at (or very near) the bottom of the console. Used to
// decide whether the live tail should keep following — it disengages as soon as the user
// scrolls up to read something.
export function isPinnedToBottom(element) {
    if (!element) {
        return true;
    }

    return element.scrollHeight - element.scrollTop - element.clientHeight <= PIN_THRESHOLD_PX;
}

export function scrollToBottom(element) {
    if (element) {
        element.scrollTop = element.scrollHeight;
    }
}
