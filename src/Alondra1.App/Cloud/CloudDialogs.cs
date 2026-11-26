using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Alondra1.App.ViewModels;
using Alondra1.App.Notes;
using Alondra1.Core.Cloud;

namespace Alondra1.App.Cloud;

/// <summary>Settings → Cloud: optional account (email code), plan, Cloud Sync, sync issues, devices; and the archive window.</summary>
public static class CloudDialogs
{
    /// <summary>The Cloud settings page. It redraws itself after every action; nothing here blocks local work.</summary>
    public static UIElement Page(MainViewModel model)
    {
        var page = new StackPanel { Margin = new Thickness(24, 20, 24, 24), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        _ = RenderAsync(page, model);
        return page;
    }

    private static async Task RenderAsync(StackPanel page, MainViewModel model)
    {
        var cloud = model.Cloud;
        page.Children.Clear();
        page.Children.Add(Dialogs.Title("Alondra1 account & Cloud"));
        if (!cloud.Available || cloud.Account is null)
        {
            page.Children.Add(Dialogs.Note("Cloud features are not available: the to-do database could not be opened or the cloud address is invalid. Mail, calendar, notes and tasks keep working on this PC."));
            return;
        }

        page.Children.Add(Dialogs.Note("Optional. Alondra1 works fully on this PC without an account. A paid plan adds Cloud Sync of your tasks with your other devices, and a cloud Mail Archive."));
        async Task Redraw() => await RenderAsync(page, model);

        if (!await cloud.Account.IsSignedInAsync(CancellationToken.None))
        {
            SignIn(page, cloud, Redraw);
            return;
        }

        var email = await cloud.Account.EmailAsync(CancellationToken.None) ?? "your account";
        page.Children.Add(Dialogs.Subtitle("Account", first: true));
        page.Children.Add(Dialogs.Note($"Signed in as {email}."));
        var loading = Dialogs.Note("Loading your plan…");
        page.Children.Add(loading);

        CloudSpace space;
        CloudEntitlements rights;
        try
        {
            space = await cloud.Account.PersonalSpaceAsync(CancellationToken.None);
            rights = await cloud.Account.EntitlementsAsync(space.Id, CancellationToken.None);
        }
        catch (CloudApiException ex)
        {
            loading.Text = ex.IsNetwork ? "The Alondra1 cloud cannot be reached right now. Your data on this PC is not affected." : ex.Message;
            page.Children.Add(Dialogs.ButtonRow(Dialogs.Button("Try again", Redraw), Dialogs.Button("Log out", async () => { await cloud.LogoutAsync(); await Redraw(); })));
            return;
        }

        page.Children.Remove(loading);
        page.Children.Add(Dialogs.Subtitle("Plan"));
        page.Children.Add(Dialogs.Note($"{rights.PlanName} ({rights.State.Replace('_', ' ')}). {rights.Describe()}"));
        if (rights.Plan == "free" || rights.State != "active")
        {
            try
            {
                var plans = await cloud.Account.PlansAsync(CancellationToken.None);
                foreach (var plan in plans.Where(p => p.PriceCents > 0))
                    page.Children.Add(Dialogs.Note($"• {plan.Name}: {(plan.StartingAt ? "from " : "")}{plan.PriceCents / 100m:0.00} {plan.Currency}/{plan.Interval}"));
            }
            catch (CloudApiException)
            {
                // The price list is informational only.
            }

            page.Children.Add(Dialogs.Note("Online payment is not open yet. Upgrades will be available from your account page."));
        }

        // ---- Cloud Sync
        var engine = cloud.Engine!;
        page.Children.Add(Dialogs.Subtitle("Cloud Sync for To-do"));
        var enabled = await engine.EnabledAsync();
        var status = Dialogs.Note(cloud.StatusDetail);
        page.Children.Add(status);
        if (!rights.Sync)
        {
            page.Children.Add(Dialogs.Note("Cloud Sync needs a paid plan. Your tasks stay on this PC."));
        }
        else if (!enabled)
        {
            var preview = engine.Preview();
            page.Children.Add(Dialogs.Note($"Turning it on merges this PC's {preview.LocalTasks} tasks in {preview.LocalLists} lists with the tasks already in your cloud. Nothing is replaced or deleted, and a copy of todo.db is saved in the backups folder first."));
            page.Children.Add(Dialogs.ButtonRow(Dialogs.Primary("Turn on Cloud Sync", async () =>
            {
                var owner = Window.GetWindow(page);
                if (Dialogs.Alert(owner, $"Merge {preview.LocalTasks} tasks from this PC with your cloud tasks?\n\nNothing is deleted. A backup of todo.db is made first.", "Turn on Cloud Sync", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                    return;
                status.Text = "Merging… this can take a moment.";
                var result = await cloud.EnableAsync(space.Id);
                await Redraw();
                if (result.State != SyncState.Synced)
                    Dialogs.Alert(owner, result.Message ?? cloud.StatusText, "Cloud Sync");
            })));
        }
        else
        {
            if (!rights.SyncWrite)
                page.Children.Add(Dialogs.Note("Your plan has ended: changes from other devices still arrive, but new changes stay on this PC until you renew."));
            page.Children.Add(Dialogs.ButtonRow(
                Dialogs.Primary("Sync now", async () => { status.Text = "Syncing…"; await cloud.SyncAsync(); await Redraw(); }),
                Dialogs.Button("Pause", async () => { await cloud.PauseAsync(); await Redraw(); })));
            if (cloud.Engine is { } syncEngine)
            {
                var choice = await syncEngine.LegacyTaskFilesChoiceAsync();
                page.Children.Add(Dialogs.Note(choice switch
                {
                    true => "Files attached before Cloud Sync handled attachments: uploaded to your cloud space.",
                    false => "Files attached before Cloud Sync handled attachments: kept on this PC only.",
                    _ => "Files attached before Cloud Sync handled attachments: on this PC only until you decide.",
                }));
                page.Children.Add(Dialogs.ButtonRow(Dialogs.Button("Existing attachments…", async () => { await cloud.AskAboutLegacyTaskFilesAsync(again: true); await Redraw(); })));
            }
        }

        if (cloud.Store is { } store)
        {
            var issues = await store.IssuesAsync(CancellationToken.None);
            if (issues.Count > 0)
            {
                page.Children.Add(Dialogs.Subtitle($"Sync issues ({issues.Count})"));
                foreach (var issue in issues)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                    var dismiss = Dialogs.Button("Dismiss", async () => { await store.DismissIssueAsync(issue.Id, CancellationToken.None); await Redraw(); });
                    DockPanel.SetDock(dismiss, Dock.Right);
                    row.Children.Add(dismiss);
                    if (issue.CopyId is not null && model.Notes.IsAvailable)
                    {
                        // A note conflict: compare both versions and choose (Notes spec 10.2).
                        var compare = Dialogs.Button("Compare", async () =>
                        {
                            await NoteConflictDialog.ShowAsync(Window.GetWindow(page), model.Notes, issue);
                            await Redraw();
                        });
                        DockPanel.SetDock(compare, Dock.Right);
                        row.Children.Add(compare);
                    }
                    row.Children.Add(new TextBlock { Text = $"{issue.Title}: {issue.Message}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                    page.Children.Add(row);
                }
            }
        }

        // ---- Mail Archive
        page.Children.Add(Dialogs.Subtitle("Mail Archive"));
        page.Children.Add(Dialogs.Note(rights.Archive
            ? $"{rights.ArchiveMessages} messages ({rights.ArchiveBytes / 1024d / 1024d:0.0} MB) in the cloud archive. In Mail, choose More → Archive to Alondra1 Cloud. Your mailbox is never changed."
            : "Mail Archive is part of the paid plans."));
        if (rights.ArchiveSearch)
            page.Children.Add(Dialogs.ButtonRow(Dialogs.Button("Search the archive", () => ShowArchive(Window.GetWindow(page), cloud, space.Id))));

        // ---- Devices
        page.Children.Add(Dialogs.Subtitle("Devices"));
        try
        {
            foreach (var device in await cloud.Account.DevicesAsync(CancellationToken.None))
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                if (!device.Current)
                {
                    var remove = Dialogs.Button("Sign out", async () => { await cloud.Account.RemoveDeviceAsync(device.Id, CancellationToken.None); await Redraw(); });
                    DockPanel.SetDock(remove, Dock.Right);
                    row.Children.Add(remove);
                }

                row.Children.Add(new TextBlock
                {
                    Text = $"{device.Name} · {device.Platform} · last seen {device.LastSeen.ToLocalTime():g}{(device.Current ? " · this PC" : "")}",
                    VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                });
                page.Children.Add(row);
            }
        }
        catch (CloudApiException)
        {
            page.Children.Add(Dialogs.Note("The device list is not available right now."));
        }

        page.Children.Add(Dialogs.Subtitle("Session"));
        page.Children.Add(Dialogs.Note("Logging out keeps every task, message and setting on this PC. Cloud Sync pauses until you sign in again."));
        page.Children.Add(Dialogs.ButtonRow(
            Dialogs.Button("Manage account online", () => Open(new Uri(cloud.BaseUri!, "account/"))),
            Dialogs.Button("Log out", async () => { await cloud.LogoutAsync(); await Redraw(); })));
    }

    private static void SignIn(StackPanel page, CloudController cloud, Func<Task> redraw)
    {
        page.Children.Add(Dialogs.Subtitle("Sign in or create an account", first: true));
        page.Children.Add(Dialogs.Note("Enter your email address. We send a 6-digit code; there is no password."));
        var email = new TextBox();
        page.Children.Add(Dialogs.Labeled("Email", email));
        var codePanel = new StackPanel { Visibility = Visibility.Collapsed };
        var code = new TextBox { MaxLength = 6, Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        codePanel.Children.Add(Dialogs.Labeled("Code from the email", code));
        var feedback = Dialogs.Note("");
        string? challenge = null;
        page.Children.Add(Dialogs.ButtonRow(Dialogs.Primary("Send code", async () =>
        {
            if (!email.Text.Contains('@'))
            {
                feedback.Text = "Enter a valid email address.";
                return;
            }

            try
            {
                challenge = await cloud.Account!.StartAsync(email.Text, CancellationToken.None);
                codePanel.Visibility = Visibility.Visible;
                feedback.Text = "If the address can sign in, a code is on its way. It is valid for 10 minutes.";
                code.Focus();
            }
            catch (CloudApiException ex)
            {
                feedback.Text = ex.IsNetwork ? "The Alondra1 cloud cannot be reached. Check the connection and try again." : ex.Message;
            }
        })));
        codePanel.Children.Add(Dialogs.ButtonRow(Dialogs.Primary("Sign in", async () =>
        {
            if (challenge is null)
                return;
            try
            {
                await cloud.Account!.VerifyAsync(email.Text, challenge, code.Text, CancellationToken.None);
                await cloud.SyncAsync();
                await redraw();
            }
            catch (CloudApiException ex)
            {
                feedback.Text = ex.Code switch
                {
                    "OTP_INVALID" => "That code is not right. Check the latest email and try again.",
                    "OTP_EXPIRED" => "That code has expired. Send a new one.",
                    "OTP_LOCKED" or "RATE_LIMITED" => "Too many attempts. Wait a few minutes and send a new code.",
                    _ => ex.Message,
                };
            }
        })));
        page.Children.Add(codePanel);
        page.Children.Add(feedback);
    }

    private static void Open(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Dialogs.Alert(uri.ToString(), "Open in your browser");
        }
    }

    /// <summary>Search the paid cloud archive (local mail search stays free and separate).</summary>
    public static void ShowArchive(Window? owner, CloudController cloud, string tenantId)
    {
        var window = Dialogs.Create("Cloud Mail Archive", owner ?? Application.Current.MainWindow, 760, 600);
        var panel = Dialogs.Panel(window);
        var client = new ArchiveClient(cloud.Account!.Api);
        var query = new TextBox();
        panel.Children.Add(Dialogs.Labeled("Search subject, sender and text", query));
        var results = new ListBox { MinHeight = 320 };
        System.Windows.Automation.AutomationProperties.SetName(results, "Archived messages");
        var detail = Dialogs.Note("");
        detail.TextWrapping = TextWrapping.Wrap;
        var found = new List<ArchivedMessage>();

        async Task Search()
        {
            try
            {
                var (messages, _) = await client.SearchAsync(tenantId, new ArchiveQuery(Text: string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim(), Limit: 50), CancellationToken.None);
                found = messages.ToList();
                results.Items.Clear();
                foreach (var m in found)
                    results.Items.Add($"{m.SentAt?.ToLocalTime():d}  {m.From}  —  {m.Subject}");
                detail.Text = found.Count == 0 ? "No archived messages match." : $"{found.Count} messages.";
            }
            catch (CloudApiException ex)
            {
                detail.Text = ex.Message;
            }
        }

        results.SelectionChanged += async (_, _) =>
        {
            if (results.SelectedIndex < 0 || results.SelectedIndex >= found.Count)
                return;
            try
            {
                var m = await client.GetAsync(tenantId, found[results.SelectedIndex].Id, CancellationToken.None);
                detail.Text = $"{m.Subject}\nFrom {m.From} to {string.Join(", ", m.To)}\n\n{(m.Text.Length > 4000 ? m.Text[..4000] + "…" : m.Text)}";
            }
            catch (CloudApiException ex)
            {
                detail.Text = ex.Message;
            }
        };
        panel.Children.Add(Dialogs.ButtonRow(Dialogs.Primary("Search", Search)));
        panel.Children.Add(results);
        panel.Children.Add(detail);
        var footer = Dialogs.Footer(window);
        footer.Children.Add(Dialogs.Cancel(window));
        _ = Search();
        window.ShowDialog();
    }
}
