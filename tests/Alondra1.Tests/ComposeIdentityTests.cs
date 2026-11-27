using Alondra1.Core.Config;
using Alondra1.Core.Mail;

namespace Alondra1.Tests;

/// <summary>Mail UI spec §3.2 (T05): the sending account is explicit and never guessed.</summary>
public class ComposeIdentityTests
{
    private static readonly AccountConfig Work = new() { Id = "w", DisplayName = "Work", Address = "me@work.example" };
    private static readonly AccountConfig Home = new() { Id = "h", DisplayName = "Home", Address = "me@home.example" };
    private static readonly AccountConfig[] Both = [Work, Home];

    [Fact]
    public void NewMailInAnAccountViewUsesThatAccount() =>
        Assert.Equal("h", ComposeIdentity.DefaultAccount(Both, new MailScope("h", FolderRole.Inbox), null, null));

    [Fact]
    public void NewMailInTheUnifiedViewNeedsAChoiceWithoutADefault() =>
        Assert.Null(ComposeIdentity.DefaultAccount(Both, MailScope.UnifiedInbox, null, null));

    [Fact]
    public void NewMailInTheUnifiedViewUsesTheConfiguredDefault() =>
        Assert.Equal("h", ComposeIdentity.DefaultAccount(Both, MailScope.UnifiedInbox, null, "h"));

    [Fact]
    public void AnUnknownDefaultIsIgnored() =>
        Assert.Null(ComposeIdentity.DefaultAccount(Both, MailScope.UnifiedInbox, null, "gone"));

    [Fact]
    public void TheOnlyAccountNeedsNoChoice() =>
        Assert.Equal("w", ComposeIdentity.DefaultAccount([Work], MailScope.UnifiedInbox, null, null));

    [Fact]
    public void ReplyUsesTheReceivingAccountEvenInAnotherAccountsView() =>
        Assert.Equal("h", ComposeIdentity.DefaultAccount(Both, new MailScope("w", FolderRole.Inbox), "h", "w"));

    [Fact]
    public void ReplyAfterTheReceivingAccountWasRemovedNeedsAChoice() =>
        Assert.Null(ComposeIdentity.DefaultAccount(Both, MailScope.UnifiedInbox, "removed", "w"));

    [Fact]
    public void ReplyAllAddsEveryoneButMe()
    {
        var message = new StoredMessage { FromAddress = "maya@example.com", Recipients = "Me <ME@work.example>, liam@example.com; \"Ava, P\" <ava@example.com>, maya@example.com" };
        Assert.Equal(["maya@example.com", "liam@example.com", "ava@example.com"], ComposeIdentity.ReplyAllRecipients(message, "me@work.example"));
    }

    [Fact]
    public void LabelShowsNameAndAddress()
    {
        Assert.Equal("Work <me@work.example>", ComposeIdentity.Label(Work));
        Assert.Equal("x@example.com", ComposeIdentity.Label(new AccountConfig { DisplayName = "", Address = "x@example.com" }));
    }
}

public class InitialsTests
{
    [Theory]
    [InlineData("Maya Chen", "MC")]
    [InlineData("Work (you@work.example)", "W")]
    [InlineData("O'Brien, Liam", "OL")]
    [InlineData("Ава Петрова", "АП")]
    [InlineData("you@work.example", "YW")]
    [InlineData("  ", "?")]
    public void InitialsComeFromTheNameWithoutPunctuation(string name, string expected) =>
        Assert.Equal(expected, Alondra1.Core.Mail.ComposeIdentity.Initials(name));
}
