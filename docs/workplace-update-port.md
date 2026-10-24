# Frontend workplace updates in the native desktop client

Ported on 22 October 2026. The implementation uses Avalonia 12 and .NET 10. The native LiveKit bridge, P/Invoke contracts, platform capture providers, Windows build scripts, package versions and deployment configuration are unchanged.

## Changed behavior

Administrators can open the new forms from **Calendar → Vacation → Workplace settings**.

- Calendar and call attendee selection use a large native window with search results, selected people, select-all, removal, validated external email addresses and authorized business units. Unit selections resolve their child-unit members through the server. Removing an individual clears the unit selection that would otherwise add them back. Individual and bulk transfers animate across the lists in 240 ms; reduced motion bypasses the transfer, and closing the picker cancels animations.
- Meeting members can invite employees, business units or external email guests. Internal invitation answers now send the server's scoped invitation ID before joining. External invitation links, passwords and admission policy remain server-controlled.
- Vacation requests use inclusive starting and ending dates. The server determines working days, holidays, applicable schedules and balances. Days and balance year are read-only. Readiness errors explain missing assignments or policy setup. Stale previews cannot replace newer date selections.
- Vacation decisions, cancellations and submissions use native detail reviews. Cancellation takes effect immediately and informs the reporting line; the desktop no longer offers cancellation-confirmation approval. Submission messaging allows the server to apply automatic approval when no reporting position exists.
- Native workplace settings support editing policies and qualification rules in place, annual renewal boundaries, organization shift presets, optional unit schedules and ending employee position assignments. Policy identity, revision and rule identity are retained. These forms do not reset employee balances. Current annual accounting and renewal remain backend operations.
- Schedule, vacation, announcement and management mutations require native review. Calendar drafts retain the saved entry ID after meeting setup or guest email failure; retry updates that entry. Closing the editor releases the retry scope so another new schedule cannot silently reuse the previous entry.
- Approval toasts and the notification center expose Approve/Reject or Accept/Decline. Actions fetch the current authorized resource and open a review; old or unauthorized actions show a concrete message. Vacation notifications fetch the specific request, including requests outside the first list page. Calendar notifications use their recorded start date to find the correct calendar period.
- Actual native meeting participation sets **In a meeting**, screen sharing sets **Presenting**, and stopping sharing restores meeting/call status. Leaving restores the prior user status. Messages from the current caller open call chat and suppress the ordinary message toast. Busy/do-not-disturb incoming calls use the existing non-activating Answer/Decline native alert without a ringtone. All incoming calls use one Answer/Decline alert showing the caller’s name and compressed profile picture (initials when unavailable); generic incoming-call and call-history message toasts are suppressed. Repeated events for the same invitation reuse that alert.
- Calendar details include the meeting link. Profile-header typography is aligned. New native forms use the shared light/dark resources and show prerequisite explanations on disabled controls.

## Shared backend behavior

Approved-only vacation calendars, automatic approval without a reporting line, immediate cancellation notifications, annual balance renewal, policy edits preserving existing balances, guest email delivery, and the Mongo `SeenDetails` mapping fix are shared backend behavior and apply to desktop API calls too. The desktop does not maintain a second implementation of these rules.

Approval email HTML remains the usual full-detail Fire User template. Its links request a new browser tab and use the protected web result route. Browser authentication is independent of the native bearer session: the browser asks for sign-in when it lacks a valid session, then returns to the action. Mail clients control whether links open in a tab or a separate browser window. The native client does not intercept those links to bypass web authentication.

The desktop has no application brightness slider or cross-tab brightness synchronization; the frontend-specific fluctuation mechanism is absent. It continues to use native light/dark themes.

Existing broader limits documented in `native-collaboration.md` and `desktop-functional-parity.md` still apply, including the dedicated encrypted meeting-history adapter, native guest entry/admission and other management surfaces. In-call conversation chat remains explicitly named. This update does not establish complete Angular feature parity.

## Requirement evidence

| Behavior | Regression evidence |
| --- | --- |
| Search/selected transfer, filtering and select-all | `WorkplaceUpdateTests.Picker_TransfersSelectedPeopleAndFiltersUnselectedResults` |
| Unit expansion, child members, authority failure and individual removal | `WorkplaceUpdateTests.Picker_UnitExpansionIncludesResolvedDescendantsAndUnselectionClearsUnitGrant` |
| Guest email validation and ordinary-call restriction | `WorkplaceUpdateTests.Picker_EmailValidationRejectsMalformedAndHeaderInputs`; `Picker_EmailsDeduplicateAndCannotBeAddedToOrdinaryCalls` |
| Server-calculated date ranges, setup prerequisites and stale responses | `WorkplaceUpdateTests.VacationRange_UsesServerDaysAndRefusesMissingPrerequisites`; `VacationRange_IgnoresStalePreviewAfterDateChange` |
| Reviewed token and changed-draft rejection | `CollaborationParityTests.VacationSubmissionUsesReviewedToken_AndDraftChangesRequireNewPreview` |
| Call/meeting/share presence and caller chat | `WorkplaceUpdateTests.MeetingPresence_FollowsActualJoinShareAndNativeStopAndRestoresUserStatus`; `CallerMessage_ExpandsChatAndQuietStatusesSuppressRingingPolicy` |
| Scoped incoming meeting accept/decline | `WorkplaceUpdateTests.IncomingMeetingInvitation_AnswersItsScopedInvitationBeforeJoining` |
| Policy identity, rules and review | `WorkplaceUpdateTests.PolicyEdit_PreservesIdentityRevisionAndRulesAndRequiresReview`; `WorkplaceApiUpdateTests.PolicySave_PreservesIdentityVersionAndRulePayload` |
| Day/midday/night boundaries and optional organization schedule | `WorkplaceUpdateTests.WorkingSchedulePresets_KeepDayMiddayAndOvernightBoundaries`; `Management_ReviewsOrganizationScheduleAndAllowsUnitToUseDefault` |
| Position unassignment and fresh revision | `WorkplaceUpdateTests.UnassignPosition_RequiresReviewAndUsesFreshOrganizationRevision`; `WorkplaceApiUpdateTests.UnitAndAssignmentWrites_UseExpectedRevisionAndAllowDefaultSchedule` |
| Notification review, stale decisions and independent toast actions | `WorkplaceUpdateTests.VacationNotification_OpensTargetedRequestAndReviewsBeforeDecision`; `ApprovalToast_HasSeparateActionsAndDispatchesOnlyOnce` |
| Calendar review cancellation and retry identity | `WorkplaceUpdateTests.CalendarReview_CancellationPreventsSaveAndEmailRetryKeepsEntryIdentity` |
| Native views, read-only days and prerequisites | `WorkplaceUpdateTests.NativeForms_RenderReadonlyVacationDaysAndManagementPrerequisites` |
| HTTP dates, preview token, bearer session and authorized invitation routes | `WorkplaceApiUpdateTests.VacationRange_SendsInclusiveEndDateAndServerCalculatedDaysWithReviewedToken`; `MeetingUnitsAndGuestEmail_UseAuthorizedRoutesAndEncodedLocator` |

## Verification

- Native Linux Release build: passed. The final incremental managed build reported zero warnings and errors. A fresh native rebuild emitted existing indentation warnings in unchanged C++ sources.
- Linux C++ bridge ABI/lifecycle CTest: **1 passed**.
- Full desktop suite: **185 passed, 0 failed, 4 skipped**. The two Windows-only tests and two opt-in synthetic LiveKit network cases were skipped on this Linux host.
- Windows-targeted managed build: `dotnet build UserIdentityDesktop.csproj -c Release -r win-x64 -p:DesignTimeBuild=true` passed with zero warnings and errors. `DesignTimeBuild=true` bypasses the target-OS native/media build; this is a managed compatibility check, not a native Windows packaging or runtime test. The existing Windows CI job retains the full native build, tests and publish steps.
- Headless native form rendering and whitespace validation passed.

Commands for the native build and full suite were `dotnet build UserIdentityDesktop.csproj -c Release` and `dotnet test Tests/UserIdentityDesktop.Tests.csproj -c Release`. No Windows runtime, physical camera/microphone/screen capture, real SMTP delivery, external guest session or production SignalR connection was exercised. No deployment or production data/configuration change was made.

## Incoming-call notification correction

The desktop previously displayed the call-history message toast, a generic incoming-call toast and the actionable call window. Only the actionable alert now appears. The server's `callerAvatar` field is retained and rendered; fallback loading uses the compressed-profile endpoint. Late images from an older caller are discarded, and borrowed cached images are not disposed with the alert. Regular chat messages keep their notifications and call-history cards remain in chat.

Evidence: `IncomingCallAlertTests.IncomingCall_ShowsOneActionableAlertWithCallerPhoto_WithoutHistoryOrGenericToasts` (Online, Busy and DoNotDisturb), `CompressedPhotoFallback_IgnoresLateImageFromReplacedCaller`, and `IncomingPayload_PreservesServerCompressedCallerAvatar`.

Call-alert correction verification: five focused regressions passed. Windows-targeted managed compilation passed with zero warnings/errors. The ordinary parallel suite twice hit the existing two-second shutdown-test timeout (`ShutdownTests.ExplicitExit_IsBounded_AndStartsCleanupOnlyOnce`); that test passed alone. With test collections serialized using a temporary xUnit runsettings file, the complete suite passed: **190 passed, 0 failed, 4 skipped**. No shutdown code or timeout was changed.

Vacation form, policy activation, timezone/DST, shared clock formats and speaking tiles were refined in the coordinated October 22 update. See [the backend verification report](../../../Backend/User%20Identity/docs/workplace-ux-refresh.md). Desktop Release tests: 217 passed, 4 skipped. Linux native CTest passed; Windows-targeted managed compilation passed. Windows native runtime and actual audio remain unverified here.
