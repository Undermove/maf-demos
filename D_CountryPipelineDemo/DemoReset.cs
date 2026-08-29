using System.Text;
using System.Text.Json;
using Octokit;

namespace D_CountryPipelineDemo;

/// <summary>
/// Сброс демо в исходное состояние (запуск: dotnet run -- reset):
/// закрывает PR и ветки, чистит и ПЕРЕОТКРЫВАЕТ карточку (доска сама закрывает issue при Done),
/// возвращает её в backlog/Todo (в т.ч. из архива) и сносит локальную БД шагов.
/// </summary>
public static class DemoReset
{
    public static async Task RunAsync(GitHubClient client, string owner, string repo, string token, StepsDb db)
    {
        var cards = db.CardNumbers();
        if (cards.Count == 0)
        {
            Console.WriteLine("⚠ Карточки ещё не созданы. Сначала: dotnet run -- seed");
            return;
        }

        Console.WriteLine("→ Закрываю открытые PR…");
        foreach (var pr in await client.PullRequest.GetAllForRepository(owner, repo,
                     new PullRequestRequest { State = ItemStateFilter.Open }))
        {
            await client.PullRequest.Update(owner, repo, pr.Number, new PullRequestUpdate { State = ItemState.Closed });
            Console.WriteLine($"  PR #{pr.Number} закрыт");
        }

        Console.WriteLine("→ Удаляю feature-ветки…");
        foreach (var branch in await client.Repository.Branch.GetAll(owner, repo))
            if (branch.Name.StartsWith("feature/"))
            {
                await client.Git.Reference.Delete(owner, repo, $"heads/{branch.Name}");
                Console.WriteLine($"  ветка {branch.Name} удалена");
            }

        foreach (var cardNumber in cards)
        {
            Console.WriteLine($"→ Карточка #{cardNumber}: переоткрываю, чищу комменты, лейбл → backlog…");
            var update = new IssueUpdate { State = ItemState.Open };   // доска закрывает issue при Done — возвращаем
            var issue = await client.Issue.Get(owner, repo, cardNumber);
            foreach (var label in issue.Labels.Where(l => l.Name.StartsWith("status:")))
                update.RemoveLabel(label.Name);
            update.AddLabel("status:backlog");
            await client.Issue.Update(owner, repo, cardNumber, update);

            foreach (var comment in await client.Issue.Comment.GetAllForIssue(owner, repo, cardNumber))
                await client.Issue.Comment.Delete(owner, repo, comment.Id);

            Console.WriteLine($"→ Доска: карточка #{cardNumber} из архива → колонка Todo…");
            await ResetBoardAsync(owner, repo, token, cardNumber);
        }

        // Именно сбрасываем флаги, а НЕ удаляем файл: в базе лежит привязка шагов к карточкам,
        // и если её снести, придётся заново гонять seed.
        db.ResetProgress();
        Console.WriteLine("→ Прогресс шагов сброшен (привязка к карточкам сохранена).");

        Console.WriteLine("✅ Демо готово к запуску с чистого листа.");
    }

    private static async Task ResetBoardAsync(string owner, string repo, string token, int cardNumber)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        http.DefaultRequestHeaders.Add("User-Agent", "maf-country-pipeline-demo");

        var data = await GraphQlAsync(http, $$"""
            query {
              repository(owner: "{{owner}}", name: "{{repo}}") {
                issue(number: {{cardNumber}}) {
                  projectItems(first: 5, includeArchived: true) {
                    nodes {
                      id
                      project {
                        id
                        field(name: "Status") {
                          ... on ProjectV2SingleSelectField { id options { id name } }
                        }
                      }
                    }
                  }
                }
              }
            }
            """);

        var item = data.GetProperty("repository").GetProperty("issue")
            .GetProperty("projectItems").GetProperty("nodes")[0];
        var projectId = item.GetProperty("project").GetProperty("id").GetString();
        var itemId = item.GetProperty("id").GetString();
        var field = item.GetProperty("project").GetProperty("field");
        var todoId = field.GetProperty("options").EnumerateArray()
            .First(o => o.GetProperty("name").GetString() == "Todo").GetProperty("id").GetString();

        // Доска могла заархивировать Done-карточку — возвращаем (если не в архиве, GitHub вернёт ошибку — глотаем).
        try
        {
            await GraphQlAsync(http, $$"""
                mutation { unarchiveProjectV2Item(input: {projectId: "{{projectId}}", itemId: "{{itemId}}"}) { item { id } } }
                """);
        }
        catch (InvalidOperationException) { }

        await GraphQlAsync(http, $$"""
            mutation {
              updateProjectV2ItemFieldValue(input: {
                projectId: "{{projectId}}", itemId: "{{itemId}}",
                fieldId: "{{field.GetProperty("id").GetString()}}", value: { singleSelectOptionId: "{{todoId}}" }
              }) { projectV2Item { id } }
            }
            """);
    }

    private static async Task<JsonElement> GraphQlAsync(HttpClient http, string query)
    {
        var payload = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        var response = await http.PostAsync("https://api.github.com/graphql", payload);
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (json.TryGetProperty("errors", out var errors))
            throw new InvalidOperationException($"GraphQL error: {errors}");
        return json.GetProperty("data");
    }
}
