# Desktop chat previews and attachments

Implemented and verified 2026-10-23.

The conversation list opens and verifies FUCHAT1 previews locally. Text is shown directly; attachment-only messages show Attachments and a paperclip count; mixed messages show text with the paperclip count. A locked or unverifiable preview displays an unlock hint, without exposing ciphertext. Unlocking refreshes conversation summaries. Preview validation binds signed content to the current account and conversation.

Attachments use the Angular layout: up to four rotated cards, a total count badge, then the message text bubble. Clicking a stack or single media card opens an owned native gallery. Previous/Next and arrow keys navigate one attachment at a time. Photos render inside the application, sound files use the existing native player, and video plays through packaged FFmpeg and native audio. Video supports play/pause, seeking, fullscreen and explicit fullscreen exit; Escape exits fullscreen before closing the gallery. Unsupported files retain a visible download action. Shared-files entries use the same gallery.

Right-clicking a message shows five reaction buttons in one horizontal row at the top: Like, Love, Laugh, Sad and Angry. Download All saves all attachments as one local ZIP with safe, distinct filenames. Downloads authenticate encrypted frames before publication; incomplete archives do not overwrite an existing destination. Downloaded files are saved without automatically launching them. Preview downloads can be cancelled when navigating or closing, temporary plaintext is cleaned up, and Unix temporary files are owner-readable/writable. Existing streamed upload/download behavior and attachment size policy remain unchanged.

Sent bubbles use the Fire User orange gradient with white text and a lower-right tail. Received bubbles use the themed card surface and border with a lower-left tail. Attachments, reactions and timestamp/read receipts sit outside the text bubble, matching the web arrangement.

## Requirement coverage

| Requirement | Exact test evidence |
| --- | --- |
| Verified text, attachment-only and mixed sidebar previews | ChatAttachmentParityTests.SidebarShowsVerifiedTextAndAttachmentCounts (three cases) |
| Preview confidentiality and conversation binding | ChatAttachmentParityTests.PreviewRejectsAnotherConversationOrAudience; EncryptionTests.DecryptsAngularJoseVector_AndRejectsSpoofedSenderAndTampering |
| Native stacked attachment entry and sequential navigation | ChatAttachmentParityTests.StackedAttachmentsOpenOwnedGalleryAndNavigateWithoutDownloadingDocuments |
| Photo/audio/video preview, fullscreen/exit and cleanup | ChatAttachmentParityTests.GalleryPreviewsImageAndAudioAndFullscreenVideoThenCleansTemporaryFiles |
| Native video decoding and process cancellation | ChatAttachmentParityTests.NativeVideoDecoderReadsSyntheticFramesAndCancelsWithoutDeviceCapture |
| Cancelled preview cleanup | ChatAttachmentParityTests.CancelledPreviewDeletesItsTemporaryPlaintext |
| Download All ZIP integrity, duplicate names and failed-download preservation | ChatAttachmentParityTests.DownloadAllCreatesVerifiedZipWithSafeDistinctNamesAndPreservesDestinationOnFailure |
| Direct row of five reactions and direct Download All action | ChatAttachmentParityTests.ReactionsAreFiveButtonsAndDownloadAllIsDirect; ChatPresentationTests.RightClickMessage_OpensNativeMenu_AndReplyTargetsClickedMessage |
| Gradient sent/bordered received bubbles, tail geometry and both themes | ChatPresentationTests.ChatPreview_UsesQuietMessagesAndNativeActionAccess (light/dark) |

## Verification

- `dotnet test Tests/UserIdentityDesktop.Tests.csproj -c Release`: **242 passed, 4 skipped**. An initial run timed out launching the isolated D-Bus portal fixture; the final full run passed without modifying portal code or its assertions.
- Final focused chat, presentation, encryption and voice suites: **39 passed**.
- Linux native desktop and test builds passed. Windows x64 and macOS ARM64 managed cross-target builds passed with no errors or warnings using `-p:DesignTimeBuild=true`. Those checks bypass native packaging; they do not certify native Windows/macOS playback or installers.
- Synthetic FFmpeg video decoding, headless gallery playback/navigation and light/dark rendering were exercised. No real camera, screen, microphone or OS remote-control input was used.
- `git diff --check` passed. No backend or Angular changes were required for this follow-up, and no production emails/data or deployment settings were changed.

Logs: `/tmp/fire-chat-full-final.log`, `/tmp/fire-chat-final-focused.log`, `/tmp/fire-chat-win-build.log`, `/tmp/fire-chat-mac-build.log`.

Synthetic native previews: `/tmp/fire-chat-artifacts/chat-dark.png`, `/tmp/fire-chat-artifacts/chat-light.png`, `/tmp/fire-chat-artifacts/chat-message-context.png`, `/tmp/fire-chat-artifacts/chat-attachment-gallery.png`.

## Opening-chat cancellation follow-up

Opening a conversation starts history/presence/read-receipt work and authenticated attachment previews. Replaced or detached preview controls intentionally cancel their requests. A debugger configured to break at throw time may stop in .NET socket code before the owning UI catches that cancellation; the exception text alone does not establish an unhandled application crash.

Group read-receipt tasks are now observed. Intentional cancellation completes quietly; actual failures are shown only for the active conversation. Unexpected HTTP transport cancellation is classified as an uncertain connection failure and activates reconnect status. Caller/account cancellation retains cancellation semantics and does not mark a healthy connection offline. Uncertain requests are never automatically replayed.

Regression evidence: BackendActionQueueTests.TransportCancellationIsReportedAsUncertainConnectionFailureWithoutReplay, BackendActionQueueTests.CallerCancellationRemainsCancellationAndDoesNotMarkTheConnectionOffline, ChatAttachmentParityTests.OpeningGroupChatObservesCancelledReadReceiptsAndReportsRealFailuresOnlyForActiveConversation. Focused result: 18 passed. The exact original request still requires the first Fire User file/line in the debugger Call Stack.

Cancellation follow-up full regression: **245 passed, 4 skipped**, no failures (`/tmp/fire-chat-cancel-full.log`).
