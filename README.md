> [!WARNING]
> This repository is still WIP, things are subject to change, you SHOULD NOT use it in production. Any contributions are welcome

## Replay Storage

Replay files are uploaded to an HTTP-accessible blob store. The server is fully pluggable — anything that speaks the contract below works (S3 with a presigned-URL fronting service, MinIO, a tiny custom server, etc.).

A reference local server lives at [`tools/ReplayStorageServer`](tools/ReplayStorageServer); useful for development and as a minimal implementation reference.

### Configuration

In `sharp/configs/timer.jsonc`:

```jsonc
"replay": {
  "storage_base_url": "https://replays.example.com",
  "upload_non_personal_best": false
}
```

- **Omit the `replay` section** (or leave `storage_base_url` empty) to disable remote uploading entirely. `Timer.RequestManager` will skip registering `IReplayProvider`, and no upload attempts will be made.
- **`upload_non_personal_best`** — `false` (default) uploads only new PB/WR replays. `true` uploads every finished run.

### HTTP contract

The timer drives three operations against your endpoint. The `{key}` segment **contains forward slashes** — your routing must treat it as a path, not a single segment.

| Method | URL                  | Request body | Success         | Notes                                                       |
|--------|----------------------|--------------|-----------------|-------------------------------------------------------------|
| `PUT`  | `{base_url}/{key}`   | raw bytes    | any `2xx`       | Body is a compressed replay file. No `Content-Type` is set. |
| `GET`  | `{url}`              | —            | `200` + bytes   | `{url}` is whatever the `PUT` returned/wrote — see below.   |
| `DELETE` | `{url}`            | —            | `2xx` or `404`  | Best-effort cleanup after a failed DB write. Errors are swallowed. |

The full URL stored in the DB and used for `GET`/`DELETE` is whatever `IReplayStorage.UploadAsync` returns. The bundled `HttpReplayStorage` simply returns `{base_url}/{key}`, so `PUT` and later `GET` hit the same path. A custom backend (e.g. S3 + CloudFront) can return a different download URL — it just has to be reachable for `GET` later.

### Key layout

```
{map}/style_{style}/{track}/{steamId}_{runId}.replay                    main replay
{map}/style_{style}/{track}/stage_{stage}/{steamId}_{runId}.replay      stage replay
```

`{map}` is always lowercase. `{steamId}` is the 64-bit SteamID. `{runId}` is the run's primary key from `surf_runs`.

### Failure handling

- **Upload fails** → the run still exists in the DB without a replay row; logged as `Failed to upload replay remotely`.
- **Upload succeeds but DB write fails** → the timer attempts a `DELETE` on the just-uploaded URL to avoid orphans. Don't treat a single `DELETE` as authoritative — periodic reconciliation against `surf_runs_replay.Replay` is recommended for long-lived deployments.

### Running the reference server

```bash
cd tools/ReplayStorageServer
dotnet run
```

Listens on `http://0.0.0.0:5080`, stores files under `./replays/`. Both are configurable via `appsettings.json`. Then point `storage_base_url` at it:

```jsonc
"replay": { "storage_base_url": "http://127.0.0.1:5080" }
```
