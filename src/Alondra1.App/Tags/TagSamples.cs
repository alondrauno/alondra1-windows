using Alondra1.Core.Demo;
using Alondra1.Core.Notes;
using Alondra1.Core.Tags;
using Alondra1.Core.Tasks;
using Alondra1.App.ViewModels;

namespace Alondra1.App.Tags;

/// <summary>Sample tags for the demo (sample data only): Travel, Work and Home across mail, tasks and notes.</summary>
public static class TagSamples
{
    public static async Task SeedAsync(TagBoard tags, TaskBoard? tasks, NotesBoard? notes)
    {
        if (tags.Tags.Count > 0)
            return;
        var travel = await tags.CreateAsync("Travel", "blue");
        var work = await tags.CreateAsync("Work", "purple");
        var home = await tags.CreateAsync("Home", "green");
        var inbox = SampleCatalog.Messages().Where(m => m.FolderId == "Inbox").ToList();
        void Mail(Tag tag, params string[] subjects)
        {
            var picked = inbox.Where(m => subjects.Contains(m.Subject)).ToList();
            _ = tags.SetTagAsync(TagRules.Mail, picked.Select(MainViewModel.MailRef).ToList(), tag.Id, true, MainViewModel.MailTargets(picked));
        }

        Mail(travel, "Booking confirmation", "Meeting moved to Tuesday");
        Mail(work, "Re: Friday handoff", "Invoice for November", "Summary of open tickets");
        Mail(home, "Coffee next week?");
        if (tasks is not null)
        {
            var open = tasks.Tasks.Where(t => !t.Completed).Take(3).Select(t => t.Id).ToList();
            if (open.Count > 0)
                await tags.SetTagAsync(TagRules.Task, open.Take(2).ToList(), work.Id, true);
            if (open.Count > 2)
                await tags.SetTagAsync(TagRules.Task, [open[2]], home.Id, true);
        }

        if (notes is null)
            return;
        // The sample notes may still be on their way (they are added in the background): tag them when they appear.
        async Task TagNotesAsync()
        {
            if (notes.Notes.OrderBy(n => n.CreatedUtc).Take(2).Select(n => n.Id).ToList() is { Count: > 0 } ids && tags.Find(travel.Id) is not null)
                await tags.SetTagAsync(TagRules.Note, ids, travel.Id, true);
        }

        if (notes.Notes.Count > 0)
        {
            await TagNotesAsync();
            return;
        }

        void Once()
        {
            if (notes.Notes.Count == 0)
                return;
            notes.Changed -= Once;
            _ = TagNotesAsync();
        }

        notes.Changed += Once;
    }
}
