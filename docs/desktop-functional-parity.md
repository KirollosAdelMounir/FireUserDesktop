# Desktop functionality follow-up (19 October 2026)

All surfaces remain native Avalonia. This change does not establish complete Angular feature parity.

| Requested behavior | Implemented behavior and evidence | Remaining limits |
| --- | --- | --- |
| Stable navigation | Delayed pointer-exit collapse, preserved keyboard focus and reduced motion | Physical pointer/compositor verification still required |
| Call participants | Room connect snapshot and participant connect/disconnect independent of video; names, initials, microphone state, quality, participant count and local clock | Active-speaker animation, real avatar fetching and full call diagnostics not yet ported |
| Meeting joining | Native invitation review, conflict acknowledgement and acceptance; host starts on token issuance, attendees wait for host; explicit ended/cancelled feedback | Guest entry/admission and recording management remain pending |
| Screen/window choice and Wayland capture | Native source picker; Wayland uses compositor ScreenCast approval and a scoped PipeWire FD through GStreamer; Windows/X11 use selected monitor/window IDs; macOS uses ScreenCaptureKit | System portal/GStreamer dependencies are required on Wayland. Physical compositor capture and Windows/macOS execution remain unverified; remote input still refuses Wayland. See `screen-sharing.md` |
| Focused presentation | First screen frame focuses a full-size stage; Gallery returns to participant cards; Full screen/F11 shows only the presentation with an exit overlay; Escape restores the previous window and side panels, and leaving/unpublishing restores the window | Multiple-share chooser remains pending |
| Call tools | Members, device selection, quality/mute status, duration, direct/group call creation, participant invitation, active call-card joining, current-conversation chat and remote-control consent | In-call chat currently follows the explicitly named current conversation, not a dedicated meeting adapter; Angular RNNoise, echo cancellation, recording and advanced diagnostics remain pending |
| Minimize main app | Independent top-level call window; owner exit cleanup retained | Native window-manager validation beyond headless/Fedora startup is pending |
| Chat actions | Native right-click/ellipsis menus for edit, confirmed delete, reply, copy, five reactions, readable message details and downloads; encrypted attachment save/list, PIN set/disable/recovery/reset and member addition; direct/group authenticated envelopes and dedicated call cards | Resumable upload recovery, encrypted media previews, waveform/player UX and full meeting-history adapter remain pending. Details show aggregate read counts; individual receipt display-name resolution remains pending |
| Organization in profiles | Organization, position, unit hierarchy, reporting path and managers fetched through authorized directory endpoint; same overlay accessible for own profile | Interactive hierarchy navigation and delegations remain pending |
| Calendar behavior | Server-scoped reporting-user calendars; vacation working-shift/balance preview and bound-token submission, paginated requests, approval/rejection/withdrawal/cancellation decisions | Full organization-wide/shared-calendar browsing, amendments, holiday reconciliation and administrative policy editors remain pending |
| Responsiveness | Native loader/initialization and microphone opening off UI thread; pooled HTTP connections; session-bound identity/key reuse; independently applied snapshots; lazy bitmap copying | Production command latency/CPU has not been benchmarked; no claim that every source of the reported 30-second delay is eliminated |
| Angular audio | Exact three frontend audio files; native miniaudio decoder/output; ring lifecycle stopped on participant join/decline/leave | Actual audio devices not exercised; isolated tests disable output |
| Incoming call alert | Separate native topmost alert, no focus activation, green Answer/red Decline, stale call guard and background/minimized-window usability | Compositor decides placement/topmost. OS notification-center action categories for incoming calls are not yet implemented; ordinary system message notifications remain available |

## Verification

Native C++ ABI lifecycle test, .NET Release build, Fedora isolated startup smoke and headless rendered gallery/share/fullscreen/incoming alert captures. The 20 October UI and request-loop verification passed 88 desktop tests with zero failures and two skipped synthetic LiveKit network tests; the Release build had zero warnings/errors and the Linux startup smoke opened the native window. The network tests remain opt-in because no scoped media test server/tokens were supplied. Real camera, microphone, screen capture, remote input, Windows and macOS behavior were not exercised in this follow-up. No production database, account configuration, deployment or user worktree deletion was performed.

Behavioral regression tests are in `CollaborationParityTests`, `WorkspaceTests`, `ApiContractTests` and `EncryptionTests`. The tests verify state and API calls, not successful real multi-party media. Visual captures are synthetic.

| Regression requirement | Exact test |
| --- | --- |
| Audio-only participants and state | `CollaborationParityTests.AudioOnlyParticipantAppears_UpdatesMuteAndQuality_AndLeavesWithoutFrames` |
| Screen stage and track cleanup | `CollaborationParityTests.ScreenShareAutomaticallyFocusesStage_AndTrackRemovalRetainsAudioParticipant` |
| Full-screen button, chrome, Escape and panels | `CollaborationParityTests.ScreenShareFullscreen_HidesChrome_RestoresPanelsAndWindowStateOnEscape` |
| F11 and share ending from maximized state | `CollaborationParityTests.FullscreenShortcut_RequiresScreenShare_AndShareEndRestoresMaximizedWindow` |
| Invitation token/device precondition | `CollaborationParityTests.PendingMeetingInvitation_DoesNotRequestTokenOrStartDevices` |
| Review/acknowledgement/acceptance/join | `CollaborationParityTests.MeetingInvitationRequiresConflictAcknowledgement_ThenJoinsAcceptedMeeting` |
| Slow snapshot independence | `CollaborationParityTests.SlowCalendar_DoesNotHoldBackCallingCapability` |
| Vacation review binding | `CollaborationParityTests.VacationSubmissionUsesReviewedToken_AndDraftChangesRequireNewPreview` |
| Edit/reply/reaction/delete and server changes | `CollaborationParityTests.MessageActionsPreserveStableId_ForwardEditsAndReplies_AndReactToServerChanges` |
| Incoming alert controls | `CollaborationParityTests.IncomingAlertHasAnswerAndDeclineActions_WithoutTakingFocus` |
| Group authenticated envelope | `EncryptionTests.GroupEnvelopeRetainsAuthenticatedConversationAndZeroDirectRecipient` |
| Refuse XWayland fallback and use explicit source selection | `ApiContractTests.Capture_RefusesXWaylandFallbackEvenWhenDisplayExists`; `ScreenSharingTests.SelectedScreen_UsesOnlyItsBounds_AndSelectedWindowUsesItsStableId` |
| Independent minimize and teardown | `WorkspaceTests.CallUsesIndependentWindow_AndClosingItLeavesAndHidesRemoteControlWithoutNativeShare` |

The 22 October workplace update port, native management forms and current verification are documented in [workplace-update-port.md](workplace-update-port.md).
