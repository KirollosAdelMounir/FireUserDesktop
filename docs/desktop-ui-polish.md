# Native desktop presentation

The desktop uses the shared orange Fire User palette, Outfit typography and native Avalonia controls. Both themes use quiet surfaces, consistent 9–16 px control/card radii, compact vector icons and visible keyboard focus. Expensive media rendering and existing bounded frame delivery remain unchanged.

## Chat

Messages contain text, replies, attachments, reactions and delivery time. They no longer display a toolbar on every bubble. Right-click, the keyboard context-menu gesture, or the small ellipsis opens a native ContextMenu. Reply, copy, edit, reactions, download and message details are contextual. Edit/delete are restricted by the existing message permissions. Delete opens a confirmation dialog; cancelling never sends a delete request. Call history keeps a separate call card and active joining action.

Conversation ellipsis and recipient right-click open profile, files, members and security actions. These open a side panel instead of a large settings flyout. Advanced PIN actions stay behind expandable sections. The sidebar owns people discovery, conversation filtering and account availability; the application header no longer duplicates the search box. Existing profile, image preview, downloads and draft handling remain available.

People-search results open a direct conversation immediately, reuse an existing direct chat, clear filters that would hide it, close the search popup and focus the composer. This action does not fetch profile or organization information. The conversation's name and avatar form one accessible header button that opens the profile (group headers open members).

Profiles now present the authorized organization snapshot as native cards: employer/location, position and level/grade, manager initials/names, and expandable department/reporting hierarchies with connector lines and highlighted current nodes. Backend paths are reversed for root-to-employee display; the owning organization's duplicate root unit is omitted. Both the current user's profile and a colleague's profile use the same view. The overlay adapts to smaller windows and scrolls longer paths. Empty assignments, vacant reporting positions and unavailable manager names have separate states. Rendering hierarchy nodes makes no additional requests. Switching or closing a profile invalidates pending loads so an older response cannot overwrite the visible person.

## Calls and workspace

The call gallery lays out 1–3 columns according to available width and participant count, with compact names, quality and microphone status. Participant and presentation right-click menus offer focus, gallery and screen-share fullscreen. The existing F11/Escape, media teardown, consent and independent minimization behavior is retained. Muted media controls and leaving have explicit red states.

Calendar and announcements open their editors on request and preserve drafts when closed. At narrower widths the editors stack below the main view. Meetings use compact cards with contextual joining. Profile and message detail dialogs follow the same typography and spacing. Incoming call alerts use a borderless themed surface while keeping green Answer/red Decline actions and no focus activation.

## Verification

| Requirement | Regression evidence |
| --- | --- |
| Native message right-click and correct reply target | `ChatPresentationTests.RightClickMessage_OpensNativeMenu_AndReplyTargetsClickedMessage` |
| No permanent message action toolbar; light/dark layout | `ChatPresentationTests.ChatPreview_UsesQuietMessagesAndNativeActionAccess` |
| Edit/delete restrictions and attachment download menu | `ChatPresentationTests.IncomingMessageMenu_RestrictsEditDelete_AndOffersAttachmentDownload` |
| Destructive-action confirmation and cancellation | `ChatPresentationTests.DeleteFromContextMenu_RequiresConfirmation_AndCancelDoesNotDelete` |
| Participant right-click and fullscreen | `ChatPresentationTests.ParticipantRightClick_ProvidesFocusAndFullscreen_WithoutLeavingCall` |
| On-demand editors, retained drafts and compact layout | `ChatPresentationTests.EditorsOpenOnDemand_KeepDrafts_AndStackAtCompactWidths` |
| Large conversation recycling | `WorkspaceTests.MessageHistory_VirtualizesLongConversations` |
| Fullscreen exit/restoration | Existing `CollaborationParityTests` fullscreen cases |
| Search result opens/reuses a direct chat, closes popup, focuses composer and makes no profile request | `ChatProfileTests.SearchResultClick_OpensDirectChatWithoutProfileFetch_AndClosesSearch` |
| Header and own profile show native hierarchy, managers and grades in both themes and compact windows | `ChatProfileTests.ChatHeaderAndOwnProfile_RenderOrganizationAndConnectedReportingHierarchy` |
| Profile switching/closing ignores stale responses | `ChatProfileTests.ProfileSwitch_IgnoresPreviousUserResponse_AndCloseIgnoresPendingResponse` |
| Unassigned, vacant and unavailable directory states | `ChatProfileTests.OrganizationProjection_DistinguishesUnassignedVacantAndUnavailableManagers` |
| Organization HTTP payload retains hierarchy IDs, location, managers and grades with leading zeros | `ApiContractTests.ProfileDirectory_PreservesHierarchyAndGradeMetadataFromServer` |

Synthetic headless screenshots cover both themes, chat menus/drawers, the call gallery, presentations/fullscreen, incoming alerts and each workspace tab. No real camera, microphone, screen capture, network calls or remote input is needed for these tests. Visual polish does not imply completion of the remaining protocol and functional parity backlog in `desktop-functional-parity.md`.

The profile/search follow-up passed the Release suite with **95 passed, 0 failed and 2 skipped** using `dotnet test Tests/UserIdentityDesktop.Tests.csproj -c Release --nologo`. The two skipped tests require an explicitly configured LiveKit test server. Profile render captures use synthetic directory data.

## Repeated requests and empty-state bindings

Catalog snapshots reconcile the calendar selector while retaining its selected person. Intermediate selection changes during reconciliation cannot request another snapshot; a removed reporting person falls back to the current user and fetches that calendar once. Selecting a genuinely different person and SignalR invalidations still refresh data. There is no periodic snapshot polling.

Meeting invitation titles/conflicts, vacation shifts/balances and the selected chat status use null-safe view-model projections, with notifications when the underlying state changes. Hidden panels therefore do not dereference missing reviews. General data commands also stop setting the media progress flag; their own loading state is cleared on completion.

Regression evidence in `CollaborationParityTests`:

- `BoundCalendarSelector_CatalogReloadDoesNotRepeatSnapshotRequests` verifies initial loading, explicit user selection, SignalR updates, retained selection/name updates and idle request counts with a real bound ComboBox.
- `BoundCalendarSelector_RemovedReportFallsBackToOwnCalendarOnce` verifies one fallback request when reporting access disappears.
- `OptionalReviewBindings_NullAndLoadedTransitionsHaveNoBindingErrors` captures Avalonia binding warnings/errors across empty, loaded and cleared review/recipient states, and verifies live recipient status updates.
- `SnapshotCanceledByDisposal_DoesNotBecomeAnErrorOrRetry` verifies shutdown cancellation finishes without an error or another snapshot.

The backend's existing repetitive-traffic lock applies to an endpoint, including requests from other clients. After installing the fix, stop any older desktop instance and allow an existing lock to expire according to the server's `Retry-After` header (the supported repetition lock is 120 or 180 seconds). No rate-limit settings or server state were changed. A debugger's first-chance `TaskCanceledException` alone does not establish a crash; cancelled UI transitions and shutdown requests may be handled normally, and exit code 0 indicates a successful process exit.
