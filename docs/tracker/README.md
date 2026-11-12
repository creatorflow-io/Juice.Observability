# Local tracker

`tasks.json` is the local copy of progress for two claude.ai pages whose data this repo's sessions can't always reach:

- **Observability MVP Tracker** (https://claude.ai/artifact/PPUBLFLKaTKePJDu6fKbrP): its `tasks` collection.
- **Architecture page** (https://claude.ai/artifact/5GuMPfEX4FojS3oxoTTxGC): a static page. `architecturePageUpdates` lists what is out of date on it.

Update `tasks.json` as work moves (status `todo` | `doing` | `blocked` | `done`, plus a short note), then sync from a session that
can write to the tracker's database.

## Row shape (the tracker page reads these fields)

`group` (g1–g3, p0, p1a, p1b, p2–p10, x1, x2) · `order` (within the group) · `kind` (`task` | `gate`) · `title` · `ref` ·
`status` · `note` · `updatedAt` (ISO time, set on sync).

## Syncing

1. Query the tracker's `tasks` collection for the groups in `tasks.json`.
2. Match each local row to a live row by `group` and `title`. A match: `update` its `status` and `note`, pinned to the version read.
   No match: `set` the row under the local `doc_id`.
3. `groupStatus` applies to every open row of that group: mark it `done` and keep the note on its gate row.
4. Use one `batch` (50 writes at most per call) and set `updatedAt` to the sync time.
5. Republish the architecture page with the `architecturePageUpdates`, then clear that list here.
