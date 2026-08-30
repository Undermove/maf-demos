using Octokit;

namespace D_CountryPipelineDemo;

/// <summary>
/// Разовая подготовка доски: dotnet run -- seed
/// Создаёт (или обновляет) по одной карточке на каждый шаг пайплайна и связывает их с шагами в БД.
/// Один шаг = одна карточка. Инструкции живут только здесь, в ямликах их нет.
/// </summary>
public static class DemoSeed
{
    public static async Task RunAsync(GitHubClient client, string owner, string repo, StepsDb db)
    {
        var existing = (await client.Issue.GetAllForRepository(owner, repo,
                new RepositoryIssueRequest { State = ItemStateFilter.All }))
            .Where(i => i.PullRequest is null)
            .ToList();

        foreach (var (title, body) in StepsDb.Cards)
        {
            var issue = existing.FirstOrDefault(i => i.Title == title);

            if (issue is null)
            {
                var created = new NewIssue(title) { Body = body };
                created.Labels.Add("status:backlog");
                issue = await client.Issue.Create(owner, repo, created);
                Console.WriteLine($"  + карточка #{issue.Number}: {title}");
            }
            else
            {
                await client.Issue.Update(owner, repo, issue.Number, new IssueUpdate { Body = body });
                Console.WriteLine($"  = карточка #{issue.Number}: {title} (текст обновлён)");
            }

            db.BindCard(title, issue.Number);
        }

        Console.WriteLine("\n✅ Доска подготовлена. Не забудь добавить карточки в GitHub Project, если их там ещё нет.");
    }
}
