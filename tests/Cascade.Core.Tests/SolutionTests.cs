using System.Xml.Linq;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>The solution file itself. Opening a throwaway project in the editor while it has this solution
/// open ADDS THAT PROJECT TO IT, and a scratch project living outside the repository builds perfectly well
/// on the machine that made it and fails everywhere else. That has cost a red build, so it is checked.</summary>
public class SolutionTests
{
    private static FileInfo SolutionFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var file = new FileInfo(Path.Combine(dir.FullName, "Cascade.slnx"));
            if (file.Exists) return file;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Cascade.slnx was not found above {AppContext.BaseDirectory}, so this check cannot run.");
    }

    [Fact]
    public void Every_project_in_the_solution_lives_inside_the_repository()
    {
        var solution = SolutionFile();
        string root = solution.Directory!.FullName;

        var strays = new List<string>();
        foreach (var project in XDocument.Load(solution.FullName).Descendants("Project"))
        {
            string? path = (string?)project.Attribute("Path");
            if (path is null) continue;

            string full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                strays.Add(path);
        }

        Assert.True(strays.Count == 0,
            $"These projects are outside the repository, so only the machine that added them can build: " +
            string.Join(", ", strays));
    }

    [Fact]
    public void Every_project_in_the_solution_is_actually_there()
    {
        var solution = SolutionFile();
        string root = solution.Directory!.FullName;

        var missing = XDocument.Load(solution.FullName).Descendants("Project")
            .Select(p => (string?)p.Attribute("Path"))
            .OfType<string>()
            .Where(p => !File.Exists(Path.Combine(root, p.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        Assert.True(missing.Count == 0, "These projects do not exist: " + string.Join(", ", missing));
    }
}
