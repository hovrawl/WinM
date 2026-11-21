# Window preview capture profiling

`WindowPreviewCapture` writes newline-delimited JSON diagnostics while the carousel captures owned snapshots. By default, the recording is `%LOCALAPPDATA%\HomeDeskManager\preview-capture.jsonl`; set `HOMEDESK_PREVIEW_CAPTURE_LOG` before starting `HomeDeskManager` to choose another path. The current file is capped at approximately 4 MiB, with the previous file retained as `.1`.

Each `capture` record includes a batch ID, opaque window ID, retry attempt, outcome, and elapsed times:

- `queueMs`: elapsed time from the opening batch start until the attempt begins.
- `deviceSetupMs`: shared D3D device initialization or retrieval; the first attempt includes lazy device creation, later attempts should be near zero.
- `sessionSetupMs`: capture-item, frame-pool, and per-window capture-session setup, including starting capture.
- `frameWaitMs`: time from starting the session until the first available frame, or the timeout duration.
- `readbackMs`: D3D texture staging, GPU copy, mapping, and transfer into a CPU bitmap.
- `resizeMs`: bounded downscaling and copy into the owned BGRA pixel buffer.
- `totalMs`: capture-worker elapsed time for the attempt, including backend work and service overhead.

`ui-handoff` records measure creation and population of the Avalonia `WriteableBitmap` from owned pixels. This is not a measurement of the later compositor/GPU presentation or a guarantee about when the monitor displays the frame.

`frame-callback` records capture exceptions thrown by a frame-change subscriber. Subscribers are isolated so one bad callback does not prevent other subscribers from receiving the frame or stop a worker; each exception is recorded without its message or captured content.

Failures and retries are recorded with a failure code, exception type, and HRESULT when available. Window titles and captured pixels are not written to the log; window IDs are generated identifiers, but may still correlate with a running manager session. Recordings are local diagnostic data and should be handled accordingly.

For meaningful performance comparisons, run a Release build on an interactive Windows desktop, open the carousel with a representative set of windows, and compare several runs. Inspect median and tail (`p90`) `frameWaitMs`, `readbackMs`, `resizeMs`, and `totalMs`, along with failure/retry counts; distinguish first-use device initialization from later openings. Unit tests validate scheduling and timing propagation but do not measure real WGC or GPU latency. Protected, minimized, or otherwise unavailable sources can still time out.
