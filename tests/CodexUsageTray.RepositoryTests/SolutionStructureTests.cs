using System.Xml.Linq;

namespace CodexUsageTray.RepositoryTests;

public sealed class SolutionStructureTests
{
    [Fact]
    public void EveryProjectUsesItsRepositoryFolder()
    {
        var repositoryRoot = FindRepositoryRoot();
        var solution = XDocument.Load(Path.Combine(repositoryRoot, "CodexUsageTray.slnx"));
        var entries = solution
            .Descendants("Project")
            .Select(project => new
            {
                Folder = project.Parent?.Attribute("Name")?.Value,
                Path = project.Attribute("Path")!.Value.Replace('\\', '/')
            })
            .ToArray();

        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.Equal($"/{entry.Path.Split('/')[0]}/", entry.Folder));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexUsageTray.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
