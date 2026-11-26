# Feature map: Alondra1 for Windows

<img src="images/alondra1-logo-64.png" alt="" width="48" align="right">

A complete, structured list of what the Windows app does: high-level **capabilities**, the **features** inside each, and every **atomic feature** (one behavior you can test). Each feature names the code that implements it, the tests that cover it and the document that explains it. The goal is to let you plan and add a feature **without scanning the code**: find the closest existing feature, follow its code pointers, and use the [recipes](#recipes-adding-a-feature) at the end.

- User-facing description: [FEATURES.md](FEATURES.md). Design and rules: [ARCHITECTURE.md](ARCHITECTURE.md), [GUIDELINES.md](GUIDELINES.md).
- The same capabilities on Android and in the backend: [alondra1-android FEATURE-MAP](https://github.com/alondrauno/alondra1-android/blob/main/docs/FEATURE-MAP.md) and [alondra1-backend FEATURE-MAP](https://github.com/alondrauno/alondra1-backend/blob/main/docs/FEATURE-MAP.md) (which also has the cross-platform parity table).
- Keep this file current: a change that adds, removes or changes a user-visible behavior updates its row here in the same commit.

**Status:** **Done**: implemented and tested. **Partial**: works with a stated limit. **Gap**: known missing piece (not hidden; listed so it can be planned).

**IDs:** `CAPABILITY-FEATURE-NN`, stable once published. Shared capabilities use the same codes on every platform (`TODO`, `NOTE`, `CAL`, `SYNC`, …).

**Paths** are relative to `src/` unless they start with `tests/`, `docs/` or another top-level folder. `App` = `Alondra1.App`, `Core` = `Alondra1.Core`, `Persistence` = `Alondra1.Persistence`, `Mail` = `Alondra1.Mail`, `Providers` = `Alondra1.Providers`. Tests are in `tests/Alondra1.Tests/`.

## Capability overview

| # | Capability | Features | Main code |
| --- | --- | --- | --- |
| 1 | [Shell and navigation](#1-shell-and-navigation-shell) | Workspaces, header and toolbar, responsive layout, search box, keyboard | `App/MainWindow.xaml(.cs)`, `App/ViewModels/MainViewModel.cs` |
| 2 | [Mail](#2-mail-mail) | Accounts, modes, sync, reading, HTML and images, attachments, writing and sending, organizing, search, layout, notes from mail, cloud archive | `Core/Sync`, `Core/Mail`, `Core/Html`, `Mail/`, `Persistence/SqlitePersonalStore.cs`, `App/Controls/MailBodyView.cs` |
| 3 | [Calendar](#3-calendar-cal) | Month grid of account events, day list, meeting notes | `App/ViewModels/MainViewModel.cs`, `Providers/OrganizerGateway.cs` |
| 4 | [To-do](#4-to-do-todo) | Tasks, steps, dates and repeats, reminders, lists and groups, smart views, quick add, organizing, task calendar, undo, data | `Core/Tasks`, `Persistence/SqliteTaskStore.cs`, `App/Tasks` |
| 5 | [Notes](#5-notes-note) | Organization, editor, lists, tables, pictures, saving, search, links, mail and calendar, import and export, sync, conflicts | `Core/Notes`, `Persistence/SqliteTaskStore.Notes.cs`, `App/Notes`, editor bundle `App/Assets/NotesEditor` |
| 6 | [Contacts](#6-contacts-cont) | Account contacts with search | `Providers/OrganizerGateway.cs` |
| 7 | [Account organizer data](#7-account-organizer-data-org) | Feature switches, account tasks, provider notes import | `Providers/OrganizerGateway.cs`, `Core/Organizer` |
| 8 | [Alondra1 account and cloud](#8-alondra1-account-and-cloud-cloud) | Sign-in, plan, devices | `Core/Cloud/CloudAccount.cs`, `App/Cloud` |
| 9 | [Cloud Sync](#9-cloud-sync-sync) | Enable and merge, outbox, push and pull, conflicts, pictures, status | `Core/Cloud/CloudSyncEngine.cs`, `App/Cloud/CloudController.cs` |
| 10 | [Appearance](#10-appearance-look) | Themes, colors, backgrounds, icons, density | `App/ThemeService.cs`, `App/Themes`, `App/Shell/Background*.cs` |
| 11 | [Settings, configuration and data](#11-settings-configuration-and-data-set) | Settings pages, configuration import and export, first run, demo, storage | `App/Dialogs.cs`, `Core/Config`, `Persistence/ConfigFileStore.cs` |
| 12 | [Security and privacy](#12-security-and-privacy-sec) | Secrets, transport, OAuth, content isolation, no telemetry | `Persistence/DpapiSecretStore.cs`, `Core/Security`, `Providers/OAuthService.cs` |
| 13 | [Installation and diagnostics](#13-installation-and-diagnostics-ops) | Installer, command-line switches, snapshots, checks, crash log | `installer/`, `App/App.xaml.cs`, `App/CommandLineSetup.cs`, `App/Shell/Snapshot.cs` |

---

## 1. Shell and navigation (`SHELL`)

### SHELL-WS: Workspaces
Code: `App/MainWindow.xaml(.cs)` (`OpenWorkspace`, key handling), `App/ViewModels/MainViewModel.cs` (`Workspaces`, `SelectWorkspace`). Tests: `UiDesignTests`, `PaneLayoutTests`. Docs: [UI-DESIGN.md](UI-DESIGN.md).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| SHELL-WS-01 | Six workspaces: Mail, Calendar, To-do, Notes, Contacts, Chat | Done | Chat is shown as **Soon** and has no function. |
| SHELL-WS-02 | Switch with Ctrl+1 … Ctrl+6 | Done | |
| SHELL-WS-03 | Workspaces that don't fit move into a More menu | Done | |
| SHELL-WS-04 | Workspace-specific toolbar row under the header | Done | |
| SHELL-WS-05 | Reminder bar visible in every workspace | Done | See TODO-REM. |

### SHELL-WIN: Window and responsive layout
Code: `App/Shell/ShellWindow.cs`, `App/MainWindow.xaml`, `App/Shell/GeometryCheck.cs`. Tests: `UiDesignTests`, `LegibilityTests`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| SHELL-WIN-01 | Borderless window with its own caption and window buttons | Done | |
| SHELL-WIN-02 | Dialog title bars follow the theme (DWM) | Done | |
| SHELL-WIN-03 | Labels turn into icons and items move to More menus as the window narrows (down to about 470 px) | Done | |
| SHELL-WIN-04 | Narrow Mail and Notes show one pane at a time with Back (Esc) | Done | Notes below 900 px. |
| SHELL-WIN-05 | Geometry report for snapshots (heights, insets, overlaps) | Done | Used by `scripts/snapshot-matrix.ps1`. |

### SHELL-SRCH: Header search
Code: `App/MainWindow.xaml.cs` (search box handlers).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| SHELL-SRCH-01 | Searches the open workspace: Mail messages, To-do tasks, Notes, Contacts | Done | |
| SHELL-SRCH-02 | Ctrl+E or Ctrl+F focuses search | Done | |
| SHELL-SRCH-03 | One search across all workspaces | Gap | Android has it (search screen). |

---

## 2. Mail (`MAIL`)

### MAIL-ACC: Accounts
Code: `Core/Config/Configuration.cs` (`AccountConfig`, presets), `Core/Config/PortAdvisor.cs`, `App/Dialogs.cs` (`ShowAccount`), `Mail/MailKitGateway.cs` (probe). Tests: `AcceptanceTests`, `PortAdvisorTests`, `LiveMailTests` (opt-in). Docs: [OAUTH-SETUP.md](OAUTH-SETUP.md).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| MAIL-ACC-01 | Microsoft 365 / Exchange Online with OAuth | Done | User registers their own public client id. |
| MAIL-ACC-02 | Gmail / Google Workspace with OAuth | Done | |
| MAIL-ACC-03 | Any provider by manual IMAP or POP3 plus SMTP, password sign-in | Done | |
| MAIL-ACC-04 | Presets fill server names and ports | Done | |
| MAIL-ACC-05 | Test connection checks incoming and outgoing separately, with readable reasons | Done | IMAP disabled, SMTP AUTH off, consent missing… |
| MAIL-ACC-06 | Port and security advice for typed settings | Done | `PortAdvisor`. |
| MAIL-ACC-07 | Edit and delete an account; delete removes its local data, secrets and outbox after confirmation | Done | Server mail is never changed. |
| MAIL-ACC-08 | All accounts view, grouped by account or together | Done | Mail → View settings. |

### MAIL-MODE: Online and Personal modes
Code: `Core/Sync/MailPolicies.cs` (`ModeTransition`), `Core/Ports/Ports.cs` (`SessionCache`), `Persistence/SqlitePersonalStore.cs`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| MAIL-MODE-01 | Online mode: messages in memory only | Done | |
| MAIL-MODE-02 | Personal mode: full local copy in `personal.db` with offline reading and search | Done | |
| MAIL-MODE-03 | Switching modes is blocked while sends are queued, unless confirmed | Done | |
| MAIL-MODE-04 | Leaving Personal mode deletes that account's local rows | Done | |

### MAIL-SYNC: Synchronization
Code: `Core/Sync/SyncCoordinator.cs`, `Core/Sync/MailPolicies.cs` (`ImapReconciler`, `Pop3Policy`, `ConflictPolicy`), `Mail/MailKitGateway.cs`. Tests: `AcceptanceTests`, `MailboxScaleTests`, `SpecialFoldersTests`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| MAIL-SYNC-01 | Sync on demand (F5, More → Sync now) | Done | |
| MAIL-SYNC-02 | Per-folder checkpoints; IMAP folder reset on `UIDVALIDITY` change | Done | |
| MAIL-SYNC-03 | Special folders learned per server (Inbox, Sent, Drafts, Archive, Trash) | Done | `Core/Mail/SpecialFolders.cs`. |
| MAIL-SYNC-04 | POP3 never imports twice (UIDL); deletes from server only when enabled | Done | |
| MAIL-SYNC-05 | Per-account serialization; different accounts sync in parallel | Done | |
| MAIL-SYNC-06 | Status dot: Up to date, Syncing…, Offline, Sync failed, Sample data, No accounts; details per account in its tooltip | Done | |
| MAIL-SYNC-07 | Automatic background sync or IMAP IDLE push | Gap | `MailKitGateway.IdleAsync` exists but the app does not call it. |

### MAIL-READ: Reading
Code: `App/ViewModels/MainViewModel.cs`, `App/Controls/MailBodyView.cs`, `Mail/MimeBody.cs`, `Core/Mail/MessageCount.cs`. Tests: `MailListTests`, `MailRenderingTests`, `MimeHeadersTests`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| MAIL-READ-01 | Folder tabs Inbox, Sent, Drafts, Archive, Trash (Ctrl+Shift+1 … 5) | Done | |
| MAIL-READ-02 | Compact list with unread marks and attachment indicator | Done | |
| MAIL-READ-03 | Reading pane with sender, account and time | Done | |
| MAIL-READ-04 | Message count that matches the list ("5 of 24 messages") and explained empty states | Done | |
| MAIL-READ-05 | HTML version by default, HTML / Plain text switch, default in Settings → Mail | Done | |
| MAIL-READ-06 | Plain text with clickable links | Done | |
| MAIL-READ-07 | Switching folders quickly never shows a stale-page error | Done | `MailBodyView` ignores outdated navigations. |

### MAIL-HTML: Safe HTML and images
Code: `Core/Html/HtmlMailSanitizer.cs`, `Core/Html/MailHtml.cs`, `Core/Mail/ReaderPolicy.cs`, `App/Controls/MailBodyView.cs`. Tests: `MailRenderingTests`, `scripts/check-reader.ps1`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| MAIL-HTML-01 | Allow-list sanitizer and strict CSP document | Done | |
| MAIL-HTML-02 | Isolated WebView2: no scripts, forms, frames, downloads or navigation | Done | |
| MAIL-HTML-03 | Embedded (`cid:`) pictures show offline | Done | |
| MAIL-HTML-04 | Remote images blocked until Load images, Always load from this sender, or the global setting | Done | Trusted senders in Settings → Mail. |
| MAIL-HTML-05 | Tracking pixels stay blocked | Done | |
| MAIL-HTML-06 | Links open in the browser or mail app | Done | |

### MAIL-ATT: Attachments
Code: `Persistence/AttachmentFiles.cs`, `App/ViewModels/MainViewModel.cs` (`SaveAttachmentAsync`). Tests: `AcceptanceTests`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| MAIL-ATT-01 | List with name, type and size; Open and Save | Done | |
| MAIL-ATT-02 | Files that can run programs ask before opening | Done | |
| MAIL-ATT-03 | Remove unpinned attachment files (Settings → Data) | Done | |
| MAIL-ATT-04 | Attachments in compose | Gap | |

### MAIL-SEND: Writing and sending
Code: `App/Dialogs.cs` (`ShowCompose`), `Core/Sync/MailPolicies.cs` (`SmtpStateMachine`, `SentCopyPolicy`, `MessageIdentity`), `Core/Sync/SyncCoordinator.cs`. Tests: `AcceptanceTests`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| MAIL-SEND-01 | New (Ctrl+N), Reply (Ctrl+R), Reply all, Forward (Ctrl+Shift+F) | Done | |
| MAIL-SEND-02 | Save draft | Done | |
| MAIL-SEND-03 | Stable Message-ID per message | Done | |
| MAIL-SEND-04 | Retry with backoff before the server accepted it | Done | |
| MAIL-SEND-05 | Lost connection after hand-over is marked Uncertain and never resent automatically | Done | |
| MAIL-SEND-06 | Durable outbox survives a restart | Done | |
| MAIL-SEND-07 | Sent copy appended only when the provider does not do it | Done | |

### MAIL-ORG: Organizing
Code: `App/ViewModels/MainViewModel.cs`, `Core/Sync/SyncCoordinator.cs` (queued operations).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| MAIL-ORG-01 | Archive, Delete (Del), Mark read or unread | Done | |
| MAIL-ORG-02 | Multi-select with Shift/Ctrl | Done | |
| MAIL-ORG-03 | Actions apply locally at once and are queued for the server | Done | |
| MAIL-ORG-04 | Conflict rules: both-changed drafts keep both; flags merge | Done | |
| MAIL-ORG-05 | Move to folder, flags | Gap | |

### MAIL-SRCH: Search
Code: `Persistence/SqlitePersonalStore.cs` (FTS5). Tests: `MailboxScaleTests` (10,000 messages).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| MAIL-SRCH-01 | Search follows the selected account and folder | Done | |
| MAIL-SRCH-02 | Full-text index for Personal accounts | Done | |

### MAIL-LAY: Layout
Code: `App/Shell/MailDockPanel.cs`, `Core/Layout/LayoutArranger.cs`, `App/Controls/MailColumns.cs`. Tests: `PaneLayoutTests`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| MAIL-LAY-01 | Folders, list and reader dock to any edge (25 combinations) | Done | |
| MAIL-LAY-02 | Resizable splitters, sizes remembered | Done | |
| MAIL-LAY-03 | Folders as toolbar tabs, side pane or hidden | Done | |
| MAIL-LAY-04 | Row density 32 or 40 px | Done | |

### MAIL-NOTE: Notes from mail
Code: `App/ViewModels/MainViewModel.cs` (`CreateNoteFromMailAsync`), `App/Notes/NotesViewModel.cs` (`CreateFromMailAsync`). Tests: `NotesTests`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| MAIL-NOTE-01 | More → Create note from this email (subject, sender, date, text) | Done | The note links back to the message. |
| MAIL-NOTE-02 | Create note with its pictures (picture attachments become note pictures) | Done | Per-note picture limit applies. |

### MAIL-ARCH: Cloud Mail Archive
Code: `Core/Cloud/ArchiveClient.cs`, `App/Cloud/CloudController.cs` (`ArchiveMessageAsync`), `App/Cloud/CloudDialogs.cs` (`ShowArchive`). Tests: `CloudSyncTests`, `CloudLiveTests`. Docs: [CLOUD.md](CLOUD.md).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| MAIL-ARCH-01 | More → Archive to Alondra1 Cloud copies the raw message (≤ 25 MB) | Done | Paid plan; the mailbox is not changed. |
| MAIL-ARCH-02 | Duplicate archive is reported, not stored twice | Done | |
| MAIL-ARCH-03 | Search the archive window (Settings → Cloud) | Done | |
| MAIL-ARCH-04 | Read-only plan blocks new archive uploads with a reason | Done | |

---

## 3. Calendar (`CAL`)

Code: `App/ViewModels/MainViewModel.cs` (`BuildCalendar`, `AgendaEvents`, `OpenMeetingNotesAsync`), `App/MainWindow.xaml` (Calendar section), `Providers/OrganizerGateway.cs` (`ListEventsAsync`). Tests: `NotesTests` (meeting notes). Docs: [FEATURES.md](FEATURES.md#calendar-ctrl2).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| CAL-VIEW-01 | Month grid of events from connected Microsoft 365 or Google calendars | Done | Days with events are marked. |
| CAL-VIEW-02 | Previous and next month; account selector | Done | |
| CAL-VIEW-03 | Selected day lists its events (time, subject, account) | Done | |
| CAL-MEET-01 | **Meeting notes** on each listed event: opens the linked note, or creates one from the meeting template, linked to the event | Done | Template shared with Android (`Core/Notes/NoteMerge.cs` → `MeetingNotes`). |
| CAL-EDIT-01 | Create or edit account events from the window | Gap | `OrganizerGateway.SaveEventAsync` exists; no UI. |
| CAL-EDIT-02 | Week and day views for account events | Gap | Task dates have their own calendar (TODO-CAL). |
| CAL-SYNC-01 | Show cloud calendar events (written by Android) | Gap | Windows has no local event store. |

---

## 4. To-do (`TODO`)

Code: domain `Core/Tasks/` (`TaskBoard` = single writer, `TodoModels`, `Recurrence`, `Reminders`, `TaskQueries`, `NaturalDateParser`, `TodoTransfer`); storage `Persistence/SqliteTaskStore.cs`, `Persistence/TaskFileStore.cs`; UI `App/Tasks/`. Tests: `TodoTests`, `ContractSchemaTests`. Docs: [TODO.md](TODO.md). Contract: `contract/schemas/todo-v1.schema.json`.

### TODO-TASK: Tasks

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| TODO-TASK-01 | Create, edit, delete (with undo), complete and reopen | Done | |
| TODO-TASK-02 | Title, notes, importance (star) | Done | |
| TODO-TASK-03 | Steps (subtasks) with own completion and a progress bar | Done | |
| TODO-TASK-04 | Turn a step into a task | Done | |
| TODO-TASK-05 | Reorder steps with up and down buttons | Partial | No drag and drop for steps. |
| TODO-TASK-06 | Attach files (copied into `task-files\`), drag files onto the details pane; with Cloud Sync, files go up before the task and other devices' files download when opened; only passive files whose bytes match their type open (from a marked copy), everything else is Save as only; files attached before Cloud Sync handled attachments upload only after consent; status per file with Retry; conflicts merge attachments three-way | Done | `Core/Tasks/AttachmentSafety.cs`, `Persistence/TaskFileStore.cs`, `Core/Cloud/CloudSyncEngine.cs`, `Core/Cloud/AttachmentMerge.cs`. |
| TODO-TASK-07 | Duplicate a task | Done | |
| TODO-TASK-08 | Move between lists (details, context menu, bulk bar, drag) | Done | |
| TODO-TASK-09 | Collapsible details pane (Esc), remembered | Done | |

### TODO-DATE: Dates and repeats

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| TODO-DATE-01 | Due date, optional time, start date | Done | |
| TODO-DATE-02 | My Day (date-stamped; starts empty each day) | Done | |
| TODO-DATE-03 | Repeats: daily, weekly on chosen weekdays, monthly, yearly, every N, end date | Done | Calendar-date math; Jan 31 → Feb 28 → Mar 31. |
| TODO-DATE-04 | Completing a repeating task moves it to the next occurrence and records the completion | Done | |
| TODO-DATE-05 | Typed dates accept culture formats, ISO and words (tomorrow, fri, next week) | Done | English words only. |

### TODO-REM: Reminders
Code: `Core/Tasks/Reminders.cs`, `App/Tasks/ReminderNotifier.cs`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| TODO-REM-01 | Multiple reminders per task; duplicates merged | Done | |
| TODO-REM-02 | Reminder bar with Open, Snooze, Dismiss | Done | Snooze length 10 min by default. |
| TODO-REM-03 | Windows notification (can be turned off) | Done | Clicking opens the task. |
| TODO-REM-04 | Correct across DST; never shown twice | Done | Marked delivered before display. |
| TODO-REM-05 | Reminders move with each repeat | Done | |
| TODO-REM-06 | Reminders while Alondra1 is closed | Gap | Shown at next start. |

### TODO-LIST: Lists and groups

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| TODO-LIST-01 | Lists with emoji names, eight colors, optional tint | Done | |
| TODO-LIST-02 | Rename, duplicate (tasks copied with new ids), delete with confirmation and undo | Done | Default **Tasks** list can't be deleted. |
| TODO-LIST-03 | Collapsible groups; deleting a group keeps its lists | Done | |
| TODO-LIST-04 | Drag to reorder lists or move between groups | Done | |
| TODO-LIST-05 | Show task counts (option) | Done | |
| TODO-LIST-06 | Per-list picture backgrounds | Gap | Window background only. |

### TODO-VIEW: Smart views, sort, filter, search
Code: `Core/Tasks/TaskQueries.cs`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| TODO-VIEW-01 | My Day, Important, Planned, Upcoming, Overdue, All, Completed, Unscheduled | Done | Filters; tasks are never copied. |
| TODO-VIEW-02 | Sort by my order, due date, importance, created, title; per view, remembered | Done | |
| TODO-VIEW-03 | Filter open / all / completed; important only | Done | |
| TODO-VIEW-04 | Search all lists as you type (title, notes, steps, list name; accent-insensitive) | Done | |
| TODO-VIEW-05 | Manual order: drag or Alt+Up/Down | Done | |

### TODO-ADD: Quick add
Code: `Core/Tasks/NaturalDateParser.cs`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| TODO-ADD-01 | Natural language offline: "Call Sam tomorrow at 3pm", "every month on the 1st", "!" for important | Done | |
| TODO-ADD-02 | Live preview of what was understood | Done | |
| TODO-ADD-03 | Context: My Day adds to My Day, Important stars, Calendar sets the day | Done | |

### TODO-BULK: Organizing in bulk

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| TODO-BULK-01 | Shift/Ctrl+click, Ctrl+A; bulk bar | Done | |
| TODO-BULK-02 | Bulk complete, move, star, My Day, delete | Done | |
| TODO-BULK-03 | Context menu (Menu key, Shift+F10) | Done | |

### TODO-CAL: Task calendar

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| TODO-CAL-01 | Day, week and month views of task dates | Done | Inside To-do. |
| TODO-CAL-02 | Overdue and unscheduled side panels | Done | |
| TODO-CAL-03 | Drag to reschedule; drop on an hour to set the time | Done | Reminders and start date move along. |
| TODO-CAL-04 | Tasks next to account events in the Calendar workspace | Gap | |

### TODO-HIST: Undo, history, summary

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| TODO-HIST-01 | Undo 50 steps (Ctrl+Z) for every change | Done | Inverse change sets. |
| TODO-HIST-02 | Completion history, one entry per completion (each repeat too) | Done | |
| TODO-HIST-03 | Summary view with statistics | Done | |

### TODO-DATA: Import, export, backup, print
Code: `Core/Tasks/TodoTransfer.cs`, `App/Tasks/TodoDataActions.cs`, `App/Tasks/TodoPrinter.cs`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| TODO-DATA-01 | Export JSON (`alondra1-todo` v1) | Done | |
| TODO-DATA-02 | Import JSON with validation and new ids | Done | Never overwrites. |
| TODO-DATA-03 | Export CSV (formula-safe) | Done | |
| TODO-DATA-04 | Export iCalendar (VTODO with RRULE and VALARM) | Done | |
| TODO-DATA-05 | Back up now; automatic daily backup (7 kept) | Done | |
| TODO-DATA-06 | Restore with a safety copy first | Done | |
| TODO-DATA-07 | Print the current view (Ctrl+P) | Done | |
| TODO-DATA-08 | iCalendar import | Gap | |
| TODO-DATA-09 | Encrypted backups | Gap | Deliberate (portability). |

---

## 5. Notes (`NOTE`)

Code: domain `Core/Notes/` (`NotesBoard` = single writer, `NotesModels`, `NoteAssets`, `NoteMerge`); storage `Persistence/SqliteTaskStore.Notes.cs` (todo.db tables `notes`, `note_folders`, `note_assets`, FTS5 `notes_fts`); UI `App/Notes/` (`NotesView`, `NotesViewModel`, `NoteEditorHost`, `NoteConflictDialog`); editor bundle `App/Assets/NotesEditor/` built from alondra1-backend `contract/notes-editor`. Tests: `NotesTests`, plus the editor's own tests in alondra1-backend. Docs: [NOTES.md](NOTES.md). Contract: `note-document-v1`, `notes-v1`, `note-folders-v1` schemas in alondra1-backend.

### NOTE-ORG: Organization

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| NOTE-ORG-01 | Three panes: views and folders, note list, editor; lists can be hidden | Done | |
| NOTE-ORG-02 | Views: All notes, Favorites, folders (nested, with counts), Archive, Trash | Done | |
| NOTE-ORG-03 | Create (Ctrl+N), rename (title), duplicate | Done | |
| NOTE-ORG-04 | Favorite, pin, archive | Done | |
| NOTE-ORG-05 | Move to a folder (menu, or drag notes onto a folder) | Done | |
| NOTE-ORG-06 | Folders: new, new inside, rename, move to top level, delete (notes kept) | Done | |
| NOTE-ORG-07 | Drag a folder into another folder; expand and collapse folders | Gap | |
| NOTE-ORG-08 | Trash: restore, delete permanently (asks), empty (asks); 30-day retention, configurable | Done | `notes.trashRetentionDays`. |
| NOTE-ORG-09 | Sort by last edited, created, title; multi-select | Done | |
| NOTE-ORG-10 | List previews, dates, favorite and pin marks, picture thumbnails | Done | |

### NOTE-EDIT: Editor and formatting
Code: `App/Notes/NoteEditorHost.cs` (WebView2 host, bridge), editor source in alondra1-backend `contract/notes-editor/src/editor.js`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| NOTE-EDIT-01 | Bold, italic, underline, strikethrough, inline code | Done | Ctrl+B/I/U, Ctrl+Shift+S, Ctrl+E. |
| NOTE-EDIT-02 | Font family and size (8–72 pt) | Done | |
| NOTE-EDIT-03 | Text color (palette or hex) and highlight | Done | |
| NOTE-EDIT-04 | Headings 1–3, alignment (Ctrl+Shift+L/E/R/J), indent, paragraph spacing | Done | |
| NOTE-EDIT-05 | Links (Ctrl+K), quote, code block, divider | Done | Links open only for web, mail and phone addresses. |
| NOTE-EDIT-06 | Undo and redo, clear formatting | Done | |
| NOTE-EDIT-07 | Find in note (Ctrl+F) | Done | |
| NOTE-EDIT-08 | Unknown content from newer versions is kept (grey box); newer schema opens read-only | Done | |
| NOTE-EDIT-09 | Paste from web, Word and mail is reduced to the schema (no scripts or handlers) | Done | Tested with synthetic HTML only. |
| NOTE-EDIT-10 | Floating selection toolbar | Gap | Optional in the spec. |
| NOTE-EDIT-11 | Configurable editor shortcuts | Gap | Checklist is fixed at Ctrl+Shift+9. |

### NOTE-LIST: Lists and checklists

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| NOTE-LIST-01 | Bulleted (Ctrl+Shift+8), numbered (Ctrl+Shift+7), checklist (Ctrl+Shift+9) | Done | |
| NOTE-LIST-02 | Nesting with Tab and Shift+Tab | Done | |
| NOTE-LIST-03 | Checklist item → To-do task, linked both ways | Done | |

### NOTE-TBL: Tables

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| NOTE-TBL-01 | Insert with a size grid | Done | |
| NOTE-TBL-02 | Add and remove rows and columns; delete table | Done | |
| NOTE-TBL-03 | Header row, cell colors | Done | |
| NOTE-TBL-04 | Column resizing | Done | |
| NOTE-TBL-05 | Multi-cell selection, copy and paste | Done | Not tested with real clipboard apps. |
| NOTE-TBL-06 | Merge cells | Gap | Format already allows colspan/rowspan. |

### NOTE-PIC: Pictures
Code: `Core/Notes/NoteAssets.cs`, `App/Notes/NotesViewModel.cs` (`InsertPicturesAsync`, `StoreImageAsync`, `FetchImageAsync`).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| NOTE-PIC-01 | Add with picker, drag and drop, or paste | Done | |
| NOTE-PIC-02 | PNG, JPEG, GIF, WebP checked from the bytes | Done | |
| NOTE-PIC-03 | Stored once by SHA-256 in `notes-assets\` | Done | Never embedded in the document. |
| NOTE-PIC-04 | Resize (25 %, 50 %, full, keep aspect), align, caption, delete | Done | |
| NOTE-PIC-05 | Limits: 20 MB per picture (configurable), 100 MB per note | Done | |
| NOTE-PIC-06 | Pasted web pictures load only after **Load picture** | Done | |
| NOTE-PIC-07 | Text wrap around pictures | Gap | |

### NOTE-SAVE: Saving and storage

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| NOTE-SAVE-01 | Autosave about 0.45 s after typing stops | Done | |
| NOTE-SAVE-02 | Immediate save on note switch, leaving Notes, app close, Ctrl+S | Done | Close waits up to 5 s. |
| NOTE-SAVE-03 | "Saved locally" only after the write succeeds | Done | |
| NOTE-SAVE-04 | 2 MB limit for text and tables per note | Done | |
| NOTE-SAVE-05 | Notes encrypted at rest | Gap | Same as tasks (plain SQLite in the profile). |

### NOTE-SRCH: Search
Code: `Persistence/SqliteTaskStore.Notes.cs` (FTS5).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| NOTE-SRCH-01 | Full-text search over titles and text; every word as a prefix; accents ignored | Done | 10,000 notes in < 500 ms (test). |

### NOTE-LINK: Links to other items

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| NOTE-LINK-01 | Link a task, a calendar event or a contact (**Link**) | Done | |
| NOTE-LINK-02 | Linked items as chips; open, or remove the link | Done | |
| NOTE-LINK-03 | A deleted target is reported as unavailable | Done | |
| NOTE-LINK-04 | Meeting notes from Calendar | Done | See CAL-MEET-01. |
| NOTE-LINK-05 | Notes from mail | Done | See MAIL-NOTE. |

### NOTE-IO: Import and export
Code: `App/Notes/NotesView.xaml.cs` (More menu), editor `exportAs`/`importHtml`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| NOTE-IO-01 | Export as self-contained HTML | Done | |
| NOTE-IO-02 | Export as Markdown with a picture folder | Done | |
| NOTE-IO-03 | Import an HTML file as a new note | Done | |
| NOTE-IO-04 | Import the plain-text notes earlier versions kept in mail accounts | Done | One-time copy. |

### NOTE-SYNC: Sync and conflicts
Code: `Core/Cloud/CloudSyncEngine.cs`, `Core/Notes/NotesBoard.cs` (`ResolveConflictAsync`), `Core/Notes/NoteMerge.cs`, `App/Notes/NoteConflictDialog.cs`. Tests: `NotesTests`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| NOTE-SYNC-01 | Notes, folders and pictures sync with Cloud Sync | Done | Pictures upload before their note; missing ones download. |
| NOTE-SYNC-02 | A note changed on two devices keeps both (conflict copy + sync issue with its copy id) | Done | |
| NOTE-SYNC-03 | Banner above the open note; **Compare** in Sync issues | Done | |
| NOTE-SYNC-04 | Side-by-side compare with differing lines marked | Done | |
| NOTE-SYNC-05 | Keep other device's, keep this PC's, combine both, keep both | Done | Copy goes to Trash; result syncs under the original id. |
| NOTE-SYNC-06 | Automatic merge inside a paragraph | Gap | Combine keeps both paragraphs. |
| NOTE-SYNC-07 | Remove cloud pictures no note uses | Gap | |

---

## 6. Contacts (`CONT`)

Code: `Providers/OrganizerGateway.cs` (`ListContactsAsync`), `App/ViewModels/MainViewModel.cs` (`VisibleContacts`).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| CONT-LIST-01 | Contacts from Microsoft 365 or Google People | Done | |
| CONT-LIST-02 | Search by name or e-mail | Done | |
| CONT-LIST-03 | Open a contact from a note link | Done | |
| CONT-EDIT-01 | Create, edit or delete contacts | Gap | `SaveContactAsync` exists; no UI. |

## 7. Account organizer data (`ORG`)

Code: `Providers/OrganizerGateway.cs`, `Core/Organizer/OrganizerModels.cs`, `App/ViewModels/MainViewModel.cs`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| ORG-FEAT-01 | Turn Calendar, To-do, Contacts, Notes provider features on or off (Settings → Connections) | Done | |
| ORG-FEAT-02 | Capability status explains missing consent instead of failing silently | Done | |
| ORG-TASK-01 | Account tasks (Microsoft To Do, Google Tasks) in their own To-do sidebar section: list, add, check off | Done | Separate from local tasks. |
| ORG-NOTE-01 | Provider notes (OneDrive app folder / Drive app data) can be imported once into local Notes | Done | See NOTE-IO-04. |
| ORG-CACHE-01 | Organizer items cached in `personal.db` for Personal accounts | Done | |

---

## 8. Alondra1 account and cloud (`CLOUD`)

Code: `Core/Cloud/CloudApi.cs` (HTTPS client, refresh, `X-Request-ID`), `Core/Cloud/Discovery/` (signed discovery, Ed25519, failover), `Core/BuildEnvironment.cs`, `Core/Cloud/CloudAccount.cs`, `App/Cloud/CloudDialogs.cs` (Settings → Cloud), `App/Cloud/CloudController.cs`. Tests: `CloudSyncTests`, `DiscoveryTests` (shared fixture cases), `CloudLiveTests` (opt-in). Docs: [CLOUD.md](CLOUD.md).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| CLOUD-AUTH-01 | Sign in or create an account with a 6-digit email code | Done | No password. |
| CLOUD-AUTH-02 | Refresh token stored with DPAPI; access token in memory only | Done | |
| CLOUD-AUTH-03 | Sign out (this PC) | Done | |
| CLOUD-PLAN-01 | Show plan and its state (active, grace, read-only) from server entitlements | Done | |
| CLOUD-PLAN-02 | Manage account online (opens the web account page) | Done | Payment is a placeholder (backend `BILLING_SPEC.md`). |
| CLOUD-DEV-01 | List signed-in devices | Done | |
| CLOUD-CFG-01 | `cloud.baseUrl`, `cloud.syncMinutes`; `ALONDRA1_CLOUD_URL` override | Done | HTTPS only, except loopback; must be a host of the build's environment. |
| CLOUD-DISC-01 | Signed discovery manifest (`alondra1.discovery/v1`): Ed25519 over the exact bytes, environment, validity, monotonic version, host allowlist | Done | Passes all 32 shared fixture cases (contract/fixtures/discovery-v1). |
| CLOUD-DISC-02 | Sources: primary, mirror, verified cache (`discovery-cache.json`, atomic), bundled list until its expiry; no redirects, conditional GET | Done | Only when the cloud is in use; trust list empty until the owner creates the signing keys. |
| CLOUD-DISC-03 | Failover on transport errors, timeouts, 502/503/504 (Retry-After honoured); never on 401/403/404/429; same body on retry; no refresh replay | Done | `EndpointFailoverHandler`. |
| CLOUD-DISC-04 | Refresh every 15 min with jitter (checked before each sync) and right after a sync that found the cloud offline | Done | `CloudController.RefreshEndpointsAsync`. |
| CLOUD-ENV-01 | Dev build (`-p:Alondra1Environment=dev`): own data folder `alondra1-dev`, DPAPI entropy, dev endpoints and keys, DEV badge and title | Done | `BuildEnvironment`. |
| CLOUD-ENV-02 | A dev build refuses prod hosts and a prod build refuses dev hosts | Done | |
| CLOUD-SPACE-01 | Choose among several spaces, accept invitations; per-space outbox and cursors | Gap | Uses the personal space (spec v1.1 §8.2, P0 personal / P1 shared). |
| CLOUD-CAL-01 | Local calendar event store and sync kind `event` | Gap | Spec v1.1 P1; provider events stay separate. |

## 9. Cloud Sync (`SYNC`)

Code: `Core/Cloud/CloudSyncEngine.cs`, `Core/Cloud/CloudContracts.cs` (`ICloudSyncStore`, kinds), `Persistence/SqliteTaskStore.cs` (outbox, versions, meta, issues), `App/Cloud/CloudController.cs`. Tests: `CloudSyncTests`, `NotesTests`, `CloudLiveTests`. Protocol: alondra1-backend ADR 0005.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| SYNC-KIND-01 | Kinds: list groups, task lists, tasks, note folders, notes (+ note pictures) | Done | Events are not synced on Windows. |
| SYNC-ON-01 | Turn on: backup of todo.db, then download and merge (nothing replaced or deleted) | Done | |
| SYNC-ON-02 | Never-synced local default list hands its tasks to the cloud default list | Done | |
| SYNC-OUT-01 | Every local change queued in the same transaction (outbox) | Done | |
| SYNC-OUT-02 | Mutation id kept until a send is attempted (idempotent retries) | Done | |
| SYNC-RUN-01 | Push first, then pull; JSON merge patches against the last server copy | Done | |
| SYNC-RUN-02 | A pull never overwrites unsent local edits | Done | |
| SYNC-RUN-03 | Push in size-based batches (pictures uploaded first) | Done | 6 MB per batch. |
| SYNC-RUN-04 | Periodic sync (`syncMinutes`), sync soon after local changes, Sync now, Pause | Done | |
| SYNC-CONF-01 | Conflicts keep both: cloud version keeps the id, local becomes "(conflict copy)", pushed in the same run | Done | |
| SYNC-CONF-02 | Sync issues list with Dismiss; note conflicts have Compare | Done | |
| SYNC-ERR-01 | Plan ended: read-only, nothing deleted, changes stay queued | Done | |
| SYNC-ERR-02 | Lost access (`TENANT_NOT_FOUND`) turns sync off and keeps data; network errors show Offline | Done | |
| SYNC-STAT-01 | Status button in the To-do toolbar | Done | |
| SYNC-NOT-01 | Not synced: mail, contacts, task attachments, reminder delivery state, provider data | Done | By design. |

---

## 10. Appearance (`LOOK`)

Code: `App/ThemeService.cs`, `App/Themes/{Light,Dark,Controls,Icons}.xaml`, `App/Shell/BackgroundService.cs`, `App/Shell/BackgroundEditor.cs`, `App/Controls/Alondra1Icon.cs`. Tests: `UiDesignTests`, `LegibilityTests`. Docs: [UI-DESIGN.md](UI-DESIGN.md).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| LOOK-THEME-01 | Light, Dark, or follow Windows | Done | |
| LOOK-THEME-02 | Edit light and dark colors per token; reset | Done | |
| LOOK-BG-01 | Seven built-in backgrounds or your own PNG, JPEG, WebP (copied and processed locally) | Done | |
| LOOK-BG-02 | Opacity, blur, desaturation, brightness, overlay, position, fit, live preview; theme-adaptive brightness | Done | |
| LOOK-ICON-01 | 62 custom stroke icons | Done | |
| LOOK-DENS-01 | Compact or Comfortable density | Done | |
| LOOK-NOTE-01 | Notes editor follows the app theme tokens | Done | |

## 11. Settings, configuration and data (`SET`)

Code: `App/Dialogs.cs` (`ShowSettings`, `ShowImport`, `ShowWelcome`), `Core/Config/Configuration.cs` (`AppConfig`, `ConfigWorkflow`), `Persistence/ConfigFileStore.cs`. Tests: `AcceptanceTests`.

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| SET-PAGE-01 | Settings pages: Appearance, Background, Mail, Accounts, Connections, Data, Cloud | Done | |
| SET-CFG-01 | Export configuration (public client ids, never passwords or tokens) | Done | |
| SET-CFG-02 | Import configuration with preview: Merge, or Replace after review | Done | |
| SET-CFG-03 | `config.json.bak` kept for recovery | Done | |
| SET-DATA-01 | Storage use; remove unpinned attachment files | Done | |
| SET-FIRST-01 | First launch: sample data, import a configuration, or add an account | Done | |
| SET-DEMO-01 | Demo mode (`--demo`): labeled sample data in a temporary folder | Done | Seeds tasks and notes. |
| SET-NOTE-01 | Notes settings: sort, panes, max picture size, trash retention | Done | `config.json` → `notes`. |
| SET-ABOUT-01 | Settings → About: version and environment, Copy diagnostics (no email address, ids, mail or contents), Send feedback on dev builds (the public alondra1-www issue form) | Done | `Core/Diagnostics.cs`, `DiagnosticsTests`. |

## 12. Security and privacy (`SEC`)

Code: `Persistence/DpapiSecretStore.cs`, `Core/Security/ConnectionPolicy.cs`, `Providers/OAuthService.cs`, `Core/Cloud/CloudApi.cs`. Docs: [ARCHITECTURE.md](ARCHITECTURE.md#security-model).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| SEC-SECRET-01 | Passwords and tokens with DPAPI (current user + app entropy), never in config or exports | Done | |
| SEC-NET-01 | Implicit TLS or required STARTTLS; plaintext only after explicit warning; OAuth never over plaintext | Done | |
| SEC-OAUTH-01 | Public OAuth clients only, user-supplied client ids | Done | |
| SEC-WEB-01 | Mail HTML and the notes editor run in isolated WebView2 hosts with strict CSP; navigation blocked | Done | |
| SEC-FILE-01 | Stored file paths are relative and checked to stay in their folder | Done | |
| SEC-PRIV-01 | No telemetry or ads; network goes only to added mail accounts and, if the user signs in, the Alondra1 cloud | Done | |

## 13. Installation and diagnostics (`OPS`)

Code: `installer/` (Inno Setup, `publish.ps1`), `App/App.xaml.cs`, `App/CommandLineSetup.cs`, `App/MainWindow.xaml.cs` (`SnapshotAsync`), `App/Shell/Snapshot.cs`, `scripts/`. Docs: [CODE-GUIDE.md](CODE-GUIDE.md), [runbooks/windows-reinstall-check.md](runbooks/windows-reinstall-check.md).

| ID | Atomic feature | Status | Notes |
| --- | --- | --- | --- |
| OPS-INST-01 | Per-user installer, no admin prompt; Start menu and optional desktop shortcut; Apps entry | Done | |
| OPS-INST-02 | Upgrades in place; uninstall keeps `%LOCALAPPDATA%\alondra1` | Done | |
| OPS-INST-03 | Self-contained single-file build (no .NET needed), Windows 10/11 x64 | Done | |
| OPS-CLI-01 | Setup switches: `--import-config`, `--import-passwords`, `--sync`, `--send-check`, `--account`, `--exit` (exit codes) | Done | |
| OPS-CLI-02 | Snapshot switches: `--snapshot`, `--open`, `--width`, `--height`, `--scale`, `--search`, `--select`, `--detail`, `--day`, `--dialog` (incl. `conflict`), `--theme` | Done | |
| OPS-CLI-03 | Diagnostics: `--home`, `--demo`, `--perf`, `--open-eml`, `--load-images`, `--trace` | Done | |
| OPS-LOG-01 | Crash handler writes `logs\crash.txt` | Done | |
| OPS-CHK-01 | Reinstall check, reader check, snapshot matrix scripts | Done | |
| OPS-CI-01 | CI: build, test, Markdown link check, gitleaks; both builds published and checked for the other environment's endpoints | Done | `scripts/check-environment.ps1`. |
| OPS-REL-01 | Dev setup (Alondra1 Dev, own AppId) on every main push as a workflow artifact | Done | |
| OPS-REL-02 | Prod setup on `vX.Y.Z`, signed and published only after approval in the `prod` environment; refuses to publish unsigned | Done | Signing identity: `WINDOWS_SIGNING_PFX_BASE64`, `WINDOWS_SIGNING_PASSWORD` (prod only). |
| OPS-INST-04 | Dev installer identity: `Alondra1 Dev`, own AppId and folder; installs next to prod | Done | `installer/alondra1.iss /DDev`. |

---

## Data stores

| Store | File | Owner (single writer) | Schema / version |
| --- | --- | --- | --- |
| Configuration | `config.json` (+ `.bak`) | `ConfigFileStore` via `MainViewModel` | `AppConfig.SchemaVersion` |
| Secrets | `secrets\*` | `DpapiSecretStore` | one file per account; `alondra1-cloud` for the cloud refresh token |
| Mail copy, outbox, organizer cache | `personal.db` | `SqlitePersonalStore` (batches from `SyncCoordinator`) | `PRAGMA user_version` |
| Tasks, notes, sync state | `todo.db` | `TaskBoard` (tasks), `NotesBoard` (notes), `CloudSyncEngine` (remote apply) | schema **5**: 3 = sync tables, 4 = notes, 5 = `sync_issues.copy_id` |
| Task files | `task-files\<task>\` | `TaskFileStore` | |
| Note pictures | `notes-assets\<ab>\<sha256>` | `NoteAssets` | content-addressed |
| Backups | `backups\` | `TodoDataActions`, `CloudSyncEngine` (before enable) | |

## Recipes: adding a feature

Each recipe lists every place to touch, in order. Follow the closest existing feature (named in brackets) as a template.

- **A new To-do field** [`TODO-DATE-01` start date]: `Core/Tasks/TodoModels.cs` → `TaskBoard` change set (and its inverse) → `TaskQueries` if it affects views → `TodoTransfer` (JSON/CSV/iCal) → `App/Tasks/TodoView*.cs` details pane → contract `contract/schemas/todo-v1.schema.json` + fixtures (mirrored in alondra1-backend; `contract-sync` CI) → backend `internal/tasks` validation → Android `TaskEntity` + `WireMapper` → tests `TodoTests`, `ContractSchemaTests`. Stored as JSON in todo.db, so no SQL migration unless it must be indexed.
- **A new note editor command** [`NOTE-EDIT-05` divider]: alondra1-backend `contract/notes-editor/src/editor.js` (toolbar item) and `schema.js` (if it adds a node or mark; also `contract/schemas/note-document-v1.schema.json` and `docs/DOCUMENT-FORMAT.md`) → `npm test` → `npm run build` copies the bundle into both apps → commit the bundle in alondra1-windows and alondra1-android.
- **A new host ↔ editor message** [`checklistToTask`]: editor `editor.js` (`post` / `window.alondra.*`) → `contract/notes-editor/docs/HOST-BRIDGE.md` → Windows `App/Notes/NotesViewModel.cs` `OnEditorMessageAsync` → Android `features/notes/NotesScreens.kt` `handleEditorMessage`.
- **A new note action in the menus** [`NOTE-ORG-04` archive]: rule in `Core/Notes/NotesBoard.cs` (via `EditAsync` so it is queued for sync) → `App/Notes/NotesViewModel.cs` wrapper → menu item in `App/Notes/NotesView.xaml.cs` → `NotesTests`.
- **A new synced kind** [`note_folder`]: backend (schema in `contract/schemas`, OpenAPI paths, `internal/tasks` kind, migration) → `Core/Cloud/CloudContracts.cs` `CloudKinds` → `CloudSyncEngine` (apply, patch, push order, bootstrap stage) → store support in `SqliteTaskStore` → Android `EntityType`, `WireMapper`, `SyncEngine` → tests on all three.
- **A todo.db schema change** [`(5, "ALTER TABLE sync_issues …")`]: append to `SqliteTaskStore.Migrations`, bump `SchemaVersion` (a pre-migration backup is automatic) → extend `NotesTests.Migration_from_schema_3_keeps_tasks_and_adds_notes` or add a migration test.
- **A new setting** [`SET-NOTE-01`]: property in `Core/Config/Configuration.cs` (default value; it is persisted with `config.json`) → control in `App/Dialogs.cs` settings page → read it where needed → mention it in FEATURES.md.
- **A new Calendar or Contacts provider action** [`CAL-MEET-01`]: `IOrganizerGateway` in `Core/Ports/Ports.cs` (most CRUD already exists in `Providers/OrganizerGateway.cs`) → command in `App/ViewModels/MainViewModel.cs` → XAML in `App/MainWindow.xaml` → demo data in `Core/Demo/SampleCatalog.cs` so snapshots show it.
- **A new dialog** [`NoteConflictDialog`]: build it with `Dialogs.Create` / `Panel` / `Footer` (`App/Dialogs.cs`) → add a `--dialog=<name>` case in `MainWindow.SnapshotAsync` so it can be captured.
- **A new workspace**: `MainViewModel.Workspaces` → `MainWindow.xaml` content and toolbar → shortcut in `MainWindow.xaml.cs` → icon in `Themes/Icons.xaml` → `--open=<name>`.

After any feature: update its row here, [FEATURES.md](FEATURES.md) (user view), the module doc ([NOTES.md](NOTES.md), [TODO.md](TODO.md) or [CLOUD.md](CLOUD.md)), and, for shared behavior, the Android map and the backend parity table.
