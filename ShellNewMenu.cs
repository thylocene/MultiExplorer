using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace MultiExplorer;

/// <summary>
/// Replaces the unusable ShellNew placeholder supplied by ExplorerBrowser with
/// the templates registered for the current Windows user and machine.
/// </summary>
internal static class ShellNewMenu
{
    private const int FirstCommandId = 0x9000;
    private static readonly object CacheGate = new();
    private static IReadOnlyList<ShellNewItem>? _cachedItems;
    private static Task? _cacheWarmTask;

    internal static Task WarmCacheAsync(CancellationToken cancellationToken = default)
    {
        Task warmTask;
        lock (CacheGate)
        {
            if (_cachedItems is not null) return Task.CompletedTask;
            _cacheWarmTask ??= Task.Run(WarmCacheCore);
            warmTask = _cacheWarmTask;
        }

        return cancellationToken.CanBeCanceled
            ? warmTask.WaitAsync(cancellationToken)
            : warmTask;
    }

    internal static IReadOnlyDictionary<int, ShellNewItem> Populate(IntPtr rootMenu)
    {
        IntPtr newMenu = FindNewSubmenu(rootMenu);
        if (newMenu == IntPtr.Zero)
        {
            return new Dictionary<int, ShellNewItem>();
        }

        for (int index = NativeMethods.GetMenuItemCount(newMenu) - 1; index >= 0; index--)
            NativeMethods.DeleteMenu(newMenu, (uint)index, NativeMethods.MF_BYPOSITION);

        var commands = new Dictionary<int, ShellNewItem>();
        int commandId = FirstCommandId;
        foreach (ShellNewItem item in GetItems())
        {
            if (!NativeMethods.AppendMenu(newMenu, NativeMethods.MF_STRING,
                    (nuint)commandId, item.DisplayName))
                continue;

            commands.Add(commandId, item);
            commandId++;
        }

        return commands;
    }

    internal static bool TryExecute(int commandId,
        IReadOnlyDictionary<int, ShellNewItem> commands, string targetFolder)
        => TryExecute(commandId, commands, targetFolder, out _);

    internal static bool TryExecute(int commandId,
        IReadOnlyDictionary<int, ShellNewItem> commands, string targetFolder,
        out string? createdPath)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        createdPath = null;
        if (!commands.TryGetValue(commandId, out ShellNewItem? item)) return false;

        return TryExecute(item, targetFolder, out createdPath);
    }

    internal static bool TryExecute(ShellNewItem item, string targetFolder)
        => TryExecute(item, targetFolder, out _);

    internal static bool TryExecute(ShellNewItem item, string targetFolder,
        out string? createdPath)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        createdPath = null;

        try
        {
            switch (item.Kind)
            {
                case ShellNewKind.Folder:
                    createdPath = GetUniquePath(targetFolder, "New folder", string.Empty);
                    Directory.CreateDirectory(createdPath);
                    break;
                case ShellNewKind.Shortcut:
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "rundll32.exe",
                        Arguments = $"appwiz.cpl,NewLinkHere \"{targetFolder}\"",
                        UseShellExecute = true,
                    });
                    break;
                case ShellNewKind.EmptyFile:
                    createdPath = GetUniquePath(targetFolder,
                        $"New {item.DisplayName}", item.Extension);
                    File.WriteAllBytes(createdPath, []);
                    break;
                case ShellNewKind.DataFile:
                    createdPath = GetUniquePath(targetFolder,
                        $"New {item.DisplayName}", item.Extension);
                    File.WriteAllBytes(createdPath, item.Data ?? []);
                    break;
                case ShellNewKind.TemplateFile when item.TemplatePath is { } templatePath:
                    createdPath = GetUniquePath(targetFolder,
                        $"New {item.DisplayName}", item.Extension);
                    File.Copy(templatePath, createdPath);
                    break;
                case ShellNewKind.ApplicationCommand when item.Command is { } command:
                    createdPath = GetUniquePath(targetFolder,
                        $"New {item.DisplayName}", item.Extension);
                    StartApplicationNewDocument(command, createdPath);
                    break;
                default:
                    return false;
            }

            if (createdPath is not null
                && item.Kind is not ShellNewKind.ApplicationCommand)
                NotifyCreatedItem(createdPath, item.Kind == ShellNewKind.Folder);
            return true;
        }
        catch (Exception ex)
        {
            createdPath = null;
            AppLog.Warn(ex, nameof(ShellNewMenu),
                $"Could not create a new {item.DisplayName}.");
            return true;
        }
    }

    internal static void NotifyCreatedItem(string path, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SHChangeNotify(isDirectory ? 0x00000008u : 0x00000002u,
            0x0005u /* SHCNF_PATHW */, path, IntPtr.Zero);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags,
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr secondPath);

    /// <summary>
    /// Gets the compact, everyday document set used by Explorer's modern
    /// background menu. The source remains the local Shell registrations, so
    /// application-specific entries naturally vary between PCs.
    /// </summary>
    internal static IReadOnlyList<ShellNewItem> GetPreferredItems()
    {
        ShellNewItem[] available = GetItems().ToArray();
        var preferred = new List<ShellNewItem>();
        foreach (string displayName in PreferredDisplayNames)
        {
            ShellNewItem? item = available.FirstOrDefault(candidate =>
                string.Equals(candidate.DisplayName, displayName,
                    StringComparison.OrdinalIgnoreCase));
            if (item is not null)
            {
                preferred.Add(item);
                continue;
            }

            ShellNewItem? fallback = CreateCommonFallback(displayName);
            if (fallback is not null) preferred.Add(fallback);
        }

        return preferred;
    }

    private static readonly string[] PreferredDisplayNames =
    [
        "Folder",
        "Shortcut",
        "Bitmap image",
        "Microsoft Word Document",
        "PDF Document",
        "Microsoft PowerPoint Presentation",
        "Microsoft Publisher Document",
        "Rich Text Format",
        "SAP GUI Shortcut",
        "Text Document",
        "Microsoft Visio Drawing",
        "WinHTTrack Project",
        "Microsoft Excel Worksheet",
    ];

    private static ShellNewItem? CreateCommonFallback(string displayName) => displayName switch
    {
        "Bitmap image" => new ShellNewItem(displayName, ShellNewKind.DataFile, ".bmp",
            null, OnePixelBitmap, null),
        "PDF Document" => new ShellNewItem(displayName, ShellNewKind.DataFile, ".pdf",
            null, EmptyPdfDocument, null),
        "Text Document" => new ShellNewItem(displayName, ShellNewKind.EmptyFile, ".txt",
            null, null, null),
        "SAP GUI Shortcut" => new ShellNewItem(displayName, ShellNewKind.EmptyFile, ".sap",
            null, null, null),
        _ => null,
    };

    private static readonly byte[] OnePixelBitmap =
    [
        0x42, 0x4D, 0x3A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x36, 0x00, 0x00, 0x00, 0x28, 0x00, 0x00, 0x00, 0x01, 0x00,
        0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x18, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x13, 0x0B,
        0x00, 0x00, 0x13, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00,
    ];

    private static readonly byte[] EmptyPdfDocument = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Count 0/Kids[]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n");

    private static IntPtr FindNewSubmenu(IntPtr rootMenu)
    {
        int count = NativeMethods.GetMenuItemCount(rootMenu);
        for (int index = 0; index < count; index++)
        {
            var text = new System.Text.StringBuilder(260);
            NativeMethods.GetMenuString(rootMenu, (uint)index, text, text.Capacity,
                NativeMethods.MF_BYPOSITION);
            if (string.Equals(text.ToString().Replace("&", string.Empty), "New",
                StringComparison.OrdinalIgnoreCase))
                return NativeMethods.GetSubMenu(rootMenu, index);
        }

        return IntPtr.Zero;
    }

    private static IReadOnlyList<ShellNewItem> GetItems()
    {
        lock (CacheGate)
        {
            if (_cachedItems is { } cached)
                return cached;
        }

        IReadOnlyList<ShellNewItem> loaded = LoadItems();
        lock (CacheGate)
            return _cachedItems ??= loaded;
    }

    private static void WarmCacheCore()
    {
        try
        {
            IReadOnlyList<ShellNewItem> loaded = LoadItems();
            lock (CacheGate)
                _cachedItems ??= loaded;
        }
        catch (Exception ex) when (ex is IOException
                                   or SecurityException
                                   or UnauthorizedAccessException)
        {
            AppLog.Debug(ex, nameof(ShellNewMenu),
                "Could not pre-load the Windows New-item catalogue.");
        }
        finally
        {
            lock (CacheGate)
                _cacheWarmTask = null;
        }
    }

    private static IReadOnlyList<ShellNewItem> LoadItems()
    {
        var leadingItems = new ShellNewItem[]
        {
            new("Folder", ShellNewKind.Folder, string.Empty, null, null, null),
            new("Shortcut", ShellNewKind.Shortcut, ".lnk", null, null, null),
        };

        var items = new List<ShellNewItem>();
        string[] classNames = Registry.ClassesRoot.GetSubKeyNames();
        var progIdExtensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string extension in classNames.Where(static name =>
                     name.StartsWith(".", StringComparison.Ordinal)))
        {
            using RegistryKey? extensionKey = Registry.ClassesRoot.OpenSubKey(extension);
            if (extensionKey is null) continue;

            if (extensionKey.GetValue(null) is string progId && !string.IsNullOrWhiteSpace(progId)
                && !progIdExtensions.ContainsKey(progId))
                progIdExtensions.Add(progId, extension);

            ShellNewItem? item = CreateItem(extension, extensionKey,
                classKeyIsProgId: false);
            if (item is not null) items.Add(item);
        }

        foreach ((string progId, string extension) in progIdExtensions)
        {
            using RegistryKey? progIdKey = Registry.ClassesRoot.OpenSubKey(progId);
            if (progIdKey is null) continue;

            ShellNewItem? item = CreateItem(extension, progIdKey,
                classKeyIsProgId: true);
            if (item is not null) items.Add(item);
        }

        return leadingItems
            .Concat(items
                .GroupBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private static ShellNewItem? CreateItem(string extension, RegistryKey classKey,
        bool classKeyIsProgId)
    {
        string displayName = GetDisplayName(extension, classKey, classKeyIsProgId);
        using RegistryKey? shellNewKey = classKey.OpenSubKey("ShellNew");
        if (shellNewKey is null)
            return CreateApplicationCommandItem(extension, classKey, displayName);

        if (shellNewKey.GetValue("FileName") is string fileName)
        {
            string templatePath = ResolveTemplatePath(fileName);
            return File.Exists(templatePath)
                ? new ShellNewItem(displayName, ShellNewKind.TemplateFile,
                    extension, templatePath, null, null)
                : null;
        }

        if (shellNewKey.GetValue("Data") is byte[] data)
            return new ShellNewItem(displayName, ShellNewKind.DataFile, extension, null, data, null);

        return shellNewKey.GetValueNames().Contains("NullFile", StringComparer.OrdinalIgnoreCase)
            ? new ShellNewItem(displayName, ShellNewKind.EmptyFile, extension, null, null, null)
            : null;
    }

    private static ShellNewItem? CreateApplicationCommandItem(string extension,
        RegistryKey classKey, string displayName)
    {
        using RegistryKey? commandKey = classKey.OpenSubKey(@"shell\New\command");
        if (commandKey?.GetValue(null) is not string command
            || string.IsNullOrWhiteSpace(command))
            return null;

        return new ShellNewItem(displayName, ShellNewKind.ApplicationCommand,
            extension, null, null, command);
    }

    private static string GetDisplayName(string extension, RegistryKey classKey,
        bool classKeyIsProgId)
    {
        string? className = classKey.GetValue(null) as string;
        if (classKeyIsProgId)
            return className ?? extension.TrimStart('.').ToUpperInvariant() + " file";

        string? progId = className;
        using RegistryKey? progIdKey = string.IsNullOrWhiteSpace(progId)
            ? null
            : Registry.ClassesRoot.OpenSubKey(progId);
        return progIdKey?.GetValue(null) as string
            ?? extension.TrimStart('.').ToUpperInvariant() + " file";
    }

    private static string ResolveTemplatePath(string fileName)
    {
        string expanded = Environment.ExpandEnvironmentVariables(fileName);
        return Path.IsPathFullyQualified(expanded)
            ? expanded
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "ShellNew", expanded);
    }

    private static string GetUniquePath(string folder, string stem, string extension)
    {
        string normalizedExtension = string.IsNullOrEmpty(extension) || extension.StartsWith('.')
            ? extension
            : "." + extension;
        string candidate = Path.Combine(folder, stem + normalizedExtension);
        for (int suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
            candidate = Path.Combine(folder, $"{stem} ({suffix}){normalizedExtension}");
        return candidate;
    }

    private static void StartApplicationNewDocument(string command, string targetPath)
    {
        string expandedCommand = Environment.ExpandEnvironmentVariables(command)
            .Replace("%1", targetPath, StringComparison.OrdinalIgnoreCase)
            .Replace("%l", targetPath, StringComparison.OrdinalIgnoreCase);
        if (!expandedCommand.Contains(targetPath, StringComparison.Ordinal))
            expandedCommand += $" \"{targetPath}\"";

        (string fileName, string arguments) = SplitCommandLine(expandedCommand);
        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
        });
    }

    internal static (string FileName, string Arguments) SplitCommandLine(string commandLine)
    {
        ReadOnlySpan<char> trimmed = commandLine.AsSpan().Trim();
        if (trimmed.IsEmpty)
            throw new ArgumentException("The registered New command is empty.", nameof(commandLine));

        if (trimmed[0] == '\"')
        {
            int closingQuote = trimmed[1..].IndexOf('\"');
            if (closingQuote < 0)
                throw new ArgumentException("The registered New command has an unmatched quote.",
                    nameof(commandLine));

            return (trimmed[1..(closingQuote + 1)].ToString(),
                trimmed[(closingQuote + 2)..].Trim().ToString());
        }

        int separator = trimmed.IndexOfAny(' ', '\t');
        return separator < 0
            ? (trimmed.ToString(), string.Empty)
            : (trimmed[..separator].ToString(), trimmed[(separator + 1)..].Trim().ToString());
    }

    internal sealed record ShellNewItem(string DisplayName, ShellNewKind Kind,
        string Extension, string? TemplatePath, byte[]? Data, string? Command);

    internal enum ShellNewKind
    {
        Folder,
        Shortcut,
        EmptyFile,
        DataFile,
        TemplateFile,
        ApplicationCommand,
    }
}
