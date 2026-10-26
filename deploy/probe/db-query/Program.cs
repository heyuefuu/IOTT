using System.Text;
using System.Reflection;


using MySqlConnector;

AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var simple = new AssemblyName(e.Name).Name;
    var candidate = Path.Combine(AppContext.BaseDirectory, simple + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};


// db-query - minimal read-only MySQL client using the MySqlConnector assembly shipped by IOTT.
// usage:
//   db-query "<connection string>" ["SQL"]
//   db-query --host 192.168.1.200 --port 3306 --user iott --password <pwd> --database IndustrialIoT --sql "SELECT ..."
// examples:
//   db-query "Server=192.168.1.200;Port=3306;User ID=iott;Password=...;Database=IndustrialIoT;SslMode=None;AllowPublicKeyRetrieval=True" "SELECT VERSION()"
//   db-query --host 192.168.1.200 --user iott --password ... --sql "SHOW DATABASES"

var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
string sql = null;
string connStr = null;

for (int i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (a == "--sql" || a == "-q") { sql = i + 1 < args.Length ? args[++i] : null; continue; }
    if (a == "--file" && i + 1 < args.Length) { sql = File.ReadAllText(args[++i]); continue; }
    if (a.StartsWith("--") && i + 1 < args.Length)
    {
        opts[a.Substring(2)] = args[++i];
        continue;
    }
    if (connStr == null && a.Contains('=') && a.Contains(';')) { connStr = a; continue; }
    if (sql == null) { sql = a; }
}

if (connStr == null)
{
    if (!opts.ContainsKey("host"))
    {
        Console.WriteLine("usage: db-query --host <ip> [--port 3306] [--user iott] [--password <pwd>] [--database IndustrialIoT] [--sql \"SELECT ...\"]");
        return 2;
    }
    var sb = new StringBuilder();
    sb.Append($"Server={opts["host"]};");
    sb.Append($"Port={(opts.TryGetValue("port", out var p) ? p : "3306")};");
    sb.Append($"User ID={(opts.TryGetValue("user", out var u) ? u : "iott")};");
    if (opts.TryGetValue("password", out var pw)) sb.Append($"Password={pw};");
    if (opts.TryGetValue("database", out var db)) sb.Append($"Database={db};");
    sb.Append("SslMode=None;AllowPublicKeyRetrieval=True;Connection Timeout=8;Default Command Timeout=30;");
    connStr = sb.ToString();
}
if (string.IsNullOrWhiteSpace(sql)) sql = "SELECT VERSION() AS version, CURRENT_USER() AS current_user";

try
{
    using var conn = new MySqlConnection(connStr);
    conn.Open();
    Console.WriteLine($"[db-query] connected: server={conn.ServerVersion}  state={conn.State}");
    foreach (var stmt in sql.Split(new[] { ";\n", ";\r\n" }, StringSplitOptions.RemoveEmptyEntries))
    {
        var q = stmt.Trim().TrimEnd(';');
        if (q.Length == 0) continue;
        Console.WriteLine($"[db-query] SQL: {(q.Length > 200 ? q.Substring(0, 200) + "..." : q).Replace("\r", " ").Replace("\n", " ")}");
        using var cmd = new MySqlCommand(q, conn) { CommandTimeout = 60 };
        using var rd = cmd.ExecuteReader();
        var cols = new string[rd.FieldCount];
        for (int i = 0; i < rd.FieldCount; i++) cols[i] = rd.GetName(i);
        Console.WriteLine(string.Join(" | ", cols));
        Console.WriteLine(new string('-', Math.Min(160, Math.Max(20, string.Join(" | ", cols).Length))));
        int rows = 0;
        while (rd.Read())
        {
            var vals = new string[rd.FieldCount];
            for (int i = 0; i < rd.FieldCount; i++) vals[i] = rd.IsDBNull(i) ? "" : rd.GetValue(i)?.ToString() ?? "";
            Console.WriteLine(string.Join(" | ", vals));
            rows++;
            if (rows >= 500) { Console.WriteLine($"[db-query] (truncated at 500 rows)"); break; }
        }
        Console.WriteLine($"[db-query] rows={rows}");
    }
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"[db-query] ERROR: {ex}");
    return 1;
}

