# Remote control text commands

This is the grammar `RemoteCommandParser` understands and the behavior `RemoteControlSession`
gives each command. The parser is deliberately tolerant: filler words are ignored, verbs have
synonyms, and quoted strings are taken literally. Anything the parser cannot place becomes an
`Unknown` command whose `Error` says what was not understood and suggests the nearest form.

## Identifiers

| Thing | Id form | Where it comes from |
| --- | --- | --- |
| Screen (monitor) | `1`, `2`, ... | `screens`; 1 is the primary monitor |
| Window | `w1`, `w2`, ... or the raw native handle (`w` prefix optional for large numbers, e.g. `w133412` or `133412`) | `windows`; aliases are stable for the life of the session: the same window always gets the same `wN` |
| Control | `c1`, `c2`, ... | `controls ...`; aliases are stable for the life of the session and map to the platform runtime id, so `c12` still means the same element after a re-listing |

A window can also be named by **text**: a case-insensitive substring of its title or its process
name (`chrome`, `notepad`, `my report.docx`). A control can be named by its visible **label**,
optionally preceded by a **type** word (`link Next Page`, `button Save`, `text box Email`).

Type words the parser normalizes (left) to platform-neutral control types (right):

- `link`, `hyperlink`, `anchor` -> `hyperlink`
- `button`, `btn` -> `button`
- `text box`, `textbox`, `edit`, `edit box`, `entry`, `box` -> `edit` (single-line: matches only `edit` controls)
- `field`, `input`, `text field`, `edit field` -> `field` (matches `edit` and `combobox` controls)
- `text area`, `textarea`, `text view`, `text editor`, `editor`, `document`, `page` -> `document` (matches `document`
  and `edit` controls). Listings are broader: `edit boxes in w1` also lists documents, after the edits.
- `checkbox`, `check box`, `tick box` -> `checkbox`
- `radio`, `radio button`, `option button` -> `radiobutton`
- `dropdown`, `drop down`, `combo`, `combo box`, `select`, `picker` -> `combobox`
- `menu`, `menu item` -> `menuitem` (`menu` alone also matches `menu`)
- `tab`, `tab item` -> `tabitem`
- `list item`, `item`, `row` -> `listitem` (also matches `dataitem`, `treeitem`)
- `tree item`, `node` -> `treeitem`
- `slider` -> `slider`; `image`, `picture`, `icon` -> `image`; `text`, `label`, `heading` -> `text` (also matches `heading` controls)
- `pane`, `panel`, `group`, `area` -> `pane`/`group`
- `window`, `app`, `application`, `program`, `dialog`, `sheet`, `popup` name a **window**, not a control
  (`click Save in the Save As dialog`)

A trailing lowercase type word wins over a leading one (`the Page 2 link` is the link `Page 2`, `the Text link`
is the link `Text`); a capitalized type word at the start is part of the name (`click Page 2`). Filler at the end of a
name is kept when it follows a capitalized word or has no object (`click Log in`, `the Remember me checkbox`,
`click Show all`), and capitalized words are never dropped as filler (`click Go`).

## Filler

These words are dropped before matching unless they are inside quotes or are part of a control
name that follows a type word: `give`, `me`, `a`, `an`, `the`, `of`, `please`, `and`, `all`,
`their`, `its`, `to`, `on`, `in`, `at`, `for`, `with`, `then`, `now`, `can`, `you`, `could`,
`would`, `go`, `ahead`, `just`, `show`, `get`, `want`, `i`, `need`, `let's`, `lets`.
`all` / `every` / `everything` on a control listing sets `Verbose` (include unnamed and
structural elements) instead of being dropped.

## Commands

Examples on the left; the parsed command on the right.

### Help and session

| Text | Parsed |
| --- | --- |
| `help`, `?`, `commands`, `what can you do` | `Help` |
| `quit`, `exit`, `bye`, `q` | `Quit` |
| `where`, `where is the mouse`, `mouse position`, `what has focus` | `Where` (mouse position, control under it, focused control, foreground window) |
| `wait 2`, `wait 500 ms`, `pause 1.5 seconds`, `sleep 3s` | `Wait` with `Delay` |
| `guard off`, `guard on`, `guard 500 ms`, `guard` | `SetGuard` (`Value` off / on / set / status, `Delay` = idle time required) |

### Screens

| Text | Parsed |
| --- | --- |
| `screens`, `list screens`, `monitors`, `displays`, `how many screens` | `ListDisplays` |
| `screenshot`, `take a screenshot`, `capture the screen`, `grab the screen` | `Screenshot` of `Primary` |
| `screenshot screen 2`, `give me a screenshot of screen 2`, `capture monitor 2`, `screenshot display 2`, `screenshot of the second screen`, `screenshot of screen two`, `screenshot 2` | `Screenshot` `Display`, `DisplayIndex = 2` (`screenshot screen foo` is `Unknown`; a window by number is `screenshot window 2`) |
| `screenshot all screens`, `screenshot everything`, `capture all monitors`, `screenshot the whole desktop` | `Screenshot` `AllDisplays` |
| `screenshot region 100,100 800x600`, `screenshot area 100 100 900 700` | `Screenshot` `Region` (left, top, width x height; or left top right bottom) |

### Windows

| Text | Parsed |
| --- | --- |
| `windows`, `list windows`, `apps`, `list apps`, `give me a list of all running apps and their window ids`, `what is open`, `what's running` | `ListWindows` |
| `find window chrome`, `which window is notepad`, `where is excel` (with a non-mouse noun) | `FindWindow`, `NameText = "chrome"` |
| `focus window w3`, `activate chrome`, `switch to notepad`, `bring w2 to the front`, `go to window w3`, `show window chrome` | `FocusWindow` |
| `screenshot window w3`, `screenshot of window w3 chrome`, `screenshot chrome`, `capture the notepad window`, `screenshot w3` | `Screenshot` `Window`; `w3` -> `Alias`, `chrome` after an id -> `Hint`, `chrome` alone -> `NameText` |
| `give a screenshot of window w3 chrome and the names of all of the controls and their values`, `screenshot window w3 with controls`, `screenshot and controls for chrome` | `Screenshot` `Window` with `IncludeControls = true` |

Window selector rules: a token matching `w\d+` is an alias; a bare integer >= 1000 is a native
handle; otherwise the words after `window`, or the words before `window` (`the chrome window`),
or the word after `in`/`on`/`of` when a window is clearly meant, are the `NameText`. When both an
alias and extra words appear (`window w3 chrome`), the words become `Hint`.
Clarification: `window X in chrome` gives `NameText = X`, `Hint = chrome`; when `X` matches no window the session
tries the hint and says so. `w` followed by a number >= 1000 is a native handle.
Clarification: a window name after `window` or `in`/`on`/`of`/`for` runs to the end or to a stop token (a verb, a
lowercase type word, `and`, `with`, `to`, `=`, `:`, a quoted value, `control`/`element`/`id`, `up to`/`timeout`);
capitalized words are always part of the name (`click Save in Remote Control Page Two`).

### Controls

| Text | Parsed |
| --- | --- |
| `controls`, `list controls`, `controls in window w3`, `controls of chrome`, `what controls are in notepad`, `elements in w3`, `list all the controls and their values in window w3` | `ListControls`; `Verbose` when `all`/`every`/`everything` is present |
| `controls in w3 matching save`, `buttons in chrome`, `links in w3`, `edit boxes in notepad`, `find control Next Page in chrome`, `find link Next Page`, `is there a button called Save in w3` | `FindControl` with `ControlType` and/or `NameText` (plural type words filter by type) |
| `inspect c12`, `describe c12`, `what is c12`, `value of c12`, `read c12`, `what does c12 say` | `Inspect` (re-read the control and print every property) |
| `click c12`, `click on c12`, `press c12`, `tap c12`, `hit c12` | `Click`, `Alias = "c12"` |
| `in the chrome window click on the link Next Page`, `click the Next Page link in chrome`, `click link "Next Page" in window w3`, `click Next Page` | `Click`, `Window = chrome / w3 / (none -> current window)`, `Control = hyperlink 'Next Page'` |
| `double click c4`, `double-click on the Report.docx item`, `open c4` | `Click`, `Clicks = 2` |
| `right click c4`, `right-click on the file`, `context menu on c4` | `Click`, `Button = Right` |
| `middle click c4` | `Click`, `Button = Middle` |
| `click at 100,200`, `click 100 200`, `click at 100,200 on screen 2`, `double click at 50,50`, `right click at 300,400` | `Click` with `Point`; `DisplayIndex` + `PointIsDisplayRelative` when `on screen N` |
| `invoke c12`, `activate c12`, `toggle c7`, `check c7`, `uncheck c7`, `select c9`, `expand c3`, `collapse c3` | `Invoke` with the matching `ControlAction` carried in `Value` (`invoke`, `toggle`, `check`, `uncheck`, `select`, `expand`, `collapse`) |

Control selector rules: `c\d+` is an alias; after `control id`, `control`, `element`, `id` the
next token is `IdText` (an alias, an AutomationId, or a name); a type word followed by words is
`ControlType` + `NameText`; words followed by a type word (`the Next Page link`) are the same;
`"quoted text"` is always the exact `NameText`. `first`/`second`/`third`/`2nd`/`#2`/`number 2`
before a type word sets `Ordinal`. When a window selector is present it is removed from the
control words first, so `click Next Page in chrome` leaves `Next Page` as the name.
Clarification: a trailing type word typed with a capital is part of the name (`click Next Page` names `Next Page`,
not a page). A type is a hard filter in every lookup (click, set, inspect, find, wait for): a control outside
the type's family is never a candidate, and when none of that type matches the result says
`No edit control matching 'Password'` instead of falling back to other types.

### Setting values and typing

| Text | Parsed |
| --- | --- |
| `set c12 to "This is fun!"`, `set c12 = This is fun!`, `set the value of c12 to hello`, `change c12 to hello`, `put "hello" in c12`, `enter hello into c12`, `fill c12 with hello`, `make c12 say hello`, `clear c12` (value "") | `SetValue`, `Control = c12`, `Value = ...` |
| `change window w3 in chrome control id c7 and set the value to "This is fun!"`, `in window w3 set control c7 to "This is fun!"`, `set control id email in chrome to bob@example.com`, `set the Email text box in w3 to bob@example.com`, `type "hello" into the Search field in chrome` | `SetValue`, `Window = w3 / chrome`, `Control = c7 / IdText email / edit 'Email' / edit 'Search'`, `Value = ...` |
| `type hello world`, `type "hello world"`, `type: hello`, `write hello`, `enter text hello` (no `into` target) | `TypeText`, `Value = "hello world"` (types into the focused control) |
| `press enter`, `press ctrl+s`, `hit enter`, `key ctrl shift t`, `keys alt+f4`, `press the escape key`, `hotkey win+r`, `send ctrl+a` | `PressKeys`, `Keys = "ctrl+s"` (normalized: lowercase, `+`-joined, modifiers first in the order `primary`, `ctrl`, `alt`, `shift`, `win`; `control`->`ctrl`, `command`/`cmd`->`win`, `mod`/`cmdorctrl`->`primary`, `return`->`enter`, `esc`->`escape`, `del`->`delete`, `pgdn`->`pagedown`, `windows`->`win`) |
| `press tab 3 times`, `press enter twice`, `press down x5`, `press ctrl+c then ctrl+v`, `press ctrl+c, ctrl+v`, `press ctrl+c ctrl+v` | `PressKeys` with `KeySequence` (every chord in order; `Keys` is the first) and `Repeat` |
| `select all` | `PressKeys`, `Keys = "primary+a"` (`primary` is Control on Windows, Command on macOS) |

After `press`/`hit`/`push`/`key`/`keys`/`hotkey`/`send`, words that start with a key or modifier are keys: a bad chord
(`press ctrl+foo`) is `Unknown` with the key error, never a click. `press` is a click only when the words name a
control (`press the Home button`, `press c12`).

Value rules: the value is the quoted string if there is one; otherwise everything after `to`,
`=`, `:`, `with`, `as`, `say`, or `value` (the last such marker before the end that still leaves a
control selector before it). The value keeps its original casing and inner spacing. A straight quote opens only
at the start of a word (`type 5" monitor` types `5" monitor`), a quote never spans a line, and an unmatched quote is a
literal character. Quotes may be
`"..."`, `'...'`, or `“...”`. `clear X` is `SetValue` with an empty string. The `type` verb is
`SetValue` when an `into`/`in`/`to` target names a control, otherwise `TypeText`.
Clarification: the first `=`, `:` or `to` marker that leaves a control before it wins; `with`/`as`/`say` apply only
when none of those appear and `value` last (`set c12 to play with fire` keeps `play with fire`). For `type`,
`into` always names a target; `in`/`to` only when the target is an alias, `control`/`id`, quoted, or has a type word.

### Waiting for state

| Text | Parsed |
| --- | --- |
| `wait for window "Save As"`, `wait for a window called Save As`, `wait until notepad appears`, `wait for the Export dialog` | `WaitFor` `WindowAppears`, `Window = NameText` |
| `wait until window w5 is gone`, `wait for w5 to close`, `wait until the Save As dialog closes/disappears` | `WaitFor` `WindowGone` |
| `wait for c12`, `wait for the Save button in chrome`, `wait until a button called Done appears in w3` | `WaitFor` `ControlAppears` |
| `wait until c12 is gone`, `wait for c12 to disappear` | `WaitFor` `ControlGone` |
| `wait for c12 value to be "Done"`, `wait until c12 says Done`, `wait until c12 equals Done` | `WaitFor` `ControlValueEquals`, `Value = "Done"` |
| `wait for c12 to change`, `wait until c12 changes` | `WaitFor` `ControlValueChanges` |
| `wait for the Next button to be enabled`, `wait until the Downloads window shows up`, `wait until the Downloads window is open` | A single condition word after `be`/`is` (`enabled`, `gone`, `visible`, `present`, `open`) is the condition, not a value; `shows up` means appears |
| `wait until c12 is enabled` | `WaitFor` `ControlEnabled` |
| any of the above followed by `up to 10 seconds`, `for 10 seconds`, `timeout 10`, `max 2 minutes` | `Delay` = the timeout (default 30 s) |

`wait` with only a duration is `Wait`; `wait for`/`wait until` with a window or control is `WaitFor`.
Clarification: `wait for 2 seconds` (a duration right after `for`) is a plain `Wait`. A quoted control name in `wait for ... appears`
must match a control's name exactly (case-insensitive); it is never a substring match.
The session polls every 75 ms (re-listing windows or re-walking the control tree each time, so it
returns as soon as the state appears) and reports how long the condition took or fails with category
`timeout` and the last observed state.

### Mouse

| Text | Parsed |
| --- | --- |
| `move mouse to 100,200`, `mouse 100 200`, `move the cursor to 100,200 on screen 2`, `hover c12`, `hover over the Save button in w3` | `MoveMouse` with `Point` or `Control` |
| `shift click c3`, `ctrl click c3`, `shift+click c3`, `control-click c3`, `cmd click c3` | `Click` with `Modifiers` (`shift`, `ctrl`, `primary`; `cmd`/`command` mean `primary`) |
| `drag c3 to c4`, `drag c3 to 100,200`, `drag from 10,10 to 50,50`, `drag the Report item to the Archive folder in w3` | `Drag` with `Point`/`Control` (from) and `TargetPoint`/`TargetControl` (to) |
| `scroll down`, `scroll down 5`, `scroll up 3 in chrome`, `scroll down at 500,500`, `scroll c12 down 2`, `wheel down`, `page down in w3` | `Scroll` (`ScrollY` negative = down, positive = up; default 3 notches), optional `Point`/`Control`/`Window` |
| `scroll left 2`, `scroll right` | `Scroll` with `ScrollX` |

## Several commands at once

One call can carry several commands: one per line, or several on a line separated by ` ; ` (a
semicolon with a space on each side). Quoted text is never split, so `set c12 to "a ; b"` is one
step. Blank lines and lines starting with `#` are skipped. Steps run in order and the batch stops at
the first failure; a step that starts with `try ` (for example `try click the Dismiss button`) may
fail without stopping it. `quit` ends the batch. `RemoteControlSession.ExecuteAll` returns one result
per step that ran; `BatchSucceeded` says whether every step ran and every failure was a `try` step.

```
controls in Remote Control Page Two ; set the Email text box to "bob@example.com" ; check the Include attachments checkbox
try click the Dismiss button ; click the Save button ; wait until a text called "Saved" appears up to 5 seconds
```

## Session behavior (`RemoteControlSession`)

- **Current window.** The session remembers the window it last listed controls for, captured,
  focused, or acted in. A control command without a window selector uses it; if there is none it
  uses the foreground window and says so in the result. If the current window has closed, the command fails
  with `window-gone` ("w2 (excel: Book1) has closed") rather than acting in another window.
- **Window aliases** remember the handle, process id and process name. When the OS reuses a handle for a
  different process, the old alias reports `window-gone` and the new window gets a new alias. `window 2` means `w2`.
- **Window resolution.** Alias -> handle from the alias map (error if the window closed). Handle ->
  must exist. Name text -> case-insensitive substring of title or process name; preference: process
  name exact > title exact > title starts-with > process name contains > title contains >
  all words present in title plus process name (`Remote Control Smoke chrome` matches
  `Remote Control Smoke - Google Chrome` in process `chrome`). When the best matches tie, reads and
  actions alike prefer the session's current window, then the foreground window, and add a note
  (`Using w1 (current window); also matching: w4`); a read with neither falls back to z-order, and an
  action with neither refuses and lists the candidates so the user can pick an alias. `Hint` words
  that match nothing about the chosen window produce a warning line, not a refusal.
- **Control resolution.** Alias -> the remembered runtime key; the session re-reads the element
  (`RefreshControl`) and fails clearly if it is gone. An alias is bound to the window it was listed in and is never
  moved: `click c1 in w2` when `c1` lives in `w1` is refused. `where` gives no alias to a control whose window is
  unknown. `IdText` -> alias, then exact AutomationId,
  then name. An alias lookup never walks the tree (it re-reads that one control), and verification after an action
  re-reads only the target. Name text -> within the window's current control list, reused for
  `ListingFreshness` (default 10 s). A miss, or no fresh list, walks once with the selector's type family
  (`ControlQuery.ControlTypes`) and most selective name word (`NameContains`) as filters so the adapter can skip
  everything else; only a bare name without a type that still misses gets one unfiltered walk. `wait for` re-walks
  on every poll with the same filters and the time left, and waits at least as long as the previous walk took
  before polling again. `LastExecutionStats` reports walks, walk time and re-reads (the host prints
  `[walks: 2, 1830 ms]` with `--timing`): exact name > starts-with >
  contains > all words present, case-insensitive, filtered by `ControlType` when given (with the
  type families above: `listitem` also accepts `dataitem`/`treeitem`, `edit` accepts `document`
  for rich text boxes, `button` accepts `splitbutton`/`menubutton`). Disabled or offscreen
  controls lose ties. Equally good matches are tie-broken for reads and actions alike (web pages repeat
  names): on-screen and enabled with non-empty bounds, then keyboard focus, then the largest area, then
  document order, with a note `Using c12; also matching: c7`. An action refuses with `ambiguous` only when
  the tied candidates differ in type, or a non-exact selector matched different names (`Sav` matching
  `Save` and `Save As...`); an exact name always beats a partial one.
- **Click.** Focus the window (fail with the OS reason if refused), re-read the control, scroll it
  into view if offscreen and the platform can, then hit-test its center (`GetControlAt`): the element there must
  be the target, inside it, or its ancestor in the same window. If something else covers it (a popup), a plain
  left click falls back to the Invoke pattern with a note naming the cover; otherwise it fails with `os-refused`
  and nothing is clicked. Then move the mouse to the center and click physically with the requested button,
  count and modifiers. If the control has no on-screen bounds, fall back to the Invoke/Toggle/Select pattern and
  say which method was used. The result reports the control, the point clicked, and the method.
- **Set value.** Prefer the platform `SetValue` action; verify by re-reading the control; if the
  value did not take (or the control has no value pattern) fall back to: focus window, click the
  control, select all (`primary+a`), type the text (only for the `edit` family and comboboxes; anything else,
  such as a pane or a button, fails with `unsupported` naming its type and supported actions); re-read and
  report the value now shown. A value the app reformats (`5` shown as `5.00`) is success, not a reason to
  retype: the outcome is `set; control shows "5.00"` with a note. Passwords are
  set the same way; the session never echoes a value into logs beyond the result text the user
  asked for. Read-only controls fail with a clear message.
- **Screenshots** are written as PNG to `RemoteControlSessionOptions.CaptureDirectory` with a name
  like `screen2-20261024-153012-345.png` / `w3-chrome-....png`, and the result text gives the full
  path, pixel size, and the screen rectangle it covers. The directory is bounded with
  only this tool's own file names (`screenN-`, `wN-process-`, `all-screens-`, `region-` followed by
  `yyyyMMdd-HHmmss-fff`): the newest `MaxRetainedCaptures` are kept and nothing younger than `CaptureMaxAge` is
  removed, each time a capture is written. Other files in the folder are never touched. An unwritable folder
  fails with `permission`.
- **Listings** are plain text tables, one row per item, aligned columns, no truncation of names
  or values beyond a single-line rendering (newlines become `⏎`). Window rows: alias, handle,
  process, title, screen, bounds, state (`foreground`, `minimized`, `elevated`). Control rows:
  alias, type, name, value, automation id, bounds, flags (`disabled`, `offscreen`, `focused`,
  `readonly`, `password`), supported actions. Indentation by depth is shown as leading dots. A password
  field's value never appears in text or `Data` (listings, `inspect`, `where`, receipts, and `type` into a
  password field). `inspect` prints the value first. For agents, `ListingMaxRows` and `ListingNameMaxLength`
  (default 0 = unlimited) make the text compact: interactive controls (edits, buttons, links, check boxes, radio
  buttons, combo boxes, list/tree/tab/menu items, sliders) are kept first, long names and values end with `…`, and
  a last line says `N more controls not shown; narrow with a type word or \`matching <text>\``. `Data` always has
  the full list.
- **Embedded screenshots.** With `EmbedScreenshotBase64`, a `ScreenshotScaler` (given the PNG and
  `EmbeddedImageMaxSide`, default 1568) supplies the embedded image: `PngBase64` then holds the scaled bytes and
  `ImageMime`, `ImageWidth`, `ImageHeight`, `ImageScale` describe it, while `Width`/`Height` and `Path` stay the full
  capture. Without a scaler the full PNG is embedded.
- **Unknown** commands return `Succeeded = false` with the parser's error and `help` hint.
- **User activity.** Before an action that takes focus or sends input (focus window, click, the typing
  fallback of `set`, `type`, `press`, scroll, mouse moves) the session checks how long the person has been idle.
  If they used the keyboard or mouse within `UserIdleRequired` (default 350 ms; zero disables) it waits, polling
  every 50 ms, up to `UserIdleWaitLimit` (default 1.5 s; the first command of a batch, or a single command, waits
  that long a second time), then fails with `user-active`: "The person is using the keyboard or mouse. Wait a few
  seconds and retry the same step; do not re-plan." plus the observed idle time. Reads and pattern actions (value
  pattern, invoke, toggle) send no input and are not held. A platform that cannot report idle time never waits.
  `guard off`, `guard on` and `guard 500 ms` (`SetGuard`; a bare number is milliseconds, `guard` alone shows the
  setting) change it for the session, for a person who is deliberately watching; the host takes `--guard <ms>` and
  `--no-guard`.
- **Failure categories.** Every failed result sets `FailureCategory` to one of the `FailureCategories`
  constants (`control-gone`, `window-gone`, `target-elevated`, `app-busy`, `authentication-required`,
  `permission`, `os-refused`, `unsupported`, `ambiguous`, `not-found`, `not-understood`, `timeout`), taken
  from `DesktopAutomationException.Category` when the platform raised it, (a refused `FocusWindow` on an elevated
  window reports `target-elevated`, otherwise `os-refused`; a bad key chord is `not-understood`), so a person or an agent can pick
  the right recovery: observe again, run elevated, wait, hand to a human, grant a permission, or rephrase.
- **Ensure semantics.** `check`/`uncheck` and `set ... to` first read the current state and do nothing when
  it already matches, so a retry never flips a setting back. The result says `already on` / `unchanged`. A
  control that does not report its checked state cannot be ensured: `check`/`uncheck` fail with `unsupported`
  ("use toggle").
- **Verification.** Every action re-reads the target afterwards and reports the observed state (the value
  now shown, the toggle state, whether the window is now in the foreground). Accessibility trees lag input
  by a few milliseconds, so an action that expects a change (set value, check/uncheck, toggle, select, focus
  window) re-reads every 40 ms for up to 1 s until the change shows, before choosing between `verified`, the
  keyboard fallback, or a mismatch; the receipt notes how long that took (`State observed after 80 ms.`). An issued action that cannot
  be verified says so (`issued, not verified`) rather than claiming success. Outcomes are `verified`, `unchanged`,
  `issued, not verified`, or for a reformatted value `set; control shows "..."`. Clicks, key presses, scrolls and
  drags are always `issued, not verified`, with the observed state after them.
- **Wait-for walks** re-walk the window on every poll with the time left as the walk's `Timeout` and the name
  (and a type without a family) as filters. When a miss follows a truncated walk the state says
  `not reached (walk truncated: reason)` instead of not-found.
- **No silent caps.** `wait` and scroll amounts are taken as given; a very long wait or a scroll of 1000 or more
  notches adds a note.
- Every result also carries structured `Data` so a JSON host can return the same information.
