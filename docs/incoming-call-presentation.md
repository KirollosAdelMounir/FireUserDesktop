# Incoming call presentation

Updated 2026-10-23.

Web users with Busy availability hear the existing ringtone while a compact receiving card appears at the middle-right. The workspace stays visible and interactive. The card follows the desktop layout: compressed caller photo (or initials), caller name, call description, green Answer and red Decline. It replaces the generic incoming action toast, so there is one receiving surface. Do Not Disturb retains the compact card without ringing; ordinary Online calls retain their existing full overlay. Answer/Decline and call-end cleanup use the existing actions and authorization.

The native desktop receiving window is vertically centered within the primary screen working area and inset from the right. Placement accounts for display scaling, working-area offsets and updated card height. Entry uses the shared Toast motion, sliding in from the right, and respects reduced motion. It stays non-activating and usable when the main app is minimized.

| Requirement | Evidence |
| --- | --- |
| Busy rings with a compact card and no full overlay | app-call-notifications.spec.ts: `uses the right receiving surface and ringtone for status 3` |
| DND stays silent; Online retains its full receiving overlay | Same test for statuses 4 and 1 |
| Photo/name, direct Answer/Decline, no duplicate generic toast, ended-call cleanup | The same three tests render the actual AppComponent template and invoke its card buttons |
| Native middle-right placement, scaled and negative coordinates | IncomingCallAlertTests.IncomingAlertIsCenteredVerticallyAndInsetFromRightAtDisplayScale |
| One native alert, caller photo, actions and lifecycle | IncomingCallAlertTests.IncomingCall_ShowsOneActionableAlertWithCallerPhoto_WithoutHistoryOrGenericToasts |

Validation: 4 web tests passed and Angular production build passed. Native focused tests: 7 passed. Final full desktop regression: 247 passed, 4 skipped. An initial full run timed out in the pre-existing bounded-shutdown test; the final full rerun passed without changing shutdown code or assertions. git diff --check passed in both workspaces.

Visual checks used a static synthetic rendering extracted from the actual web card markup, component stylesheet and project theme/font tokens, in light/dark mode. Actual Angular action, state and sound-dispatch behavior is covered by the component tests. Native card rendering and placement were exercised headlessly. No live calls or real sound/device capture were used; Windows/macOS native window-manager behavior was not exercised here.

Artifacts: /tmp/fire-busy-artifacts/web-busy-dark.png, /tmp/fire-busy-artifacts/web-busy-light.png, /tmp/fire-busy-artifacts/incoming-call-right-center.png.
Logs: /tmp/fire-busy-web-tests.log, /tmp/fire-busy-web-build.log, /tmp/fire-busy-desktop-tests.log, /tmp/fire-busy-desktop-full-final.log.

Windows x64 and macOS ARM64 managed build checks also passed with zero errors/warnings (`DesignTimeBuild=true`; native packaging/runtime not exercised). Logs: /tmp/fire-busy-win-build.log and /tmp/fire-busy-mac-build.log.
