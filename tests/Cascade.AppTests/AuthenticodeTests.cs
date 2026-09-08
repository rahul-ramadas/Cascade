using Cascade.App;

namespace Cascade.AppTests;

/// <summary>
/// Whether a file carries a valid Authenticode signature, and who signed it. The updater asks this before
/// it will replace the running executable, so "not signed" and "signed by somebody else" have to be the
/// same answer as "no" - and asking must never throw, because it is asked at startup on a background
/// thread about a file that may be half-downloaded or gone.
///
/// <para>The signed side is put to a Windows binary rather than to a build of Cascade: a local build is
/// unsigned, only CI signs, and a test that could only pass on the release machine is a test that never
/// runs. Windows is also the authority on the answer, which is the whole point of asking it rather than
/// reading the certificate out of the file.</para>
/// </summary>
public class AuthenticodeTests
{
    private static string SignedSystemBinary => Path.Combine(Environment.SystemDirectory, "kernel32.dll");

    [Fact]
    public void A_validly_signed_binary_names_who_signed_it()
    {
        string? identity = Authenticode.IdentityOf(SignedSystemBinary);

        Assert.False(string.IsNullOrWhiteSpace(identity));
        // Either a durable identity out of a custom extended key usage, or the subject as a fallback. Both
        // are meant to be the same for the same signer, which is what the updater compares.
        Assert.Equal(identity, Authenticode.IdentityOf(SignedSystemBinary));
    }

    [Fact]
    public void An_unsigned_file_is_signed_by_nobody()
    {
        string path = Path.Combine(Path.GetTempPath(), "cascade_unsigned_" + Guid.NewGuid().ToString("N") + ".exe");
        // Enough of a PE header to be a plausible executable rather than obvious rubbish; it still carries
        // no signature, which is the point.
        File.WriteAllBytes(path, [0x4D, 0x5A, .. new byte[510]]);
        try { Assert.Null(Authenticode.IdentityOf(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_signed_binary_that_has_been_altered_is_signed_by_nobody()
    {
        // The check that matters. A signature says the digest matches the bytes, so a copy with one byte
        // changed must fail - if it did not, verifying an update would prove nothing about what was
        // downloaded. Struck near the end of the file, well past the headers, so what breaks is the digest
        // and not the file's ability to be parsed at all.
        string copy = Path.Combine(Path.GetTempPath(), "cascade_tampered_" + Guid.NewGuid().ToString("N") + ".dll");
        File.Copy(SignedSystemBinary, copy, overwrite: true);
        try
        {
            Assert.NotNull(Authenticode.IdentityOf(copy));   // the copy alone is still valid

            byte[] bytes = File.ReadAllBytes(copy);
            int at = bytes.Length - 4096;
            bytes[at] ^= 0xFF;
            File.WriteAllBytes(copy, bytes);

            Assert.Null(Authenticode.IdentityOf(copy));
        }
        finally { try { File.Delete(copy); } catch { /* best effort */ } }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-such-file-anywhere.exe")]
    [InlineData("Q:\\not\\even\\a\\drive\\that\\exists.exe")]
    [InlineData("\0invalid")]
    public void Asking_about_something_that_is_not_a_file_answers_rather_than_throws(string path)
        => Assert.Null(Authenticode.IdentityOf(path));

    [Fact]
    public void A_directory_is_signed_by_nobody()
        => Assert.Null(Authenticode.IdentityOf(Environment.SystemDirectory));
}
