# Choosing browser and desktop tools

Use browser tools for pages in a browser connected to the host when DOM reads and
actions cover the task. Use `desktop.*` for native windows, system dialogs, file
pickers, or a browser window without a browser connection. Prefer named controls
when available; use pixel interaction for surfaces that expose no readable controls.

The tool families can be combined within one task, but their identifiers cannot:
browser tab/observation IDs and desktop window/control aliases belong to their
respective sessions.

The shared RemoteControl projects provide parsing, session contracts, Windows and
macOS adapters, agent descriptors and learned-procedure replay. Hosts supply
authorization, settings, capture directories, credentials and procedure storage.
Credential slots carry names rather than secret values in recorded procedures.

Windows CI runs the portable command, procedure and macOS mapping tests. It does
not establish native macOS Accessibility, screen capture, Keychain, signing or
installer acceptance. Run platform-specific acceptance separately.
