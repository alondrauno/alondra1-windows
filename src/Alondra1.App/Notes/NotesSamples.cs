using System.Text.Json.Nodes;
using System.Windows;
using Alondra1.Core.Notes;

namespace Alondra1.App.Notes;

/// <summary>Sample notes for demo runs (screenshots, first look). Never written outside demo mode.</summary>
public static class NotesSamples
{
    public static async Task SeedAsync(NotesBoard board, NoteAssets? assets)
    {
        if (board.Notes.Count > 0)
            return;
        string? picture = null;
        if (assets is not null && Application.GetResourceStream(new Uri("pack://application:,,,/Assets/logo-64.png")) is { } resource)
        {
            using var stream = new MemoryStream();
            await resource.Stream.CopyToAsync(stream);
            picture = (await assets.AddAsync(stream.ToArray())).Sha256;
        }

        var work = await board.AddFolderAsync("Work");
        await board.AddFolderAsync("Personal");
        var kickoff = Parse("""
            {"schemaVersion":1,"type":"doc","content":[
              {"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Goals"}]},
              {"type":"paragraph","content":[
                {"type":"text","text":"Ship the "},
                {"type":"text","text":"Notes module","marks":[{"type":"bold"},{"type":"textStyle","attrs":{"color":"#2563EB"}}]},
                {"type":"text","text":" by "},
                {"type":"text","text":"Friday","marks":[{"type":"highlight","attrs":{"color":"#FDE047"}}]},
                {"type":"text","text":". Keep it "},
                {"type":"text","text":"local-first","marks":[{"type":"italic"},{"type":"textStyle","attrs":{"fontSize":"16pt","fontFamily":"Georgia"}}]},
                {"type":"text","text":"."}]},
              {"type":"taskList","content":[
                {"type":"taskItem","attrs":{"checked":true},"content":[{"type":"paragraph","content":[{"type":"text","text":"Agree on the document format"}]}]},
                {"type":"taskItem","attrs":{"checked":false},"content":[{"type":"paragraph","content":[{"type":"text","text":"Tables and pictures"}]},
                  {"type":"taskList","content":[{"type":"taskItem","attrs":{"checked":false},"content":[{"type":"paragraph","content":[{"type":"text","text":"Resize columns"}]}]}]}]},
                {"type":"taskItem","attrs":{"checked":false},"content":[{"type":"paragraph","content":[{"type":"text","text":"Sync with Android"}]}]}]},
              {"type":"table","content":[
                {"type":"tableRow","content":[
                  {"type":"tableHeader","attrs":{"colspan":1,"rowspan":1},"content":[{"type":"paragraph","content":[{"type":"text","text":"Owner"}]}]},
                  {"type":"tableHeader","attrs":{"colspan":1,"rowspan":1},"content":[{"type":"paragraph","content":[{"type":"text","text":"Task"}]}]},
                  {"type":"tableHeader","attrs":{"colspan":1,"rowspan":1},"content":[{"type":"paragraph","attrs":{"textAlign":"right"},"content":[{"type":"text","text":"Days"}]}]}]},
                {"type":"tableRow","content":[
                  {"type":"tableCell","attrs":{"colspan":1,"rowspan":1},"content":[{"type":"paragraph","content":[{"type":"text","text":"Ana"}]}]},
                  {"type":"tableCell","attrs":{"colspan":1,"rowspan":1},"content":[{"type":"paragraph","content":[{"type":"text","text":"Editor"}]}]},
                  {"type":"tableCell","attrs":{"colspan":1,"rowspan":1,"backgroundColor":"#DCFCE7"},"content":[{"type":"paragraph","attrs":{"textAlign":"right"},"content":[{"type":"text","text":"3"}]}]}]},
                {"type":"tableRow","content":[
                  {"type":"tableCell","attrs":{"colspan":1,"rowspan":1},"content":[{"type":"paragraph","content":[{"type":"text","text":"Sam"}]}]},
                  {"type":"tableCell","attrs":{"colspan":1,"rowspan":1},"content":[{"type":"paragraph","content":[{"type":"text","text":"Sync"}]}]},
                  {"type":"tableCell","attrs":{"colspan":1,"rowspan":1,"backgroundColor":"#FEF3C7"},"content":[{"type":"paragraph","attrs":{"textAlign":"right"},"content":[{"type":"text","text":"5"}]}]}]}]},
              PICTURE
              {"type":"blockquote","content":[{"type":"paragraph","content":[{"type":"text","text":"Everything works offline; Cloud Sync is optional."}]}]}
            ]}
            """.Replace("PICTURE", picture is null ? "" : "{\"type\":\"image\",\"attrs\":{\"assetId\":\"" + picture + "\",\"width\":96,\"alignment\":\"left\",\"caption\":\"Alondra1\"}},", StringComparison.Ordinal));
        var first = await board.CreateAsync(work.Id, "Project kickoff", kickoff, text: "Goals\nShip the Notes module by Friday.", assets: picture is null ? null : [picture]);
        await board.SetFavoriteAsync([first.Id], true);
        await board.CreateAsync(null, "Groceries", Parse("""
            {"schemaVersion":1,"type":"doc","content":[{"type":"taskList","content":[
              {"type":"taskItem","attrs":{"checked":false},"content":[{"type":"paragraph","content":[{"type":"text","text":"Oat milk"}]}]},
              {"type":"taskItem","attrs":{"checked":true},"content":[{"type":"paragraph","content":[{"type":"text","text":"Bread"}]}]},
              {"type":"taskItem","attrs":{"checked":false},"content":[{"type":"paragraph","content":[{"type":"text","text":"Coffee"}]}]}]}]}
            """), text: "[ ] Oat milk\n[x] Bread\n[ ] Coffee");
        await board.CreateAsync(work.Id, "Meeting notes · Design review", Parse("""
            {"schemaVersion":1,"type":"doc","content":[
              {"type":"bulletList","content":[
                {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Rounded tabs stay"}]}]},
                {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Dark theme contrast"}]},
                  {"type":"orderedList","attrs":{"start":1},"content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Check muted text"}]}]}]}]}]}]}
            """), text: "Rounded tabs stay\nDark theme contrast");
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;
}
