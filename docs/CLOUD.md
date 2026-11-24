# Alondra1 cloud on Windows (optional)

Alondra1 works fully without an account. Signing in to an Alondra1 account adds, on paid plans:

- **Cloud Sync for To-do:** tasks, lists and list groups, shared with the Android app and other PCs.
- **Mail Archive:** cloud copies of messages you choose, with search.

Everything is in **Settings → Cloud**. When Cloud Sync is on, a status button appears in the To-do toolbar.

## How it works

| Part | Where |
| --- | --- |
| HTTP client: HTTPS only (plain HTTP only for loopback), bearer auth, one refresh at a time, `X-Request-ID` | `src/Alondra1.Core/Cloud/CloudApi.cs` |
| Email-code sign-in, spaces, entitlements, plans, devices, log out | `CloudAccount.cs` |
| Sync engine: outbox push, then pull, conflicts, bootstrap merge | `CloudSyncEngine.cs` |
| Mail Archive client | `ArchiveClient.cs` |
| Outbox, versions, cursor and issues (todo.db schema 3) | `src/Alondra1.Persistence/SqliteTaskStore.cs` |
| UI: status, background sync timer, archive action | `src/Alondra1.App/Cloud/CloudController.cs` |
| UI: Settings → Cloud page, archive search window | `src/Alondra1.App/Cloud/CloudDialogs.cs` |

- **Single writer.** `TaskBoard` stays the only writer. Remote changes go through `TaskBoard.ApplyRemoteAsync`, which writes the changes, their server versions and the cursor in one transaction, without queueing them again.
- **Outbox.** Once Cloud Sync has been set up, every local change is queued in `sync_outbox`, in the same transaction as the change. A queued item keeps its mutation id until a send is attempted, so retries are idempotent.
- **Order and patches.** A sync run pushes first, then pulls. Patches are JSON merge patches against the last server copy; a removed field is sent as `null`.
- **Pull safety.** A pull never overwrites an item that has unsent local edits.
- **Conflicts keep both versions.** The cloud version keeps the original id, and the local version becomes a new task named "… (conflict copy)". The conflict is listed under **Sync issues**. The copy is pushed in the same sync run.
- **Turning sync on.** First, todo.db is copied to `backups\todo-before-cloud-*.db`. Then the cloud copy is downloaded and merged; nothing is replaced or deleted. A local default list that has never synced hands its tasks over to the cloud's default list, so each person has exactly one default list.
- **Plans.** The server's entitlements decide what is allowed. When a plan ends (`grace`, then `read_only`), nothing on the PC is deleted; new changes stay queued locally.
  - Access errors (`TENANT_NOT_FOUND`) turn Cloud Sync off and keep the data.
  - Network errors show the "Offline" state.
- **Secrets.** The refresh token is stored with DPAPI (`secrets\alondra1-cloud`), never in todo.db, config.json or logs. The access token is kept in memory only.
- **Not synced:**
  - Mail, contacts, task attachments and per-device reminder delivery state.
  - Notes and Calendar on Windows: they come from the mail providers (Microsoft 365 / Google), not from a local store, so the cloud notes and events written by the Android app are not shown on Windows yet.
- **Payments.** Payment is a placeholder: the price list comes from the backend catalog, and upgrades will open from the account page. See alondra1-backend `docs/BILLING_SPEC.md`.

## Configuration

`config.json` → `cloud`: `baseUrl` (default `https://api.alondra1.com`) and `syncMinutes` (default 5). For development, the environment variable `ALONDRA1_CLOUD_URL` overrides the address, for example `http://127.0.0.1:8080`.

## Tests

- `tests/Alondra1.Tests/CloudSyncTests.cs` runs against a scripted server and is always run. It covers the merge on enable, merge patches, conflicts, pull safety, restart, plan, access and offline errors, deletes, the HTTPS rule, and entitlements.
- `tests/Alondra1.Tests/CloudLiveTests.cs` is opt-in and drives a PC data folder step by step against a running backend devserver:

```sh
# alondra1-backend:  go run ./cmd/devserver -addr 127.0.0.1:8080 -dir .dev -grant-paid
export ALONDRA1_CLOUD_URL=http://127.0.0.1:8080 ALONDRA1_DEVCODES=<backend>/.dev/codes.json ALONDRA1_E2E_HOME=<folder>
ALONDRA1_E2E_ACTIONS="signin:me@example.com;add:Pay rent;enable;sync;expect:Pay rent" dotnet test --filter CloudLiveTests
# then open the same data in the app:  Alondra1.exe --home=<folder>
```

The steps are: `signin:<email>`, `add:<title>`, `rename:<old>=><new>`, `complete:<title>`, `delete:<title>`, `enable`, `sync`, `expect:<title>`, `expect-not:<title>`, `expect-issues:<n>`, `archive:<subject>` and `logout`.
