# Desktop attachment transfer toasts

Attachment sends and explicit saves now use a persistent native Fire User toast. Each operation shows uploaded/downloaded file counts, transferred/total bytes, current transfer rate, a progress bar and percentage. Rates scale through B/s, KB/s, MB/s, GB/s, TB/s and larger units using the same 1024 conversion as attachment sizes.

Concurrent operations share a scrollable card, with separate upload and download rows. The card uses the existing theme, orange accent, vector icons, rounded surface and toast entrance motion; reduced motion suppresses entrance and indeterminate animations. It never takes focus. Active transfers do not consume the three ordinary notification slots and take priority when the working area cannot fit all notifications. Ordinary notification expiry and window-close shortcuts cannot dismiss an active transfer. Owner/application shutdown still closes it normally.

## Progress and lifetime

- Uploads appear during encryption/preparation, remain visible while waiting for connection, and count encrypted file-body bytes only after the backend confirms each resumable chunk. Retried bytes are not counted twice. File counts advance after every chunk of that file has been confirmed. Success waits for the server response; bytes reaching the total alone cannot produce 100% or a success message.
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
| Integrated encrypted resumable send succeeds/fails only after backend response | `FullEncryptedUploadWaitsForServerAndPublishesFinalResult` |
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


## Large-transfer recovery (24 October 2026)

The desktop previously sent a complete encrypted multipart batch through the ordinary 100-second authenticated-request deadline. That deadline aborted healthy long uploads and marked the shared action queue offline. Production nginx recorded two aborted multipart submissions at 17:18:03 and 17:20:13 UTC, with no intervening backend restart. This evidence is consistent with the client deadline; nginx alone does not identify the cancellation source.

Desktop uploads now use the existing sender-bound `/api/Chat/Uploads` create/status/chunk/complete endpoints. Each PUT carries at most one MiB of encrypted data; this is a transport chunk size, not a file/batch size limit. Memory use stays bounded while file sizes and offsets use 64-bit values. Progress counts server-confirmed bytes. After a transient chunk failure, status determines whether to resend, skip an already committed chunk, or resume a committed prefix. Recovery stops after three consecutive unconfirmed failures. Session creation and final publication are never automatically replayed, preventing duplicate messages after an uncertain response. Prepared local ciphertext is removed after the operation; restarting the process does not resume its old upload. Abandoned server sessions retain the existing expiry behavior.

A shared request marker exempts attachment operations, including completion and streamed downloads, from the ordinary whole-request deadline. Genuine transport failures still update connection state; HTTP permission/validation failures do not mark the backend offline. Account changes and explicit cancellation still cancel queued/request work. There is no transfer size cap or total-duration cutoff in this client path. Downloads continue writing to a private temporary file and replace the destination only after authenticated decryption succeeds. Backend, nginx and Angular configuration were not changed for this client repair.

| Requirement | Regression evidence |
| --- | --- |
| More than 121 MiB uploads with bounded request memory | `UploadAbove121MiBUsesBoundedRequestsAndOnlyCompletesAfterAllConfirmedBytes` |
| Recover missing, partially committed and fully committed chunk responses | `LostChunkResponseQueriesConfirmedOffsetWithoutDuplicatingBytes` |
| Invalid server session/offset cannot publish | `InvalidSessionOrOffsetNeverPublishes` |
| Permission failure and uncertain completion cannot replay publication | `ForbiddenChunkIsNotRetriedAndLostCompletionIsNeverReplayed` |
| Repeated network failures stop recovery | `RepeatedNetworkFailuresStopAfterBoundedRecoveryWithoutPublishing` |
| Cancellation does not start/retry upload | `CallerCancellationDoesNotCreateOrRetryAnUpload` |
| Transfer duration cannot trigger the ordinary deadline/offline state | `TransferDurationDoesNotTriggerOrdinaryRequestDeadlineOrOfflineState` |
| More than 121 MiB downloaded through authenticated handler beyond ordinary deadline | `LargeDownloadStreamsPastRequestDeadlineWithoutMarkingBackendOffline` |
| Real encrypted multi-file orchestration and final result | `FullEncryptedUploadWaitsForServerAndPublishesFinalResult` |

The large-transfer tests use local prepared files and controlled HTTP transports; they do not publish a test message into a production conversation. Install the refreshed Linux client before retrying the reported upload. Existing Linux Wayland consent behavior is retained; no Windows/macOS packages were published.

Release verification: full desktop suite **354 passed, 4 platform/environment skips (358 total)**. Linux self-contained publication and native bridge dependency checks succeeded. Archive: `/home/kiroadel/Downloads/FireUserConnect-linux-x64-wayland-transfers-20261024.tar.gz`; SHA-256 `ebc9bae90334a174dbc13a4d6e3d25cd3f1f9430f28f4bcc7193a7a29e4e21fe`. Server services remained active and no restart was performed for this client repair.
