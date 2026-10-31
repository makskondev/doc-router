using DocRouter.Core;
using Microsoft.Extensions.Options;
using SMBLibrary;
using SMBLibrary.Client;
using SmbAttrs = SMBLibrary.FileAttributes;

namespace DocRouter.Infrastructure;

/// <summary>\\host\share\dir\file → компоненты.</summary>
internal sealed record SmbPath(string Host, string Share, string Relative)
{
    public static SmbPath Parse(string unc)
    {
        var t = unc.Replace('/', '\\');
        if (!t.StartsWith(@"\\")) throw new ArgumentException($"Ожидается UNC-путь (\\\\host\\share\\...): {unc}");
        var parts = t[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException($"Некорректный UNC-путь: {unc}");
        return new SmbPath(parts[0], parts[1], string.Join('\\', parts.Skip(2)));
    }

    public string Full => $@"\\{Host}\{Share}" + (Relative.Length > 0 ? "\\" + Relative : "");
    public SmbPath Combine(string name) => this with { Relative = Relative.Length == 0 ? name : Relative + "\\" + name };
}

/// <summary>Соединение на операцию: SMBLibrary-клиент не потокобезопасен, а опрос редкий — переподключение дешевле отладки обрывов.</summary>
internal sealed class SmbSession : IDisposable
{
    private readonly SMB2Client _client = new();
    public ISMBFileStore Store { get; }
    public uint MaxRead => _client.MaxReadSize;
    public uint MaxWrite => _client.MaxWriteSize;

    public SmbSession(SmbPath path, SmbOptions o)
    {
        if (!_client.Connect(path.Host, SMBTransportType.DirectTCPTransport))
            throw new IOException($"SMB: не удалось подключиться к {path.Host}");

        var st = _client.Login(o.Domain, o.User, o.Password);
        if (st != NTStatus.STATUS_SUCCESS) throw new IOException($"SMB: ошибка входа на {path.Host}: {st}");

        Store = _client.TreeConnect(path.Share, out st);
        if (st != NTStatus.STATUS_SUCCESS) throw new IOException($"SMB: не удалось открыть шару {path.Host}\\{path.Share}: {st}");
    }

    public void Dispose()
    {
        try { Store.Disconnect(); _client.Logoff(); } catch { /* ignore */ }
        try { _client.Disconnect(); } catch { /* ignore */ }
    }
}

public sealed class SmbFileStore(IOptions<SmbOptions> options) : IFileStore
{
    private readonly SmbOptions _o = options.Value;

    private const CreateOptions FileOpts = CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT;

    public Task<IReadOnlyList<FileEntry>> ListAsync(string dir, CancellationToken ct) => Task.Run(() =>
    {
        var p = SmbPath.Parse(dir);
        using var s = new SmbSession(p, _o);

        var st = s.Store.CreateFile(out var h, out _, p.Relative, AccessMask.GENERIC_READ, SmbAttrs.Directory,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
        Check(st, $"открытие каталога {dir}");

        try
        {
            st = s.Store.QueryDirectory(out List<QueryDirectoryFileInformation> list, h, "*", FileInformationClass.FileDirectoryInformation);
            if (st != NTStatus.STATUS_SUCCESS && st != NTStatus.STATUS_NO_MORE_FILES) Check(st, $"чтение каталога {dir}");

            var result = new List<FileEntry>();
            foreach (var fi in list.OfType<FileDirectoryInformation>())
            {
                if (fi.FileName is "." or ".." || (fi.FileAttributes & SmbAttrs.Directory) != 0) continue;
                result.Add(new FileEntry(fi.FileName, Paths.Combine(dir, fi.FileName), fi.EndOfFile, fi.LastWriteTime.ToUniversalTime()));
            }
            return (IReadOnlyList<FileEntry>)result;
        }
        finally { s.Store.CloseFile(h); }
    }, ct);

    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        var p = SmbPath.Parse(path);
        using var s = new SmbSession(p, _o);
        var st = s.Store.CreateFile(out var h, out _, p.Relative, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            SmbAttrs.Normal, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, FileOpts, null);
        if (st == NTStatus.STATUS_SUCCESS) { s.Store.CloseFile(h); return true; }
        if (st is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND) return false;
        Check(st, $"проверка файла {path}");
        return false;
    }, ct);

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        var p = SmbPath.Parse(path);
        using var s = new SmbSession(p, _o);
        using var ms = new MemoryStream();
        ReadTo(s, p, ms, ct);
        return ms.ToArray();
    }, ct);

    public Task<string> MoveUniqueAsync(string sourcePath, string targetDir, string targetFileName, CancellationToken ct) => Task.Run(() =>
    {
        var src = SmbPath.Parse(sourcePath);
        var dstDir = SmbPath.Parse(targetDir);
        using var s1 = new SmbSession(src, _o);
        using var s2 = new SmbSession(dstDir, _o);

        var name = targetFileName;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var dst = dstDir.Combine(name);

            // FILE_CREATE: упадёт, если файл существует → гонки и перезапись исключены
            var st = s2.Store.CreateFile(out var hDst, out _, dst.Relative, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
                SmbAttrs.Normal, ShareAccess.None, CreateDisposition.FILE_CREATE, FileOpts, null);
            if (st == NTStatus.STATUS_OBJECT_NAME_COLLISION) { name = WithSuffix(targetFileName, attempt); continue; }
            Check(st, $"создание {dst.Full}");

            long total;
            try
            {
                total = CopyChunks(s1, src, s2, hDst, ct);
            }
            catch
            {
                s2.Store.CloseFile(hDst);
                TryDelete(s2, dst);   // не оставляем недописанный файл в общем хранилище
                throw;
            }
            s2.Store.CloseFile(hDst);

            // Контроль размера, затем удаляем источник
            var size = SizeOf(s2, dst);
            if (size != total) { TryDelete(s2, dst); throw new IOException($"Размер после копирования не совпал: {size} != {total}"); }

            DeleteInSession(s1, src);
            return dst.Full;
        }
        throw new IOException($"Не удалось подобрать уникальное имя в {targetDir} для {targetFileName}");
    }, ct);

    public Task DeleteAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        var p = SmbPath.Parse(path);
        using var s = new SmbSession(p, _o);
        DeleteInSession(s, p);
    }, ct);

    // ---------- helpers ----------

    // name.pdf → name_20261031_153045.pdf; при повторной коллизии добавляется счётчик
    private static string WithSuffix(string fileName, int attempt)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return attempt == 0 ? $"{stem}_{ts}{ext}" : $"{stem}_{ts}_{attempt}{ext}";
    }

    private static void ReadTo(SmbSession s, SmbPath p, Stream target, CancellationToken ct)
    {
        var st = s.Store.CreateFile(out var h, out _, p.Relative, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            SmbAttrs.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, FileOpts, null);
        Check(st, $"открытие {p.Full}");
        try
        {
            long offset = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                st = s.Store.ReadFile(out var data, h, offset, (int)s.MaxRead);
                if (st == NTStatus.STATUS_END_OF_FILE || (st == NTStatus.STATUS_SUCCESS && data.Length == 0)) break;
                Check(st, $"чтение {p.Full}");
                target.Write(data, 0, data.Length);
                offset += data.Length;
            }
        }
        finally { s.Store.CloseFile(h); }
    }

    private static long CopyChunks(SmbSession from, SmbPath src, SmbSession to, object hDst, CancellationToken ct)
    {
        var st = from.Store.CreateFile(out var hSrc, out _, src.Relative, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            SmbAttrs.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, FileOpts, null);
        Check(st, $"открытие {src.Full}");
        try
        {
            long offset = 0;
            var maxWrite = (int)to.MaxWrite;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                st = from.Store.ReadFile(out var data, hSrc, offset, (int)from.MaxRead);
                if (st == NTStatus.STATUS_END_OF_FILE || (st == NTStatus.STATUS_SUCCESS && data.Length == 0)) break;
                Check(st, $"чтение {src.Full}");

                for (var pos = 0; pos < data.Length;)
                {
                    var chunk = data.AsSpan(pos, Math.Min(maxWrite, data.Length - pos)).ToArray();
                    st = to.Store.WriteFile(out var written, hDst, offset + pos, chunk);
                    Check(st, "запись в целевой файл");
                    pos += written;
                }
                offset += data.Length;
            }
            return offset;
        }
        finally { from.Store.CloseFile(hSrc); }
    }

    private static long SizeOf(SmbSession s, SmbPath p)
    {
        var st = s.Store.CreateFile(out var h, out _, p.Relative, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            SmbAttrs.Normal, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, FileOpts, null);
        Check(st, $"открытие {p.Full}");
        try
        {
            st = s.Store.GetFileInformation(out var info, h, FileInformationClass.FileStandardInformation);
            Check(st, $"размер {p.Full}");
            return ((FileStandardInformation)info).EndOfFile;
        }
        finally { s.Store.CloseFile(h); }
    }

    private static void DeleteInSession(SmbSession s, SmbPath p)
    {
        var st = s.Store.CreateFile(out var h, out _, p.Relative, AccessMask.DELETE | AccessMask.SYNCHRONIZE,
            SmbAttrs.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
            CreateDisposition.FILE_OPEN, FileOpts, null);
        Check(st, $"открытие для удаления {p.Full}");
        try
        {
            st = s.Store.SetFileInformation(h, new FileDispositionInformation { DeletePending = true });
            Check(st, $"удаление {p.Full}");
        }
        finally { s.Store.CloseFile(h); }
    }

    private static void TryDelete(SmbSession s, SmbPath p)
    {
        try { DeleteInSession(s, p); } catch { /* best effort */ }
    }

    private static void Check(NTStatus st, string what)
    {
        if (st != NTStatus.STATUS_SUCCESS) throw new IOException($"SMB: {what}: {st}");
    }
}
