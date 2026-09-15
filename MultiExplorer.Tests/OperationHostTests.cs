using System.Diagnostics;

namespace MultiExplorer.Tests;

public sealed class OperationHostTests
{
    [Fact]
    public void HelperProcess_CopiesFileAndPublishesTerminalState()
    {
        Guid id = Guid.NewGuid();
        string testRoot = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.OperationHost.Tests", id.ToString("N"));
        string sourceDirectory = Path.Combine(testRoot, "source");
        string destinationDirectory = Path.Combine(testRoot, "destination");
        string operationDirectory = Path.Combine(testRoot, "operations");
        string sourcePath = Path.Combine(sourceDirectory, "sample.txt");
        string? previousOperationDirectory = Environment.GetEnvironmentVariable(
            FileOperationStore.DirectoryOverrideEnvironmentVariable);

        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        File.WriteAllText(sourcePath, "operation-host smoke test");

        try
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                operationDirectory);
            var request = new FileOperationRequest
            {
                Id = id,
                Kind = FileOperationKind.Copy,
                Sources = [sourcePath],
                Destination = destinationDirectory,
                CreatedUtc = DateTime.UtcNow,
            };
            FileOperationStore.WriteRequest(request);

            string hostPath = Path.Combine(AppContext.BaseDirectory,
                "MultiExplorer.OperationHost.dll");
            Assert.True(File.Exists(hostPath), $"Operation host not found at {hostPath}");

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                ArgumentList = { hostPath, "--request", FileOperationStore.RequestPath(id) },
            });
            Assert.NotNull(process);
            if (!process!.WaitForExit(10_000))
            {
                FileOperationStore.RequestCancellation(id);
                if (!process.WaitForExit(5_000))
                    process.Kill(entireProcessTree: true);
                FileOperationState? stalled = FileOperationStore.TryReadState(
                    FileOperationStore.StatePath(id));
                Assert.Fail("The operation host did not exit. Last state: " +
                    (stalled == null
                        ? "none"
                        : $"{stalled.Status}, {stalled.CompletedItems}/{stalled.TotalItems}, " +
                          $"result 0x{stalled.Result:X8}, {stalled.Error}"));
            }
            Assert.Equal(0, process.ExitCode);

            Assert.Equal("operation-host smoke test",
                File.ReadAllText(Path.Combine(destinationDirectory, "sample.txt")));
            FileOperationState? state = FileOperationStore.TryReadState(
                FileOperationStore.StatePath(id));
            Assert.NotNull(state);
            Assert.Equal(FileOperationStatus.Completed, state!.Status);
            Assert.Equal(1, state.CompletedItems);
            Assert.False(File.Exists(FileOperationStore.RequestPath(id)));
            Assert.False(File.Exists(FileOperationStore.CancelPath(id)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                previousOperationDirectory);
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void HelperProcess_HonoursCancellationBeforeStarting()
    {
        Guid id = Guid.NewGuid();
        string testRoot = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.OperationHost.Tests", id.ToString("N"));
        string operationDirectory = Path.Combine(testRoot, "operations");
        string sourcePath = Path.Combine(testRoot, "do-not-delete.txt");
        string? previousOperationDirectory = Environment.GetEnvironmentVariable(
            FileOperationStore.DirectoryOverrideEnvironmentVariable);

        Directory.CreateDirectory(testRoot);
        File.WriteAllText(sourcePath, "keep");
        try
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                operationDirectory);
            var request = new FileOperationRequest
            {
                Id = id,
                Kind = FileOperationKind.DeletePermanently,
                Sources = [sourcePath],
                CreatedUtc = DateTime.UtcNow,
            };
            FileOperationStore.WriteRequest(request);
            FileOperationStore.RequestCancellation(id);

            string hostPath = Path.Combine(AppContext.BaseDirectory,
                "MultiExplorer.OperationHost.dll");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                ArgumentList = { hostPath, "--request", FileOperationStore.RequestPath(id) },
            });
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(10_000));
            Assert.Equal(3, process.ExitCode);
            Assert.True(File.Exists(sourcePath));

            FileOperationState? state = FileOperationStore.TryReadState(
                FileOperationStore.StatePath(id));
            Assert.NotNull(state);
            Assert.Equal(FileOperationStatus.Cancelled, state!.Status);
            Assert.False(File.Exists(FileOperationStore.RequestPath(id)));
            Assert.False(File.Exists(FileOperationStore.CancelPath(id)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                previousOperationDirectory);
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void RetentionCleanup_DeletesExpiredTerminalArtifacts()
    {
        Guid id = Guid.NewGuid();
        string testRoot = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.OperationHost.Tests", id.ToString("N"));
        string operationDirectory = Path.Combine(testRoot, "operations");
        string? previousOperationDirectory = Environment.GetEnvironmentVariable(
            FileOperationStore.DirectoryOverrideEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                operationDirectory);
            FileOperationStore.WriteRequest(new FileOperationRequest
            {
                Id = id,
                Kind = FileOperationKind.Copy,
                Sources = [@"C:\source.txt"],
                Destination = @"C:\destination",
                CreatedUtc = DateTime.UtcNow,
            });
            FileOperationStore.RequestCancellation(id);
            FileOperationStore.WriteState(new FileOperationState
            {
                Id = id,
                Kind = FileOperationKind.Copy,
                Status = FileOperationStatus.Completed,
                UpdatedUtc = DateTime.UtcNow,
            });
            File.SetLastWriteTimeUtc(FileOperationStore.StatePath(id),
                DateTime.UtcNow - TimeSpan.FromDays(2));

            OperationManager.CleanupExpiredOperationArtifacts();

            Assert.False(File.Exists(FileOperationStore.RequestPath(id)));
            Assert.False(File.Exists(FileOperationStore.CancelPath(id)));
            Assert.False(File.Exists(FileOperationStore.StatePath(id)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                previousOperationDirectory);
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void RetentionCleanup_MarksAbandonedOperationAsFailedAndRemovesTransientArtifacts()
    {
        Guid id = Guid.NewGuid();
        string testRoot = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.OperationHost.Tests", id.ToString("N"));
        string operationDirectory = Path.Combine(testRoot, "operations");
        string? previousOperationDirectory = Environment.GetEnvironmentVariable(
            FileOperationStore.DirectoryOverrideEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                operationDirectory);
            FileOperationStore.WriteRequest(new FileOperationRequest
            {
                Id = id,
                Kind = FileOperationKind.Delete,
                Sources = [@"C:\source.txt"],
                CreatedUtc = DateTime.UtcNow,
            });
            FileOperationStore.RequestCancellation(id);
            FileOperationStore.WriteState(new FileOperationState
            {
                Id = id,
                Kind = FileOperationKind.Delete,
                Status = FileOperationStatus.Running,
                HostProcessId = int.MaxValue,
                UpdatedUtc = DateTime.UtcNow,
            });

            OperationManager.CleanupExpiredOperationArtifacts();

            FileOperationState? state = FileOperationStore.TryReadState(
                FileOperationStore.StatePath(id));
            Assert.NotNull(state);
            Assert.Equal(FileOperationStatus.Failed, state!.Status);
            Assert.False(File.Exists(FileOperationStore.RequestPath(id)));
            Assert.False(File.Exists(FileOperationStore.CancelPath(id)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                previousOperationDirectory);
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }
}
