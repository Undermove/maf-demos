using System.ComponentModel;
using Octokit;

namespace CountryOpeningDemo;

public sealed class GitHubTools(GitHubClient client, string owner, string repo)
{
    [Description("Read a file from the repository (main branch)")]
    public async Task<string> ReadFile(
        [Description("File path, e.g. countries/germany.json")] string path)
    {
        var files = await client.Repository.Content.GetAllContents(owner, repo, path);
        Log($"ReadFile(\"{path}\")");
        return files[0].Content;
    }

    #region other_tools

        [Description("Create a new branch from main. Returns the actual branch name — use it for commits and the PR.")]
    public async Task<string> CreateBranch(
        [Description("Branch name, e.g. feature/country-ge")] string name)
    {
        var main = await client.Git.Reference.Get(owner, repo, "heads/main");
        try
        {
            await client.Git.Reference.Create(owner, repo, new NewReference($"refs/heads/{name}", main.Object.Sha));
        }
        catch (ApiValidationException)
        {
            // Ветка осталась с прошлого прогона демо — не падаем, берём уникальное имя.
            name = $"{name}-{DateTime.Now:HHmmss}";
            await client.Git.Reference.Create(owner, repo, new NewReference($"refs/heads/{name}", main.Object.Sha));
        }

        Log($"CreateBranch(\"{name}\")");
        return $"Branch '{name}' created from main. Use exactly this name for commits and the pull request.";
    }

    [Description("Create or update a file on a branch")]
    public async Task<string> CommitFile(
        [Description("Branch to commit to")] string branch,
        [Description("File path, e.g. countries/ge.json")] string path,
        [Description("Full file content")] string content,
        [Description("Commit message")] string message)
    {
        try
        {
            var existing = await client.Repository.Content.GetAllContentsByRef(owner, repo, path, branch);
            await client.Repository.Content.UpdateFile(owner, repo, path,
                new UpdateFileRequest(message, content, existing[0].Sha, branch));
        }
        catch (NotFoundException)
        {
            await client.Repository.Content.CreateFile(owner, repo, path,
                new CreateFileRequest(message, content, branch));
        }

        Log($"CommitFile(\"{path}\" on \"{branch}\")");
        return $"File {path} committed to '{branch}'.";
    }

    [Description("Open a pull request from a branch into main")]
    public async Task<string> CreatePullRequest(
        [Description("Source branch")] string branch,
        [Description("PR title")] string title,
        [Description("PR body / description")] string body)
    {
        var pr = await client.PullRequest.Create(owner, repo,
            new NewPullRequest(title, branch, "main") { Body = body });

        Log($"CreatePullRequest(#{pr.Number})");
        return $"PR #{pr.Number} opened: {pr.HtmlUrl}";
    }

    private static void Log(string call)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"   🔧 {call}");
        Console.ResetColor();
    }

    #endregion
}
