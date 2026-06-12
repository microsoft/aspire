// Roguelike keyboard controls — binds WASD and arrow keys to movement actions.
const keyMap = {
    'ArrowUp': 'move-up',
    'ArrowDown': 'move-down',
    'ArrowLeft': 'move-left',
    'ArrowRight': 'move-right',
    'w': 'move-up',
    'W': 'move-up',
    's': 'move-down',
    'S': 'move-down',
    'a': 'move-left',
    'A': 'move-left',
    'd': 'move-right',
    'D': 'move-right',
};

function onKeyDown(e) {
    const action = keyMap[e.key];
    if (!action) {
        return;
    }

    // Don't interfere when the user is typing in an input/textarea.
    if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') {
        return;
    }

    e.preventDefault();

    const button = document.querySelector(`a[data-action="${action}"]`);
    if (button) {
        button.click();
    }
}

let initialized = false;

export function initialize() {
    if (initialized) {
        return;
    }

    document.addEventListener('keydown', onKeyDown);
    initialized = true;
}

export function dispose() {
    if (!initialized) {
        return;
    }

    document.removeEventListener('keydown', onKeyDown);
    initialized = false;
}

initialize();
