using System.Security.Cryptography;
using System.Text;

namespace SoundPreviewer.Core.Scanning;

// A Logic patch is a directory, not a single plist. Hash every packaged member
// so changing a strip or bundled asset invalidates its indexed preview identity.
public static class LogicPatchSource
{
    public sealed record Snapshot(string Sha256, string Md5, long Bytes, DateTime ModifiedUtc);

    public static Snapshot Read(string path, CancellationToken token = default)
    {
        path = Path.GetFullPath(path);
        if (!path.EndsWith(".patch", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(path))
            throw new InvalidDataException("Logic Patch 必须是 .patch 包目录，不支持平面文件。");
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(path);
        int members = 0;
        while (pending.TryPop(out string? directory))
        {
            token.ThrowIfCancellationRequested();
            RejectLink(directory);
            foreach (string member in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++members > 4096) throw new InvalidDataException("Logic Patch 超过 4096 个包成员限制。");
                RejectLink(member);
                if (Directory.Exists(member)) pending.Push(member);
                else files.Add(Path.GetRelativePath(path, member).Replace('\\', '/'));
            }
        }
        if (!files.Contains("data.plist") || !files.Contains("#Root.cst"))
            throw new InvalidDataException("Logic Patch 缺少 data.plist 或 #Root.cst。");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        long total = 0;
        DateTime modified = Directory.GetLastWriteTimeUtc(path);
        foreach (string name in files.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            string member = Path.Combine(path, name);
            var info = new FileInfo(member);
            long length = info.Length;
            DateTime stamp = info.LastWriteTimeUtc;
            total = checked(total + length);
            if (total > 256L * 1024 * 1024) throw new InvalidDataException("Logic Patch 超过 256 MiB 读取限制。");
            string hash = Models.Fingerprints.HashFile(member, token);
            info.Refresh();
            if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != stamp)
                throw new IOException("扫描期间 Logic Patch 发生变化，请重新扫描。");
            byte[] identity = Encoding.UTF8.GetBytes(name + "\0" + hash + "\0");
            sha.AppendData(identity);
            md5.AppendData(identity);
            if (stamp > modified) modified = stamp;
        }
        return new(Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(), total, modified);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Logic Patch 不支持链接成员：" + path);
    }
}
