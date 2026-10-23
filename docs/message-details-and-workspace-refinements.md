# Message details and workspace refinements

Message details now open in an opaque native window with the normal operating-system title bar. The window shows the sender and sent time, readers and their recorded read times, and a separate played-by list for each voice attachment. Names resolve from known people or the authenticated profile endpoint. Legacy receipts without a timestamp explicitly say the time is unavailable. Group reads are never inferred from the aggregate message flag.

Opening details fetches the authenticated message snapshot. SignalR message, read and attachment-play events update the window; a direct-chat read boundary triggers a snapshot because that event does not contain the stored read timestamp. No change-detection polling is added. Closing the window removes subscriptions and cancels pending work. A received voice attachment reports playback after audio output actually starts; downloading, preview preparation and playing one's own note do not mark it played.

The desktop header contains a centered, themed people search that opens the selected direct chat immediately. Profile appearance controls live in a compact submenu. The footer uses the compressed profile picture with initials as fallback. Conversation headers, people and the status menu use actual presence signs; late snapshots cannot overwrite a newer live status. An empty conversation selection does not produce binding errors.

The vacation tab no longer exposes Workplace settings or its dedicated management window. Vacation lists and decisions resolve the requester name rather than displaying the account identifier. Calendar cards and their legend, the upcoming-meetings list and empty states, and workplace notification cards have consistent spacing and theme resources. Message/conversation notices remain in chat rather than appearing in the workplace notification center.

The Angular time-format control uses the existing app dropdown. Its selected label is available before projected options finish rendering, and both 12-hour and 24-hour choices expose their selected state.

## Verification

- Message details: receipt timestamps, legacy and suppressed reads, names, live updates, snapshot races, subscription cleanup, actual playback versus download, clock preferences and both themes.
- Desktop workspace: search selection and measured header centering, compressed avatar, status snapshot race, all presence signs, compact profile controls, requester names, notification filtering, management-view removal and themed workspace rendering.
- Angular: shared dropdown, clock preference and navigator integration tests; production build.
- Platform checks compile the Windows x64 and macOS ARM64 managed targets. They do not validate native packaging, hardware media or running the application on those systems.

Test screenshots use synthetic data in an Avalonia headless renderer. They are not live customer screenshots.

Final checks on 23 October 2026: desktop Release suite **313 passed, 4 skipped, 0 failed**; Angular affected tests **10 passed** and production build succeeded; Windows x64 and macOS ARM64 managed builds each succeeded with **0 warnings and 0 errors**. The platform skips require Windows or the opt-in native two-client media fixture.
