using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using Core.Enums;
using Core.Models;
using Crypto;
using Vault.Porting;
using Xunit;

namespace Vault.Tests;

public sealed class KeePassXmlImportCodecTests
{
    [Fact]
    public void Parse_PreservesWhitespaceAndNestedFolderRelationships()
    {
        var rootEntry = Entry("Root item", "  user  ", "  password\nline two  ");
        var childEntry = Entry("A & B", "u", "p", "https://example.test/?a=1&b=2");
        var xml = Document(new XElement("Group", new XElement("Name", "Database"), rootEntry,
            new XElement("Group", new XElement("Name", "Work"),
                new XElement("Group", new XElement("Name", "Accounts"), childEntry))));

        var result = KeePassXmlImportCodec.Parse(xml);

        Assert.Equal(2, result.Ciphers.Count);
        Assert.Equal("  user  ", result.Ciphers[0].Login!.Username);
        Assert.Equal("  password\nline two  ", result.Ciphers[0].Login!.Password);
        Assert.Null(result.Ciphers[0].FolderId);
        Assert.Equal(["Work", "Work/Accounts"], result.Folders.Select(folder => folder.Name));
        Assert.Equal((1, 1), Assert.Single(result.Relations));
        Assert.Equal("https://example.test/?a=1&b=2", result.Ciphers[1].Login!.Uris[0].Uri);
        Assert.All(result.Ciphers, cipher => Assert.Equal(string.Empty, cipher.Id));
    }

    [Fact]
    public void Parse_ProtectedCustomFieldsRemainHiddenEvenWhenLongOrMultiline()
    {
        var value = "  " + new string('x', 2_500) + "\nsecond line  ";
        var entry = Entry("Custom", "u", "p");
        entry.Add(Field("API token", value, protectInMemory: true));
        entry.Add(Field("Whitespace", "   "));

        var cipher = Assert.Single(KeePassXmlImportCodec.Parse(Document(Group(entry))).Ciphers);

        var hidden = Assert.Single(cipher.Fields, field => field.Name == "API token");
        Assert.Equal(CipherFieldType.Hidden, hidden.Type);
        Assert.Equal(value, hidden.Value);
        Assert.Equal("   ", Assert.Single(cipher.Fields, field => field.Name == "Whitespace").Value);
    }

    [Fact]
    public void Parse_HistoryBecomesPasswordHistoryRatherThanAdditionalEntries()
    {
        var entry = Entry("Current", "current user", "current password");
        entry.Add(new XElement("History",
            Entry("Old title", "old user", "old password", modified: "2024-01-02T03:04:05Z"),
            Entry("Older title", "older user", "older password", modified: "2023-01-02T03:04:05Z")));

        var result = KeePassXmlImportCodec.ParseWithSummary(Document(Group(entry)));

        var cipher = Assert.Single(result.Data.Ciphers);
        Assert.Equal("Current", cipher.Name);
        Assert.Equal(2, result.HistoricalVersions);
        Assert.Equal(["old password", "older password"], cipher.PasswordHistory.Select(item => item.Password));
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), cipher.PasswordHistory[0].LastUsedDate);
    }

    [Fact]
    public void Parse_DefaultExcludesRecycleBinByUuidWithoutMatchingTranslatedNames()
    {
        var recycleId = Identifier(2);
        var recycle = new XElement("Group", new XElement("UUID", recycleId), new XElement("Name", "Trash in another language"),
            Entry("Deleted", "u", "p"),
            new XElement("Group", new XElement("Name", "Child"), Entry("Also deleted", "u", "p")));
        var similarlyNamed = new XElement("Group", new XElement("UUID", Identifier(3)), new XElement("Name", "Recycle Bin"),
            Entry("Keep this", "u", "p"));
        var xml = Document(Group(recycle, similarlyNamed), recycleId);

        var result = KeePassXmlImportCodec.ParseWithSummary(xml);
        var includingTrash = KeePassXmlImportCodec.ParseWithSummary(xml, includeRecycleBin: true);

        Assert.Equal("Keep this", Assert.Single(result.Data.Ciphers).Name);
        Assert.Equal(2, result.ExcludedRecycleBinEntries);
        Assert.Equal(3, includingTrash.Data.Ciphers.Count);
        Assert.Equal(0, includingTrash.ExcludedRecycleBinEntries);
    }

    [Fact]
    public void Parse_GroupNotesArePreservedAsSecureNotes()
    {
        var group = Group(new XElement("Notes", "Database instructions\nKeep this text."), Entry("Login", "u", "p"));

        var result = KeePassXmlImportCodec.Parse(Document(group));

        var note = Assert.Single(result.Ciphers, cipher => cipher.Type == CipherType.SecureNote);
        Assert.Equal("Database instructions\nKeep this text.", note.Notes);
        Assert.NotNull(note.SecureNote);
        Assert.Single(result.Ciphers, cipher => cipher.Type == CipherType.Login);
    }

    [Fact]
    public void Parse_PreservesAutoTypeAndPluginDataAsHiddenFields()
    {
        var entry = Entry("Login", "u", "p");
        entry.Add(new XElement("AutoType", new XElement("DefaultSequence", "{USERNAME}{TAB}{PASSWORD}{ENTER}")));
        entry.Add(new XElement("CustomData", new XElement("Item", new XElement("Key", "Plugin"), new XElement("Value", "Private plugin data"))));
        entry.Add(new XElement("Tags", "personal;important"));
        entry.Add(new XElement("OverrideURL", "https://alternate.example.test"));

        var cipher = Assert.Single(KeePassXmlImportCodec.Parse(Document(Group(entry))).Ciphers);

        Assert.Contains(cipher.Fields, field => field.Name == "KeePass auto-type configuration"
            && field.Type == CipherFieldType.Hidden && field.Value!.Contains("{PASSWORD}", StringComparison.Ordinal));
        Assert.Contains(cipher.Fields, field => field.Name == "KeePass custom data"
            && field.Type == CipherFieldType.Hidden && field.Value!.Contains("Private plugin data", StringComparison.Ordinal));
        Assert.Contains(cipher.Fields, field => field.Name == "KeePass tags" && field.Value == "personal;important");
        Assert.Contains(cipher.Fields, field => field.Name == "KeePass override URL" && field.Value == "https://alternate.example.test");
    }

    [Fact]
    public void Parse_BinaryAttachmentRejectsTheWholeDocument()
    {
        var withAttachment = Entry("File owner", "u", "p");
        withAttachment.Add(new XElement("Binary", new XElement("Key", "document.txt"), new XElement("Value", new XAttribute("Ref", "0"))));
        var xml = Document(Group(Entry("Earlier valid item", "u", "p"), withAttachment));

        var exception = Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(xml));

        Assert.Contains("attachments", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("True")]
    [InlineData("true")]
    [InlineData("1")]
    public void Parse_EncryptedInnerXmlIsNeverTreatedAsPlaintext(string protectedValue)
    {
        var entry = Entry("Protected", "u", "p");
        entry.Add(new XElement("String", new XElement("Key", "Secret"),
            new XElement("Value", new XAttribute("Protected", protectedValue), "ZW5jcnlwdGVk")));

        Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(Document(Group(entry))));
    }

    [Fact]
    public void Parse_DtdAndExternalEntitiesAreRejected()
    {
        const string xml = "<!DOCTYPE KeePassFile [<!ENTITY external SYSTEM 'file:///never-read-this-file'>]>"
            + "<KeePassFile><Root><Group><Name>&external;</Name></Group></Root></KeePassFile>";

        Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(xml));
    }

    [Fact]
    public void Parse_ExcessiveDepthIsRejectedBeforeTreeConstruction()
    {
        var nested = new StringBuilder("<KeePassFile><Root>");
        for (var depth = 0; depth < KeePassXmlImportCodec.MaximumDepth + 1; depth++) nested.Append("<Group>");
        for (var depth = 0; depth < KeePassXmlImportCodec.MaximumDepth + 1; depth++) nested.Append("</Group>");
        nested.Append("</Root></KeePassFile>");

        Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(nested.ToString()));
    }

    [Fact]
    public void Parse_EntryCountIsBounded()
    {
        var group = new XElement("Group", new XElement("Name", "Root"));
        for (var index = 0; index <= KeePassXmlImportCodec.MaximumEntries; index++)
            group.Add(new XElement("Entry"));

        Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(Document(group)));
    }

    [Fact]
    public void Parse_TooManyFieldsAreRejected()
    {
        var entry = Entry("Fields", "u", "p");
        for (var index = 0; index < 257; index++) entry.Add(Field("Extra " + index, "value"));

        Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(Document(Group(entry))));
    }

    [Fact]
    public void Parse_CumulativeFolderPathsCannotAmplifyACommonPrefixWithoutABudget()
    {
        var group = Group(new XElement("Group", new XElement("Name", new string('a', 600)),
            new XElement("Group", new XElement("Name", new string('b', 600)), Entry("Child", "u", "p"))));

        var exception = Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(Document(group)));

        Assert.Contains("folder path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DuplicateStringKeysAreRejected()
    {
        var entry = Entry("Duplicate", "u", "p");
        entry.Add(Field("Password", "another password"));

        Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(Document(Group(entry))));
    }

    [Fact]
    public void Parse_UnknownEntryStructuresAreNotSilentlyDropped()
    {
        var entry = Entry("Unsupported", "u", "p");
        entry.Add(new XElement("FutureCredential", "data that must not disappear"));

        Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(Document(Group(entry))));
    }

    [Theory]
    [InlineData("<NotKeePass />")]
    [InlineData("<KeePassFile><Root /></KeePassFile>")]
    [InlineData("<KeePassFile><Root><Group /><Group /></Root></KeePassFile>")]
    [InlineData("<KeePassFile><Root><Group><Entry><String><Key>Title</Key></String></Entry></Group></Root></KeePassFile>")]
    public void Parse_MalformedOrAmbiguousDocumentsAreRejected(string xml) =>
        Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(xml));

    [Theory]
    [InlineData("TimeOtp-Secret", "12345678901234567890")]
    [InlineData("TimeOtp-Secret-Hex", "3132333435363738393031323334353637383930")]
    [InlineData("TimeOtp-Secret-Base32", "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ")]
    [InlineData("TimeOtp-Secret-Base64", "MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=")]
    public void Parse_NativeOtpEncodingsPreserveTheRfc6238Secret(string field, string secret)
    {
        var entry = Entry("OTP", "u", "p");
        entry.Add(Field(field, secret), Field("TimeOtp-Length", "8"), Field("TimeOtp-Period", "30"));

        var cipher = Assert.Single(KeePassXmlImportCodec.Parse(Document(Group(entry))).Ciphers);

        Assert.True(TotpGenerator.TryGenerate(cipher.Login!.Totp, 59, out var code));
        Assert.Equal("94287082", code);
        Assert.Contains(cipher.Fields, value => value.Name == field && value.Type == CipherFieldType.Hidden && value.Value == secret);
    }

    [Fact]
    public void Parse_NativeOtpRetainsAlgorithmLengthAndPeriod()
    {
        var entry = Entry("OTP", "u", "p");
        entry.Add(Field("TimeOtp-Secret-Base32", "JBSWY3DPEHPK3PXP"), Field("TimeOtp-Length", "8"),
            Field("TimeOtp-Period", "60"), Field("TimeOtp-Algorithm", "HMAC-SHA-512"));

        var totp = Assert.Single(KeePassXmlImportCodec.Parse(Document(Group(entry))).Ciphers).Login!.Totp;

        Assert.Contains("algorithm=SHA512", totp);
        Assert.Contains("digits=8", totp);
        Assert.Contains("period=60", totp);
    }

    [Theory]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP&algorithm=SHA256&digits=8&period=60")]
    [InlineData("steam://JBSWY3DPEHPK3PXP")]
    [InlineData("JBSWY3DPEHPK3PXP")]
    public void Parse_OtpPluginRepresentationsRemainUsable(string otp)
    {
        var entry = Entry("OTP", "u", "p");
        entry.Add(Field("otp", otp));

        var cipher = Assert.Single(KeePassXmlImportCodec.Parse(Document(Group(entry))).Ciphers);

        Assert.Equal(otp, cipher.Login!.Totp);
        Assert.True(TotpGenerator.TryGenerate(cipher.Login.Totp, 59, out _));
        Assert.Equal(CipherFieldType.Hidden, Assert.Single(cipher.Fields).Type);
    }

    [Fact]
    public void Parse_LegacyKeeOtpQueryDoesNotDiscardItsParameters()
    {
        var entry = Entry("OTP", "u", "p");
        entry.Add(Field("otp", "key=JBSWY3DPEHPK3PXP&step=60&size=8"));

        var totp = Assert.Single(KeePassXmlImportCodec.Parse(Document(Group(entry))).Ciphers).Login!.Totp;

        Assert.Contains("period=60", totp);
        Assert.Contains("digits=8", totp);
    }

    [Theory]
    [InlineData("TimeOtp-Algorithm", "HMAC-MD5")]
    [InlineData("TimeOtp-Length", "0")]
    [InlineData("TimeOtp-Length", "9")]
    [InlineData("TimeOtp-Period", "0")]
    public void Parse_InvalidNativeOtpNeverFallsBackToAnotherAlgorithm(string field, string value)
    {
        var entry = Entry("OTP", "u", "p");
        entry.Add(Field("TimeOtp-Secret-Base32", "JBSWY3DPEHPK3PXP"), Field(field, value));

        Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(Document(Group(entry))));
    }

    [Fact]
    public void Parse_AmbiguousOtpConfigurationsAreRejected()
    {
        var entry = Entry("OTP", "u", "p");
        entry.Add(Field("TimeOtp-Secret-Base32", "JBSWY3DPEHPK3PXP"), Field("otp", "JBSWY3DPEHPK3PXP"));

        Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(Document(Group(entry))));
    }

    [Theory]
    [InlineData("otpauth://totp/Name?secret=JBSWY3DPEHPK3PXP&Secret=GEZDGNBVGY3TQOJQ")]
    [InlineData("otpauth://totp/Name?secret=JBSWY3DPEHPK3PXP&encoder=steam&algorithm=SHA256")]
    [InlineData("otpauth://totp/Name?secret=JBSWY3DPEHPK3PXP&encoder=steam&period=60")]
    public void Parse_RejectsAmbiguousOrUnsupportedOtpUriSemantics(string otp)
    {
        var entry = Entry("OTP", "u", "p");
        entry.Add(Field("otp", otp));

        Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(Document(Group(entry))));
    }

    [Theory]
    [InlineData("{REF:P@I:0FECBD5CFA514F63A929CA6587B3A309}")]
    [InlineData("prefix{S:Another field}")]
    [InlineData("{USERNAME}")]
    public void Parse_DoesNotTurnUnresolvedReferencesIntoLiteralCredentials(string password)
    {
        var entry = Entry("Reference", "u", password);

        var exception = Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(Document(Group(entry))));

        Assert.Contains("references", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_PreservesManyEscapedTextCharactersWithoutAllowingCustomEntities()
    {
        var entry = Entry("Escaping", "u", "p");
        var notes = string.Concat(Enumerable.Repeat("<&>", 2_000));
        entry.Add(Field("Notes", notes));

        var cipher = Assert.Single(KeePassXmlImportCodec.Parse(Document(Group(entry))).Ciphers);

        Assert.Equal(notes, cipher.Notes);
    }

    [Fact]
    public void Parse_HotpSettingsArePreservedAsHiddenCustomDataWithoutPretendingToGenerateTotp()
    {
        var entry = Entry("HOTP", "u", "p");
        entry.Add(Field("HmacOtp-Secret-Base32", "JBSWY3DPEHPK3PXP"), Field("HmacOtp-Counter", "42"));

        var cipher = Assert.Single(KeePassXmlImportCodec.Parse(Document(Group(entry))).Ciphers);

        Assert.Null(cipher.Login!.Totp);
        Assert.All(cipher.Fields, field => Assert.Equal(CipherFieldType.Hidden, field.Type));
        Assert.Contains(cipher.Fields, field => field.Name == "HmacOtp-Counter" && field.Value == "42");
    }

    [Fact]
    public void Parse_AcceptsIsoAndBinaryKeePassTimeEncodings()
    {
        var time = new DateTimeOffset(2025, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)(time.Ticks / TimeSpan.TicksPerSecond));
        var entry = Entry("Times", "u", "p");
        entry.Add(new XElement("Times", new XElement("CreationTime", Convert.ToBase64String(bytes)),
            new XElement("LastModificationTime", "2025-02-04T04:05:06Z")));

        var cipher = Assert.Single(KeePassXmlImportCodec.Parse(Document(Group(entry))).Ciphers);

        Assert.Equal(time, cipher.CreationDate);
        Assert.Equal(time.AddDays(1), cipher.RevisionDate);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("//////////8=")]
    public void Parse_RejectsMalformedTimeInsteadOfReplacingItWithNow(string encoded)
    {
        var entry = Entry("Times", "u", "p");
        entry.Add(new XElement("Times", new XElement("CreationTime", encoded)));

        Assert.Throws<FormatException>(() => KeePassXmlImportCodec.Parse(Document(Group(entry))));
    }

    [Fact]
    public void Parse_AcceptsAnInitialUnicodeBom()
    {
        var xml = "\uFEFF" + Document(Group(Entry("BOM", "u", "p")));

        Assert.Equal("BOM", Assert.Single(KeePassXmlImportCodec.Parse(xml).Ciphers).Name);
    }

    private static XElement Group(params object[] entries) =>
        new("Group", new XElement("Name", "Database"), entries);

    private static XElement Entry(string title, string username, string password, string? url = null, string? modified = null)
    {
        var entry = new XElement("Entry", Field("Title", title), Field("UserName", username), Field("Password", password));
        if (url is not null) entry.Add(Field("URL", url));
        if (modified is not null) entry.Add(new XElement("Times", new XElement("LastModificationTime", modified)));
        return entry;
    }

    private static XElement Field(string name, string value, bool protectInMemory = false) =>
        new("String", new XElement("Key", name), new XElement("Value",
            protectInMemory ? new XAttribute("ProtectInMemory", "True") : null, value));

    private static string Document(XElement group, string? recycleBin = null) =>
        new XDocument(new XElement("KeePassFile",
            recycleBin is null ? null : new XElement("Meta", new XElement("RecycleBinEnabled", "True"), new XElement("RecycleBinUUID", recycleBin)),
            new XElement("Root", group))).ToString(SaveOptions.DisableFormatting);

    private static string Identifier(byte value) => Convert.ToBase64String(Enumerable.Repeat(value, 16).ToArray());
}
