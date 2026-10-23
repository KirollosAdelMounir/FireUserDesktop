# Call focus, presentation controls and browser controllers

Implemented October 22, 2026. Browser scope follows the owner's explicit choice: browsers may control supported native desktop shares; browser-hosted screen shares remain view-only.

## Call behavior

Selecting a participant focuses that participant while keeping the other participants visible in a side gallery. Selecting the focused stage again restores the gallery; the Gallery action remains available. Camera-off tiles use the compressed profile picture with an initials fallback.

The native call window rejects ordinary full-screen requests. Full screen is entered explicitly for a focused screen share. Its bottom hover/keyboard-focus toolbar provides exit, microphone, camera, sharing, remote-control request/revoke, chat/members and leave. A pending control approval stays visible. Escape leaves full screen; opening chat/members restores the regular call layout.

## Remote control

| Controller | Shared host | Behavior |
| --- | --- | --- |
| Angular browser | Supported native desktop | Request, explicit sharer approval, mouse/keyboard control, stop |
| Native desktop | Supported native desktop | Existing explicit-consent flow retained |
| Either | Browser screen share | View-only |
| Either | Native window, secondary screen or Wayland portal share | View-only |

The native primary-screen picker now advertises hosting when the source matches the OS input provider's desktop origin and dimensions. The old no-selected-source check prevented that picker path from offering control. OS input permissions remain required. No native input-provider or C ABI changes were needed.

Angular uses the existing reliable `fireuser.native.v1` topic with recipient identities. It trusts the authenticated LiveKit participant, requires the matching native capability/share/scope and a pending request before accepting a grant, and never offers hosting. Grants expire after five minutes; pending requests expire after 30 seconds. Share changes, departure, reconnect/disconnect, revoke and stop clear control. Coordinates account for video letterboxing; key codes, packet sizes, sequences, event rates and outstanding sends are bounded. Blur releases held input. Escape stops browser control. Native host-side consent and validation remain authoritative.

This protocol supports pointer movement, buttons and the existing keyboard subset. It does not add wheel, clipboard, file transfer, unattended control or privileged desktop access. Capability packets are feature negotiation, not proof of which application a participant runs.

LiveKit reference: [data packets](https://docs.livekit.io/transport/data/packets/), [publish options](https://docs.livekit.io/reference/client-sdk-js/types/DataPublishOptions.html).

## Verification

- Desktop Release build: passed, zero warnings/errors.
- Desktop full Release suite with test collections serialized: **200 passed, 0 failed, 4 skipped**. Skips are two Windows-only and two opt-in two-client LiveKit cases. The pre-existing parallel shutdown timing timeout remains outside this change; its isolated test passes.
- Focused call/layout/consent desktop regressions: **30 passed**.
- Angular call-component and native-controller tests: **28 passed** in headless Chromium.
- Angular production build: passed.
- Windows-targeted managed build (`win-x64`, `DesignTimeBuild=true`): passed, zero warnings/errors. This bypasses native target-OS packaging and is not a Windows native build or runtime validation.
- Native headless screenshots verified focused participant thumbnails and bottom full-screen controls. An isolated browser harness used the actual component and controller with synthetic approval packets to verify Request → Controlling → Stop → Request. It never joined a live call or applied OS input.
- Both repositories passed `git diff --check`. Existing environment configuration was preserved.

A packaged Windows run and a real two-person consent/input session still require validation on the target machines. No production deployment, real screen/device capture or OS input injection was performed.

## Microphone join follow-up

Answered direct calls now enable microphone capture in desktop and web. The native shared join path also enables audio when starting or rejoining a direct call. Desktop meeting joins honor the **Join meetings with microphone muted** checkbox, including incoming meeting alerts. Calendar meeting actions first open the meeting controls. Web retains its prejoin microphone checkbox and no longer auto-connects admitted guests with a forced mute. Angular explicitly enables/disables the actual publication, then reads back its microphone state.

| Requirement | Regression evidence |
| --- | --- |
| Answer audio/video with microphone on despite meeting mute preference | `CallMicrophoneJoinTests.AnswerDirectCall_EnablesMicrophoneRegardlessOfMeetingPreference` |
| Meeting choice, regular and incoming paths | `CallMicrophoneJoinTests.MeetingJoin_HonorsMutedChoiceForRegularAndIncomingMeetings` |
| Calendar opens choice before token/capture | `CallMicrophoneJoinTests.CalendarMeeting_OpensMicrophoneChoiceBeforeObtainingMediaToken` |
| Native permission/device error keeps call connected and visibly muted | `CallMicrophoneJoinTests.AnswerMicrophoneFailure_KeepsCallConnectedAndShowsMutedDeviceError` |
| Checkbox binds choice and stays hidden on direct alerts | `CallMicrophoneJoinTests.MeetingMicrophoneCheckbox_BindsChoiceAndIsHiddenForDirectCallAlerts` |
| Capture waits for call connection, then enables audio | `DesktopRegressionTests.AcceptCall_ShowsConnectingBeforeTokenReturns_ThenEnablesMicrophone` |
| Actual Angular publication honors direct-call/default and both meeting choices | `call join microphone` / `sets the actual publication for answered direct call`, `meeting joined muted`, `meeting joined unmuted` |
| Angular permission rejection does not report microphone success | `does not claim that microphone access succeeded when the device rejects it` |
| Admitted guests retain choice before Join | `keeps an admitted guest at prejoin and preserves their microphone choice until Join` |

Microphone verification uses mocked capture; it does not open any real device. Existing environment configuration is preserved.

Follow-up verification: **209 desktop tests passed, 0 failed, 4 skipped** with collections serialized; **24 affected Angular tests passed**; Angular production build passed. Desktop Release compilation and Windows-targeted managed compilation passed (zero warnings/errors). Windows native packaging/runtime and real-device call audio remain unverified on this Linux host.
