using Cascade.Core.Updating;

namespace Cascade.Core.Tests;

/// <summary>
/// Where the updater is willing to find git, and what it will hand the user's credential to.
///
/// <para>This is a security boundary, not a convenience. <c>CreateProcess</c> searches the calling image's
/// own directory first, and Cascade is a single executable meant to be copied onto shared folders and USB
/// sticks - so a git.exe dropped beside it would be run, as the user, at every startup, and handed their
/// own GitHub credential for its trouble. The rule was written after that was noticed; these are what keep
/// it written.</para>
///
/// <para>No git is ever launched, and nothing process-wide is touched: the three things the rule depends on
/// - the image's directory, the working directory and the search path - are passed in. The directories are
/// made up and populated with a file called git.exe that is not one, so the question asked is exactly
/// "would this path be chosen", which is the part that decides whether anything unsafe happens.</para>
/// </summary>
public class GitCredentialTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cascade_git_" + Guid.NewGuid().ToString("N"));

    public GitCredentialTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>A directory with something called git.exe in it. Not a real one - nothing here runs it.</summary>
    private string Plant(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "git.exe"), "not really git");
        return dir;
    }

    private string Empty(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Git_on_the_path_is_found_by_its_full_path()
    {
        string dir = Plant("tools");

        string? git = GitCredentialToken.ResolveGit(ownDirectory: null, currentDirectory: null, [dir]);

        Assert.Equal(Path.Combine(dir, "git.exe"), git);
        Assert.True(Path.IsPathFullyQualified(git!), "a bare name would let CreateProcess search for it");
    }

    [Fact]
    public void A_git_planted_beside_the_executable_is_refused()
    {
        // The reported hazard, and the reason any of this exists: copy Cascade.exe into a folder, drop a
        // git.exe next to it, and every launch runs it.
        string beside = Plant("portable");

        Assert.Null(GitCredentialToken.ResolveGit(ownDirectory: beside, currentDirectory: null, [beside]));
    }

    [Fact]
    public void A_git_planted_in_the_working_directory_is_refused()
    {
        string cwd = Plant("cwd");

        Assert.Null(GitCredentialToken.ResolveGit(ownDirectory: null, currentDirectory: cwd, [cwd]));
    }

    [Theory]
    [InlineData("{0}\\")]        // trailing separator
    [InlineData("{0}\\.")]       // needs normalizing before it can be compared
    [InlineData("{0}\\sub\\..")] // and again
    public void The_refusal_is_not_fooled_by_how_the_directory_is_spelled(string shape)
    {
        string beside = Plant("portable");
        Directory.CreateDirectory(Path.Combine(beside, "sub"));

        // Same place, written differently. A plain string comparison would let this through, which would
        // make the whole rule a matter of how somebody's PATH happened to be typed.
        Assert.Null(GitCredentialToken.ResolveGit(ownDirectory: string.Format(shape, beside),
                                                  currentDirectory: null, [beside]));
    }

    [Fact]
    public void A_git_further_along_the_path_is_still_found_when_the_first_is_refused()
    {
        // The refusal must skip the entry, not abandon the search - otherwise planting a git.exe beside the
        // executable would be enough to switch updating off for everyone.
        string beside = Plant("portable"), good = Plant("real");

        Assert.Equal(Path.Combine(good, "git.exe"),
                     GitCredentialToken.ResolveGit(ownDirectory: beside, currentDirectory: null, [beside, good]));
    }

    [Fact]
    public void The_first_git_on_the_path_wins()
    {
        string first = Plant("first"), second = Plant("second");

        Assert.Equal(Path.Combine(first, "git.exe"),
                     GitCredentialToken.ResolveGit(null, null, [first, second]));
    }

    [Theory]
    [InlineData("\"{0}\"")]      // quoted, as PATH entries written by installers often are
    [InlineData("  {0}  ")]      // padded
    [InlineData("{0}\\.")]
    public void An_awkwardly_written_path_entry_still_resolves(string shape)
    {
        string dir = Plant("tools");

        Assert.Equal(Path.Combine(dir, "git.exe"),
                     GitCredentialToken.ResolveGit(null, null, [string.Format(shape, dir)]));
    }

    [Fact]
    public void An_unusable_path_entry_does_not_stop_the_search()
    {
        string good = Plant("tools");
        string[] path = ["", "   ", "|<>:invalid", new string('x', 400), good];

        Assert.Equal(Path.Combine(good, "git.exe"), GitCredentialToken.ResolveGit(null, null, path));
    }

    [Fact]
    public void No_git_anywhere_means_no_git_rather_than_a_guess()
        => Assert.Null(GitCredentialToken.ResolveGit(null, null, [Empty("nothing-here")]));

    [Fact]
    public void The_places_it_looks_are_all_absolute()
    {
        // Every entry ends up as a directory to look in, and a relative one would be interpreted against
        // the working directory - the very place the rule above refuses.
        foreach (string dir in GitCredentialToken.SearchDirectories())
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (dir.Contains('%')) continue;              // an unexpanded PATH entry is skipped anyway
            Assert.True(Path.IsPathRooted(dir.Trim().Trim('"')), $"not rooted: {dir}");
        }
    }

    [Fact]
    public async Task A_credential_supplied_by_the_environment_is_used_without_consulting_git()
    {
        // The escape hatch the update tests and unattended machines use. It has to be consulted BEFORE git,
        // or a machine with no git could never be given one.
        string? previous = Environment.GetEnvironmentVariable(GitCredentialToken.EnvironmentVariable);
        Environment.SetEnvironmentVariable(GitCredentialToken.EnvironmentVariable, "gho_pretend");
        try
        {
            Assert.Equal("gho_pretend", await GitCredentialToken.GetAsync("github.com", CancellationToken.None));
        }
        finally { Environment.SetEnvironmentVariable(GitCredentialToken.EnvironmentVariable, previous); }
    }
}
