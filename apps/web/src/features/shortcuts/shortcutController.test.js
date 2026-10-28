import { createShortcutController, topShortcutModal } from './shortcutController';

let state, calls, controller;
const visible = (element) => element.isConnected && !element.closest('[hidden],[inert],[aria-hidden="true"]');
function setup(html = '<textarea id="editor"></textarea><button data-tf-shortcut="primary">Submit</button>') {
  document.body.innerHTML = html;
  state = { enabled: true, hints: true, authenticated: true, admin: true, editor: true, minecraft: true, pinned: false };
  calls = { clicks: [], nav: [], tabs: [], help: 0 };
  document.querySelectorAll('button,a').forEach(node => node.addEventListener('click', event => {
    event.preventDefault(); calls.clicks.push(node.id || node.dataset.tfShortcut || node.textContent);
  }));
  controller = createShortcutController({ doc: document, win: window, getState: () => state, visible,
    navigate: href => calls.nav.push(href), openTab: href => calls.tabs.push(href),
    openHelp: () => { calls.help += 1; }, clearPinned: () => { state.pinned = false; } });
}
function press(code, extra = {}, type = 'keydown') {
  const key = ({ AltLeft: 'Alt', AltRight: 'Alt', ControlLeft: 'Control' })[code] || code.replace(/^Key/, '');
  const event = new KeyboardEvent(type, { key, code, bubbles: true, cancelable: true, altKey: true, ...extra });
  (document.activeElement || document.body).dispatchEvent(event);
  return event;
}
function release(code, extra = {}) { return press(code, extra, 'keyup'); }

afterEach(() => { controller?.dispose(); controller = null; document.body.innerHTML = ''; });

test('Alt hints disappear on release without mutating control text', () => {
  setup(); const original = document.body.innerHTML;
  press('AltLeft'); expect(document.documentElement.dataset.tfShortcuts).toBe('on');
  release('AltLeft', { altKey: false }); expect(document.documentElement.dataset.tfShortcuts).toBe('off');
  expect(document.body.innerHTML).toBe(original);
});
test('Alt Enter dispatches one normal click directly from a code textarea', () => {
  setup(); document.getElementById('editor').focus();
  expect(press('Enter').defaultPrevented).toBe(true);
  expect(calls.clicks).toEqual(['primary']);
  expect(document.activeElement.id).toBe('editor');
});
test('held or repeated Enter never sends a second solution', () => {
  setup(); press('Enter'); press('Enter', { repeat: true }); press('Enter');
  expect(calls.clicks).toEqual(['primary']);
});
test('quick key release/repress is also deduplicated', () => {
  setup(); press('Enter'); release('Enter'); press('Enter'); expect(calls.clicks.length).toBe(1);
});
test('ordinary Enter and editing keystrokes remain untouched', () => {
  setup(); expect(press('Enter', { altKey: false }).defaultPrevented).toBe(false);
  expect(press('KeyC', { altKey: false, ctrlKey: true }).defaultPrevented).toBe(false);
  expect(calls.clicks.length).toBe(0);
});
test('disabled submit consumes the chord without sending', () => {
  setup('<button disabled data-tf-shortcut="primary">Submit</button>');
  expect(press('Enter').defaultPrevented).toBe(true); expect(calls.clicks.length).toBe(0);
});
test('a disabled fieldset and aria-busy parent also block submission', () => {
  setup('<fieldset disabled><button data-tf-shortcut="primary">Submit</button></fieldset>');
  press('Enter'); expect(calls.clicks.length).toBe(0);
  controller.dispose(); setup('<div aria-busy="true"><button data-tf-shortcut="primary">Submit</button></div>');
  press('Enter'); expect(calls.clicks.length).toBe(0);
});
test('Ctrl Alt Enter cannot turn a submit into an extra submission', () => {
  setup(); document.querySelector('button').focus(); press('Enter', { ctrlKey: true });
  expect(calls.clicks).toEqual([]); expect(calls.tabs).toEqual([]);
});
test('Ctrl Alt Enter opens a focused same-origin link and preserves the current page', () => {
  setup('<a href="/leaderboard?days=7">Rating</a>'); document.querySelector('a').focus();
  press('Enter', { ctrlKey: true }); expect(calls.tabs).toEqual(['/leaderboard?days=7']);
  expect(calls.clicks).toEqual([]); expect(calls.nav).toEqual([]);
});
test('Ctrl Alt Enter rejects an external or javascript link', () => {
  setup('<a href="https://untrusted.example/">External</a>'); document.querySelector('a').focus();
  press('Enter', { ctrlKey: true }); expect(calls.tabs.length).toBe(0);
});
test('Ctrl Alt L opens navigation in a new tab even when the sidebar is collapsed', () => {
  setup(); press('KeyL', { ctrlKey: true }); expect(calls.tabs).toEqual(['/leaderboard']); expect(calls.nav).toEqual([]);
});
test('Ctrl Alt letter never invokes a mutation button', () => {
  setup('<button data-tf-shortcut="save">Save</button>'); press('KeyW', { ctrlKey: true }); expect(calls.clicks.length).toBe(0);
});
test('Cyrillic input layout uses the physical C key', () => {
  setup(); press('KeyC', { key: 'с' }); expect(calls.nav).toEqual(['/courses']);
});
test('AltGr and the right Alt key do not trigger commands', () => {
  setup(); press('AltRight'); press('KeyC', { ctrlKey: true }); expect(calls.tabs.length).toBe(0);
  release('AltRight', { altKey: false });
  const event = new KeyboardEvent('keydown', { code: 'KeyC', key: 'c', altKey: true, ctrlKey: true, bubbles: true });
  Object.defineProperty(event, 'getModifierState', { value: name => name === 'AltGraph' });
  document.body.dispatchEvent(event); expect(calls.tabs.length).toBe(0); expect(calls.nav.length).toBe(0);
});
test('IME composition never submits', () => {
  setup(); press('Enter', { isComposing: true }); expect(calls.clicks.length).toBe(0);
});
test('browser history and window management chords are left alone', () => {
  setup(); expect(press('ArrowLeft').defaultPrevented).toBe(false); expect(press('F4').defaultPrevented).toBe(false);
});
test('a modal blocks background submission and global navigation', () => {
  setup('<button data-tf-shortcut="primary">Submit</button><div role="dialog" aria-modal="true"><button>Confirm delete</button></div>');
  expect(press('Enter').defaultPrevented).toBe(true); press('KeyC');
  expect(calls.clicks.length).toBe(0); expect(calls.nav.length).toBe(0);
});
test('a legacy modal backdrop blocks background submit too', () => {
  setup('<button data-tf-shortcut="primary">Submit</button><div class="fixed inset-0 bg-black/60"><button>Confirm</button></div>');
  press('Enter'); expect(calls.clicks.length).toBe(0);
});
test('only the foreground modal can receive its own primary action', () => {
  setup('<button id="page" data-tf-shortcut="primary">Submit</button><div role="dialog" style="z-index:50"><button id="lower" data-tf-shortcut="primary">Lower</button></div><div role="dialog" style="z-index:120"><button id="upper" data-tf-shortcut="primary">Upper</button></div>');
  press('Enter'); expect(calls.clicks).toEqual(['upper']);
});
test('nested ARIA panel is the active scope instead of its backdrop', () => {
  setup('<div class="tf-modal-backdrop"><div id="panel" role="dialog" aria-modal="true"><button data-tf-shortcut="close">Close</button></div></div>');
  expect(topShortcutModal(document, visible).id).toBe('panel'); press('KeyX'); expect(calls.clicks).toEqual(['close']);
});
test('hints belong only to the active modal', () => {
  setup('<button data-tf-shortcut="primary">Page</button><div id="panel" role="dialog"><button data-tf-shortcut="close">Close</button></div>');
  press('AltLeft'); expect(document.documentElement.dataset.tfShortcutModal).toBe('true');
  expect(document.getElementById('panel').dataset.tfShortcutScope).toBe('active');
});
test('hidden controls cannot become shortcut targets', () => {
  setup('<div hidden><button data-tf-shortcut="primary">Hidden</button></div>'); press('Enter'); expect(calls.clicks.length).toBe(0);
});
test('ambiguous repeated account actions are not applied to the first row', () => {
  setup('<button id="first" data-tf-shortcut="ignore">Ignore</button><button id="second" data-tf-shortcut="ignore">Ignore</button>');
  press('Minus'); expect(calls.clicks.length).toBe(0);
});
test('focus scopes a repeated action to the selected row', () => {
  setup('<section data-hotkey-context="row"><input id="firstInput" /><button id="first" data-tf-shortcut="ignore">Ignore</button></section><section data-hotkey-context="row"><input id="secondInput" /><button id="second" data-tf-shortcut="ignore">Ignore</button></section>');
  document.getElementById('secondInput').focus(); press('Minus'); expect(calls.clicks).toEqual(['second']);
});
test('an explicit priority resolves duplicated map toolbars', () => {
  setup('<button id="outer" data-tf-shortcut="tab.2">Cards</button><button id="inner" data-tf-shortcut="tab.2" data-tf-shortcut-priority="1">Cards</button>');
  press('Digit2'); expect(calls.clicks).toEqual(['inner']);
});
test('administrator shortcuts remain unavailable to a learner', () => {
  setup(); state.admin = false; press('KeyU', { shiftKey: true }); expect(calls.nav).toEqual([]);
});
test('disabling shortcuts restores native keyboard behavior immediately', () => {
  setup(); state.enabled = false; expect(press('Enter').defaultPrevented).toBe(false); expect(calls.clicks.length).toBe(0);
});
test('hints can be hidden while shortcuts still work', () => {
  setup(); state.hints = false; press('AltLeft'); expect(document.documentElement.dataset.tfShortcuts).toBe('off');
  press('Enter'); expect(calls.clicks).toEqual(['primary']);
});
test('blur clears held modifiers and pinned hints', () => {
  setup(); state.pinned = true; press('AltLeft'); window.dispatchEvent(new Event('blur'));
  expect(document.documentElement.dataset.tfShortcuts).toBe('off'); expect(state.pinned).toBe(false);
});
test('help opens once without a route change', () => {
  setup(); press('Slash'); press('Slash', { repeat: true }); expect(calls.help).toBe(1); expect(calls.nav.length).toBe(0);
});
test('disposing a controller removes its event handlers', () => {
  setup(); controller.dispose(); controller = null; press('Enter'); expect(calls.clicks.length).toBe(0);
});
test('focus shortcuts focus their input without clicking submit', () => {
  setup('<input id="search" data-tf-shortcut="search" data-tf-shortcut-focus="true" />'); press('KeyQ'); expect(document.activeElement.id).toBe('search');
});
test('shell actions work when their menu is closed', () => {
  setup(); let count = 0; state.actions = { notifications: () => { count++; } }; press('KeyJ'); expect(count).toBe(1);
});

test('search labels activate only the associated input', () => {
  setup('<label for="search" data-tf-shortcut="search">Search</label><input id="search" /><button data-tf-shortcut="primary">Submit</button>');
  let clicks = 0;
  document.getElementById('search').addEventListener('click', () => { clicks++; });
  press('KeyQ'); expect(clicks).toBe(1); expect(calls.clicks.length).toBe(0);
});
test('mobile navigation retains individual chords inside its modal', () => {
  setup('<section role="dialog"><a data-tf-shortcut="nav.courses" href="/courses">Courses</a><a data-tf-shortcut="nav.solutions" href="/my/solutions">Solutions</a><button data-tf-shortcut="close">Close</button></section>');
  press('KeyC'); expect(calls.clicks).toEqual(['nav.courses']);
  press('KeyR', { ctrlKey: true }); expect(calls.tabs).toEqual(['/my/solutions']);
  press('KeyX'); expect(calls.clicks).toEqual(['nav.courses', 'close']);
});
test('freshly rendered controls are found without registering new window listeners', () => {
  setup(); document.querySelector('button').remove();
  const next = document.createElement('button'); next.dataset.tfShortcut = 'primary'; let hits = 0;
  next.addEventListener('click', () => { hits++; }); document.body.append(next);
  press('Enter'); expect(hits).toBe(1);
});
test('changing a control to busy blocks later shortcuts while its request runs', () => {
  setup(); document.querySelector('button').addEventListener('click', event => { event.currentTarget.disabled = true; });
  press('Enter'); release('Enter'); press('Enter'); expect(calls.clicks).toEqual(['primary']);
});
test('a modal with a disabled primary never falls through to the background', () => {
  setup('<button id="background" data-tf-shortcut="primary">Submit</button><div role="dialog"><button id="dialog" disabled data-tf-shortcut="primary">Apply</button></div>');
  press('Enter'); expect(calls.clicks).toEqual([]);
});

test('Linux Alt+backquote and Alt+arrows are never intercepted by the site', () => {
  setup('<button data-tf-shortcut="editorMode">Editor</button>');
  expect(press('Backquote').defaultPrevented).toBe(false);
  expect(press('ArrowUp').defaultPrevented).toBe(false);
  expect(press('ArrowDown').defaultPrevented).toBe(false);
  expect(press('Period').defaultPrevented).toBe(false);
  expect(calls.clicks).toEqual([]);
});
test('Alt+E toggles the editor through the existing control without Shift', () => {
  setup('<button data-tf-shortcut="editorMode">Editor</button>');
  const event = press('KeyE');
  expect(event.defaultPrevented).toBe(true);
  expect(calls.clicks).toEqual(['editorMode']);
  expect(press('KeyE', { shiftKey: true }).defaultPrevented).toBe(false);
});
test('Alt+E does nothing when editor permissions are absent', () => {
  setup('<button data-tf-shortcut="editorMode">Editor</button>'); state.editor=false;
  press('KeyE'); expect(calls.clicks).toEqual([]);
});
