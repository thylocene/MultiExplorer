using System.Diagnostics;

namespace MultiExplorer.Tests;

public sealed class OperationHostTests
{
    [Fact]
    public void HelperProcess_CopiesAllSelectedFilesAndPublishesTerminalState()
    {
        Guid id = Guid.NewGuid();
        string testRoot = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.OperationHost.Tests", id.ToString("N"));
        string sourceDirectory = Path.Combine(testRoot, "source");
        string destinationDirectory = Path.Combine(testRoot, "destination");
        string operationDirectory = Path.Combine(testRoot, "operations");
        string firstSourcePath = Path.Combine(sourceDirectory, "first-sample.txt");
        string secondSourcePath = Path.Combine(sourceDirectory, "second-sample.txt");
        string folderSourcePath = Path.Combine(sourceDirectory, "sample-folder");
        string nestedFolderPath = Path.Combine(folderSourcePath, "nested");
        string? previousOperationDirectory = Environment.GetEnvironmentVariable(
            FileOperationStore.DirectoryOverrideEnvironmentVariable);

        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        Directory.CreateDirectory(nestedFolderPath);
        File.WriteAllText(firstSourcePath, "first operation-host smoke test");
        File.WriteAllText(secondSourcePath, "second operation-host smoke test");
        File.WriteAllText(Path.Combine(folderSourcePath, "child.txt"), "child");
        File.WriteAllText(Path.Combine(nestedFolderPath, "deep.txt"), "deep");

        try
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                operationDirectory);
            var request = new FileOperationRequest
            {
                Id = id,
                Kind = FileOperationKind.Copy,
                Sources = [firstSourcePath, secondSourcePath, folderSourcePath],
                Destination = destinationDirectory,
                CreatedUtc = DateTime.UtcNow,
            };
            FileOperationStore.WriteRequest(request);

            string hostPath = GetOperationHostPath();
            Assert.True(File.Exists(hostPath), $"Operation host not found at {hostPath}");

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = hostPath,
                UseShellExecute = false,
                ArgumentList = { "--request", FileOperationStore.RequestPath(id) },
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

            Assert.Equal("first operation-host smoke test",
                File.ReadAllText(Path.Combine(destinationDirectory, "first-sample.txt")));
            Assert.Equal("second operation-host smoke test",
                File.ReadAllText(Path.Combine(destinationDirectory, "second-sample.txt")));
            Assert.Equal("deep", File.ReadAllText(Path.Combine(
                destinationDirectory, "sample-folder", "nested", "deep.txt")));
            FileOperationState? state = FileOperationStore.TryReadState(
                FileOperationStore.StatePath(id));
            Assert.NotNull(state);
            Assert.Equal(FileOperationStatus.Completed, state!.Status);
            Assert.True(state.TotalBytes > 0);
            Assert.Equal(6, state.TotalItems);
            Assert.Equal(6, state.CompletedItems);
            Assert.False(File.Exists(FileOperationStore.RequestPath(id)));
            Assert.False(File.Exists(FileOperationStore.CancelPath(id)));
            Assert.False(File.Exists(FileOperationStore.PausePath(id)));
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
    public async Task HelperProcess_PublishesActionableFailureForMissingDestinationAsync()
    {
        Guid id = Guid.NewGuid();
        string testRoot = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.OperationHost.Tests", id.ToString("N"));
        string operationDirectory = Path.Combine(testRoot, "operations");
        string sourcePath = Path.Combine(testRoot, "source.txt");
        string missingDestination = Path.Combine(testRoot, "missing-destination");
        string? previousOperationDirectory = Environment.GetEnvironmentVariable(
            FileOperationStore.DirectoryOverrideEnvironmentVariable);

        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(sourcePath, "failure reporting test");
        try
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                operationDirectory);
            FileOperationStore.WriteRequest(new FileOperationRequest
            {
                Id = id,
                Kind = FileOperationKind.Copy,
                Sources = [sourcePath],
                Destination = missingDestination,
                CreatedUtc = DateTime.UtcNow,
            });

            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = GetOperationHostPath(),
                UseShellExecute = false,
                ArgumentList = { "--request", FileOperationStore.RequestPath(id) },
            });
            Assert.NotNull(process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process!.WaitForExitAsync(timeout.Token);

            Assert.Equal(1, process.ExitCode);
            FileOperationState? state = FileOperationStore.TryReadState(
                FileOperationStore.StatePath(id));
            Assert.NotNull(state);
            Assert.Equal(FileOperationStatus.Failed, state!.Status);
            Assert.True(state.Result < 0);
            Assert.False(string.IsNullOrWhiteSpace(state.Error));
            Assert.Equal("Resolve destination Shell item", state.FailureStage);
            Assert.False(string.IsNullOrWhiteSpace(state.FailureExceptionType));
            Assert.False(File.Exists(FileOperationStore.RequestPath(id)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                previousOperationDirectory);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
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

            string hostPath = GetOperationHostPath();
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = hostPath,
                UseShellExecute = false,
                ArgumentList = { "--request", FileOperationStore.RequestPath(id) },
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
    public void HelperProcess_PausesAndResumesAnOperation()
    {
        Guid id = Guid.NewGuid();
        string testRoot = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.OperationHost.Tests", id.ToString("N"));
        string operationDirectory = Path.Combine(testRoot, "operations");
        string destinationDirectory = Path.Combine(testRoot, "destination");
        string sourcePath = Path.Combine(testRoot, "pause-resume.txt");
        string? previousOperationDirectory = Environment.GetEnvironmentVariable(
            FileOperationStore.DirectoryOverrideEnvironmentVariable);
        Process? process = null;

        Directory.CreateDirectory(testRoot);
        Directory.CreateDirectory(destinationDirectory);
        File.WriteAllText(sourcePath, "pause and resume");
        try
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                operationDirectory);
            FileOperationStore.WriteRequest(new FileOperationRequest
            {
                Id = id,
                Kind = FileOperationKind.Copy,
                Sources = [sourcePath],
                Destination = destinationDirectory,
                CreatedUtc = DateTime.UtcNow,
            });
            FileOperationStore.RequestPause(id);

            process = Process.Start(new ProcessStartInfo
            {
                FileName = GetOperationHostPath(),
                UseShellExecute = false,
                ArgumentList = { "--request", FileOperationStore.RequestPath(id) },
            });
            Assert.NotNull(process);

            var waitForPause = Stopwatch.StartNew();
            FileOperationState? pausedState = null;
            while (waitForPause.Elapsed < TimeSpan.FromSeconds(5)
                   && !process!.HasExited)
            {
                pausedState = FileOperationStore.TryReadState(
                    FileOperationStore.StatePath(id));
                if (pausedState?.Status == FileOperationStatus.Paused)
                    break;
                Thread.Sleep(25);
            }

            Assert.NotNull(pausedState);
            Assert.Equal(FileOperationStatus.Paused, pausedState!.Status);
            Assert.False(File.Exists(Path.Combine(
                destinationDirectory, Path.GetFileName(sourcePath))));

            FileOperationStore.RequestResume(id);
            Assert.True(process!.WaitForExit(10_000));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("pause and resume", File.ReadAllText(Path.Combine(
                destinationDirectory, Path.GetFileName(sourcePath))));
            FileOperationState? completedState = FileOperationStore.TryReadState(
                FileOperationStore.StatePath(id));
            Assert.NotNull(completedState);
            Assert.Equal(FileOperationStatus.Completed, completedState!.Status);
            Assert.False(File.Exists(FileOperationStore.PausePath(id)));
        }
        finally
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
            process?.Dispose();
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                previousOperationDirectory);
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void HelperProcess_PermanentDeleteCompletesWithoutConfirmation()
    {
        Guid id = Guid.NewGuid();
        string testRoot = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.OperationHost.Tests", id.ToString("N"));
        string operationDirectory = Path.Combine(testRoot, "operations");
        string sourcePath = Path.Combine(testRoot, "permanent-delete.txt");
        string sourceDirectory = Path.Combine(testRoot, "permanent-delete-folder");
        string? previousOperationDirectory = Environment.GetEnvironmentVariable(
            FileOperationStore.DirectoryOverrideEnvironmentVariable);

        Directory.CreateDirectory(testRoot);
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(sourcePath, "delete without confirmation");
        File.WriteAllText(Path.Combine(sourceDirectory, "nested.txt"),
            "delete folder without confirmation");
        try
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                operationDirectory);
            FileOperationStore.WriteRequest(new FileOperationRequest
            {
                Id = id,
                Kind = FileOperationKind.DeletePermanently,
                Sources = [sourcePath, sourceDirectory],
                CreatedUtc = DateTime.UtcNow,
            });

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = GetOperationHostPath(),
                UseShellExecute = false,
                ArgumentList = { "--request", FileOperationStore.RequestPath(id) },
            });
            Assert.NotNull(process);
            if (!process!.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Permanent delete waited for an unexpected confirmation dialog.");
            }

            Assert.Equal(0, process.ExitCode);
            Assert.False(File.Exists(sourcePath));
            Assert.False(Directory.Exists(sourceDirectory));
            FileOperationState? state = FileOperationStore.TryReadState(
                FileOperationStore.StatePath(id));
            Assert.NotNull(state);
            Assert.Equal(FileOperationStatus.Completed, state!.Status);
            Assert.Equal(0UL, state.TotalBytes);
            Assert.Equal(2, state.TotalItems);
            Assert.Equal(3, state.ProcessedItems);
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
    public void BrokerProcess_ExecutesMultipleRequestsInOneHost()
    {
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        string testRoot = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.OperationHost.Tests", firstId.ToString("N"));
        string firstDestination = Path.Combine(testRoot, "destination-one");
        string secondDestination = Path.Combine(testRoot, "destination-two");
        string operationDirectory = Path.Combine(testRoot, "operations");
        string firstSource = Path.Combine(testRoot, "first.txt");
        string secondSource = Path.Combine(testRoot, "second.txt");
        string? previousOperationDirectory = Environment.GetEnvironmentVariable(
            FileOperationStore.DirectoryOverrideEnvironmentVariable);

        Directory.CreateDirectory(testRoot);
        Directory.CreateDirectory(firstDestination);
        Directory.CreateDirectory(secondDestination);
        File.WriteAllText(firstSource, "first broker operation");
        File.WriteAllText(secondSource, "second broker operation");

        try
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                operationDirectory);
            FileOperationStore.WriteRequest(new FileOperationRequest
            {
                Id = firstId,
                Kind = FileOperationKind.Copy,
                Sources = [firstSource],
                Destination = firstDestination,
                CreatedUtc = DateTime.UtcNow,
            });
            FileOperationStore.WriteRequest(new FileOperationRequest
            {
                Id = secondId,
                Kind = FileOperationKind.Copy,
                Sources = [secondSource],
                Destination = secondDestination,
                CreatedUtc = DateTime.UtcNow.AddMilliseconds(1),
            });

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = GetOperationHostPath(),
                UseShellExecute = false,
                ArgumentList = { "--broker", "--exit-when-idle" },
            });
            Assert.NotNull(process);
            if (!process!.WaitForExit(15_000))
            {
                FileOperationStore.RequestCancellation(firstId);
                FileOperationStore.RequestCancellation(secondId);
                if (!process.WaitForExit(5_000))
                    process.Kill(entireProcessTree: true);
                Assert.Fail("The shared operation broker did not exit after becoming idle.");
            }

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("first broker operation", File.ReadAllText(
                Path.Combine(firstDestination, Path.GetFileName(firstSource))));
            Assert.Equal("second broker operation", File.ReadAllText(
                Path.Combine(secondDestination, Path.GetFileName(secondSource))));

            FileOperationState? firstState = FileOperationStore.TryReadState(
                FileOperationStore.StatePath(firstId));
            FileOperationState? secondState = FileOperationStore.TryReadState(
                FileOperationStore.StatePath(secondId));
            Assert.NotNull(firstState);
            Assert.NotNull(secondState);
            Assert.Equal(FileOperationStatus.Completed, firstState!.Status);
            Assert.Equal(FileOperationStatus.Completed, secondState!.Status);
            Assert.Equal(process.Id, firstState.HostProcessId);
            Assert.Equal(process.Id, secondState.HostProcessId);
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
    public async Task BrokerProcess_ExitsForInstallerSessionShutdownAsync()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "MultiExplorer.OperationHost.Tests",
            Guid.NewGuid().ToString("N"));
        string operationDirectory = Path.Combine(testRoot, "operations");
        Directory.CreateDirectory(operationDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = GetOperationHostPath(),
            UseShellExecute = false,
            ArgumentList = { "--broker" },
        };
        startInfo.Environment[
            FileOperationStore.DirectoryOverrideEnvironmentVariable] = operationDirectory;

        using Process? process = Process.Start(startInfo);
        Assert.NotNull(process);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            IntPtr window = await WaitForTopLevelWindowAsync(
                process!.Id,
                timeout.Token);

            const uint wmQueryEndSession = 0x0011;
            const uint wmEndSession = 0x0016;
            Assert.NotEqual(IntPtr.Zero, NativeMethods.SendMessageI(
                window, wmQueryEndSession, IntPtr.Zero, IntPtr.Zero));
            NativeMethods.SendMessageI(
                window, wmEndSession, (IntPtr)1, IntPtr.Zero);

            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BrokerProcess_RejectsExpiredUnacknowledgedRequestAsync()
    {
        Guid id = Guid.NewGuid();
        string testRoot = Path.Combine(Path.GetTempPath(),
            "MultiExplorer.OperationHost.Tests", id.ToString("N"));
        string sourceDirectory = Path.Combine(testRoot, "source");
        string destinationDirectory = Path.Combine(testRoot, "destination");
        string operationDirectory = Path.Combine(testRoot, "operations");
        string sourcePath = Path.Combine(sourceDirectory, "stale.txt");
        string destinationPath = Path.Combine(destinationDirectory, "stale.txt");
        string? previousOperationDirectory = Environment.GetEnvironmentVariable(
            FileOperationStore.DirectoryOverrideEnvironmentVariable);

        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        await File.WriteAllTextAsync(sourcePath, "must not move");

        Process? process = null;
        try
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                operationDirectory);
            FileOperationStore.WriteRequest(new FileOperationRequest
            {
                Id = id,
                Kind = FileOperationKind.Move,
                Sources = [sourcePath],
                Destination = destinationDirectory,
                CreatedUtc = DateTime.UtcNow
                    - FileOperationStore.StartupAcknowledgementTimeout
                    - TimeSpan.FromSeconds(1),
            });

            process = Process.Start(new ProcessStartInfo
            {
                FileName = GetOperationHostPath(),
                UseShellExecute = false,
                ArgumentList = { "--broker", "--exit-when-idle" },
            });
            Assert.NotNull(process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process!.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(destinationPath));
            FileOperationState? state = FileOperationStore.TryReadState(
                FileOperationStore.StatePath(id));
            Assert.NotNull(state);
            Assert.Equal(FileOperationStatus.Failed, state!.Status);
            Assert.Contains("expired", state.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(FileOperationStore.RequestPath(id)));
        }
        finally
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
            process?.Dispose();
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                previousOperationDirectory);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void InitialStateWrite_DoesNotOverwriteExistingAcknowledgement()
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
            var acknowledgement = new FileOperationState
            {
                Id = id,
                Kind = FileOperationKind.Move,
                Status = FileOperationStatus.Queued,
                HostProcessId = 42,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            };
            var competingFailure = new FileOperationState
            {
                Id = id,
                Kind = FileOperationKind.Move,
                Status = FileOperationStatus.Failed,
                Error = "startup timeout",
                CreatedUtc = acknowledgement.CreatedUtc,
                UpdatedUtc = DateTime.UtcNow,
            };

            Assert.True(FileOperationStore.TryWriteInitialState(acknowledgement));
            Assert.False(FileOperationStore.TryWriteInitialState(competingFailure));

            FileOperationState? stored = FileOperationStore.TryReadState(
                FileOperationStore.StatePath(id));
            Assert.NotNull(stored);
            Assert.Equal(FileOperationStatus.Queued, stored!.Status);
            Assert.Equal(42, stored.HostProcessId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                previousOperationDirectory);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StateWrite_RetriesWhenExistingStateIsTemporarilyLockedAsync()
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
            Assert.True(FileOperationStore.TryWriteInitialState(new FileOperationState
            {
                Id = id,
                Status = FileOperationStatus.Queued,
            }));

            string statePath = FileOperationStore.StatePath(id);
            using var blockingReader = new FileStream(statePath, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            var started = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task writer = Task.Run(() =>
            {
                started.SetResult();
                FileOperationStore.WriteState(new FileOperationState
                {
                    Id = id,
                    Status = FileOperationStatus.Running,
                });
            });

            try
            {
                await started.Task;
                await Task.Delay(150);
                Assert.False(writer.IsCompleted);
            }
            finally
            {
                blockingReader.Dispose();
                await writer;
            }

            FileOperationState? state = FileOperationStore.TryReadState(statePath);
            Assert.NotNull(state);
            Assert.Equal(FileOperationStatus.Running, state.Status);
            Assert.True(state.StateWriteRetries > 0);
            Assert.Empty(Directory.EnumerateFiles(operationDirectory, "*.tmp"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                previousOperationDirectory);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
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
            FileOperationStore.RequestPause(id);
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
            Assert.False(File.Exists(FileOperationStore.PausePath(id)));
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
            FileOperationStore.RequestPause(id);
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
            Assert.False(File.Exists(FileOperationStore.PausePath(id)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                FileOperationStore.DirectoryOverrideEnvironmentVariable,
                previousOperationDirectory);
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string GetOperationHostPath()
    {
        var testOutputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = testOutputDirectory.Parent?.Name
            ?? throw new DirectoryNotFoundException(
                "The test build configuration directory was not found.");
        string repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Path.Combine(repositoryRoot, "MultiExplorer.OperationHost", "bin",
            configuration, "net8.0-windows", "win-x64",
            "MultiExplorer.OperationHost.exe");
    }

    private static async Task<IntPtr> WaitForTopLevelWindowAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            IntPtr window = FindTopLevelWindow(processId);
            if (window != IntPtr.Zero)
                return window;

            await Task.Delay(50, cancellationToken);
        }
    }

    private static IntPtr FindTopLevelWindow(int processId)
    {
        IntPtr result = IntPtr.Zero;
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint ownerProcessId);
            if (ownerProcessId != unchecked((uint)processId))
                return true;

            var title = new System.Text.StringBuilder(128);
            NativeMethods.GetWindowText(window, title, title.Capacity);
            if (!title.ToString().EndsWith("% complete", StringComparison.Ordinal))
                return true;

            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }
}
