using Xunit;

namespace App.Tests;

public sealed class FileTransferStreamingContractTests
{
    [Fact]
    public void ProductionTransferPaths_DoNotUseWholeFileBufferingApis()
    {
        var files = new[]
        {
            RepoFile("src", "App", "Views", "VaultPage.xaml.cs"),
            RepoFile("src", "App", "Views", "SendPage.xaml.cs"),
            RepoFile("src", "App", "Views", "ImportExportPage.xaml.cs"),
            RepoFile("src", "App", "Services", "AttachmentUiService.cs"),
            RepoFile("src", "App", "Services", "StorageFileSource.cs"),
            RepoFile("src", "Vault", "AttachmentService.cs"),
            RepoFile("src", "Vault", "SendAccessService.cs"),
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("ReadAsByteArrayAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ReadBufferAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WriteBytesAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".ToArray()", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SaveHandlers_TrackEntireTransactionalDownloadAndCommitLast()
    {
        foreach (var file in new[]
        {
            RepoFile("src", "App", "Views", "VaultPage.xaml.cs"),
            RepoFile("src", "App", "Views", "SendPage.xaml.cs"),
            RepoFile("src", "App", "Views", "ImportExportPage.xaml.cs"),
        })
        {
            var source = File.ReadAllText(file);
            var tracked = source.IndexOf("FilePickerHelper.RunTrackedAsync", StringComparison.Ordinal);
            var transaction = source.IndexOf("OpenTransactedWriteAsync", tracked, StringComparison.Ordinal);
            var flush = source.IndexOf("FlushAsync(ct)", transaction, StringComparison.Ordinal);
            var cancellationCheck = source.IndexOf("ct.ThrowIfCancellationRequested()", flush, StringComparison.Ordinal);
            var commit = source.IndexOf("FilePickerHelper.CommitSensitiveAsync", cancellationCheck, StringComparison.Ordinal);

            Assert.True(tracked >= 0);
            Assert.True(transaction > tracked);
            Assert.True(flush > transaction);
            Assert.True(cancellationCheck > flush);
            Assert.True(commit > cancellationCheck);
            Assert.DoesNotContain("transaction.CommitAsync()", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StorageFileOpenUsesCancelWinsLateDisposalAdapter()
    {
        var source = File.ReadAllText(
            RepoFile("src", "App", "Services", "StorageFileSource.cs"));

        Assert.Contains("CancelWinsTask.AwaitAsync", source, StringComparison.Ordinal);
        Assert.Contains("stream.DisposeAsync()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await file.OpenStreamForReadAsync()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PickerLifecycleCancellationStopsManagedWaitAndBestEffortCancelsWinRtOperation()
    {
        var source = File.ReadAllText(
            RepoFile("src", "App", "Views", "FilePickerHelper.cs"));
        var callback = source.IndexOf("void CancelPickerWait()", StringComparison.Ordinal);
        var registration = source.IndexOf("CancelPickerWait,", callback, StringComparison.Ordinal);

        Assert.True(callback >= 0);
        Assert.True(registration > callback);
        Assert.Contains("Interlocked.Exchange(ref cancellationRequested, 1)", source, StringComparison.Ordinal);
        Assert.Contains("TryCancel(linked);", source[callback..registration], StringComparison.Ordinal);
        Assert.Contains("TryCancel(operation);", source[callback..registration], StringComparison.Ordinal);
        Assert.Contains("catch (ObjectDisposedException)", source[registration..], StringComparison.Ordinal);
    }

    [Fact]
    public void TrackedAndPickerHelpersRecheckCancellationAfterSuccessfulAwait()
    {
        var source = File.ReadAllText(
            RepoFile("src", "App", "Views", "FilePickerHelper.cs"));
        var trackedAwait = source.IndexOf("var result = await operation(linked.Token);", StringComparison.Ordinal);
        var trackedCheck = source.IndexOf("linked.Token.ThrowIfCancellationRequested();", trackedAwait, StringComparison.Ordinal);
        var trackedReturn = source.IndexOf("return result;", trackedCheck, StringComparison.Ordinal);
        var pickerAwait = source.IndexOf("var result = await operation.AsTask(linked.Token);", StringComparison.Ordinal);
        var pickerCheck = source.IndexOf("Volatile.Read(ref cancellationRequested)", pickerAwait, StringComparison.Ordinal);
        var pickerReturn = source.IndexOf("return result;", pickerCheck, StringComparison.Ordinal);

        Assert.True(trackedAwait >= 0 && trackedCheck > trackedAwait && trackedReturn > trackedCheck);
        Assert.True(pickerAwait >= 0 && pickerCheck > pickerAwait && pickerReturn > pickerCheck);
        Assert.Contains("linked.IsCancellationRequested", source[pickerCheck..pickerReturn], StringComparison.Ordinal);

        var dialog = File.ReadAllText(
            RepoFile("src", "App", "Views", "DialogHelper.cs"));
        var dialogAwait = dialog.IndexOf("var result = await dialog.ShowAsync().AsTask(linked.Token);", StringComparison.Ordinal);
        var dialogCheck = dialog.IndexOf("linked.Token.ThrowIfCancellationRequested();", dialogAwait, StringComparison.Ordinal);
        var dialogReturn = dialog.IndexOf("return result;", dialogCheck, StringComparison.Ordinal);
        Assert.True(dialogAwait >= 0 && dialogCheck > dialogAwait && dialogReturn > dialogCheck);
    }

    [Fact]
    public void ImportExportUsesTransactionalExportAndSessionBoundCancelWinsImport()
    {
        var source = File.ReadAllText(
            RepoFile("src", "App", "Views", "ImportExportPage.xaml.cs"));

        Assert.DoesNotContain("FileIO.WriteTextAsync", source, StringComparison.Ordinal);
        Assert.Contains("RunTrackedAsync<bool>", source, StringComparison.Ordinal);
        Assert.Contains("OpenTransactedWriteAsync", source, StringComparison.Ordinal);
        Assert.Contains("FilePickerHelper.CommitSensitiveAsync", source, StringComparison.Ordinal);
        Assert.Contains("CancelWinsTask.AwaitAsync", source, StringComparison.Ordinal);
        Assert.Contains("TryCaptureOperation", source, StringComparison.Ordinal);
        Assert.Contains("TryPublish", source, StringComparison.Ordinal);
        Assert.Contains("_lifecycle.TryRunSensitive", source, StringComparison.Ordinal);
        Assert.Contains("_session.TryRunIfUnlocked", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitStartIsAtomicWithLifecycleAndExpectedSessionValidation()
    {
        var helper = File.ReadAllText(
            RepoFile("src", "App", "Views", "FilePickerHelper.cs"));
        var method = helper.IndexOf("Task CommitSensitiveAsync", StringComparison.Ordinal);
        var lifecycleGate = helper.IndexOf("lifecycle.TryRunSensitive", method, StringComparison.Ordinal);
        var sessionGate = helper.IndexOf("session.TryRunIfUnlocked", lifecycleGate, StringComparison.Ordinal);
        var start = helper.IndexOf("transaction.CommitAsync()", sessionGate, StringComparison.Ordinal);
        var awaitOperation = helper.IndexOf("commitOperation.AsTask()", start, StringComparison.Ordinal);

        Assert.True(method >= 0);
        Assert.Contains("VaultSessionLease expectedLease", helper[method..lifecycleGate], StringComparison.Ordinal);
        Assert.True(lifecycleGate > method && sessionGate > lifecycleGate && start > sessionGate);
        Assert.True(awaitOperation > start);
        Assert.DoesNotContain("TryBeginSensitiveCommit", helper, StringComparison.Ordinal);

        foreach (var file in new[]
        {
            RepoFile("src", "App", "Views", "VaultPage.xaml.cs"),
            RepoFile("src", "App", "Views", "SendPage.xaml.cs"),
        })
        {
            var source = File.ReadAllText(file);
            var capture = source.IndexOf("TryCaptureExpectedLease", StringComparison.Ordinal);
            var picker = source.IndexOf("FileSavePicker", capture, StringComparison.Ordinal);
            var commit = source.IndexOf("CommitSensitiveAsync", picker, StringComparison.Ordinal);
            Assert.True(capture >= 0 && picker > capture && commit > picker);
        }
    }

    private static string RepoFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
