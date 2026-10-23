# Desktop attachment transfer toasts

Attachment sends and explicit saves now use a persistent native Fire User toast. Each operation shows uploaded/downloaded file counts, transferred/total bytes, current transfer rate, a progress bar and percentage. Rates scale through B/s, KB/s, MB/s, GB/s, TB/s and larger units using the same 1024 conversion as attachment sizes.

Concurrent operations share a scrollable card, with separate upload and download rows. The card uses the existing theme, orange accent, vector icons, rounded surface and toast entrance motion; reduced motion suppresses entrance and indeterminate animations. It never takes focus. Active transfers do not consume the three ordinary notification slots and take priority when the working area cannot fit all notifications. Ordinary notification expiry and window-close shortcuts cannot dismiss an active transfer. Owner/application shutdown still closes it normally.

## Progress and lifetime

- Uploads appear during encryption/preparation, remain visible while waiting for connection, and count encrypted file-body bytes only after HTTP writes succeed. Multipart envelope/header bytes are excluded. File counts advance when each body has streamed. Success waits for the server response; bytes reaching the total alone cannot produce 100% or a success message.
- Explicit downloads start after the save picker accepts a destination. Counts advance after authenticated decryption/verification and atomic destination replacement. The response Content-Length supplies the exact total when available, with attachment metadata as a fallback.
- Download All creates one batch, using encrypted metadata to account for authentication tags. It stays active through plaintext verification, ZIP writes and archive finalization. The destination is replaced only after the entire archive succeeds.
- Automatic thumbnails, media playback and gallery previews stay quiet. Their streams still use existing authentication and cancellation. No attachment-size cap or whole-file buffering was introduced.
- Rates use real file-body bytes over a rolling two-second window. Stalls settle to zero. A local 350ms display clock samples the thread-safe tracker; it does not poll the backend or dispatch every byte onto the UI thread.
- Unknown totals show indeterminate progress (static status under reduced motion), rather than an invented percentage. Known totals stay at or below 99% until successful completion. Success remains visible for four seconds, failure for ten seconds; active work has no expiration.
- Sign-out/account changes clear visible transfer details. Stale operation handles cannot repopulate the cleared tracker. The transfer display is in-memory and does not add a persistent retry queue or replay uncertain requests.

## Requirement evidence

All new cases are in `AttachmentTransferTests`.

| Requirement | Evidence |
| --- | --- |
| File count, bytes, rate, percentage, stalls and server confirmation | `RealBytesSpeedStallsAndServerConfirmationRemainDistinct` |
| Streaming upload counts only successful writes | `UploadContentReportsAfterWriteAndCountsOnlyFinishedFiles` |
| Integrated encrypted multipart send succeeds/fails only after backend response | `FullEncryptedUploadWaitsForServerAndPublishesFinalResult` |
| Download wire-byte accounting; automatic preview remains silent | `StreamingDownloadReportsActualBytesAndKeepsPreviewSilent` |
| Failed authenticated decryption preserves destination | `InvalidCiphertextNeverReplacesDestinationOrMarksDownloadSuccessful` |
| No toast on picker cancellation; one ZIP batch with final counts/failure | `ExplicitDownloadStartsAfterPickerAndArchiveUsesOneBatch` |
| Concurrent operations, unknown lengths, failure and account isolation | `ConcurrentUnknownFailuresAndAccountClearDoNotResurrectTransfers` |
| B/KB/MB/GB/TB formatting | `ByteUnitsScale` |
| Persistence beyond ordinary toast lifetime; unknown progress and successful finalization | `TransferOutlivesOrdinarySixSecondLifetimeAndUnknownTotalsStayIndeterminate` |
| Light/dark native cards, both directions, no focus theft/dismiss controls, working-area placement and account-clear closure | `PersistentToastSurvivesNormalNotificationsShowsBothDirectionsAndClearsWithAccount` |

Synthetic light/dark renders are captured with `FIREUSER_TEST_ARTIFACTS`. Native runtime testing is performed on Linux; Windows/macOS checks are managed builds only and do not establish target-OS native runtime behavior.

Verification on 2026-10-23:

- Full Linux desktop run: `dotnet test Tests/UserIdentityDesktop.Tests.csproj -c Release` — 270 passed, 4 skipped, 0 failed. This run includes all 16 new transfer cases.
- Linux Release build passed. Windows x64 and macOS ARM64 managed checks passed with `dotnet build UserIdentityDesktop.csproj -c Release -r <rid> -p:DesignTimeBuild=true` (native packaging intentionally excluded).
- Synthetic dark/light cards rendered and visually inspected. `git diff --check` passed.
