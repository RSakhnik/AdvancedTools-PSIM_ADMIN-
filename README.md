# AdvancedTools / BIM Bridge bulk v2

The Pilot desktop extension remains a WPF/.NET Framework 4.7.2 worker. It connects
outbound through the authenticated Pilot session's Custom API. It does not expose
an HTTP server. Existing interactive query operations remain available.

## Query and export operations

Query operations (`get-model-context`, `search-elements`, properties, relations,
spatial data, files and Excel) retain their legacy payloads/results and limits.
`search-elements` additionally accepts PageSize/Cursor when the worker advertises
`paged-search-v2`. Cursors refer to an immutable, disk-backed selection, pinned to
the model version and query. Continue on CursorWorkerInstanceId; cursors expire.

`export-elements` accepts a model/snapshot, ModelPartIds and search conditions,
PropertyNames and IncludeIdentifiers. It reads selection and properties locally,
then writes gzip NDJSON: manifest → element/error records → summary. It does not
round-trip all element IDs through Python. Per-element failures are explicit;
Complete=false must not be treated as a full export.

`export-spatial` accepts the same selection (also explicit ElementIds),
IncludeDescendants, IncludeMeshes, IncludeNormals, IncludeIndices and
IncludeEdgeIndices. It writes BBSPAT2 binary artifacts. Float/index arrays are
binary float32/uint32 blocks; placements/IDs remain metadata. A disk-backed
SQLite queue/deduplication index bounds traversal and mesh-key memory. The
legacy `get-element-spatial-data` query is unchanged.

## Durable delivery

Before extraction, the worker persists a stable TransferId intent and reserves it
on Manager. This permits adoption of a ready local artifact even if the first
Begin request never reached the server and the assignment lease subsequently
expired. The artifact and descriptor are retained outside the temporary workspace.
Reconnect uses BeginOrResume + missing ranges and sends only missing chunks.
Duplicate chunks with matching content succeed; conflicting chunks are errors.

Upload concurrency defaults to three chunks, configurable from one to four.
Buffers belong to each in-flight request/retry and are never mutated after a
timeout. Backoff is exponential with jitter. A timeout does not imply that a
remote request stopped. Artifacts are deleted after confirmed Complete or local
retention expiry. A process lock prevents simultaneous use of the same durable
identity/spool. Fatal integrity/protocol errors stop the worker with diagnostics.

Manager must advertise `artifact-upload-v2` before the worker uses this path.
An older Manager still works for legacy queries through the original uploader;
bulk operations explicitly fail capability negotiation rather than extracting a
large result that cannot be delivered reliably.

## Worker responsibilities and telemetry

`Worker.cs` owns registration, assignment and execution lifetime.
`ResultFileWriter.cs` writes legacy query results; `LegacyResultUploader.cs`
keeps the original upload path. `ArtifactTransfer.cs` owns durable spool and
resumable delivery, while spatial processing remains in `ExportSpatial.cs`,
`SpatialSdkSource.cs` and `BinarySpatialWriter.cs`.
`ModelCatalog.cs` builds and refreshes the model catalog; its partial
`ModelCatalog.Resources.cs` groups storage/search acquisition, warmup, ownership
and release. Pilot SDK calls still use the original single-threaded dispatcher.

`TelemetryMode` defaults to `Basic`; `Off` omits per-task spatial metrics and
`Detailed` adds cache hit/miss counters. The worker sends one profile per
TaskId/AssignmentId with monotonic SDK, encoding, artifact and spool durations.
Process-wide cache size, entries and evictions are copied as a snapshot at task
completion; they must not be summed across tasks. Retry count and delay are
checkpointed only when a transfer retry occurs. A telemetry checkpoint failure
does not invalidate an already prepared artifact.

## New settings (PilotBimBridge/settings.json)

| Setting | Default |
| --- | --- |
| DurableWorkerId | persisted generated GUID |
| MaxInFlightChunks | 3 (1–4) |
| MaxUploadChunkBytes | 4 MiB |
| MaxArtifactBytes | 8 GiB |
| ArtifactDiskQuotaBytes | 16 GiB |
| ArtifactRetentionHours | 24 |
| MaxExportElements | 10,000,000 |
| MaxExportRecordBytes | 16 MiB |
| MaxBulkSpatialNodes | 10,000,000 |
| SpatialScratchQuotaBytes | 2 GiB |
| SelectionDiskQuotaBytes | 1 GiB |
| CursorRetentionMinutes | 60 |

Existing query resource limits retain their original values. Quotas can fail an
export with RESOURCE_LIMIT; result/record byte bounds use RESULT_TOO_LARGE.
SDK reads, search and geometry use one pinned snapshot. A changed model fails
with MODEL_CHANGED; search cannot silently use an older index snapshot.

## Build and tests

Use Visual Studio MSBuild for this legacy project. Override OutputPath and
BaseIntermediateOutputPath to isolated directories when validating. Its original
default OutputPath points into Pilot's development extension directory.

The adjacent Manager integration-test project links the production selection,
cursor, export writer and uploader code and supplies fake SDK environment
interfaces. It covers 100,000-element lazy selection, property filtering/errors,
snapshot changes, pagination, binary geometry, durable transfers and injected
lost responses. No test starts Pilot.

Public protocol copies are checked by the adjacent PSIMpy tool
`tools/sync_contracts.py --check`; Manager's Contracts project is canonical.

## Bounded execution/delivery pipeline

One worker still executes one SDK operation at a time. PreparedArtifactPipeline runs one independent artifact uploader. Durable spool publication precedes Manager BeginOrResume acknowledgement; the SDK executor sends this small acknowledgement independently of the large-upload lane. Only then can the next SDK task be admitted. A late acknowledgement cannot resurrect already completed spool state. Assignment heartbeats remain active until that acknowledgement; afterwards artifact transfer has its own lifetime. Upload/retry delays run outside the SDK execution loop.

Defaults: MaxPreparedArtifacts=2, MaxPreparedArtifactBytes=16 GiB, one uploader, existing MaxInFlightChunks=3. Admission counts the in-flight artifact, reserves MaxArtifactBytes worst-case headroom and obeys ArtifactDiskQuotaBytes. Full queue produces controlled backpressure with worker heartbeats. Pending bytes survive stop/reconnect/restart. Terminal transfers are moved to .failed journals with immutable bytes retained for inspection; retained bytes still count against quota. Retention cleanup remains in force. DurableWorkerId/MachineName/PilotUserId identity and exclusive spool owner lock are preserved.

Diagnostics include SdkBusyMs/SdkIdleMs (execution-slot wall, not pure native SDK active), UploadBusyMs (retry waits excluded), UploadRetryWaitMs, PreparedQueueDepth/Bytes and retained failed deliveries. SDK geometry speed remains the separate TransportProfiler measurement. Build validation must override OutputPath and BaseIntermediateOutputPath so running Pilot DLLs are not replaced.

WorkerProcessingWallMs is measured with Stopwatch around operation execution and optional JSON result serialization, persisted with the artifact and reported separately from delivery. No Manager clock or uploader queue/retry delay enters that duration. Spool-journal publication is outside this operation metric. SDK and upload busy intervals can overlap; their totals must not be added as elapsed wall time.
