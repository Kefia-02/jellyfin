using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.MediaEncoding.Encoder;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.Encoder;

/// <summary>
/// Real EBML attachment-copy tests; no FFmpeg invocation is needed for the fast path.
/// </summary>
public class MatroskaAttachmentReaderTests
{
    private static readonly byte[] _jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0xFF, 0xD9 };

    [Fact]
    public async Task NamedCover_CopiesOriginalBytes()
    {
        var input = NewTempPath();
        var output = NewTempPath();
        try
        {
            CreateMatroska(input, largeCluster: false, mime: "image/jpeg");
            Assert.True(await MatroskaAttachmentReader.TryExtractAsync(input, "COVER.JPG", output, TestContext.Current.CancellationToken));
            Assert.Equal(_jpeg, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task AttachmentAfterLargeCluster_DoesNotReadEntireVideo()
    {
        var input = NewTempPath();
        var output = NewTempPath();
        try
        {
            CreateMatroska(input, largeCluster: true, mime: "image/jpeg");
            Assert.True(await MatroskaAttachmentReader.TryExtractAsync(input, "cover.jpg", output, TestContext.Current.CancellationToken));
            Assert.Equal(_jpeg, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
            Assert.True(new FileInfo(input).Length > 128 * 1024 * 1024);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData("poster.jpg", "image/jpeg")]
    [InlineData("cover.jpg", "application/octet-stream")]
    public async Task MissingOrNonImageAttachment_FallsBackWithoutWritingFile(string requestedName, string mimeType)
    {
        var input = NewTempPath();
        var output = NewTempPath();
        try
        {
            CreateMatroska(input, largeCluster: false, mime: mimeType);
            Assert.False(await MatroskaAttachmentReader.TryExtractAsync(input, requestedName, output, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(output));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mkv");

    private static void CreateMatroska(string path, bool largeCluster, string mime)
    {
        using var stream = File.Create(path);
        WriteElement(stream, 0x1A45DFA3, Array.Empty<byte>()); // EBML Header
        WriteId(stream, 0x18538067); // Segment
        stream.WriteByte(0xFF); // Unknown-size segment is valid for streaming MKV.

        if (largeCluster)
        {
            WriteId(stream, 0x1F43B675); // Cluster
            const long clusterBytes = 128L * 1024 * 1024;
            WriteSize(stream, clusterBytes);
            stream.Position += clusterBytes; // Sparse file: do not allocate or read the large cluster.
        }

        using var entry = new MemoryStream();
        WriteElement(entry, 0x466E, Encoding.UTF8.GetBytes("cover.jpg"));
        WriteElement(entry, 0x4660, Encoding.ASCII.GetBytes(mime));
        WriteElement(entry, 0x465C, _jpeg);

        using var attached = new MemoryStream();
        WriteElement(attached, 0x61A7, entry.ToArray());
        WriteElement(stream, 0x1941A469, attached.ToArray());
        stream.SetLength(stream.Position);
    }

    private static void WriteElement(Stream stream, ulong id, byte[] payload)
    {
        WriteId(stream, id);
        WriteSize(stream, payload.LongLength);
        stream.Write(payload);
    }

    private static void WriteId(Stream stream, ulong id)
    {
        var width = 1;
        while (id >= (1UL << (width * 8)))
        {
            width++;
        }

        for (var i = width - 1; i >= 0; i--)
        {
            stream.WriteByte((byte)(id >> (i * 8)));
        }
    }

    private static void WriteSize(Stream stream, long payloadLength)
    {
        var width = 1;
        while ((ulong)payloadLength >= (1UL << (width * 7)) - 1)
        {
            width++;
        }

        var encoded = (ulong)payloadLength | (1UL << (width * 7));
        for (var i = width - 1; i >= 0; i--)
        {
            stream.WriteByte((byte)(encoded >> (i * 8)));
        }
    }
}
