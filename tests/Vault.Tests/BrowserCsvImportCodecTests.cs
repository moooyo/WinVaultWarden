using System.Globalization;
using Core.Enums;
using Core.Models;
using Vault.Porting;
using Xunit;

namespace Vault.Tests;

public sealed class BrowserCsvImportCodecTests
{
    [Theory]
    [InlineData(BrowserCsvSource.Chrome)]
    [InlineData(BrowserCsvSource.Edge)]
    public void Chromium_PreservesQuotedCredentialsAndMultilineNotes(BrowserCsvSource source)
    {
        const string password = "  password,\"雪🙂\"\r\nnext\t ";
        const string notes = "First line\r\nSecond line, with \"quotes\"";
        var content = "\uFEFF" + Csv(
            ["name", "url", "username", "password", "note"],
            ["Example, Inc", "https://example.test/sign-in", " user ", password, notes]);

        var cipher = Assert.Single(BrowserCsvImportCodec.Parse(source, content).Ciphers);

        Assert.Equal("Example, Inc", cipher.Name);
        Assert.Equal(" user ", cipher.Login!.Username);
        Assert.Equal(password, cipher.Login.Password);
        Assert.Equal(notes, cipher.Notes);
        Assert.Equal("https://example.test/sign-in", Assert.Single(cipher.Login.Uris).Uri);
        Assert.Equal(CipherType.Login, cipher.Type);
    }

    [Theory]
    [InlineData(BrowserCsvSource.Chrome)]
    [InlineData(BrowserCsvSource.Edge)]
    public void Chromium_AcceptsTheHistoricalFourColumnExport(BrowserCsvSource source)
    {
        var parsed = BrowserCsvImportCodec.Parse(source,
            "name,url,username,password\nExample,https://example.test,u,p\n\n");

        Assert.Single(parsed.Ciphers);
        Assert.Empty(parsed.Folders);
        Assert.Empty(parsed.Relations);
        Assert.Null(parsed.Ciphers[0].Notes);
    }

    [Fact]
    public void Chromium_PreservesAndroidFacetAndEmptySourceName()
    {
        const string facet = "android://base64hash@com.example.android/";
        var cipher = Assert.Single(BrowserCsvImportCodec.Parse(BrowserCsvSource.Chrome,
            Csv(["name", "url", "username", "password"], ["", facet, "", "  "])).Ciphers);

        Assert.Equal(string.Empty, cipher.Name);
        Assert.Equal(facet, Assert.Single(cipher.Login!.Uris).Uri);
        Assert.Equal(string.Empty, cipher.Login.Username);
        Assert.Equal("  ", cipher.Login.Password);
    }

    [Fact]
    public void Firefox_RetainsAllExportedMetadataAndTimestampMeaning()
    {
        var content = Csv(
            ["url", "username", "password", "httpRealm", "formActionOrigin", "guid", "timeCreated", "timeLastUsed", "timePasswordChanged"],
            ["https://accounts.example.test/login", " firefox user ", " p ", "Private realm", "https://accounts.example.test/submit", "{fixture-guid}", "1700000000000", "1700000100000", "1700000050000"]);

        var cipher = Assert.Single(BrowserCsvImportCodec.Parse(BrowserCsvSource.Firefox, content).Ciphers);

        Assert.Equal("accounts.example.test", cipher.Name);
        Assert.Equal(" firefox user ", cipher.Login!.Username);
        Assert.Equal(" p ", cipher.Login.Password);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), cipher.CreationDate);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000050000), cipher.Login.PasswordRevisionDate);
        Assert.Equal(6, cipher.Fields.Count);
        Assert.All(cipher.Fields, field => Assert.Equal(CipherFieldType.Hidden, field.Type));
        Assert.Equal("Private realm", Field(cipher, "Firefox: httpRealm"));
        Assert.Equal("https://accounts.example.test/submit", Field(cipher, "Firefox: formActionOrigin"));
        Assert.Equal("{fixture-guid}", Field(cipher, "Firefox: guid"));
        Assert.Equal("1700000100000", Field(cipher, "Firefox: timeLastUsed"));
    }

    [Fact]
    public void Firefox_AcceptsLegacyHostnameWithoutSilentlyDroppingInternalEntries()
    {
        var parsed = BrowserCsvImportCodec.Parse(BrowserCsvSource.Firefox,
            "hostname,username,password\r\nhttps://example.test,u,p\r\nchrome://FirefoxAccounts,internal,synthetic\r\n");

        Assert.Equal(2, parsed.Ciphers.Count);
        Assert.Equal("https://example.test", Assert.Single(parsed.Ciphers[0].Login!.Uris).Uri);
        Assert.Equal("chrome://FirefoxAccounts", Assert.Single(parsed.Ciphers[1].Login!.Uris).Uri);
    }

    [Fact]
    public void Firefox_ConflictingUrlAliasesRejectTheDocument()
    {
        const string content = "url,hostname,username,password\nhttps://one.test,https://two.test,u,p";

        Assert.Throws<FormatException>(() => BrowserCsvImportCodec.Parse(BrowserCsvSource.Firefox, content));
    }

    [Theory]
    [InlineData("not-a-timestamp")]
    [InlineData("9223372036854775807")]
    [InlineData("1.5")]
    public void Firefox_InvalidMetadataTimestampsAreNotSilentlyDiscarded(string timestamp)
    {
        var content = Csv(["url", "username", "password", "timeCreated"], ["https://example.test", "u", "p", timestamp]);

        Assert.Throws<FormatException>(() => BrowserCsvImportCodec.Parse(BrowserCsvSource.Firefox, content));
    }

    [Fact]
    public void LastPass_PreservesFoldersFavoriteNotesTotpAndOptionalCliMetadata()
    {
        var content = Csv(
            ["url", "username", "password", "extra", "name", "grouping", "fav", "totp", "id", "last_touch", "attachpresent"],
            ["https://example.test", " u ", " p ", "note\nbody", "Example", "Work\\Cloud", "1", "otpauth://totp/test?secret=JBSWY3DPEHPK3PXP", "record-1", "1700000000", "0"],
            ["https://second.test", "u2", "p2", "", "Second", "Work\\Cloud", "0", "", "record-2", "", "0"]);

        var parsed = BrowserCsvImportCodec.Parse(BrowserCsvSource.LastPass, content);

        Assert.Equal(2, parsed.Ciphers.Count);
        Assert.Equal("Work\\Cloud", Assert.Single(parsed.Folders).Name);
        Assert.Equal([(0, 0), (1, 0)], parsed.Relations);
        Assert.All(parsed.Ciphers, cipher => Assert.Equal("0", cipher.FolderId));
        var first = parsed.Ciphers[0];
        Assert.True(first.Favorite);
        Assert.False(parsed.Ciphers[1].Favorite);
        Assert.Equal("note\nbody", first.Notes);
        Assert.Equal(" u ", first.Login!.Username);
        Assert.Equal(" p ", first.Login.Password);
        Assert.Equal("otpauth://totp/test?secret=JBSWY3DPEHPK3PXP", first.Login.Totp);
        Assert.Equal("record-1", Field(first, "LastPass: id"));
        Assert.Equal("1700000000", Field(first, "LastPass: last_touch"));
    }

    [Fact]
    public void LastPass_StructuredSecureNoteIsRetainedInFull()
    {
        const string extra = "NoteType:Credit Card\nNumber:4111111111111111\nNotes:First line\nSecond line:with colon";
        var content = Csv(
            ["url", "username", "password", "extra", "name", "grouping", "fav", "totp"],
            ["http://sn", "auxiliary-user", "auxiliary-password", extra, "Card note", "Personal", "1", "JBSWY3DPEHPK3PXP"]);

        var cipher = Assert.Single(BrowserCsvImportCodec.Parse(BrowserCsvSource.LastPass, content).Ciphers);

        Assert.Equal(CipherType.SecureNote, cipher.Type);
        Assert.NotNull(cipher.SecureNote);
        Assert.Null(cipher.Login);
        Assert.Equal(extra, cipher.Notes);
        Assert.Equal("auxiliary-user", Field(cipher, "LastPass: username"));
        Assert.Equal("auxiliary-password", Field(cipher, "LastPass: password"));
        Assert.Equal("JBSWY3DPEHPK3PXP", Field(cipher, "LastPass: totp"));
    }

    [Fact]
    public void LastPass_NoneGroupDoesNotCreateAnArtificialFolder()
    {
        var parsed = BrowserCsvImportCodec.Parse(BrowserCsvSource.LastPass,
            "url,username,password,extra,name,grouping,fav\nhttps://example.test,u,p,,Example,(none),0");

        Assert.Empty(parsed.Folders);
        Assert.Empty(parsed.Relations);
        Assert.Null(parsed.Ciphers[0].FolderId);
        Assert.Equal("(none)", Field(parsed.Ciphers[0], "LastPass: grouping"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public void LastPass_ReportsUncontainedAttachmentsInsteadOfClaimingCompleteImport(string present)
    {
        var content = Csv(
            ["url", "username", "password", "extra", "name", "grouping", "fav", "attachpresent"],
            ["https://example.test", "u", "p", "", "Example", "", "0", present]);

        Assert.Throws<FormatException>(() => BrowserCsvImportCodec.Parse(BrowserCsvSource.LastPass, content));
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    public void LastPass_InvalidFavoriteIsRejected(string favorite)
    {
        var content = Csv(["url", "username", "password", "extra", "name", "grouping", "fav"],
            ["https://example.test", "u", "p", "", "Example", "", favorite]);

        Assert.Throws<FormatException>(() => BrowserCsvImportCodec.Parse(BrowserCsvSource.LastPass, content));
    }

    [Theory]
    [InlineData(BrowserCsvSource.Chrome, "name,url,username,password")]
    [InlineData(BrowserCsvSource.Edge, "name,url,username,password,note")]
    [InlineData(BrowserCsvSource.Firefox, "url,username,password")]
    [InlineData(BrowserCsvSource.LastPass, "url,username,password,extra,name,grouping,fav")]
    public void HeaderOnlyDocumentsContainNoItems(BrowserCsvSource source, string header)
    {
        var parsed = BrowserCsvImportCodec.Parse(source, header);

        Assert.Empty(parsed.Ciphers);
        Assert.Empty(parsed.Folders);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.test,u,p")]
    [InlineData("name,url,username,password,URL\nExample,https://example.test,u,p,duplicate")]
    [InlineData("name,url,username,password,\nExample,https://example.test,u,p,")]
    public void MissingOrAmbiguousHeaderIsNotTreatedAsData(string content) =>
        Assert.Throws<FormatException>(() => BrowserCsvImportCodec.Parse(BrowserCsvSource.Chrome, content));

    [Theory]
    [InlineData("name,url,username,password\nExample,https://example.test,u")]
    [InlineData("name,url,username,password\nExample,https://example.test,u,p,extra")]
    [InlineData("name,url,username,password\n\"Example,https://example.test,u,p")]
    [InlineData("name,url,username,password\nExam\"ple,https://example.test,u,p")]
    [InlineData("name,url,username,password\n\"Example\"trailing,https://example.test,u,p")]
    public void MalformedRecordsRejectTheCompleteDocument(string content) =>
        Assert.Throws<FormatException>(() => BrowserCsvImportCodec.Parse(BrowserCsvSource.Chrome, content));

    [Fact]
    public void UnsupportedNonemptyColumnFailsWithoutEchoingItsSensitiveValue()
    {
        const string secret = "synthetic future private value";
        var content = Csv(["name", "url", "username", "password", "future"],
            ["Accepted first row", "https://one.test", "u", "p", ""],
            ["Rejected later row", "https://two.test", "u", "p", secret]);

        var error = Assert.Throws<FormatException>(() => BrowserCsvImportCodec.Parse(BrowserCsvSource.Chrome, content));

        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyFutureColumnDoesNotLoseAnyData()
    {
        var content = "name,url,username,password,future\nExample,https://example.test,u,p,";

        Assert.Single(BrowserCsvImportCodec.Parse(BrowserCsvSource.Chrome, content).Ciphers);
    }

    [Fact]
    public void OversizedFieldIsRejectedBeforeReturningData()
    {
        var content = "name,url,username,password\nExample,https://example.test,u," + new string('p', BrowserCsvImportCodec.MaximumCellCharacters + 1);

        Assert.Throws<FormatException>(() => BrowserCsvImportCodec.Parse(BrowserCsvSource.Chrome, content));
    }

    [Fact]
    public void ColumnAndRecordBudgetsIncludeOtherwiseEmptyInput()
    {
        var tooManyColumns = string.Join(',', Enumerable.Range(0, BrowserCsvImportCodec.MaximumColumns + 1)
            .Select(index => "column" + index.ToString(CultureInfo.InvariantCulture)));
        var tooManyRecords = "name,url,username,password" + new string('\n', BrowserCsvImportCodec.MaximumRecords + 2);

        Assert.Throws<FormatException>(() => BrowserCsvImportCodec.Parse(BrowserCsvSource.Chrome, tooManyColumns));
        Assert.Throws<FormatException>(() => BrowserCsvImportCodec.Parse(BrowserCsvSource.Chrome, tooManyRecords));
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void InvalidUnicodeCannotBeSilentlyReplacedDuringEncryption(int invalidCodeUnit)
    {
        var invalid = new string((char)invalidCodeUnit, 1);
        var content = "name,url,username,password\nExample,https://example.test,u," + invalid;

        Assert.Throws<FormatException>(() => BrowserCsvImportCodec.Parse(BrowserCsvSource.Chrome, content));
    }

    private static string? Field(Cipher cipher, string name) => Assert.Single(cipher.Fields, field => field.Name == name).Value;

    private static string Csv(params string[][] rows) => string.Join("\r\n", rows.Select(row =>
        string.Join(',', row.Select(value => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""))));
}
