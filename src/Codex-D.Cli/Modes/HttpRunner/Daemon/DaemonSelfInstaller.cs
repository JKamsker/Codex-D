using System.Diagnostics;
using System.Reflection;

namespace CodexD.HttpRunner.Daemon;

public static class DaemonSelfInstaller
{
    public static async Task<bool> InstallSelfAsync(string daemonBinDir, string desiredVersion, bool force, CancellationToken ct)
    {
        Directory.CreateDirectory(daemonBinDir);

        var markerPath = Path.Combine(daemonBinDir, ".version");

        var currentMarker = TryReadMarker(markerPath);
        if (!force && string.Equals(currentMarker, desiredVersion, StringComparison.Ordinal))
        {
            return false;
        }

        var sourceDir = AppContext.BaseDirectory;
        try
        {
            CopyDirectoryContents(sourceDir, daemonBinDir);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Failed to upgrade daemon binaries. Stop the running daemon (or use --force) and try again.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Failed to upgrade daemon binaries due to permissions. Stop the running daemon (or use --force) and try again.", ex);
        }

        await File.WriteAllTextAsync(markerPath, desiredVersion, ct);
        return true;
    }

    public static ProcessStartInfo CreateInstalledStartInfo(string daemonBinDir, IReadOnlyList<string> args)
    {
        var processPath = Environment.ProcessPath;
        var entryLocation = Assembly.GetEntryAssembly()?.Location;

        if (!string.IsNullOrWhiteSpace(processPath) &&
            !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var exeName = Path.GetFileName(processPath);
            var installedExe = Path.Combine(daemonBinDir, exeName);
            if (File.Exists(installedExe))
            {
                var psi = new ProcessStartInfo(installedExe)
                {
                    WorkingDirectory = daemonBinDir
                };
                foreach (var a in args) psi.ArgumentList.Add(a);
                return psi;
            }
        }

        if (string.IsNullOrWhiteSpace(entryLocation))
        {
            throw new InvalidOperationException("Unable to locate entry assembly for daemon install.");
        }

        var entryFileName = Path.GetFileName(entryLocation);
        var installedEntry = Path.Combine(daemonBinDir, entryFileName);

        var installedExeCandidate = Path.Combine(
            daemonBinDir,
            Path.GetFileNameWithoutExtension(entryFileName) + ".exe");

        if (File.Exists(installedExeCandidate))
        {
            var psiExe = new ProcessStartInfo(installedExeCandidate)
            {
                WorkingDirectory = daemonBinDir
            };
            foreach (var a in args) psiExe.ArgumentList.Add(a);
            return psiExe;
        }

        if (!File.Exists(installedEntry))
        {
            throw new FileNotFoundException("Installed entry assembly was not found.", installedEntry);
        }

        var psiDotnet = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = daemonBinDir
        };
        psiDotnet.ArgumentList.Add(installedEntry);
        foreach (var a in args) psiDotnet.ArgumentList.Add(a);
        return psiDotnet;
    }

    private static string? TryReadMarker(string markerPath)
    {
        try
        {
            if (!File.Exists(markerPath))
            {
                return null;
            }

            var text = File.ReadAllText(markerPath);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
