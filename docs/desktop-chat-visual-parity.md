# Desktop chat attachment visual parity

Updated 2026-10-23 against the supplied web screenshots and the Angular message/gallery templates and styles.

- Short message bubbles size to their text and align to the sender side independently of attachment width. Sent messages retain the orange gradient, compact padding, lower-right tail and shadow; received messages retain their themed card surface.
- Multiple attachments use the compact 230×190 stack, the same four rotations/translations as the web, rounded clipped photos, outer shadows and a total badge. Summary text identifies photos, videos, audio files or mixed attachments.
- Opening a stack displays a themed thumbnail overview with filenames, sizes, media actions and Download All. Selecting an attachment opens the dark media design with filename, vector navigation, position count, download and native playback/fullscreen controls. Back/Escape can return to the overview.
- As subsequently requested, this remains a separate native window with its own title bar. Its body has no duplicate Close/X button. Message-details and workplace-management dialogs and the calendar/announcement editor headers also have no duplicate in-content Close/X controls. Borderless notification dismiss controls and embedded-panel controls retain their function.
- Navigation, media-kind, download and archive glyphs use shared Avalonia vector resources. No browser/WebView or new media runtime was added. Existing authenticated downloads, cancellation, ZIP integrity, frame verification and media cleanup remain active.

| Requirement | Evidence |
| --- | --- |
| Compact orange own-message bubble and photo caption | ChatAttachmentParityTests.OwnPhotoBundleUsesContentSizedOrangeBubbleAndWebGalleryDesign (light/dark); actual incoming event for current sender, rendered width/alignment/brush assertions |
| Media-kind and mixed bundle summaries | ChatAttachmentParityTests.BundleSummaryNamesTheMediaKind (four cases) |
| Web-like thumbnail overview, Download All and numbered media view | ChatAttachmentParityTests.OwnPhotoBundleUsesContentSizedOrangeBubbleAndWebGalleryDesign |
| Gallery image/audio/video/fullscreen/cleanup retained | ChatAttachmentParityTests.GalleryPreviewsImageAndAudioAndFullscreenVideoThenCleansTemporaryFiles |
| Separate gallery window has a title bar and no duplicate Close/X | ChatAttachmentParityTests.OwnPhotoBundleUsesContentSizedOrangeBubbleAndWebGalleryDesign |
| Message details use title-bar close | ChatAttachmentParityTests.MessageDetailsUseTitleBarWithoutAnExtraCloseButton |
| Editor windows have no duplicate header close controls | WorkspaceInteractionTests.AllCreationSurfaces_OpenOwnedWindows_AndShowSuccessAfterSavingAndRefresh (light/dark) |

Synthetic native screenshots were rendered and inspected in both themes. Headless captures show window client content; they do not capture the platform title bar. Native decoration properties are asserted separately. Actual Windows/macOS window-manager behavior has not been exercised in this Linux workspace. No live files, calls or production data were used.

Artifacts: /tmp/fire-gallery-artifacts/desktop-photo-bundle-web-parity-dark.png, /tmp/fire-gallery-artifacts/desktop-attachment-overview-web-parity-dark.png, /tmp/fire-gallery-artifacts/desktop-media-overlay-web-parity-dark.png (matching light variants available).

Verification: final focused UI/creation/shutdown selection **37 passed, 1 skipped**. Final full suite: **253 passed, 4 skipped, 1 failure** in the pre-existing timing-sensitive ShutdownTests.ExplicitExit_IsBounded_AndStartsCleanupOnlyOnce. That test passed in the focused rerun; its code/assertions were not changed. Earlier runs also exposed an intermittent isolated D-Bus fixture startup timeout. The actual duplicate editor-header controls found by new assertions were removed, then the full suite was rerun. Windows x64 and macOS ARM64 managed builds passed with zero warnings/errors using DesignTimeBuild=true, which bypasses native packaging. git diff --check passed.

Logs: /tmp/fire-gallery-final-focused.log, /tmp/fire-gallery-final-full-2.log, /tmp/fire-gallery-win-build.log, /tmp/fire-gallery-mac-build.log. No backend or web changes, deployment, production data mutations or real device capture were required.

## Current native preview behavior

The later desktop request replaces the translucent media overlay described above with an opaque native full-client viewer, while preserving native title-bar closing. See [native previews and iconography](native-preview-and-iconography.md) for current photo/music/video controls, shared labeled-action icons, spacing, emoji picker and regression evidence.
