using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Core.Enums;
using Core.Models;
using Vault.Porting;
using Xunit;

namespace Vault.Tests;

public sealed class OnePasswordArchiveImportCodecTests
{
    [Fact]
    public void Login_PreservesWhitespaceUrisTagsHistoryAndFavoriteRank()
    {
        var item = Item();
        item["favIndex"] = 7;
        item["details"]!["loginFields"] = new JsonArray(
            LoginField("username", "  synthetic user  "),
            LoginField("password", "  synthetic secret\nwith whitespace  "),
            new JsonObject { ["name"] = "memo", ["fieldType"] = "A", ["value"] = "line 1\nline 2" });
        item["details"]!["notesPlain"] = "  # Original markdown\n\nEnds with whitespace  ";
        item["overview"]!["url"] = "https://example.test/primary";
        item["overview"]!["urls"] = new JsonArray(
            new JsonObject { ["label"] = "Primary", ["url"] = "https://example.test/primary" },
            new JsonObject { ["label"] = "Secondary", ["url"] = "https://example.test/secondary" });
        item["overview"]!["tags"] = new JsonArray("one/tag", "two/tag", "two/tag");
        var history = new JsonArray();
        for (var index = 0; index < 11; index++)
            history.Add(new JsonObject { ["value"] = "old synthetic secret " + index, ["time"] = 1_600_000_000 + index });
        item["details"]!["passwordHistory"] = history;
        AddSection(item, "Security", Field("otp", "One-time password", "totp", "otpauth://totp/review?secret=JBSWY3DPEHPK3PXP", guarded: true));

        var parsed = OnePasswordArchiveImportCodec.Parse(Archive(item));

        var cipher = Assert.Single(parsed.Ciphers);
        Assert.Equal(CipherType.Login, cipher.Type);
        Assert.True(cipher.Favorite);
        Assert.True(cipher.Reprompt);
        Assert.Equal("  synthetic user  ", cipher.Login!.Username);
        Assert.Equal("  synthetic secret\nwith whitespace  ", cipher.Login.Password);
        Assert.Equal("  # Original markdown\n\nEnds with whitespace  ", cipher.Notes);
        Assert.Equal(2, cipher.Login.Uris.Count);
        Assert.Equal("https://example.test/secondary", cipher.Login.Uris[1].Uri);
        Assert.Equal("otpauth://totp/review?secret=JBSWY3DPEHPK3PXP", cipher.Login.Totp);
        Assert.Equal(11, cipher.PasswordHistory.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_010), cipher.PasswordHistory[^1].LastUsedDate);
        Assert.Equal(3, cipher.Fields.Count(field => field.Name.StartsWith("1Password tag ", StringComparison.Ordinal)));
        Assert.Contains(cipher.Fields, field => field.Value == "line 1\nline 2");
        Assert.Contains(cipher.Fields, field => field.Name == "1Password URL 2 label" && field.Value == "Secondary");
        Assert.Contains(cipher.Fields, field => field.Value == cipher.Login.Totp && field.Type == CipherFieldType.Hidden);
        Assert.Equal(string.Empty, cipher.Id);
        Assert.Equal("0", cipher.FolderId);
        Assert.Equal((0, 0), Assert.Single(parsed.Relations));
        Assert.Equal("Synthetic vault", Assert.Single(parsed.Folders).Name);
    }

    [Fact]
    public void PasswordCategory_UsesItsDedicatedPasswordWithoutDroppingWebFormValues()
    {
        var item = Item("005");
        item["details"]!["password"] = "  dedicated synthetic password  ";
        item["details"]!["loginFields"] = new JsonArray(LoginField("password", "other saved web form value"));

        var cipher = Assert.Single(OnePasswordArchiveImportCodec.Parse(Archive(item)).Ciphers);

        Assert.Equal("  dedicated synthetic password  ", cipher.Login!.Password);
        Assert.Contains(cipher.Fields, field => field.Value == "other saved web form value" && field.Type == CipherFieldType.Hidden);
    }

    [Fact]
    public void SectionOnlyLogin_MapsTheStandardUsernameAndPasswordFields()
    {
        var item = Item();
        AddSection(item, "Login",
            Field("username", "Username", "string", "  section username  "),
            Field("password", "Password", "concealed", "  section password  "));

        var cipher = Assert.Single(OnePasswordArchiveImportCodec.Parse(Archive(item)).Ciphers);

        Assert.Equal("  section username  ", cipher.Login!.Username);
        Assert.Equal("  section password  ", cipher.Login.Password);
    }

    [Fact]
    public void DesignatedCredentials_TakePriorityAndKeepDifferentSectionValues()
    {
        var item = Item();
        item["details"]!["loginFields"] = new JsonArray(LoginField("username", "designated user"), LoginField("password", "designated password"));
        AddSection(item, "Alternate",
            Field("username", "Username", "string", "section user"),
            Field("password", "Password", "concealed", "section password"));

        var cipher = Assert.Single(OnePasswordArchiveImportCodec.Parse(Archive(item)).Ciphers);

        Assert.Equal("designated user", cipher.Login!.Username);
        Assert.Equal("designated password", cipher.Login.Password);
        Assert.Contains(cipher.Fields, field => field.Value == "section password");
    }

    [Fact]
    public void ConflictingSectionOnlyCredentials_AreRejectedForReview()
    {
        var item = Item();
        AddSection(item, "Login", Field("password", "Password", "concealed", "one"));
        AddSection(item, "Other", Field("password", "Password", "concealed", "two"));

        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(item)));
    }

    [Fact]
    public void Card_MapsNativeValuesAndKeepsAdditionalFields()
    {
        var item = Item("002");
        AddSection(item, "Card",
            Field("cardholder", "Cardholder", "string", "Synthetic Cardholder"),
            Field("type", "Type", "creditCardType", "visa"),
            Field("ccnum", "Number", "creditCardNumber", "4111111111111111"),
            Field("cvv", "CVV", "concealed", "123"),
            Field("expiry", "Expiry", "monthYear", 203012),
            Field("creditLimit", "Credit limit", "string", "A preserved source value"));
        item["overview"]!["url"] = "https://bank.example.test/";

        var cipher = Assert.Single(OnePasswordArchiveImportCodec.Parse(Archive(item)).Ciphers);

        Assert.Equal(CipherType.Card, cipher.Type);
        Assert.Equal("Synthetic Cardholder", cipher.Card!.CardholderName);
        Assert.Equal("Visa", cipher.Card.Brand);
        Assert.Equal("4111111111111111", cipher.Card.Number);
        Assert.Equal("123", cipher.Card.Code);
        Assert.Equal("12", cipher.Card.ExpMonth);
        Assert.Equal("2030", cipher.Card.ExpYear);
        Assert.Contains(cipher.Fields, field => field.Value == "A preserved source value");
        Assert.Contains(cipher.Fields, field => field.Name == "1Password primary URL" && field.Value == "https://bank.example.test/");
    }

    [Fact]
    public void Identity_PreservesEmailProviderAndMultilineAddress()
    {
        var item = Item("004");
        AddSection(item, "Identity",
            Field("firstname", "First name", "string", "Synthetic"),
            Field("initial", "Middle name", "string", "M"),
            Field("lastname", "Last name", "string", "Identity"),
            Field("email", "Email", "email", new JsonObject { ["email_address"] = "review@example.test", ["provider"] = "source provider" }),
            Field("address", "Address", "address", new JsonObject
            {
                ["street"] = "First line\nSecond line", ["city"] = "Example city", ["country"] = "US", ["zip"] = "12345", ["state"] = "Example state",
            }));

        var cipher = Assert.Single(OnePasswordArchiveImportCodec.Parse(Archive(item)).Ciphers);

        Assert.Equal("Synthetic", cipher.Identity!.FirstName);
        Assert.Equal("review@example.test", cipher.Identity.Email);
        Assert.Equal("First line\nSecond line", cipher.Identity.Address1);
        Assert.Equal("12345", cipher.Identity.PostalCode);
        Assert.Contains(cipher.Fields, field => field.Name.EndsWith(" / provider", StringComparison.Ordinal) && field.Value == "source provider");
    }

    [Fact]
    public void BankAccount_MapsSupportedNativeFieldsWithoutDiscardingSourceFields()
    {
        var item = Item("101");
        AddSection(item, "Bank",
            Field("bankName", "Bank", "string", "Synthetic bank"),
            Field("owner", "Owner", "string", "Synthetic owner"),
            Field("accountNo", "Account", "concealed", "123456"),
            Field("routingNo", "Routing", "string", "654321"),
            Field("telephonePin", "PIN", "concealed", "012345"),
            Field("branchAddress", "Branch address", "string", "Preserved address"));

        var cipher = Assert.Single(OnePasswordArchiveImportCodec.Parse(Archive(item)).Ciphers);

        Assert.Equal(CipherType.BankAccount, cipher.Type);
        Assert.Equal("Synthetic bank", cipher.BankAccount!.BankName);
        Assert.Equal("123456", cipher.BankAccount.AccountNumber);
        Assert.Equal("012345", cipher.BankAccount.Pin);
        Assert.Contains(cipher.Fields, field => field.Value == "Preserved address");
    }

    [Fact]
    public void Passport_PreservesFullNameWithoutGuessingNameParts()
    {
        var item = Item("106");
        AddSection(item, "Passport",
            Field("fullname", "Full name", "string", "A Source Name With Several Parts"),
            Field("number", "Number", "string", "SYNTHETIC-123"),
            Field("birthdate", "Birth date", "date", 0),
            Field("expiry_date", "Expiry", "date", 1_893_456_000));

        var cipher = Assert.Single(OnePasswordArchiveImportCodec.Parse(Archive(item)).Ciphers);

        Assert.Equal("SYNTHETIC-123", cipher.Passport!.PassportNumber);
        Assert.Equal("1970-01-01", cipher.Passport.DateOfBirth);
        Assert.Null(cipher.Passport.GivenName);
        Assert.Null(cipher.Passport.Surname);
        Assert.Contains(cipher.Fields, field => field.Value == "A Source Name With Several Parts");
    }

    [Fact]
    public void DriversLicense_UsesTheDocumentedMonthEndForMonthYearExpiry()
    {
        var item = Item("103");
        AddSection(item, "License", Field("number", "Number", "string", "SYNTHETIC-LICENSE"), Field("expiry_date", "Expiry", "monthYear", 202402));

        var cipher = Assert.Single(OnePasswordArchiveImportCodec.Parse(Archive(item)).Ciphers);

        Assert.Equal("2024-02-29", cipher.DriversLicense!.ExpirationDate);
    }

    [Fact]
    public void ZeroMonthYear_IsPreservedAsAnUnsetSourceValue()
    {
        var item = Item("002");
        AddSection(item, "Card", Field("expiry", "Expiry", "monthYear", 0));

        var cipher = Assert.Single(OnePasswordArchiveImportCodec.Parse(Archive(item)).Ciphers);

        Assert.Null(cipher.Card!.ExpMonth);
        Assert.Null(cipher.Card.ExpYear);
        Assert.Contains(cipher.Fields, field => field.Name == "Card / Expiry" && field.Value == "0");
    }

    [Fact]
    public void Ssh_ValidatesThePublicKeyAndDerivesItsFingerprint()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = ecdsa.ExportPkcs8PrivateKeyPem();
        using var expected = SshAgentKey.Parse(privateKey);
        var publicKey = expected.Algorithm + " " + Convert.ToBase64String(expected.PublicKeyBlob);
        var item = Item("114");
        AddSection(item, "SSH", Field("private_key", "Private key", "sshKey", new JsonObject
        {
            ["privateKey"] = "A preserved original source encoding",
            ["metadata"] = new JsonObject
            {
                ["privateKey"] = privateKey, ["publicKey"] = publicKey, ["fingerprint"] = expected.Fingerprint, ["keyType"] = "ecdsa",
            },
        }, guarded: true));

        var cipher = Assert.Single(OnePasswordArchiveImportCodec.Parse(Archive(item)).Ciphers);

        Assert.Equal(privateKey, cipher.Ssh!.PrivateKey);
        Assert.Equal(publicKey, cipher.Ssh.PublicKey);
        Assert.Equal(expected.Fingerprint, cipher.Ssh.Fingerprint);
        Assert.True(cipher.Reprompt);
        Assert.Contains(cipher.Fields, field => field.Value == "A preserved original source encoding" && field.Type == CipherFieldType.Hidden);
    }

    [Fact]
    public void Ssh_RejectsInconsistentFingerprintMetadata()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var item = Item("114");
        AddSection(item, "SSH", Field("private_key", "Private key", "sshKey", new JsonObject
        {
            ["privateKey"] = "",
            ["metadata"] = new JsonObject { ["privateKey"] = ecdsa.ExportPkcs8PrivateKeyPem(), ["fingerprint"] = "SHA256:invalid" },
        }));

        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(item)));
    }

    [Theory]
    [InlineData("006")]
    [InlineData("100")]
    [InlineData("999")]
    public void UnmappedCategories_AreNotSilentlyTurnedIntoEmptyLogins(string category)
    {
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(Item(category))));
    }

    [Theory]
    [InlineData("archived")]
    [InlineData("deleted")]
    public void UnsupportedStates_DoNotBecomeActiveItems(string state)
    {
        var item = Item();
        item["state"] = state;
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(item)));
    }

    [Fact]
    public void DocumentsAndEmbeddedBinaries_AreRejectedBeforeReturningAnyItems()
    {
        var item = Item();
        item["uuid"] = "synthetic-document-item";
        item["details"]!["documentAttributes"] = new JsonObject { ["fileName"] = "Synthetic file", ["documentId"] = "synthetic-document", ["decryptedSize"] = 8 };
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(Item(), item)));
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(Item(), extraEntry: "files/synthetic-document___file.txt")));
    }

    [Theory]
    [InlineData("passkey")]
    [InlineData("reference")]
    [InlineData("future-kind")]
    public void UnsupportedValueKinds_DoNotDisappear(string kind)
    {
        var item = Item();
        AddSection(item, "Additional", Field("field-id", "Additional value", kind, "a synthetic source value"));
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(item)));
    }

    [Fact]
    public void ConflictingLoginDesignations_AreRejected()
    {
        var item = Item();
        item["details"]!["loginFields"] = new JsonArray(LoginField("password", "first value"), LoginField("password", "second value"));
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(item)));
    }

    [Fact]
    public void ItemIdsAreUniqueWithinEachVaultRatherThanAcrossTheEntireArchive()
    {
        var root = Root(Item());
        var vaults = root["accounts"]![0]!["vaults"]!.AsArray();
        var copied = vaults[0]!.DeepClone();
        copied["attrs"]!["uuid"] = "other-vault";
        copied["attrs"]!["name"] = "Other vault";
        vaults.Add(copied);

        var parsed = OnePasswordArchiveImportCodec.Parse(ArchiveRoot(root));

        Assert.Equal(2, parsed.Ciphers.Count);
        Assert.Equal(2, parsed.Folders.Count);
        Assert.Equal((1, 1), parsed.Relations[1]);
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(Item(), Item())));
    }

    [Fact]
    public void DuplicateJsonProperties_AreRejected()
    {
        var json = Root(Item()).ToJsonString().Replace("\"accounts\":", "\"accounts\":[],\"accounts\":", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(ArchiveJson(json)));
    }

    [Theory]
    [InlineData("{\"version\":4}")]
    [InlineData("{\"version\":\"3\"}")]
    [InlineData("{}")]
    public void UnsupportedOrMalformedVersions_AreRejected(string attributes)
    {
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(ArchiveJson(Root(Item()).ToJsonString(), attributes)));
    }

    [Fact]
    public void MalformedNumericFields_ReturnAControlledParseFailure()
    {
        var item = Item();
        item["createdAt"] = "not a timestamp";
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(item)));
        item = Item("002");
        AddSection(item, "Card", Field("expiry", "Expiry", "monthYear", "203012"));
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(item)));
    }

    [Fact]
    public void StreamInput_CanBeNonSeekableAndRemainsOpen()
    {
        using var stream = new NonSeekableStream(Archive(Item()));

        Assert.Single(OnePasswordArchiveImportCodec.Parse(stream).Ciphers);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void CallerOwnedArchiveBytes_AreNotModified()
    {
        var input = Archive(Item());
        var original = input.ToArray();
        _ = OnePasswordArchiveImportCodec.Parse(input);
        Assert.Equal(original, input);
    }

    [Fact]
    public void Cancellation_IsObservedBeforeReadingTheInput()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => OnePasswordArchiveImportCodec.Parse(Archive(Item()), source.Token));
        using var stream = new NonSeekableStream(Archive(Item()));
        Assert.ThrowsAny<OperationCanceledException>(() => OnePasswordArchiveImportCodec.Parse(stream, source.Token));
        Assert.Equal(0, stream.BytesRead);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("files/../../outside")]
    [InlineData("/outside")]
    [InlineData("C:/outside")]
    [InlineData("files\\outside")]
    [InlineData("files//outside")]
    public void UnsafeArchivePaths_AreRejectedWithoutExtraction(string path)
    {
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(Item(), extraEntry: path)));
    }

    [Theory]
    [InlineData("export.data")]
    [InlineData("EXPORT.DATA")]
    public void DuplicateAndCaseCollidingArchiveEntries_AreRejected(string duplicate)
    {
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(Item(), extraEntry: duplicate)));
    }

    [Fact]
    public void CorruptedEntryCrc_IsRejectedEvenWhenTheJsonIsStillValid()
    {
        var input = Archive(Item(), compression: CompressionLevel.NoCompression);
        var index = input.AsSpan().IndexOf("Synthetic vault"u8);
        Assert.True(index > 0);
        input[index] = (byte)'X';

        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(input));
    }

    [Fact]
    public void LocalAndCentralNamesMustMatch()
    {
        var input = Archive(Item());
        input[30 + 6] = (byte)'X';

        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(input));
    }

    [Fact]
    public void UnderreportedCentralEntryCount_IsRejectedBeforeLibraryAllocation()
    {
        var input = Archive(Item());
        var end = input.Length - 22;
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(end + 8, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(end + 10, 2), 1);

        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(input));
    }

    [Fact]
    public void OversizedDeclaredEntry_IsRejectedBeforeDecompression()
    {
        var input = Archive(Item());
        var central = FindCentral(input, "export.data");
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(central + 24, 4), (uint)OnePasswordArchiveImportCodec.MaximumDataBytes + 1);

        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(input));
    }

    [Fact]
    public void SplitArchivesAndZip64Counts_AreRejected()
    {
        var split = Archive(Item());
        BinaryPrimitives.WriteUInt16LittleEndian(split.AsSpan(split.Length - 22 + 4, 2), 1);
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(split));
        var zip64 = Archive(Item());
        BinaryPrimitives.WriteUInt16LittleEndian(zip64.AsSpan(zip64.Length - 22 + 8, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(zip64.AsSpan(zip64.Length - 22 + 10, 2), ushort.MaxValue);
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(zip64));
    }

    [Fact]
    public void MutatedStructuralHeaders_FailWithControlledErrors()
    {
        var source = Archive(Item());
        var central = FindCentral(source, "export.data");
        var positions = new[] { 0, 6, 8, 26, 28, central, central + 20, central + 24, central + 42, source.Length - 22 + 16 };
        foreach (var position in positions)
        {
            var input = source.ToArray();
            input[position] ^= 0x80;
            Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(input));
        }
    }

    [Fact]
    public void UnsupportedDataFailure_DoesNotEchoItsValue()
    {
        var item = Item();
        item["details"]!["unknownSensitiveData"] = "SYNTHETIC-PRIVATE-VALUE-THAT-MUST-NOT-BE-ECHOED";

        var error = Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(Archive(item)));

        Assert.DoesNotContain("SYNTHETIC-PRIVATE-VALUE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectoryEntries_CannotHideACompressedBodyBehindZeroSizeMetadata()
    {
        var input = Archive(Item(), extraEntry: "files/");
        var central = FindCentral(input, "files/");
        var local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(central + 42, 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(central + 16, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(central + 24, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(local + 14, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(local + 22, 4), 0);

        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(input));
    }

    [Fact]
    public void ValidStreamingZipDescriptors_AreAcceptedAndTamperedDescriptorsAreRejected()
    {
        var input = ArchiveJson(Root(Item()).ToJsonString(), nonSeekableOutput: true);
        Assert.Single(OnePasswordArchiveImportCodec.Parse(input).Ciphers);
        var descriptor = input.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x07, 0x08 });
        Assert.True(descriptor > 0);
        input[descriptor + 4] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(input));
    }

    [Fact]
    public void InvalidUtf8AndUnpairedUnicodeEscapes_AreControlledParseFailures()
    {
        var json = Root(Item()).ToJsonString();
        var surrogate = json.Replace("Synthetic item", "\\uD800", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(ArchiveJson(surrogate)));
        var input = Encoding.UTF8.GetBytes(json);
        var position = input.AsSpan().IndexOf("Synthetic item"u8);
        Assert.True(position > 0);
        input[position] = 0xff;
        Assert.Throws<InvalidDataException>(() => OnePasswordArchiveImportCodec.Parse(ArchiveDataBytes(input)));
    }

    private static JsonObject Item(string category = "001") => new()
    {
        ["uuid"] = "synthetic-item", ["favIndex"] = 0, ["createdAt"] = 1_600_000_000, ["updatedAt"] = 1_600_000_010,
        ["state"] = "active", ["categoryUuid"] = category,
        ["overview"] = new JsonObject { ["title"] = "Synthetic item", ["url"] = "", ["urls"] = new JsonArray(), ["tags"] = new JsonArray() },
        ["details"] = new JsonObject { ["notesPlain"] = "Synthetic notes", ["loginFields"] = new JsonArray(), ["sections"] = new JsonArray(), ["passwordHistory"] = new JsonArray() },
    };

    private static JsonObject LoginField(string designation, string value) => new()
    {
        ["designation"] = designation, ["value"] = value, ["name"] = designation, ["fieldType"] = designation == "password" ? "P" : "T",
    };

    private static JsonObject Field(string id, string title, string kind, object value, bool guarded = false) => new()
    {
        ["id"] = id, ["title"] = title, ["guarded"] = guarded,
        ["value"] = new JsonObject { [kind] = value is JsonNode node ? node : JsonValue.Create(value) },
    };

    private static void AddSection(JsonObject item, string title, params JsonObject[] fields) =>
        item["details"]!["sections"]!.AsArray().Add(new JsonObject { ["title"] = title, ["fields"] = new JsonArray(fields.Cast<JsonNode?>().ToArray()) });

    private static JsonObject Root(params JsonObject[] items) => new()
    {
        ["accounts"] = new JsonArray(new JsonObject
        {
            ["attrs"] = new JsonObject { ["uuid"] = "synthetic-account", ["accountName"] = "Synthetic account", ["name"] = "Synthetic account", ["email"] = "review@example.test", ["domain"] = "https://example.1password.com/" },
            ["vaults"] = new JsonArray(new JsonObject
            {
                ["attrs"] = new JsonObject { ["uuid"] = "synthetic-vault", ["name"] = "Synthetic vault", ["desc"] = "Synthetic description", ["type"] = "P" },
                ["items"] = new JsonArray(items.Select(item => item.DeepClone()).ToArray()),
            }),
        }),
    };

    private static byte[] Archive(JsonObject item, string? extraEntry = null, CompressionLevel compression = CompressionLevel.Optimal) =>
        ArchiveJson(Root(item).ToJsonString(), extraEntry: extraEntry, compression: compression);

    private static byte[] Archive(JsonObject first, JsonObject second) => ArchiveRoot(Root(first, second));
    private static byte[] ArchiveRoot(JsonObject root) => ArchiveJson(root.ToJsonString());

    private static byte[] ArchiveJson(string json, string? attributes = null, string? extraEntry = null,
        CompressionLevel compression = CompressionLevel.Optimal, bool nonSeekableOutput = false)
    {
        using var memory = new MemoryStream();
        using var target = nonSeekableOutput ? new NonSeekableWriteStream(memory) : null;
        using (var zip = new ZipArchive((Stream?)target ?? memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write("export.attributes", attributes ?? "{\"version\":3,\"description\":\"1Password Unencrypted Export\",\"createdAt\":1600000000}");
            Write("export.data", json);
            if (extraEntry is not null) Write(extraEntry, "{}");
            void Write(string name, string content)
            {
                var entry = zip.CreateEntry(name, compression);
                using var output = entry.Open();
                output.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return memory.ToArray();
    }

    private static byte[] ArchiveDataBytes(byte[] data)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var attributes = zip.CreateEntry("export.attributes").Open())
                attributes.Write("{\"version\":3}"u8);
            using var content = zip.CreateEntry("export.data").Open();
            content.Write(data);
        }
        return memory.ToArray();
    }

    private static int FindCentral(byte[] bytes, string name)
    {
        for (var index = 0; index <= bytes.Length - 46; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) != 0x02014b50) continue;
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index + 28, 2));
            if (index + 46 + length <= bytes.Length && Encoding.UTF8.GetString(bytes, index + 46, length) == name) return index;
        }
        throw new InvalidOperationException("The synthetic archive did not contain its expected central entry.");
    }

    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);
        public int BytesRead { get; private set; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class NonSeekableWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
