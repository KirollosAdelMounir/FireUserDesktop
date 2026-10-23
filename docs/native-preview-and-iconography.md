# Native previews, action icons and chat emojis

Desktop attachment previews now use an opaque, resizable native window with its normal title bar. The full client area belongs to the viewer; there is no translucent dimmer, centered web-style overlay or click-outside dismissal. The title-bar X remains the close action.

Photos have zoom in/out, fit, actual pixel size, rotation in either direction, scroll/drag panning and fullscreen. Rotation changes the scroll extent and fit calculation; it does not rewrite the attachment. Downloads and previous/next attachment navigation remain available. Single attachments omit unused navigation arrows.

Video previews retain play/pause, seeking, elapsed/total time and fullscreen, and now include Stop and a live volume slider. Music previews expose the existing playback-speed control alongside visible seeking, play/pause, Stop, elapsed/total time and volume. Volume is applied to decoded PCM in shared managed code, including video's audio track; it does not change the system volume. Ordinary inline voice-note bubbles keep their compact waveform design. Authenticated media, cancellation, playback shutdown and temporary-file cleanup remain in place.

## Shared action presentation

`DesktopIconography` provides consistent vector icons beside text for buttons (including checkbox/radio labels), menu items and tabs through the shared Fire User styles. Original `Content`/`Header` strings, bindings, commands and command parameters remain intact; Play/Pause changes update both the text and icon. Existing icon panels and explicit custom templates are preserved. Numeric calendar cells, emoji reactions and icon-only compact controls receive no redundant icon. Native tray actions use rendered copies of the same vector assets.

Action-only stack groups without an explicit gap receive eight pixels; wrapping groups default to eight pixels between items and lines. Existing explicit gaps/margins take precedence. Preview toolbars wrap within the available width. New labeled actions automatically use the shared presentation; extend the semantic glyph map or provide a custom icon-and-label panel for specialized actions.

## Chat emojis

The composer smile button opens an offline Unicode picker with search and Faces/Gestures/Symbols categories. Choosing an emoji inserts it at the saved cursor or replaces selected text, preserving the draft's binding and avoiding split UTF-16 surrogate pairs. Picking an emoji does not send a message. The picker closes when the conversation changes or the chat detaches; stale selections cannot modify another conversation. Message text and the composer use Outfit plus the platform's color-emoji font fallbacks. Existing five-reaction actions remain separate.

## Requirement evidence

| Behavior | Test |
| --- | --- |
| Opaque full-client preview, native title bar, no duplicate X, light/dark, photo fullscreen | `AttachmentPreviewControlTests.OpaqueGalleryUsesFullClientAreaAndPhotoControlsInBothThemes` |
| Zoom, rotation, actual size, fit, scroll extent/panning and fullscreen command | `AttachmentPreviewControlTests.PhotosHaveFunctionalZoomRotationFitActualSizeAndScrollNavigation` |
| Visible music/video controls, live volume settings, Stop and safe reattachment | `AttachmentPreviewControlTests.MusicAndVideoExposeVisibleSeekVolumeStopAndPlaybackControls` |
| Audio gain actually changes signed PCM samples, including mute | `AttachmentPreviewControlTests.VolumeAppliesToActualPcmSamples` |
| Native video decoding, fullscreen, navigation and temporary-file cleanup | `ChatAttachmentParityTests.NativeVideoDecoderReadsSyntheticFramesAndCancelsWithoutDeviceCapture`, `GalleryPreviewsImageAndAudioAndFullscreenVideoThenCleansTemporaryFiles` |
| Icons follow bound Play/Pause text and preserve a real clicked command/parameter | `DesktopIconographyTests.BoundActionKeepsLabelCommandAndParameterWhileIconFollowsState` |
| Shared button/checkbox/tab/menu icons, native raster pixels, no duplicated icons | `DesktopIconographyTests.SharedActionsCheckboxTabsAndMenusShowIconsWithoutDuplicatingCustomContent` |
| Explicit templates and content changes remain functional | `DesktopIconographyTests.ExplicitTemplatesStayIntactAndChangingStringToCustomContentRemovesAutomaticTemplate` |
| Measured gaps and explicit spacing preserved | `ChatEmojiAndSpacingTests.ButtonsHaveRealGapsAndExplicitSpacingIsPreserved` |
| Emoji search, categories, empty results and selection | `ChatEmojiAndSpacingTests.EmojiPickerSearchCategoriesEmptyStateAndSelectionAreUsable` |
| Selection replacement, cursor insertion, binding updates and Unicode boundaries | `ChatEmojiAndSpacingTests.EmojiInsertionReplacesSelectionPreservesBindingAndDoesNotSplitSurrogates` |
| Actual composer insertion without sending and close on recipient change | `ChatEmojiAndSpacingTests.ComposerInsertsEmojiWithoutSendingAndClosesPickerWhenConversationChanges` |

Verification uses synthetic media and headless native renders, without capturing devices or playing sound on the user's speakers. Actual native runtime checks are Linux-only; Windows/macOS managed cross-builds exclude native packaging and do not prove target-OS runtime behavior.

Verification on 2026-10-23:

- Focused preview/icon/emoji/chat/voice-note run: **51 passed, 0 failed**.
- Full Linux desktop suite: **286 passed, 4 skipped, 1 failed**. The failure was the previously observed `ShutdownTests.ExplicitExit_IsBounded_AndStartsCleanupOnlyOnce` two-second timeout; neither the shutdown implementation nor its test was changed. This full-suite result is retained rather than hidden by repeating the suite.
- Linux Release build passed. Windows x64 and macOS ARM64 managed checks passed, each with zero errors/warnings, using `-p:DesignTimeBuild=true` to exclude target-native packaging.
- Synthetic light/dark previews, action icons and the color-emoji picker were rendered and visually inspected. `git diff --check` passed.
- The shutdown case passed when run alone (66ms); the full-suite timeout above remains reported.
