# Mail UI, multiple accounts and global search: dev acceptance record

Spec: `.import/ALONDRA1_MAIL_UI_MULTI_ACCOUNT_GLOBAL_SEARCH_SPEC.md` (Dev only; no UAT, no production deployment).
Repository: `alondra1-windows`, branch `main`. Record written 2026-11-27.

## 1. What was run

| Item | Result |
|---|---|
| Build | .NET 10 SDK, Debug, `main` at the commit that adds this file |
| Automated tests | `dotnet test`: **384 passed, 0 failed, 8 skipped** (skipped: live mail/cloud tests that need real accounts, the 10 GB mailbox test, and the opt-in §10 benchmark below, run separately) |
| §10 benchmark | `ALONDRA1_MAIL_UI_BENCH=1 dotnet test --filter Budgets_with_ten_and_fifteen_accounts_and_100k_headers`: **passed** |
| Screenshots | `docs/screenshots/mail-ui/` (15 images, demo data and synthetic accounts only) |
| Fixtures | Synthetic only: `SampleCatalog` (2 accounts, or `--demo-accounts=N` up to 15), `MailUiScaleTests.SeedAsync` (up to 15 accounts, 100,000 headers). No real credentials or mailboxes. |

Dev machine for timings: 14 logical cores, 31 GB available to .NET, Windows 10.0.26300, .NET 10.0.15, SSD.
That is stronger than the spec's suggested 4-core, 8 GB reference machine, so the margins below matter more than the absolute numbers.

## 2. Acceptance tests T01–T24

Pass means every listed variant has automated or screenshot evidence. Partial means some variants were not executed;
the gap is named and listed again in §5.

| ID | Pri | Result | Evidence |
|---|---|---|---|
| T01 | P0 | Pass | `UiDesignTests` (two header rows, one tab style, header/toolbar heights); Chat has no tab, and a saved "Chat" module opens Mail (`MainViewModel.Open…`, comment "An unknown module…"); screenshots 02, 03 |
| T02 | P1 | **Partial** | `UiDesignTests` (spacing tokens, light/dark define the same tokens, icons, gutters), `MailListPresentationTests.HighContrastMapsEveryThemeBrush`; screenshots 01–03, 11, 15. Not done: side-by-side check at every target size and scale, and a real Windows contrast theme (15 forces the mapping with normal system colors) |
| T03 | P0 | Pass | `MailNavigationTests` (every account listed, even without mail; duplicate names by address; no accounts), `MailUiScaleTests.FifteenAccountsStayApart…`; screenshots 01, 04 (10 accounts: duplicate, long, Cyrillic names), 05 (15) |
| T04 | P0 | Pass | `MailNavigationTests` (unified unread without double counting, unknown is not zero, nested folders, missing Archive is missing), `SpecialFoldersTests`, `MailListViewTests` (stable merge); account and date grouping optional (screenshots 02, 14) |
| T05 | P0 | Pass | `ComposeIdentityTests` (default account, unified needs a choice, reply from the receiving account, account removed), `BulkActionsTests` (mixed accounts, missing folder, removed account, partial summary). Moving to an arbitrary folder is not offered, so that variant doesn't apply |
| T06 | P1 | Pass | `MailListTests.Disabled_message_actions_explain_why`, reader actions share the toolbar commands; layout menu (pane docking, density, grouping); screenshot 11 (1024 px) |
| T07 | P1 | Pass | `MailColumnLayoutTests`, `MailListViewTests`, `MailAdvancedFilterTests`, `MailListPresentationTests`; screenshots 02, 04, 12, 14 |
| T08 | P1 | **Partial** | Multi-select (Shift/Ctrl, count in header) and mixed-account bulk: `BulkActionsTests`. **Threads are not grouped**: the local store keeps no References/In-Reply-To headers for received mail, so every message is listed on its own; nothing is grouped by subject or across accounts |
| T09 | P0 | Pass | `MailListTests.Count_*`, list states (no account, empty folder, empty filter, loading with rows intact, not synced here, error), reader empty state; screenshots 01, 12; no column header over an empty list |
| T10 | P1 | Pass | Reader header and Details (all recipients, receiving account, full date with UTC offset, storage); attachment area only with files (`M06_attachments_are_listed…`); screenshots 07, 08 |
| T11 | P0 | Pass | `MailRenderingTests` M01–M12 (plain-only, alternative, newsletter, CID, quoted-printable/UTF-8, nested multiparts, wide content, malformed falls back), `MimeHeadersTests`; "Try HTML again" on renderer failure; screenshot 07 |
| T12 | P0 | Pass | `M05` (remote images blocked until allowed, trackers always removed), `M10` (active content removed), `AcceptanceTests.Html_sanitizer_strips_scripts_handlers_and_remote_images`, `Data_images_are_limited_to_raster_types_and_size`; isolated WebView2 profile |
| T13 | P1 | Pass | `AttachmentSafetyTests` (bytes must match type; mark of the web), `Risky_attachments_are_recognized`, `AttachmentTextTests` (no download or execution for search) |
| T14 | P0 | Pass | `AccountSyncStatusTests`, `MailUiScaleTests` (failing, slow and hung accounts don't block others; summary counts each state); provider and cloud status are separate; screenshot 04 (sign-in needed, offline) |
| T15 | P0 | Pass | Availability labels per message (headers only, downloaded, on the server); `AcceptanceTests` (outbox survives restart, uncertain send not retried, POP dedup needs a durable commit, UID validity reset) |
| T16 | P0 | Pass | `GlobalSearchTests` (module vs All, All persists, All mail never broadens, unknown saved reach falls back); scope chip always visible; the cloud archive is searched only on request (§3.4) |
| T17 | P0 | Pass | `GlobalSearchTests` (five modules, failing module doesn't hide others, stale results dropped, account identity kept, duplicate names apart, coverage honest, capped with total); screenshots 09, 10 |
| T18 | P1 | Pass | `MailAdvancedFilterTests`, `GlobalSearchTests.ANewerQueryDropsTheOlderResults`; Ctrl+K search, Enter, Esc, Down; 300 ms debounce. No search history is stored, so there is nothing to clear and no opt-out is needed |
| T19 | P1 | Pass with limitation | `MailboxScaleTests` (index rebuild on open, flags keep body searchable), `AcceptanceTests.Removing_an_account_…`, `MailAdvancedFilterTests` (attachment names), `AttachmentTextTests`. Content search covers only plain-text files on this PC; PDF and Office files are searched by name |
| T20 | P0 | Pass | Store queries are parameterized (`BuildFtsMatch` keeps letters and digits only; LIKE wildcards escaped: `TheStoreMatchesAttachmentNamesLiterally…`); results only from configured accounts; local search never calls a server; snippets plain text; no query text logged |
| T21 | P1 | **Partial** | Automation names on rows (`AccessibilityName` with unread, flagged, attachments, full date), resize edges and scope chip; headings on group headers; polite live regions for counts and search status; color never the only signal (dot plus weight, edge, text). Not done: a recorded Narrator pass and checks at 150 and 200 % text scaling |
| T22 | P1 | **Partial** | `PaneLayoutTests`, `UiDesignTests` gutters, `MailColumnLayoutTests` (columns adapt, nothing overlaps); screenshots at 1024×768, 1280×760, 1440×860, 1600×1000. Not done: 125, 150 and 200 % Windows scaling |
| T23 | P1 | Pass | Benchmark (§3.1); `MailUiScaleTests` (15 accounts, nested folders, non-Latin senders); memory flat over 300 switches |
| T24 | P0 | Pass | Full suite; `AcceptanceTests.Config_*` (newer file refused, damaged write recovered); no database schema change in this work (new queries only); new settings are optional fields with defaults |

## 3. Measurements and decisions

### 3.1 §10 budgets (warm cache, p95 of 10–40 runs)

| Scenario | p95 | Target |
|---|---|---|
| Switch to an account's Inbox, 1,000 headers | 4.5 ms | 300 ms |
| Switch to a large account's Inbox, 18,000 headers | 94 ms | 300 ms |
| Unified Inbox, 10 accounts (7,000 rows) | 46 ms | 300 ms |
| Unified Inbox, 15 accounts: first 300 rows shown | 62 ms | 300 ms |
| Unified Inbox, 15 accounts: all 70,000 rows (loads behind the first page) | 447 ms | informational |
| Sidebar counts, 15 accounts | 89 ms | 300 ms |
| Open a cached message (including large bodies) | 0.8 ms | 500 ms |
| Search all mail (100,000 headers), rare term | 23 ms | 500 ms |
| Search all mail, frequent term: first 200 and the total | 151 ms | 500 ms |
| Search all mail, frequent term: every match (in-list search) | 674 ms | informational |
| Attachment name lookup | 3.5 ms | 500 ms |
| All search, first result group | 19 ms | 700 ms |
| Managed memory after 300 account switches | +0.4 MB | no unbounded growth |

The first run missed two budgets: the unified Inbox over 15 accounts took 413 ms, and a frequent-word search took 694 ms.
Both loaded every row before showing any. The fix, kept in the product:
- the list shows its newest 300 rows first and loads the rest with those rows still on screen;
- All mail search reads 200 results plus the total.

No target was relaxed. Seeding 100,000 headers takes about 10 s one time and is not a budget.

### 3.2 Many accounts (§7.2)

- Each account syncs on its own, with at most 3 at a time and a 10-minute limit each.
- A thrown error or a hung provider becomes that account's failed state and frees its slot. The error type is recorded, never server text.
- Sidebar refreshes are combined when several accounts finish close together.
- A reload that a newer one overtakes drops its results, so a quick scope change never shows the old scope's rows.

### 3.3 Search coverage stated in the product

- The Mail group says what it covered: mail kept on this PC; messages whose body isn't downloaded, by sender and subject only; attachments by name, and the text only of plain-text files on this PC; any active list filters.
- The results panel says the cloud archive and mail servers are not searched.

### 3.4 Cloud archive

- It's a separate, explicit source. "Search the cloud archive…" in the results panel opens the existing archive search with the same words.
- It runs only after sign-in and a plan check. Local search never sends query text anywhere.

## 4. Traceability: review observations 1–164

Dispositions:
- **Fixed**: changed in this work or the earlier slices of this spec.
- **Verified**: already correct, now covered by tests or screenshots.
- **Limitation**: accepted and stated in §5.
- **N/A**: does not apply, with the reason.

Commits for this spec on `main`, oldest first:
- `0e51f19` shared search;
- `11bd80b` per-account sync and sending account;
- `21e41e9` filters and sort;
- `ab2d631` reader;
- `077519c` table columns;
- `84386eb` advanced filters;
- `e388c56` scale and sync scheduler;
- `1ee03c3` grouping, snippets, high contrast, archive link;
- `48245f0` bulk actions;
- `9ca5e9b` empty states, attachment text, screenshots.

Earlier P0 shell and sidebar work landed before `0e51f19`.

| IDs | Observation | Disposition | Evidence |
|---|---|---|---|
| 1, 3, 6, 8 | Header spacing, tab size, hierarchy, density | Fixed | `UiDesignTests` header/tab tokens; screenshots 02, 03 |
| 2 | Active module distinction | Fixed | One tab style, selected state without geometry change (`UiDesignTests`) |
| 4, 54 | Chat/Soon presence | Fixed | No Chat tab; a saved Chat module opens Mail; T01 |
| 5, 7 | Search width, isolated settings | Fixed | Search box fills the header with its scope chip; settings gear beside it (screenshot 02) |
| 9, 14, 15 | Toolbar crowding, spacing, groups | Fixed | Grouped toolbar with separators; overflow menu; screenshot 11 |
| 10, 11, 12, 13 | Icons, disabled states, button consistency and height | Fixed | `UiDesignTests` icons and shared styles; disabled actions explain why (`MailListTests`) |
| 16, 17, 19 | Compose label, duplicate actions, selection hierarchy | Fixed | One primary button (New mail, enforced by test); reader actions reuse the toolbar commands |
| 18 | Low visibility of sync status | Fixed | Status area summary per account; sidebar badges (T14, screenshot 04) |
| 20, 21, 22 | Empty list space, duplicate labels and messages | Fixed | One empty state per case; count text never "0 of 0" (`MailListTests`); screenshot 01 |
| 23 | Orphan table headers | Fixed | Column header hidden over an empty list (`ShowColumnHeader`); screenshot 01 |
| 24, 25 | Header contrast, column proportions | Fixed | Shared column layout, resizable and saved (`MailColumnLayoutTests`) |
| 26, 27, 28, 29 | Empty list icon/text hierarchy, distance, panel boundary | Fixed | Centered icon, title, detail, action; screenshot 01 |
| 30–37 | Reader empty state structure, divider, hierarchy, mismatch, dominance | Fixed | Quiet centered reader state that matches the list ("Nothing to read here" or "No message selected"); screenshot 01 |
| 38–46 | Margins, spacing, radii, panel balance, gutters, alignment | Fixed | `UiDesignTests` spacing tokens and one gutter in every dock combination; `PaneLayoutTests` |
| 47–53 | Contrast, surfaces, accent, artwork, icons, focal point | Fixed | Light/dark palettes share tokens; 4.5:1 over pictures (`LegibilityTests`); high-contrast mapping (screenshot 15) |
| 55 | Unclear search scope | Fixed | Scope chip and placeholder name the scope (`GlobalSearchTests.PlaceholdersNameTheScope`) |
| 56–62 | Account management, overview, unified vs single scope, groups, collapse | Fixed | Account tree with unified folders, collapse remembered, context menu (`MailNavigationTests`; screenshots 02, 04) |
| 63–66 | Account and total unread, sync and failure indicators | Fixed | Unread per folder and unified without double counting; per-account sync badges (T03, T14) |
| 67, 68 | Account folders, nested IMAP folders | Fixed | `MailNavigationTests.CustomFoldersNest…`, `FoldersNestedUnderInbox…` |
| 69–73 | Identity markers, names, group scroll, counts, filters | Fixed | Initials are supplemental to name and address (`InitialsTests`); account column and filter |
| 74 | Account classification (personal/work) | N/A | No classification exists to show; the spec forbids inferring one. The account's own name is shown |
| 75, 76 | Compose and archive/trash account ambiguity | Fixed | Send-from rule (`ComposeIdentityTests`); bulk moves per account (`BulkActionsTests`) |
| 77 | Account context menu | Fixed | Sync now, sync status, account settings |
| 78–82 | Sender/subject truncation, columns, resizing, sorting | Fixed | Ellipsis with full-value tooltips; resizable columns; sortable headers (T07) |
| 83, 84 | Unread indicators and distinction | Fixed | Dot plus heavier text; selection is an edge plus surface, never color alone |
| 85, 86 | Attachment and flag indicators | Fixed | Shown only when metadata says so (row template; `MailColumnLayoutTests` sender/flag naming) |
| 87 | Message selection controls | Fixed | Shift/Ctrl multi-select with count; bulk actions apply to all selected |
| 88–91 | Selected text, sender formatting, row monotony, separators | Fixed | Sender fallback (name, address, "(unknown sender)"); row separators; selected edge |
| 92 | Snippets | Fixed | Comfortable density shows the start of the message (screenshot 14) |
| 93 | Date grouping | Fixed | Group by date while sorted by date (`MailListPresentationTests`; screenshot 14) |
| 94–97 | Scrollbar, filters, background, column separation | Fixed | Header inset for the scroll bar; filter menu and chips; column edges drawn as separators |
| 98 | Thread indicators and grouping | Limitation | No thread metadata stored for received mail; messages are listed individually and never grouped by subject or across accounts (§5) |
| 99, 100, 101 | HTML failure, images, original formatting | Fixed | `MailRenderingTests` M02–M04, M11, M12; HTML failure bar with retry |
| 102, 103 | Long raw URLs, link readability | Fixed | M01 and M03 (safe links), wrapping in plain text; M09 wide content contained |
| 104, 105, 106 | Error notice, retry, remote content controls | Fixed | "Try HTML again"; remote image bar with load once or always for sender (screenshot 07) |
| 107–110 | Body width, metadata separation, background, footer | Fixed | Plain text capped at 90ch; header block above body; neutral rendering surface |
| 111 | Rendering mode controls | Fixed | HTML / Plain text switch in the reader header (screenshot 07) |
| 112 | Attachment area | Fixed | Only when files exist (M06) |
| 113–118 | Sender, recipients, account, initials, details, hierarchy | Fixed | Reader sender block and Details panel (screenshot 08) |
| 119, 120 | Server status and local availability | Fixed | Availability label per message ("Available offline", headers only, on the server) |
| 121, 122 | Action proximity, sender actions | Fixed | Reply/Reply all/Forward beside the sender; copy address, new mail to sender |
| 123–129 | Global and per-account sync, progress, times, initial/incremental, partial, errors | Fixed | `AccountSyncStatusTests`, scheduler tests; last success never replaced by a failed attempt |
| 130, 131, 132 | Cache/server counts, offline, provider vs cloud | Fixed | Unknown counts shown as unknown; all-offline is the app being offline; cloud sync status separate |
| 133, 134, 135 | Multi-account navigation, scrolling, receiving account | Fixed | Sidebar scrolls on its own; account column and reader account label (screenshots 04, 08) |
| 136, 137 | Wrong sending identity, cross-account bulk risk | Fixed | `ComposeIdentityTests`, `BulkActionsTests` |
| 138–143 | Folder navigation, sync failures, density, duplicate folders, unread monitoring, account operations | Fixed | T03, T04, T14, T23 evidence |
| 144 | Ambiguous multi-account search results | Fixed | `GlobalSearchTests.MailResultsKeepAccountAndFolderIdentity…` |
| 145, 146 | Long account names, deep folder hierarchy | Fixed | Ellipsis with full name; nested folders; screenshot 04, `MailUiScaleTests` nested folders |
| 147, 148, 149 | Mail-only label, scope selector, all-module search | Fixed | Scope menu (module, All mail, All); five modules (`GlobalSearchTests`) |
| 150, 151 | Account/folder scope | Fixed | Module scope follows the selected account and folder; account filter in All mail |
| 152, 153 | Local/cloud and active/archive search | Fixed | Cloud archive is a separate explicit source (§3.4); local results never depend on it |
| 154 | Advanced search | Fixed | `MailAdvancedFilterTests`; filter dialog (screenshot 13) |
| 155 | Attachment content search | Fixed, with limitation | Plain-text files on this PC (`AttachmentTextTests`); other formats by name (§5) |
| 156, 157 | History and shortcuts | Fixed | No history stored (nothing to leak or clear); Ctrl+K, Enter, Esc, arrows |
| 158 | Scope on module switching | Fixed | All persists; All mail falls back in other modules (`GlobalSearchTests`) |
| 159, 160 | Result origin and source coverage | Fixed | Account · folder on every mail result; coverage line per module |
| 161, 162, 163 | Local/remote availability, index completeness, offline search | Fixed | Headers-only coverage stated; local search works offline |
| 164 | Workspace search boundary | Verified | One local user profile; All searches this profile's modules only; there is no cross-workspace search to expose |

## 5. Known limitations (accepted for Dev)

1. **No thread grouping.** The local store doesn't keep References/In-Reply-To headers for received mail, so messages are listed individually.
2. **Attachment content search.** It covers plain-text files already on this PC (.txt, .csv, .md, .log; up to 1 MB each, 200 per query) and reads them at query time. PDF and Office files are searched by name. No separately reviewed parser was added.
3. **Cc and Reply-To.** The local store keeps all recipients in one field, so the reader's Details panel lists them together.
4. **"Add contact" from the reader.** Not offered: contacts come from Microsoft 365 or Google, and there is no local write path yet.
5. **Manual checks not executed:**
   - Windows scaling at 125, 150 and 200 %;
   - a real Windows contrast theme (only the forced mapping is captured, screenshot 15);
   - a recorded Narrator pass;
   - 10 real provider accounts syncing at once (fixtures only; dev mail stays in Mailpit by owner decision).
6. **Very wide fixed-layout newsletters.** These scroll horizontally inside the reading pane when it is narrower than the newsletter (screenshot 07). This follows the spec's rule of horizontal scroll only inside content that needs it.
7. **Timing hardware.** Timings were measured on hardware stronger than the suggested reference machine.

## 6. Migration and rollback

- **No database schema change.** This work adds only read queries:
  - `ListPageAsync`, `SearchPageAsync`;
  - `FindByAttachmentNameAsync`, `ListDownloadedAttachmentsAsync`.
- **New settings are optional fields** with defaults:
  - `Layout.MailColumnFrom`, `MailColumnAccount`, `MailColumnReceived`, `MailSort`, `MailSortDescending`, `SearchReach`;
  - `Appearance.GroupMailByDate`;
  - `AccountConfig.LastMailSyncUtc`;
  - `Reading.DefaultSendAccountId`.
- **Rolling back.** An earlier build ignores these fields, and the configuration schema version is unchanged.
- **Data is untouched.** Accounts, message ids, drafts, folder mappings and cached mail are left as they are.
