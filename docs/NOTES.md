# Notes on Windows

Local-first rich-text notes (Notes spec). The editor is the shared [notes editor](https://github.com/nikita4sea/alondra1-backend/tree/main/contract/notes-editor) (alondra1-backend `contract/notes-editor`) bundle, hosted in WebView2. The Android app hosts the same bundle, so documents are identical on both.

## Where things are

| Part | Files |
| --- | --- |
| Model, rules, single writer | `src/Alondra1.Core/Notes/` (`NotesModels.cs`, `NotesBoard.cs`) |
| Pictures (checked from their bytes, stored by SHA-256, size-limited) | `src/Alondra1.Core/Notes/NoteAssets.cs` |
| Storage in todo.db (schema 4) | `src/Alondra1.Persistence/SqliteTaskStore.Notes.cs`. Tables `notes`, `note_folders`, `note_assets` and the FTS5 index `notes_fts`. |
| Cloud Sync | `src/Alondra1.Core/Cloud/CloudSyncEngine.cs`. Kinds `note_folder` and `note`; pictures are uploaded before their note and downloaded after a pull. |
| UI | `src/Alondra1.App/Notes/`: `NotesView.xaml`, `NotesViewModel.cs`, `NoteEditorHost.cs` (WebView2 bridge), `NotesSamples.cs` (demo notes) |
| Editor bundle | `src/Alondra1.App/Assets/NotesEditor/` (copied from alondra1-backend `contract/notes-editor` by `npm run build`; do not edit here) |
| Tests | `tests/Alondra1.Tests/NotesTests.cs`, plus note steps in `CloudLiveTests.cs` |

## Behaviour

- **Saving.**
  - The editor reports changes about 0.45 s after typing stops, and they are written right away.
  - Switching notes, leaving the Notes workspace, closing the app and Ctrl+S save immediately.
  - "Saved locally" is shown only after the write succeeds.
- **Storage.**
  - A note keeps the document JSON, its plain text (for search, previews and older clients), its picture ids and its links. No HTML is stored.
  - Pictures live in `notes-assets\ab\<sha256>` next to todo.db; the same picture is stored once.
  - The default limit is 20 MB per picture (Settings: `notes.maxPictureMB` in config.json), and the pictures in one note can total at most 100 MB.
  - A document is limited to 2 MB of text and tables; pictures do not count toward this.
- **Trash.** Notes in Trash are deleted for good after 30 days, checked at start (`notes.trashRetentionDays`, 0 = never). Emptying Trash asks first.
- **Search.** SQLite FTS5 search over titles and text: every word must match as a prefix, and accents are ignored. A test covers 10,000 notes in under 500 ms.
- **Safety.**
  - The editor page and pictures are served from local folders only.
  - Navigation, new windows and downloads are blocked.
  - Links open in the default app only for web, mail and phone addresses.
  - Pasted HTML is reduced to the document schema: scripts, event handlers and `javascript:` links are dropped, and remote pictures stay placeholders until the user loads them.
- **Unknown content.**
  - A note from a newer version keeps parts this version does not know unchanged (shown as a grey box).
  - A note with a newer document version opens read-only.
- **Old notes.** Notes that earlier versions kept in a mail account (OneDrive app folder, Google Drive app data) stay there. **More → Import notes from mail accounts** copies them in once.

## Not done yet (honest list)

- Merged table cells. The format allows `colspan` and `rowspan`, but there is no UI to merge.
- Text wrapping around pictures (pictures are placed between paragraphs).
- A floating formatting bar for the selection (the toolbar is always shown).
- **Create meeting notes** from the Calendar view. Today, link an event from the note's **Link** button.
- Side-by-side conflict merging. A conflict keeps both versions, with a **(conflict copy)** note and a sync issue; you merge them by hand.
- Shared notes and live collaboration (future scope).
- Cloud pictures that no note uses any more are not removed automatically.
