using System.IO.Compression;
using KOTU.Core.Jobs;
using Xunit;

namespace KOTU.Module.Archive.Tests;

public class ArchiveMultiSourceRoundTripTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EverySelectedFileAndFolderSurvivesNativeRoundTrip(bool sevenZip)
    {
        var root = Path.Combine(Path.GetTempPath(), "KOTU-selection-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "첫 파일.txt");
            var second = Path.Combine(root, "second.txt");
            var folder = Path.Combine(root, "folder");
            Directory.CreateDirectory(folder);
            File.WriteAllText(first, "first contents");
            File.WriteAllText(second, "second contents");
            File.WriteAllText(Path.Combine(folder, "nested.txt"), "nested contents");
            var target = Path.Combine(root, sevenZip ? "result.7z" : "result.zip");
            var jobs = new BackgroundJobService();
            var coordinator = new ArchiveJobCoordinator(jobs);
            var created = await coordinator.StartCreate([first, second, folder], target, sevenZip).Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(BackgroundJobState.Succeeded, created.State);
            var backend = new SevenZipBackend();
            Assert.Equal(3, backend.List(target).Count(entry => !entry.IsDirectory));
            var extracted = await coordinator.StartExtractMany([target], true)[0].Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(BackgroundJobState.Succeeded, extracted.State);
            Assert.NotNull(extracted.ResultPath);
            Assert.Equal("first contents", File.ReadAllText(Path.Combine(extracted.ResultPath!, "첫 파일.txt")));
            Assert.Equal("second contents", File.ReadAllText(Path.Combine(extracted.ResultPath!, "second.txt")));
            Assert.Equal("nested contents", File.ReadAllText(Path.Combine(extracted.ResultPath!, "folder", "nested.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MissingSelectedSourceFailsInsteadOfCreatingPartialArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "KOTU-missing-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var existing = Path.Combine(root, "existing.txt");
            File.WriteAllText(existing, "keep");
            var target = Path.Combine(root, "result.zip");
            var result = await new ArchiveJobCoordinator(new BackgroundJobService())
                .StartCreate([existing, Path.Combine(root, "missing.txt")], target, false)
                .Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(BackgroundJobState.Failed, result.State);
            Assert.False(File.Exists(target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InvalidZipDoesNotMasqueradeAsPasswordFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "KOTU-invalid-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "plain-text.zip");
            File.WriteAllText(archive, "not an archive");

            var error = Record.Exception(() => new SevenZipBackend().List(archive));

            Assert.NotNull(error);
            Assert.IsNotType<ArchivePasswordException>(error);
            Assert.False(SevenZipBackend.HasPasswordEvidence(archive));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EncryptedSevenZipStillRequestsPasswordAndRetries()
    {
        var root = Path.Combine(Path.GetTempPath(), "KOTU-password-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "secret.txt");
            var archive = Path.Combine(root, "secret.7z");
            File.WriteAllText(source, "secret contents");
            var backend = new SevenZipBackend();
            backend.Create7z([source], archive, "correct secret");

            Assert.True(SevenZipBackend.HasPasswordEvidence(archive));
            Assert.Throws<ArchivePasswordException>(() => backend.List(archive));
            Assert.Throws<ArchivePasswordException>(() => backend.List(archive, "wrong secret"));
            Assert.Single(backend.List(archive, "correct secret"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EncryptedZipStillRequestsPasswordAndRetries()
    {
        var root = Path.Combine(Path.GetTempPath(), "KOTU-zip-password-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "secret.txt");
            var archive = Path.Combine(root, "secret.zip");
            File.WriteAllText(source, "secret contents");
            var backend = new SevenZipBackend();
            backend.CreateZip([source], archive, "correct secret");

            Assert.True(SevenZipBackend.HasPasswordEvidence(archive));
            Assert.Throws<ArchivePasswordException>(() =>
                backend.Extract(archive, Path.Combine(root, "without-password")));
            Assert.Throws<ArchivePasswordException>(() =>
                backend.Extract(archive, Path.Combine(root, "wrong-password"), password: "wrong secret"));

            var extracted = Path.Combine(root, "correct-password");
            backend.Extract(archive, extracted, password: "correct secret");
            Assert.Equal("secret contents", File.ReadAllText(Path.Combine(extracted, "secret.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MixedZipFindsEncryptedFlagAfterPlainEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "KOTU-mixed-password-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "mixed.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("plain.txt").Open()))
                    writer.Write("plain");
                using (var writer = new StreamWriter(archive.CreateEntry("encrypted.txt").Open()))
                    writer.Write("flagged later");
            }

            var bytes = File.ReadAllBytes(archivePath);
            Assert.False(SevenZipBackend.HasPasswordEvidence(archivePath));
            // 실제 암호 payload가 아니라 후행 central entry의 증거 플래그를 검사하는 파서 회귀 fixture다.
            var centralHeaders = FindSignatures(bytes, [0x50, 0x4B, 0x01, 0x02]);
            Assert.Equal(2, centralHeaders.Count);
            bytes[centralHeaders[1] + 8] |= 0x01;
            File.WriteAllBytes(archivePath, bytes);

            Assert.True(SevenZipBackend.HasPasswordEvidence(archivePath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EncryptedZipIgnoresInvalidEocdSignatureInsideComment()
    {
        var root = Path.Combine(Path.GetTempPath(), "KOTU-eocd-comment-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "secret.txt");
            var archive = Path.Combine(root, "comment.zip");
            File.WriteAllText(source, "secret contents");
            new SevenZipBackend().CreateZip([source], archive, "correct secret");

            var bytes = File.ReadAllBytes(archive);
            var eocdSignatures = FindSignatures(bytes, [0x50, 0x4B, 0x05, 0x06]);
            Assert.NotEmpty(eocdSignatures);
            var eocd = eocdSignatures[^1];
            var comment = new byte[32];
            const int fake = 3;
            new byte[] { 0x50, 0x4B, 0x05, 0x06 }.CopyTo(comment, fake);
            // ZIP64 sentinel은 EOF 조건만 맞는 이 가짜 후보의 구조 검증을 실패시킨다.
            Array.Fill(comment, (byte)0xFF, fake + 8, 12);
            BitConverter.GetBytes((ushort)(comment.Length - fake - 22)).CopyTo(comment, fake + 20);
            BitConverter.GetBytes((ushort)comment.Length).CopyTo(bytes, eocd + 20);
            using (var output = File.Create(archive))
            {
                output.Write(bytes);
                output.Write(comment);
            }

            Assert.True(SevenZipBackend.HasPasswordEvidence(archive));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EncryptedZipAllowsOptionalCentralDirectoryDigitalSignature()
    {
        var root = Path.Combine(Path.GetTempPath(), "KOTU-digital-signature-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "secret.txt");
            var archive = Path.Combine(root, "signed.zip");
            File.WriteAllText(source, "secret contents");
            new SevenZipBackend().CreateZip([source], archive, "correct secret");

            var bytes = File.ReadAllBytes(archive);
            var eocdSignatures = FindSignatures(bytes, [0x50, 0x4B, 0x05, 0x06]);
            Assert.NotEmpty(eocdSignatures);
            var eocd = eocdSignatures[^1];
            var digitalSignature = new byte[] { 0x50, 0x4B, 0x05, 0x05, 0x03, 0x00, 0x11, 0x22, 0x33 };
            var centralSize = BitConverter.ToUInt32(bytes, eocd + 12);
            BitConverter.GetBytes(centralSize + (uint)digitalSignature.Length).CopyTo(bytes, eocd + 12);
            using (var output = File.Create(archive))
            {
                output.Write(bytes, 0, eocd);
                output.Write(digitalSignature);
                output.Write(bytes, eocd, bytes.Length - eocd);
            }

            Assert.True(SevenZipBackend.HasPasswordEvidence(archive));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EncryptedSfxZipUsesCentralDirectoryFlags()
    {
        var root = Path.Combine(Path.GetTempPath(), "KOTU-sfx-password-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "secret.txt");
            var archive = Path.Combine(root, "encrypted.zip");
            var sfx = Path.Combine(root, "encrypted-sfx.zip");
            File.WriteAllText(source, "secret contents");
            new SevenZipBackend().CreateZip([source], archive, "correct secret");

            var prefix = new byte[128];
            prefix[0] = 0x4D;
            prefix[1] = 0x5A;
            using (var output = File.Create(sfx))
            {
                output.Write(prefix);
                output.Write(File.ReadAllBytes(archive));
            }

            Assert.True(SevenZipBackend.HasPasswordEvidence(sfx));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static List<int> FindSignatures(byte[] bytes, byte[] signature)
    {
        var matches = new List<int>();
        for (var i = 0; i <= bytes.Length - signature.Length; i++)
        {
            if (bytes.AsSpan(i, signature.Length).SequenceEqual(signature)) matches.Add(i);
        }
        return matches;
    }
}
