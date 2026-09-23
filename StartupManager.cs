using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>Maintains the current user's Windows sign-in launch registration.</summary>
internal static class StartupManager
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "MultiExplorer";
    internal const string TaskName = @"\MultiExplorer Start with Windows";

    private const int FileNotFoundHResult = unchecked((int)0x80070002);
    private const int PathNotFoundHResult = unchecked((int)0x80070003);
    private static readonly SemaphoreSlim RegistrationGate = new(1, 1);

    internal static string BuildTaskDefinition(string executablePath, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        string fullExecutablePath = Path.GetFullPath(executablePath);
        string workingDirectory = Path.GetDirectoryName(fullExecutablePath)
            ?? throw new ArgumentException(
                "The executable path must have a parent directory.",
                nameof(executablePath));
        XNamespace taskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        var definition = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(taskNamespace + "Task",
                new XAttribute("version", "1.4"),
                new XElement(taskNamespace + "RegistrationInfo",
                    new XElement(taskNamespace + "Description",
                        "Starts MultiExplorer promptly when this user signs in."),
                    new XElement(taskNamespace + "URI", TaskName)),
                new XElement(taskNamespace + "Triggers",
                    new XElement(taskNamespace + "LogonTrigger",
                        new XElement(taskNamespace + "Enabled", true),
                        new XElement(taskNamespace + "UserId", userId))),
                new XElement(taskNamespace + "Principals",
                    new XElement(taskNamespace + "Principal",
                        new XAttribute("id", "CurrentUser"),
                        new XElement(taskNamespace + "UserId", userId),
                        new XElement(taskNamespace + "LogonType", "InteractiveToken"),
                        new XElement(taskNamespace + "RunLevel", "LeastPrivilege"))),
                new XElement(taskNamespace + "Settings",
                    new XElement(taskNamespace + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(taskNamespace + "DisallowStartIfOnBatteries", false),
                    new XElement(taskNamespace + "StopIfGoingOnBatteries", false),
                    new XElement(taskNamespace + "AllowHardTerminate", true),
                    new XElement(taskNamespace + "StartWhenAvailable", true),
                    new XElement(taskNamespace + "RunOnlyIfNetworkAvailable", false),
                    new XElement(taskNamespace + "IdleSettings",
                        new XElement(taskNamespace + "StopOnIdleEnd", false),
                        new XElement(taskNamespace + "RestartOnIdle", false)),
                    new XElement(taskNamespace + "AllowStartOnDemand", true),
                    new XElement(taskNamespace + "Enabled", true),
                    new XElement(taskNamespace + "Hidden", false),
                    new XElement(taskNamespace + "RunOnlyIfIdle", false),
                    new XElement(taskNamespace + "WakeToRun", false),
                    new XElement(taskNamespace + "ExecutionTimeLimit", "PT0S"),
                    // Task Scheduler uses 0 as highest and 10 as idle. Four keeps
                    // sign-in initialization responsive without using high priority.
                    new XElement(taskNamespace + "Priority", 4)),
                new XElement(taskNamespace + "Actions",
                    new XAttribute("Context", "CurrentUser"),
                    new XElement(taskNamespace + "Exec",
                        new XElement(taskNamespace + "Command", fullExecutablePath),
                        new XElement(taskNamespace + "Arguments", "--startup"),
                        new XElement(taskNamespace + "WorkingDirectory", workingDirectory)))));

        return definition.ToString();
    }

    internal static async Task<(bool Success, Exception? Error)> TrySetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await RegistrationGate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                await CreateScheduledTaskAsync(
                    Application.ExecutablePath, cancellationToken);
                DeleteLegacyRunRegistration(throwOnFailure: false);
            }
            else
            {
                await DeleteScheduledTaskAsync(cancellationToken);
                DeleteLegacyRunRegistration(throwOnFailure: true);
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex);
        }
        finally
        {
            RegistrationGate.Release();
        }
    }

    private static async Task CreateScheduledTaskAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string userId = identity.User?.Value
            ?? throw new InvalidOperationException(
                "Windows could not determine the current user's security identifier.");
        string taskDefinition = BuildTaskDefinition(executablePath, userId);
        string temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"MultiExplorer.Startup.{Environment.ProcessId}.{Guid.NewGuid():N}.xml");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                taskDefinition,
                // schtasks.exe consumes task-definition files through the
                // Task Scheduler 2.0 XML loader, which requires a Unicode BOM.
                Encoding.Unicode,
                cancellationToken);

            TaskSchedulerCommandResult result = await RunTaskSchedulerAsync(
                ["/Create", "/TN", TaskName, "/XML", temporaryPath, "/F", "/HRESULT"],
                cancellationToken);
            ThrowIfTaskSchedulerFailed(result, "create the Windows logon task");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug(ex, nameof(CreateScheduledTaskAsync),
                    "Could not delete the temporary startup-task definition.");
            }
        }
    }

    private static async Task DeleteScheduledTaskAsync(
        CancellationToken cancellationToken)
    {
        TaskSchedulerCommandResult result = await RunTaskSchedulerAsync(
            ["/Delete", "/TN", TaskName, "/F", "/HRESULT"],
            cancellationToken);
        if (IsTaskNotFoundExitCode(result.ExitCode))
            return;

        ThrowIfTaskSchedulerFailed(result, "remove the Windows logon task");
    }

    internal static bool IsTaskNotFoundExitCode(int exitCode) =>
        exitCode is FileNotFoundHResult or PathNotFoundHResult;

    private static async Task<TaskSchedulerCommandResult> RunTaskSchedulerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string schedulerPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = schedulerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Windows Task Scheduler could not be started.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                AppLog.Debug(ex, nameof(RunTaskSchedulerAsync),
                    "Could not stop the cancelled Task Scheduler command.");
            }
            throw;
        }

        return new TaskSchedulerCommandResult(
            process.ExitCode,
            (await standardOutput).Trim(),
            (await standardError).Trim());
    }

    private static void ThrowIfTaskSchedulerFailed(
        TaskSchedulerCommandResult result,
        string operation)
    {
        if (result.ExitCode == 0)
            return;

        string detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        string exitCode = $"0x{unchecked((uint)result.ExitCode).ToString("X8", CultureInfo.InvariantCulture)}";
        throw new InvalidOperationException(
            $"Windows could not {operation} ({exitCode})." +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}"));
    }

    private static void DeleteLegacyRunRegistration(bool throwOnFailure)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            // The scheduled task is already active and MultiExplorer remains
            // single-instance, so an undeletable legacy entry is harmless.
            AppLog.Debug(ex, nameof(DeleteLegacyRunRegistration),
                "Could not remove the previous Run-key startup entry.");
        }
    }

    private sealed record TaskSchedulerCommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
