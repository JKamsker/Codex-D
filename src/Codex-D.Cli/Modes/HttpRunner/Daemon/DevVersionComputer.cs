using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodexD.HttpRunner.Daemon;

internal static class DevVersionComputer
{
    public static async Task<string?> TryComputeAsync(string workingDirectory, CancellationToken ct)
    {
        var repoRoot = await TryGitAsync(workingDirectory, ct, "rev-parse", "--show-toplevel");
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return null;
        }

        repoRoot = repoRoot.Trim();

        var head = await TryGitAsync(repoRoot, ct, "rev-parse", "HEAD");
        if (string.IsNullOrWhiteSpace(head))
        {
            return null;
        }

        head = head.Trim();

        var workingDiffHash = await HashGitStdoutAsync(repoRoot, ct, "diff", "--no-ext-diff", "--binary");
        var stagedDiffHash = await HashGitStdoutAsync(repoRoot, ct, "diff", "--cached", "--no-ext-diff", "--binary");
        var untrackedHash = await HashUntrackedAsync(repoRoot, ct);

        var uncommitted = Sha256Hex($"{workingDiffHash}\n{stagedDiffHash}\n{untrackedHash}");
        var final = Sha256Hex($"{head}\0{uncommitted}");
        return final[..16];
    }

    private static async Task<string?> TryGitAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct); // ignore

            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
            {
                return null;
            }

            return await stdoutTask;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> HashGitStdoutAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return Sha256Hex(string.Empty);
            }

            await using var stdout = p.StandardOutput.BaseStream;
            _ = p.StandardError.ReadToEndAsync(ct); // ignore

            var hash = await Sha256StreamHexAsync(stdout, ct);
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? hash : Sha256Hex(string.Empty);
        }
        catch
        {
            return Sha256Hex(string.Empty);
        }
    }

    private static async Task<string> HashUntrackedAsync(string repoRoot, CancellationToken ct)
    {
        var files = await TryGitBytesAsync(repoRoot, ct, "ls-files", "--others", "--exclude-standard", "-z");
        if (files is null || files.Length == 0)
        {
            return Sha256Hex(string.Empty);
        }

        using var sha = SHA256.Create();

        var start = 0;
        for (var i = 0; i <= files.Length; i++)
        {
            if (i != files.Length && files[i] != 0)
            {
                continue;
            }

            if (i > start)
            {
                var rel = Encoding.UTF8.GetString(files, start, i - start);
                var full = Path.Combine(repoRoot, rel);

                UpdateUtf8(sha, rel);
                UpdateByte(sha, 0);

                try
                {
                    await using var fs = File.Open(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    await UpdateStreamAsync(sha, fs, ct);
                }
                catch
                {
                    // ignore unreadable/missing files; still include the path
                }

                UpdateByte(sha, (byte)'\n');
            }

            start = i + 1;
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return ToHex(sha.Hash!);
    }

    private static async Task<byte[]?> TryGitBytesAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            await using var stdout = p.StandardOutput.BaseStream;
            _ = p.StandardError.ReadToEndAsync(ct); // ignore

            using var ms = new MemoryStream();
            await stdout.CopyToAsync(ms, ct);
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? ms.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> Sha256StreamHexAsync(Stream stream, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await UpdateStreamAsync(sha, stream, ct);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return ToHex(sha.Hash!);
    }

    private static async Task UpdateStreamAsync(HashAlgorithm sha, Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
            if (read <= 0)
            {
                break;
            }

            sha.TransformBlock(buffer, 0, read, null, 0);
        }
    }

    private static void UpdateUtf8(HashAlgorithm sha, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }

    private static void UpdateByte(HashAlgorithm sha, byte b) =>
        sha.TransformBlock(new[] { b }, 0, 1, null, 0);

    private static string Sha256Hex(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = SHA256.HashData(bytes);
        return ToHex(hash);
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}

