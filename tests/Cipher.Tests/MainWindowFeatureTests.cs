using Cipher;

namespace Cipher.Tests;

public class MainWindowFeatureTests {
    const string SampleUserId = "abcdefghijklmnopqrstuv";

    [Fact]
    public void NormalizeContactDisplayNameFallsBackToShortUserIdWhenNicknameMissing() {
        var displayName = MainWindow.NormalizeContactDisplayName(SampleUserId, "   ");

        Assert.Equal(MainWindow.ShortUserId(SampleUserId), displayName);
    }

    [Fact]
    public void NormalizeContactDisplayNameUsesTrimmedNicknameWhenProvided() {
        var displayName = MainWindow.NormalizeContactDisplayName(SampleUserId, "  Alice  ");

        Assert.Equal("Alice", displayName);
    }

    [Fact]
    public void NormalizeContactDisplayNameUsesPublicProfileNameWhenNicknameMissing() {
        var displayName = MainWindow.NormalizeContactDisplayName(SampleUserId, "   ", "  Alice  ");

        Assert.Equal("Alice", displayName);
    }

    [Fact]
    public void InviteContactMatchesQueryChecksBothNameAndUserId() {
        var option = new InviteContactOptionViewModel {
            Contact = new Contact {
                UserId = SampleUserId,
                DisplayName = "Alice"
            },
            GroupName = "Squad"
        };

        Assert.True(MainWindow.InviteContactMatchesQuery(option, "ali"));
        Assert.True(MainWindow.InviteContactMatchesQuery(option, "mnop"));
        Assert.False(MainWindow.InviteContactMatchesQuery(option, "zeus"));
    }

    [Fact]
    public void InviteContactOptionMarksExistingMembersAsAlreadyInGroup() {
        var option = new InviteContactOptionViewModel {
            Contact = new Contact {
                UserId = SampleUserId,
                DisplayName = "Alice"
            },
            GroupName = "Raid Team",
            IsAlreadyMember = true
        };

        Assert.False(option.CanInvite);
        Assert.Equal("in group", option.BadgeText);
        Assert.Contains("already in Raid Team", option.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public void EmojiOnlyTripleMessageUsesBubblelessProfile() {
        var profile = MsgBodyHelper.BuildRenderProfile("😀😀😀", 17d);

        Assert.True(profile.IsEmojiOnly);
        Assert.Equal(3, profile.EmojiCount);
        Assert.True(profile.UseBubblelessLayout);
        Assert.Equal(34d, profile.EffectiveFontSize);
    }

    [Fact]
    public void EmojiOnlyQuadMessageKeepsBubbleLayout() {
        var profile = MsgBodyHelper.BuildRenderProfile("😀😀😀😀", 17d);

        Assert.True(profile.IsEmojiOnly);
        Assert.Equal(4, profile.EmojiCount);
        Assert.False(profile.UseBubblelessLayout);
        Assert.Equal(34d, profile.EffectiveFontSize);
    }
}
