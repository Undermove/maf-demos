using Microsoft.Data.Sqlite;

namespace D_CountryPipelineDemo;

/// <summary>
/// «База данных» пайплайна — SQLite-файл рядом с демкой. В проде это Postgres, схема та же:
/// шаги процесса лежат ямликами, у каждого флаг done. Джоба достаёт первый несделанный шаг.
/// </summary>
public sealed class StepsDb(string path)
{
    private readonly string _connectionString = $"Data Source={path}";

    // Формат ямликов — один в один как в проде (support-bot, country-opening-template):
    // card: id/name/lane/depends_on/required_data + checklists с items {id, depends_on, type, instruction}.
    // type: manual — нужен человек (агент спрашивает менеджера), type: ai — агент делает сам.
    private static readonly string[] Seed =
    [
        """
        # yaml-language-server: $schema=../schema.json
        version: "1.0"

        card:
          id: clarify_requirements
          name: "Выяснить требования для открытия страны"
          lane: Preparation
          description: "Карточка задачи — №4 на доске https://github.com/Undermove/maf-country-opening-demo/issues/4"
          depends_on: []
          required_data: []

          checklists:
            - id: first_checklist
              name: "Checklist"
              depends_on: []
              items:
                - id: read_card
                  depends_on: []
                  type: ai
                  instruction: "Прочитать карточку задачи №4 на доске."
                - id: ask_languages
                  depends_on: [read_card]
                  type: manual
                  instruction: "Спросить у менеджера страны, какие языки нужны для локализации новой страны."
                - id: save_languages
                  depends_on: [ask_languages]
                  type: ai
                  instruction: "Записать выясненные языки комментарием в карточку №4 и передвинуть карточку в in-progress."
        """,
        """
        # yaml-language-server: $schema=../schema.json
        version: "1.0"

        card:
          id: add_country_config
          name: "Добавить конфиг страны"
          lane: Site
          description: "repo - https://github.com/Undermove/maf-country-opening-demo. Пример конфига - countries/germany.json"
          depends_on: [clarify_requirements]
          required_data: ["languages"]

          checklists:
            - id: first_checklist
              name: "Checklist"
              depends_on: []
              items:
                - id: read_sample
                  depends_on: []
                  type: ai
                  instruction: "Прочитать образец countries/germany.json."
                - id: commit_config
                  depends_on: [read_sample]
                  type: ai
                  instruction: "Создать ветку feature/country-ge и добавить countries/ge.json строго по структуре
                                образца: код ge, валюта GEL, языки — выясненные на предыдущем шаге, status: opening."
        """,
        """
        # yaml-language-server: $schema=../schema.json
        version: "1.0"

        card:
          id: open_pull_request
          name: "Открыть pull request с конфигом страны"
          lane: Final
          description: ""
          depends_on: [add_country_config]
          required_data: []

          checklists:
            - id: first_checklist
              name: "Checklist"
              depends_on: []
              items:
                - id: open_pr
                  depends_on: []
                  type: ai
                  instruction: "Открыть pull request из ветки с конфигом в main."
                - id: link_pr
                  depends_on: [open_pr]
                  type: ai
                  instruction: "Добавить ссылку на PR комментарием в карточку №4 и передвинуть карточку в done."
        """,
    ];

    public void EnsureSeeded()
    {
        using var db = Open();
        Exec(db, "CREATE TABLE IF NOT EXISTS steps (id INTEGER PRIMARY KEY, yaml TEXT NOT NULL, done INTEGER NOT NULL DEFAULT 0)");

        if ((long)Scalar(db, "SELECT COUNT(*) FROM steps")! == 0)
            foreach (var yaml in Seed)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "INSERT INTO steps (yaml) VALUES ($yaml)";
                cmd.Parameters.AddWithValue("$yaml", yaml);
                cmd.ExecuteNonQuery();
            }

        // Все шаги выполнены прошлым прогоном — сбрасываем, демо можно гонять по кругу.
        if ((long)Scalar(db, "SELECT COUNT(*) FROM steps WHERE done = 0")! == 0)
        {
            Exec(db, "UPDATE steps SET done = 0");
            Console.WriteLine("♻ Все шаги были выполнены — сбрасываю пайплайн для нового прогона.\n");
        }
    }

    public (long Id, string Yaml)? GetNextStep()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, yaml FROM steps WHERE done = 0 ORDER BY id LIMIT 1";
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetString(1)) : null;
    }

    public void MarkDone(long id)
    {
        using var db = Open();
        Exec(db, $"UPDATE steps SET done = 1 WHERE id = {id}");
    }

    public long TotalSteps()
    {
        using var db = Open();
        return (long)Scalar(db, "SELECT COUNT(*) FROM steps")!;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
