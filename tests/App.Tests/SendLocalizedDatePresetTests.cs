using System.Globalization;
using App.Localization;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public sealed class SendLocalizedDatePresetTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-12T10:20:30.1234567+00:00");

    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void DeletionChoices_UseStableValuesRegardlessOfTranslatedDisplay(string language)
    {
        var options = SendDatePresetOptions.CreateDeletionOptions(resolve: Resolver(language));
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        foreach (var preset in new[] { SendDatePreset.OneHour, SendDatePreset.OneDay, SendDatePreset.TwoDays,
            SendDatePreset.ThreeDays, SendDatePreset.SevenDays, SendDatePreset.ThirtyDays })
        {
            var option = Assert.Single(options, option => option.Value == preset);
            Assert.NotEqual(SendDatePresetOptions.DisplayLabel(preset, relative: true), option.Label);

            draft.SelectDeletion(option.Value, Now);

            Assert.Equal(preset, draft.DeletionPreset);
            Assert.Equal(Now + SendDatePresetOptions.RelativeDuration(preset), draft.DeletionDate);
            var beforeSubmission = DateTimeOffset.UtcNow;
            var submitted = draft.ToDeletionDate();
            var afterSubmission = DateTimeOffset.UtcNow;
            Assert.InRange(submitted, beforeSubmission + SendDatePresetOptions.RelativeDuration(preset),
                afterSubmission + SendDatePresetOptions.RelativeDuration(preset));
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void ExpirationChoices_UseStableValuesRegardlessOfTranslatedDisplay(string language)
    {
        var options = SendDatePresetOptions.CreateExpirationOptions(resolve: Resolver(language));
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        foreach (var preset in new[] { SendDatePreset.OneHour, SendDatePreset.OneDay, SendDatePreset.TwoDays,
            SendDatePreset.ThreeDays, SendDatePreset.SevenDays })
        {
            var option = Assert.Single(options, option => option.Value == preset);
            Assert.NotEqual(SendDatePresetOptions.DisplayLabel(preset, relative: true, expiration: true), option.Label);

            draft.SelectExpiration(option.Value, Now);

            Assert.Equal(preset, draft.ExpirationPreset);
            Assert.False(draft.IsCustomExpiration);
            Assert.Equal(Now + SendDatePresetOptions.RelativeDuration(preset), draft.ExpirationDate);
        }

        draft.SelectExpiration(Assert.Single(options, option => option.Value == SendDatePreset.Never).Value, Now);
        Assert.Null(draft.ExpirationDate);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void CustomChoice_PreservesTheChosenInstantAndUsesAnIdentityForVisibility(string language)
    {
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        var expiration = DateTimeOffset.Parse("2026-12-03T14:25:36.1234567+08:00");
        draft.ExpirationDate = expiration;
        draft.DeletionDate = expiration.AddDays(1);
        var expirationChoice = Assert.Single(
            SendDatePresetOptions.CreateExpirationOptions(compact: true, resolve: Resolver(language)),
            option => option.Value == SendDatePreset.Custom);
        var deletionChoice = Assert.Single(
            SendDatePresetOptions.CreateDeletionOptions(compact: true, resolve: Resolver(language)),
            option => option.Value == SendDatePreset.Custom);

        draft.SelectExpiration(expirationChoice.Value, Now);
        draft.SelectDeletion(deletionChoice.Value, Now);

        Assert.True(draft.IsCustomExpiration);
        Assert.True(draft.IsCustomDeletion);
        Assert.Equal(expiration.ToString("O"), draft.ExpirationDate?.ToString("O"));
        Assert.Equal(expiration.AddDays(1).ToString("O"), draft.DeletionDate?.ToString("O"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void KeepExistingChoice_UsesTheRequestedCultureAndDoesNotParseItsDisplayPrefix(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        var date = DateTimeOffset.Parse("2026-12-03T14:25:36.1234567+08:00");
        var options = SendDatePresetOptions.CreateExpirationOptions(
            includeKeepExisting: true, existingDate: date, resolve: Resolver(language), culture: culture);
        var option = Assert.Single(options, option => option.Value == SendDatePreset.KeepExisting);
        var expectedDate = date.ToLocalTime().ToString("g", culture);
        Assert.Contains(expectedDate, option.Label);
        Assert.DoesNotContain("保持不变", option.Label, StringComparison.Ordinal);
        var selection = SendExpirationSelection.FromExpiration(date);

        var unchanged = selection.Select(option.Value, Now);

        Assert.Equal(selection, unchanged);
        Assert.Equal(date.ToString("O"), unchanged.ExpirationDate?.ToString("O"));
    }

    [Fact]
    public void EqualTranslatedLabels_DoNotMergeDifferentPresetOperations()
    {
        var options = SendDatePresetOptions.CreateDeletionOptions(resolve: _ => "Same display label");
        var oneDay = Assert.Single(options, option => option.Value == SendDatePreset.OneDay);
        var sevenDays = Assert.Single(options, option => option.Value == SendDatePreset.SevenDays);
        Assert.Equal(oneDay.Label, sevenDays.Label);
        var draft = SendEditorDraft.CreateDefault(SendType.Text);

        draft.SelectDeletion(oneDay.Value, Now);
        Assert.Equal(Now.AddDays(1), draft.DeletionDate);
        draft.SelectDeletion(sevenDays.Value, Now);
        Assert.Equal(Now.AddDays(7), draft.DeletionDate);
    }

    [Fact]
    public void NeverCannotBeSelectedAsADeletionDeadline()
    {
        var draft = SendEditorDraft.CreateDefault(SendType.Text);

        Assert.Throws<ArgumentOutOfRangeException>(() => draft.SelectDeletion(SendDatePreset.Never, Now));

        Assert.Equal(SendDatePreset.SevenDays, draft.DeletionPreset);
    }

    private static Func<UiStringKey, string> Resolver(string language)
    {
        var japanese = language == "ja-JP";
        return key => key switch
        {
            UiStringKey.Text3867F70AE6EFB579 => japanese ? "削除時" : "When deleted",
            UiStringKey.Text2C60316D5EA6B49C => japanese ? "期限なし" : "Never expires",
            UiStringKey.Text5482D1ABC42F8A5B => japanese ? "1時間" : "1 hour",
            UiStringKey.Text30C21284DF4FEAC6 => japanese ? "1日" : "1 day",
            UiStringKey.Text38EEFACBB326E37F => japanese ? "7日" : "7 days",
            UiStringKey.Text84AD2952A3089CE7 => japanese ? "30日" : "30 days",
            UiStringKey.Text84FDBE4301E85B69 => japanese ? "1時間後" : "In 1 hour",
            UiStringKey.Text005F9C9E2DEC364C => japanese ? "1日後" : "In 1 day",
            UiStringKey.Text6A6ADDDEFB062D6B => japanese ? "2日後" : "In 2 days",
            UiStringKey.Text5FF022316E9B3FAB => japanese ? "3日後" : "In 3 days",
            UiStringKey.Text1CE16CF41F181338 => japanese ? "7日後" : "In 7 days",
            UiStringKey.TextDF6B112991F3410F => japanese ? "30日後" : "In 30 days",
            UiStringKey.Text4EAFA9E925B30BCD => japanese ? "カスタム" : "Custom",
            UiStringKey.Text78221BF887768C83 => japanese ? "変更しない · {0}" : "Keep unchanged · {0}",
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unexpected preset display resource."),
        };
    }
}
