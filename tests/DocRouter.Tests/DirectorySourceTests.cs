using DocRouter.Core;
using DocRouter.Engine;
using DocRouter.Steps;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocRouter.Tests;

public class DirectorySourceTests
{
    private const string Signed = @"\\srv\signed";

    private sealed class ThrowingStore : IFileStore
    {
        public int Lists;
        public Task<IReadOnlyList<FileEntry>> ListAsync(string dir, CancellationToken ct) { Lists++; throw new IOException("share down"); }
        public Task<bool> ExistsAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> MoveUniqueAsync(string s, string d, string n, CancellationToken ct) => throw new NotSupportedException();
        public Task WriteNewAsync(byte[] c, string p, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string p, CancellationToken ct) => throw new NotSupportedException();
    }

    private static (DirectorySource src, CapturingNotifier n) Create(IFileStore fs, Action<DocumentTypesBuilder> define)
    {
        var b = new DocumentTypesBuilder();
        define(b);
        var n = new CapturingNotifier();
        return (new DirectorySource(fs, n, new TestTime(DateTime.UtcNow), b.Build(), NullLogger<DirectorySource>.Instance), n);
    }

    private static void TwoTypesOnSharedFolder(DocumentTypesBuilder b)
    {
        b.Type("DEPO_AGR").FromDirectory(Signed, mask: "DEPO_AGR_*", withSignature: true).NotifyTo("d@x.ru").Step<FindFile>();
        b.Type("BROK_X").FromDirectory(Signed.ToUpperInvariant() + "\\", mask: "BROK_X_*").Step<FindFile>();   // та же папка, другая запись
    }

    [Fact]
    public async Task Shared_folder_files_are_split_between_types_by_masks()
    {
        var fs = new InMemoryFileStore();
        fs.Add(Paths.Combine(Signed, "DEPO_AGR_1.pdf"), [1]);
        fs.Add(Paths.Combine(Signed, "DEPO_AGR_1.pdf.sig"), [1]);
        fs.Add(Paths.Combine(Signed, "BROK_X_7.pdf"), [1]);
        fs.Add(Paths.Combine(Signed, "other.pdf"), [1]);            // ничей файл — не берём

        var (src, n) = Create(fs, TwoTypesOnSharedFolder);
        var items = (await src.DiscoverAsync(default)).OrderBy(i => i.DocType).ToList();

        Assert.Equal(new[] { "BROK_X", "DEPO_AGR" }, items.Select(i => i.DocType));
        Assert.Equal(new[] { "BROK_X_7.pdf", "DEPO_AGR_1.pdf" }, items.Select(i => i.FileName));
        Assert.Equal(new[] { "d@x.ru" }, items[1].Recipients);
        Assert.Empty(n.System);
    }

    [Fact]
    public async Task File_claimed_by_two_types_is_skipped_with_system_email()
    {
        var fs = new InMemoryFileStore();
        fs.Add(Paths.Combine(Signed, "SHARED_1.pdf"), [1]);
        fs.Add(Paths.Combine(Signed, "SHARED_1.pdf.sig"), [1]);
        fs.Add(Paths.Combine(Signed, "ONLY_A.pdf"), [1]);

        var (src, n) = Create(fs, b =>
        {
            b.Type("A").FromDirectory(Signed, mask: "*_1.pdf").Step<FindFile>();           // маски перекрываются
            b.Type("B").FromDirectory(Signed, mask: "SHARED_*").Step<FindFile>();
        });

        Assert.Empty(await src.DiscoverAsync(default));
        var mail = Assert.Single(n.System);
        Assert.Contains("SHARED_1.pdf", mail);
    }

    [Fact]
    public async Task Shared_folder_is_listed_once_per_poll_and_failure_reported_once()
    {
        var fs = new ThrowingStore();
        var (src, n) = Create(fs, TwoTypesOnSharedFolder);

        Assert.Empty(await src.DiscoverAsync(default));
        Assert.Equal(1, fs.Lists);
        Assert.Single(n.System);
    }

    [Fact]
    public async Task External_id_for_folder_source_is_file_name()
    {
        var fs = new InMemoryFileStore();
        fs.Add(Paths.Combine(Signed, "DEPO_AGR_5.pdf"), [1]);
        fs.Add(Paths.Combine(Signed, "DEPO_AGR_5.pdf.sig"), [1]);

        var (src, _) = Create(fs, b => b.Type("DEPO_AGR").FromDirectory(Signed, mask: "DEPO_AGR_*", withSignature: true).Step<FindFile>());
        var i = Assert.Single(await src.DiscoverAsync(default));
        Assert.Equal("DEPO_AGR_5.pdf", i.ExternalId);
        Assert.Equal(SourceKind.Directory, i.Source);
    }
}
