using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MultiExplorer;

internal enum ShortcutType
{
    ShellLink,
    HardLink,
    SymbolicLink,
}

internal enum ShortcutLocation
{
    CurrentFolder,
    ParentFolder,
    Desktop,
    OtherPane,
}

internal sealed record ShortcutCreationRequest(
    string TargetPath,
    string Name,
    ShortcutType Type,
    string DestinationFolder);

internal sealed class ShortcutCreationService
{
    internal string Create(ShortcutCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationFolder);

        string target = Path.GetFullPath(request.TargetPath.Trim());
        bool isFile = File.Exists(target);
        bool isDirectory = Directory.Exists(target);
        if (!isFile && !isDirectory)
            throw new FileNotFoundException("The shortcut target no longer exists.", target);
        if (request.Type == ShortcutType.HardLink && !isFile)
            throw new InvalidOperationException("A hard link can only target a file.");

        string destination = Path.GetFullPath(request.DestinationFolder);
        if (!Directory.Exists(destination))
            throw new DirectoryNotFoundException(
                $"The destination folder does not exist: {destination}");

        string name = ValidateName(request.Name, request.Type);
        if (request.Type == ShortcutType.ShellLink)
            return ShortcutWizard.CreateShortcut(destination, name, target);

        string path = GetUniqueLinkPath(destination, name, isDirectory);
        switch (request.Type)
        {
            case ShortcutType.HardLink:
                if (!CreateHardLink(path, target, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    int hResult = Marshal.GetHRForLastWin32Error();
                    throw new IOException(
                        $"Could not create the hard link: {new Win32Exception(error).Message}",
                        hResult);
                }
                break;
            case ShortcutType.SymbolicLink when isDirectory:
                Directory.CreateSymbolicLink(path, target);
                break;
            case ShortcutType.SymbolicLink:
                File.CreateSymbolicLink(path, target);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        return path;
    }

    internal static string SuggestName(string targetPath, ShortcutType type)
    {
        ArgumentNullException.ThrowIfNull(targetPath);
        string trimmed = Path.TrimEndingDirectorySeparator(targetPath);
        string fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "New item";

        if (type == ShortcutType.ShellLink)
            return $"{fileName} - Shortcut";

        bool isDirectory = Directory.Exists(targetPath);
        string stem = isDirectory ? fileName : Path.GetFileNameWithoutExtension(fileName);
        string extension = isDirectory ? string.Empty : Path.GetExtension(fileName);
        return type switch
        {
            ShortcutType.HardLink => $"{stem} - Hard link{extension}",
            ShortcutType.SymbolicLink => $"{stem} - Symlink{extension}",
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    internal static string? ResolveDestination(ShortcutLocation location,
        string currentFolder, string otherPaneFolder, string desktopFolder)
    {
        ArgumentNullException.ThrowIfNull(currentFolder);
        ArgumentNullException.ThrowIfNull(otherPaneFolder);
        ArgumentNullException.ThrowIfNull(desktopFolder);
        return location switch
        {
            ShortcutLocation.CurrentFolder => currentFolder,
            ShortcutLocation.ParentFolder =>
                Directory.GetParent(currentFolder)?.FullName,
            ShortcutLocation.Desktop => desktopFolder,
            ShortcutLocation.OtherPane => otherPaneFolder,
            _ => throw new ArgumentOutOfRangeException(nameof(location)),
        };
    }

    private static string ValidateName(string name, ShortcutType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        string trimmed = name.Trim();
        if (type == ShortcutType.ShellLink
            && trimmed.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        if (string.IsNullOrWhiteSpace(trimmed)
            || trimmed is "." or ".."
            || trimmed.EndsWith('.')
            || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Enter a valid shortcut name.", nameof(name));
        return trimmed;
    }

    internal static bool IsValidName(string? name, ShortcutType type)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        try
        {
            ValidateName(name, type);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string GetUniqueLinkPath(string folder, string name,
        bool isDirectory)
    {
        string extension = isDirectory ? string.Empty : Path.GetExtension(name);
        string stem = extension.Length == 0 ? name : name[..^extension.Length];
        string candidate = Path.Combine(folder, name);
        for (int suffix = 2;
             File.Exists(candidate) || Directory.Exists(candidate);
             suffix++)
            candidate = Path.Combine(folder, $"{stem} ({suffix}){extension}");
        return candidate;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        EntryPoint = "CreateHardLinkW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string linkPath,
        string existingFilePath, IntPtr securityAttributes);
}
