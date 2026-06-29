using Core.Enums;
using Core.Models;
using Core.Session;
using Vault;
using Xunit;

namespace Vault.Tests;

/// <summary>
/// Tests for VaultSession per-Id upsert/remove methods.
/// Covers: UpsertCipher (replace existing Id, append new Id), RemoveCipher (drop by Id),
/// and the same three cases for UpsertFolder / RemoveFolder.
/// </summary>
public class VaultSessionPatchTests
{
    // ──────────────────────── helpers ────────────────────────

    private static VaultSession SeedSession(
        Cipher[] ciphers,
        Folder[] folders)
    {
        var session = new VaultSession();
        session.SetSnapshot(new DecryptedVault(
            new AccountInfo("me@example.com", "https://vault.example", "M", "PBKDF2 600000"),
            folders,
            ciphers,
            0));
        return session;
    }

    private static Cipher MakeCipher(string id, string name) =>
        new() { Id = id, Type = CipherType.Login, Name = name };

    private static Folder MakeFolder(string id, string name) =>
        new() { Id = id, Name = name };

    // ──────────────────────── Cipher ─────────────────────────

    [Fact]
    public void UpsertCipher_ExistingId_ReplacesInPlaceCountUnchanged()
    {
        // Arrange
        var session = SeedSession(
            [MakeCipher("c1", "Original"), MakeCipher("c2", "Other")],
            []);

        // Act — same Id "c1", different Name
        session.UpsertCipher(MakeCipher("c1", "Updated"));

        // Assert — count stays at 2, name is replaced
        Assert.Equal(2, session.Ciphers.Count);
        var c1 = Assert.Single(session.Ciphers, c => c.Id == "c1");
        Assert.Equal("Updated", c1.Name);
    }

    [Fact]
    public void UpsertCipher_NewId_AppendsCountPlusOne()
    {
        // Arrange — start with one cipher
        var session = SeedSession(
            [MakeCipher("c1", "Existing")],
            []);

        // Act — upsert with a brand-new Id
        session.UpsertCipher(MakeCipher("c-new", "Brand New"));

        // Assert — count grows by 1, new item visible
        Assert.Equal(2, session.Ciphers.Count);
        Assert.Single(session.Ciphers, c => c.Id == "c-new" && c.Name == "Brand New");
    }

    [Fact]
    public void RemoveCipher_ExistingId_DropsCipherCountMinusOne()
    {
        // Arrange
        var session = SeedSession(
            [MakeCipher("c1", "Keep"), MakeCipher("c2", "Remove Me")],
            []);

        // Act
        session.RemoveCipher("c2");

        // Assert — count reduced, "c2" gone, "c1" intact
        Assert.Single(session.Ciphers);
        Assert.All(session.Ciphers, c => Assert.NotEqual("c2", c.Id));
        Assert.Single(session.Ciphers, c => c.Id == "c1");
    }

    [Fact]
    public void RemoveCipher_MissingId_IsNoOp()
    {
        // Arrange
        var session = SeedSession(
            [MakeCipher("c1", "Only")],
            []);

        // Act — Id that does not exist
        session.RemoveCipher("no-such-id");

        // Assert — unchanged
        Assert.Single(session.Ciphers);
    }

    // ──────────────────────── Folder ─────────────────────────

    [Fact]
    public void UpsertFolder_ExistingId_ReplacesInPlaceCountUnchanged()
    {
        // Arrange
        var session = SeedSession(
            [],
            [MakeFolder("f1", "Work"), MakeFolder("f2", "Personal")]);

        // Act
        session.UpsertFolder(MakeFolder("f1", "Work Updated"));

        // Assert
        Assert.Equal(2, session.Folders.Count);
        var f1 = Assert.Single(session.Folders, f => f.Id == "f1");
        Assert.Equal("Work Updated", f1.Name);
    }

    [Fact]
    public void UpsertFolder_NewId_AppendsCountPlusOne()
    {
        // Arrange
        var session = SeedSession(
            [],
            [MakeFolder("f1", "Existing")]);

        // Act
        session.UpsertFolder(MakeFolder("f-new", "New Folder"));

        // Assert
        Assert.Equal(2, session.Folders.Count);
        Assert.Single(session.Folders, f => f.Id == "f-new" && f.Name == "New Folder");
    }

    [Fact]
    public void RemoveFolder_ExistingId_DropsFolderCountMinusOne()
    {
        // Arrange
        var session = SeedSession(
            [],
            [MakeFolder("f1", "Keep"), MakeFolder("f2", "Gone")]);

        // Act
        session.RemoveFolder("f2");

        // Assert
        Assert.Single(session.Folders);
        Assert.All(session.Folders, f => Assert.NotEqual("f2", f.Id));
    }

    [Fact]
    public void RemoveFolder_MissingId_IsNoOp()
    {
        // Arrange
        var session = SeedSession(
            [],
            [MakeFolder("f1", "Only")]);

        // Act
        session.RemoveFolder("no-such-id");

        // Assert
        Assert.Single(session.Folders);
    }
}
