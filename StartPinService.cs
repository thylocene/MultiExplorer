using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Windows.UI.StartScreen;

namespace MultiExplorer;

/// <summary>
/// Pins folders through Windows' native shortcut route and uses secondary Start
/// tiles for files when MultiExplorer is running with its sparse package identity.
/// </summary>
internal static class StartPinService
{
    private const string CapabilityProbeTileId = "MultiExplorerCapabilityProbe";
    private const string TileIdPrefix = "MultiExplorer-";
    private static readonly Uri Square150Logo = new(
        "ms-appx:///Assets/Square150x150Logo.png");
    private static readonly Uri Square44Logo = new(
        "ms-appx:///Assets/Square44x44Logo.png");
    private static readonly TimeSpan PinStateCacheDuration = TimeSpan.FromMinutes(1);
    private static readonly Lazy<bool> Availability = new(ProbeAvailability);
    private static readonly ConcurrentDictionary<string, CachedPinState> PinStateCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsAvailable() => Availability.Value;

    private static bool ProbeAvailability()
    {
        try
        {
            _ = SecondaryTile.Exists(CapabilityProbeTileId);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(IsAvailable),
                "Start tiles require MultiExplorer's package identity.");
            return false;
        }
    }

    internal static bool IsPinned(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (PinStateCache.TryGetValue(fullPath, out CachedPinState cached)
                && DateTime.UtcNow < cached.ExpiresUtc)
                return cached.IsPinned;

            bool isPinned;
            if (Directory.Exists(fullPath)
                && NativeFolderStartPinService.IsPinned(fullPath))
            {
                isPinned = true;
            }
            else
            {
                string tileId = GetTileId(fullPath);
                isPinned = StartMenuPinningInterop.TryGetPinnedState(tileId,
                    out bool tileIsPinned) && tileIsPinned;
            }

            PinStateCache[fullPath] = new CachedPinState(
                isPinned, DateTime.UtcNow + PinStateCacheDuration);
            return isPinned;
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(IsPinned),
                $"Could not read the Start pin state for '{path}'.");
            return false;
        }
    }

    internal static string GetCommandLabel(bool isPinned) =>
        isPinned ? "Unpin from Start" : "Pin to Start";

    internal static async Task RefreshPinnedTileLogosAsync()
    {
        if (!IsAvailable()) return;

        try
        {
            IReadOnlyList<SecondaryTile> tiles =
                await SecondaryTile.FindAllAsync();
            foreach (SecondaryTile tile in tiles.Where(static tile =>
                         tile.TileId.StartsWith(TileIdPrefix,
                             StringComparison.Ordinal)))
            {
                string? path = DecodeActivationPath(
                    tile.Arguments.Split(' ',
                        StringSplitOptions.RemoveEmptyEntries));
                if (path is not null && Directory.Exists(path))
                {
                    bool nativePinned =
                        NativeFolderStartPinService.IsPinned(path)
                        || await NativeFolderStartPinService
                            .TrySetPinnedAsync(path, true);
                    if (nativePinned)
                    {
                        await Task.Run(() =>
                            StartMenuPinningInterop.TryUnpin(tile.TileId));
                    }
                    continue;
                }

                StartTileIcons? icons = path is null
                    ? null
                    : await StartTileIconService.PrepareAsync(path,
                        tile.TileId);
                ApplyLogos(tile, icons);
                await tile.UpdateAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, nameof(RefreshPinnedTileLogosAsync),
                "Could not refresh an existing Start tile icon.");
        }
    }

    internal static async Task<bool> TrySetPinnedAsync(
        string path, bool pin, IntPtr ownerWindow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (ownerWindow == IntPtr.Zero)
            throw new ArgumentException(
                "A visible owner window is required.", nameof(ownerWindow));

        string fullPath = Path.GetFullPath(path);
        PinStateCache.TryRemove(fullPath, out _);
        try
        {
            if (Directory.Exists(fullPath))
            {
                bool nativePinned = NativeFolderStartPinService.IsPinned(
                    fullPath);
                if (pin || nativePinned)
                {
                    return await NativeFolderStartPinService.TrySetPinnedAsync(
                        fullPath, pin);
                }
            }

            if (!pin)
            {
                string tileId = GetTileId(fullPath);
                return await Task.Run(
                    () => StartMenuPinningInterop.TryUnpin(tileId));
            }

            SecondaryTile tile = await CreateTileAsync(fullPath);

            // File Explorer uses this in-process Start-menu path. It creates the
            // tile-store record, pins it, and verifies that it really appeared.
            bool pinRequestSucceeded = StartMenuPinningInterop.TryPin(tile);
            if (pinRequestSucceeded)
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    if (StartMenuPinningInterop.TryGetPinnedState(
                            tile.TileId, out bool isPinned) && isPinned)
                    {
                        return true;
                    }
                    await Task.Delay(50);
                }
            }

            // Do not fall back to RequestCreateAsync here. That API displays a
            // second confirmation flyout after the user has already selected
            // Pin to Start in MultiExplorer.
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, nameof(TrySetPinnedAsync),
                $"Could not {(pin ? "pin" : "unpin")} '{fullPath}' "
                + "in Start.");
            return false;
        }
    }

    internal static string GetTileId(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return TileIdPrefix + Convert.ToHexString(digest.AsSpan(0, 24));
    }

    internal static string GetActivationArguments(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string encodedPath = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
        return $"--open-path-base64 {encodedPath}";
    }

    private readonly record struct CachedPinState(bool IsPinned, DateTime ExpiresUtc);

    internal static string? DecodeActivationPath(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string[] values = arguments.ToArray();
        int argumentIndex = Array.FindIndex(values, static value =>
            value.Equals("--open-path-base64", StringComparison.OrdinalIgnoreCase));
        if (argumentIndex < 0 || argumentIndex + 1 >= values.Length)
            return null;

        try
        {
            string path = Encoding.UTF8.GetString(
                Convert.FromBase64String(values[argumentIndex + 1]));
            string fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) || Directory.Exists(fullPath)
                ? fullPath
                : null;
        }
        catch (Exception ex) when (
            ex is FormatException or
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            AppLog.Debug(ex, nameof(DecodeActivationPath),
                "The Start tile supplied an invalid path.");
            return null;
        }
    }

    private static string GetDisplayName(string path)
    {
        string trimmedPath = path.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string displayName = Path.GetFileName(trimmedPath);
        if (!string.IsNullOrWhiteSpace(displayName)) return displayName;

        string? root = Path.GetPathRoot(path);
        return string.IsNullOrWhiteSpace(root) ? path : root;
    }

    private static async Task<SecondaryTile> CreateTileAsync(string fullPath)
    {
        string tileId = GetTileId(fullPath);
        StartTileIcons? icons = await StartTileIconService.PrepareAsync(
            fullPath, tileId);
        var tile = new SecondaryTile(
            tileId,
            GetDisplayName(fullPath),
            GetActivationArguments(fullPath),
            icons?.Square150 ?? Square150Logo,
            TileSize.Square150x150);
        ApplyLogos(tile, icons);
        return tile;
    }

    private static void ApplyLogos(SecondaryTile tile, StartTileIcons? icons)
    {
        Uri square44 = icons?.Square44 ?? Square44Logo;
        Uri square150 = icons?.Square150 ?? Square150Logo;
        tile.VisualElements.Square30x30Logo = square44;
        tile.VisualElements.Square44x44Logo = square44;
        tile.VisualElements.Square70x70Logo = square150;
        tile.VisualElements.Square71x71Logo = square150;
        tile.VisualElements.Square150x150Logo = square150;
        tile.VisualElements.ShowNameOnSquare150x150Logo = true;
        tile.VisualElements.BackgroundColor =
            Windows.UI.Color.FromArgb(0, 0, 0, 0);
    }
}
