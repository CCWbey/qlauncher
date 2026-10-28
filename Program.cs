using System.Text;
using System.Diagnostics;

namespace QLaunch;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using Mutex single = new(true, "QLaunch.SingleInstance", out bool owned);
        if (!owned)
        {
            return;
        }
        ApplicationConfiguration.Initialize();
        Native.SetProcessDpiAwarenessContext((IntPtr)(-4));

        AppSettings settings = AppSettings.Load();
        if (args.Length > 1 && args[0] == "--selftest")
        {
            File.WriteAllText(Path.Combine(settings.DataDir, "selftest.log"), SelfTest(args[1], settings.DataDir));
            return;
        }
        AppIndex index = new();
        Stopwatch sw = Stopwatch.StartNew();
        index.Load(settings.DataDir, forceRebuild: args.Contains("--reindex"));
        Debug.WriteLine($"[qlaunch] 索引 {index.Entries.Count} 个应用，用时 {sw.ElapsedMilliseconds}ms");

        Startup.Register(settings.LaunchAtStartup);
        Application.Run(new LauncherForm(settings, index, args.Contains("--show")));
    }

    /// <summary>
    /// 一次性跑通索引、文件搜索与离线翻译三条链路，结果写入 selftest.log 供无界面核对。
    /// </summary>
    /// <param name="text">用于翻译测试的英文句子。</param>
    /// <param name="dataDir">数据目录。</param>
    /// <returns>拼接后的诊断文本。</returns>
    private static string SelfTest(string text, string dataDir)
    {
        StringBuilder log = new();
        AppIndex idx = new();
        Stopwatch sw = Stopwatch.StartNew();
        idx.Load(dataDir, forceRebuild: !File.Exists(Path.Combine(dataDir, "apps.json")));
        log.AppendLine($"apps={idx.Entries.Count} scanMs={sw.ElapsedMilliseconds}");
        foreach (AppEntry a in idx.Entries.Take(5))
        {
            log.AppendLine($"  app: {a.Name} | {a.Initials} | {a.Path} | icon={a.Icon}");
        }
        foreach (string probe in new[] { "chrome", "wx", "记事本", "环境变量" })
        {
            AppEntry? hit = idx.Entries
                .Select(a => (a, Matcher.ScoreApp(probe, a)))
                .Where(x => x.Item2 > 0)
                .OrderByDescending(x => x.Item2)
                .Select(x => x.a)
                .FirstOrDefault();
            log.AppendLine($"match '{probe}' -> {hit?.Name ?? "(无)"}");
        }

        EverythingSearch ev = new();
        FileUsageStore usage = FileUsageStore.Load(dataDir);
        string fileQuery = Matcher.NormalizeQuery("ext:pdf");
        List<Result> pool = ev.Query(fileQuery, FileRanker.CandidatePool, out string? err);
        Stopwatch rankWatch = Stopwatch.StartNew();
        List<Result> files = FileRanker.Rank(pool, fileQuery, usage, 5);
        rankWatch.Stop();
        log.AppendLine($"everything err={err ?? "-"} hits={files.Count}");
        log.AppendLine($"rank pool={pool.Count} sat={(pool.Count >= FileRanker.CandidatePool ? 1 : 0)} ms={rankWatch.ElapsedMilliseconds}");
        foreach (Result r in files)
        {
            log.AppendLine($"  {r.Kind}: {r.Title} <- {r.Subtitle}");
        }

        Translator tr = new();
        TranslationOutcome outcome = tr
            .TranslateAsync(text, Path.Combine(AppContext.BaseDirectory, "models", "enzh"), CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        log.AppendLine($"translator status={tr.Status} err={tr.Error ?? "-"}");
        log.AppendLine($"translate [{outcome.Status}]: {outcome.Text ?? "(null)"} err={outcome.Error ?? "-"}");
        CloudTranslator cloud = CloudTranslator.FromCredentials(CloudCredentials.Load(dataDir));
        log.AppendLine($"cloud={cloud.Description} enabled={cloud.Enabled}");
        if (cloud.Enabled)
        {
            TranslationOutcome remote = cloud.TranslateAsync(text, CancellationToken.None).GetAwaiter().GetResult();
            log.AppendLine($"cloud translate [{remote.Status}]: {remote.Text ?? "(null)"} err={remote.Error ?? "-"}");
        }
        return log.ToString();
    }
}
