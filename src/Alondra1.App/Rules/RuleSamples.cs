using Alondra1.Core.Rules;
using Alondra1.Core.Tags;

namespace Alondra1.App.Rules;

/// <summary>Sample rules and block entries for the demo (sample data only; demo mode has no real accounts).</summary>
public static class RuleSamples
{
    public static async Task SeedAsync(MailRuleService rules, TagBoard? tags)
    {
        if (rules.Rules.Count > 0 || rules.Blocks.Count > 0)
            return;
        var work = tags?.FindByName("Work");
        if (work is not null)
        {
            await rules.SaveRuleAsync(new MailRule
            {
                Name = "Invoices", Enabled = true, Direction = RuleDirection.Incoming,
                Conditions = [new RuleCondition { Field = RuleField.Subject, Operator = RuleOperator.Contains, Value = "invoice" }],
                Actions = [new RuleAction { Kind = RuleActionKind.AddTag, Value = work.Id }, new RuleAction { Kind = RuleActionKind.Flag }],
            });
        }

        await rules.SaveRuleAsync(new MailRule
        {
            Name = "Newsletters", Enabled = true, Direction = RuleDirection.Incoming, Match = RuleMatch.Any, StopProcessing = true,
            Conditions = [new RuleCondition { Field = RuleField.From, Operator = RuleOperator.EndsWith, Value = "@news.example.com" },
                new RuleCondition { Field = RuleField.Subject, Operator = RuleOperator.Contains, Value = "weekly digest" }],
            Actions = [new RuleAction { Kind = RuleActionKind.MarkRead }, new RuleAction { Kind = RuleActionKind.Archive }],
        });
        await rules.SaveRuleAsync(new MailRule
        {
            Name = "Outside the company", Enabled = true, Direction = RuleDirection.Outgoing,
            Conditions = [new RuleCondition { Field = RuleField.Recipients, Operator = RuleOperator.EndsWith, Value = "@partner.example.org" }],
            Actions = [new RuleAction { Kind = RuleActionKind.Warn, Value = "This message goes to a partner outside the company." }],
        });
        await rules.SaveBlockAsync(new BlockEntry { Kind = BlockKind.Domain, Value = "offers.example.net", Mode = BlockMode.Broad });
        await rules.SaveBlockAsync(new BlockEntry { Kind = BlockKind.SubjectText, Value = "limited time offer" });
    }
}
