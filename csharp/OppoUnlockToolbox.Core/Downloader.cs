

using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace OppoUnlockToolbox.Core;

public sealed class ResourceItem
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Unzip { get; set; } = true;

    public List<string> Mirrors { get; set; } = new();
}

public static class ResourceCatalog
{
    private static string ConfigPath() => Path.Combine(AppConfig.AppDir(), "resources.json");

    public static string EnsureTemplate()
    {
        var path = ConfigPath();
        if (!File.Exists(path))
        {
            var template = new
            {
                _说明 = "把 url 换成你的直链（浏览器打开就直接下载的那种）；unzip=true 表示下载后自动解压；" +
                        "可以增删条目。下载完成后工具箱会自动识别 abl*.img / ocdt*.img / bundle 目录并填入对应阶段。" +
                        "改完保存，重新打开「资源下载」窗口即可生效。",
                resources = new[]
                {
                    new
                    {
                        key = "bundle",
                        group = "Find X5 Pro（SM8450 · 内核 5.10）",
                        name = "临时 root bundle（内核 5.10.198/168）",
                        url = "https://github.com/diyiqiuye/fx5p-resources/releases/download/v1/cve-2025-21479-fx5p-bundle.zip",
                        unzip = true,
                        mirrors = new[]
                        {
                            "https://gitee.com/diyiqiuye/fx5p-resources/releases/download/v1/cve-2025-21479-fx5p-bundle.zip",
                            "https://ghfast.top/{url}",
                            "https://gh-proxy.com/{url}",
                            "https://github.moeyy.xyz/{url}",
                        },
                    },
                    new
                    {
                        key = "images",
                        group = "Find X5 Pro（SM8450 · 内核 5.10）",
                        name = "解锁镜像（abl.img / ocdt.img）",
                        url = "https://github.com/diyiqiuye/fx5p-resources/releases/download/v1/8gen1_unlock_images.zip",
                        unzip = true,
                        mirrors = new[]
                        {
                            "https://gitee.com/diyiqiuye/fx5p-resources/releases/download/v1/8gen1_unlock_images.zip",
                            "https://ghfast.top/{url}",
                            "https://gh-proxy.com/{url}",
                            "https://github.moeyy.xyz/{url}",
                        },
                    },
                    new
                    {
                        key = "bundle-fx6p",
                        group = "Find X6 Pro（SM8550 · 内核 5.15）",
                        name = "临时 root bundle v0.1（内核 5.15.123 · 多版本）",
                        url = "https://github.com/diyiqiuye/fx5p-resources/releases/download/v1/fx6p-temp-root-bundle.zip",
                        unzip = true,
                        mirrors = new[]
                        {
                            "https://gitee.com/diyiqiuye/fx5p-resources/releases/download/v1/fx6p-temp-root-bundle.zip",
                            "https://ghfast.top/{url}",
                            "https://gh-proxy.com/{url}",
                            "https://github.moeyy.xyz/{url}",
                        },
                    },
                    new
                    {
                        key = "images-fx6p",
                        group = "Find X6 Pro（SM8550 · 内核 5.15）",
                        name = "解锁镜像（abl.img / ocdt.img）",
                        url = "https://github.com/diyiqiuye/fx5p-resources/releases/download/v1/fx6p_unlock_images.zip",
                        unzip = true,
                        mirrors = new[]
                        {
                            "https://gitee.com/diyiqiuye/fx5p-resources/releases/download/v1/fx6p_unlock_images.zip",
                            "https://ghfast.top/{url}",
                            "https://gh-proxy.com/{url}",
                            "https://github.moeyy.xyz/{url}",
                        },
                    },
                    new
                    {
                        key = "ksu-apk",
                        group = "通用（KernelSU 管理器）",
                        name = "KernelSU 管理器 v3.3.0（下载后自动装进手机）",
                        url = "https://github.com/tiann/KernelSU/releases/download/v3.3.0/KernelSU_v3.3.0_32601-release.apk",
                        unzip = false,
                        mirrors = new[]
                        {
                            "https://ghfast.top/{url}",
                            "https://gh-proxy.com/{url}",
                            "https://github.moeyy.xyz/{url}",
                        },
                    },
                    new
                    {
                        key = "dfroot-apk",
                        group = "通用（DFRoot 临时 root）",
                        name = "DFRoot v4.1（支持最新版系统，下载后自动装进手机）",
                        url = "https://github.com/diabl0w/DFRoot/releases/download/v4.1/DFRoot_4.1.apk",
                        unzip = false,
                        mirrors = new[]
                        {
                            "https://ghfast.top/{url}",
                            "https://gh-proxy.com/{url}",
                            "https://github.moeyy.xyz/{url}",
                        },
                    },
                },
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(template,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
        }
        return path;
    }

    private static string PropIgnoreCase(System.Text.Json.JsonElement entry, string name)
    {
        foreach (var prop in entry.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                return prop.Value.GetString() ?? "";
        }
        return "";
    }

    public static List<ResourceItem> Load()
    {
        var path = EnsureTemplate();
        var result = new List<ResourceItem>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("resources", out var array) &&
                array.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var entry in array.EnumerateArray())
                {
                    string Prop(string name) => PropIgnoreCase(entry, name);
                    result.Add(new ResourceItem
                    {
                        Key = Prop("key"),
                        Name = Prop("name"),
                        Group = Prop("group"),
                        Url = Prop("url"),
                        Unzip = !(entry.TryGetProperty("unzip", out var z) || entry.TryGetProperty("Unzip", out z)) || z.GetBoolean(),
                        Mirrors = (entry.TryGetProperty("mirrors", out var m) || entry.TryGetProperty("Mirrors", out m)) &&
                                  m.ValueKind == System.Text.Json.JsonValueKind.Array
                            ? m.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                            : new List<string>(),
                    });
                }
            }
        }
        catch
        {

        }
        return MergeWithTemplate(result, path);
    }

    /// <summary>
    /// 程序更新后新增的资源条目自动并入旧配置文件（保留用户已改的 url 等内容），
    /// 否则老用户永远看不到新增资源（例如 KernelSU 管理器）。
    /// </summary>
    private static List<ResourceItem> MergeWithTemplate(List<ResourceItem> loaded, string path)
    {
        var templateDefaults = new Dictionary<string, (string Name, string Group, string Url, bool Unzip,
            string[] Mirrors)>
        {
            ["bundle"] = ("临时 root bundle（内核 5.10.198/168）", "Find X5 Pro（SM8450 · 内核 5.10）",
                "https://github.com/diyiqiuye/fx5p-resources/releases/download/v1/cve-2025-21479-fx5p-bundle.zip",
                true, new[] { "https://gitee.com/diyiqiuye/fx5p-resources/releases/download/v1/cve-2025-21479-fx5p-bundle.zip" }),
            ["images"] = ("解锁镜像（abl.img / ocdt.img）", "Find X5 Pro（SM8450 · 内核 5.10）",
                "https://github.com/diyiqiuye/fx5p-resources/releases/download/v1/8gen1_unlock_images.zip",
                true, new[] { "https://gitee.com/diyiqiuye/fx5p-resources/releases/download/v1/8gen1_unlock_images.zip" }),
            ["bundle-fx6p"] = ("临时 root bundle v0.1（内核 5.15.123 · 多版本）", "Find X6 Pro（SM8550 · 内核 5.15）",
                "https://github.com/diyiqiuye/fx5p-resources/releases/download/v1/fx6p-temp-root-bundle.zip",
                true, new[] { "https://gitee.com/diyiqiuye/fx5p-resources/releases/download/v1/fx6p-temp-root-bundle.zip" }),
            ["images-fx6p"] = ("解锁镜像（abl.img / ocdt.img）", "Find X6 Pro（SM8550 · 内核 5.15）",
                "https://github.com/diyiqiuye/fx5p-resources/releases/download/v1/fx6p_unlock_images.zip",
                true, new[] { "https://gitee.com/diyiqiuye/fx5p-resources/releases/download/v1/fx6p_unlock_images.zip" }),
            ["ksu-apk"] = ("KernelSU 管理器 v3.3.0（下载后自动装进手机）", "通用（KernelSU 管理器）",
                "https://github.com/tiann/KernelSU/releases/download/v3.3.0/KernelSU_v3.3.0_32601-release.apk",
                false, Array.Empty<string>()),
            ["dfroot-apk"] = ("DFRoot v4.1（支持最新版系统，下载后自动装进手机）", "通用（DFRoot 临时 root）",
                "https://github.com/diabl0w/DFRoot/releases/download/v4.1/DFRoot_4.1.apk",
                false, Array.Empty<string>()),
        };

        var known = new[] { "https://ghfast.top/{url}", "https://gh-proxy.com/{url}", "https://github.moeyy.xyz/{url}" };
        var changed = false;
        foreach (var (key, meta) in templateDefaults)
        {
            if (loaded.Any(item => item.Key == key))
                continue;
            loaded.Add(new ResourceItem
            {
                Key = key,
                Name = meta.Name,
                Group = meta.Group,
                Url = meta.Url,
                Unzip = meta.Unzip,
                Mirrors = meta.Mirrors.Concat(known.Where(k => k.Contains("{url}"))).ToList(),
            });
            changed = true;
        }

        if (changed)
        {
            try
            {
                var payload = new
                {
                    _说明 = "把 url 换成你的直链（浏览器打开就直接下载的那种）；unzip=true 表示下载后自动解压；" +
                            "可以增删条目；group 是界面上的分组标题。",
                    resources = loaded,
                };
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }));
            }
            catch
            {
            }
        }
        return loaded;
    }
}

public sealed record DownloadProgress(long TotalBytes, long ReceivedBytes, long SpeedBytesPerSecond)
{
    public int Percent => TotalBytes > 0 ? (int)Math.Min(100, ReceivedBytes * 100 / TotalBytes) : 0;
}

public static class Downloader
{
    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    static Downloader()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("OPPOUnlockToolbox/0.1.2");
    }

    public static List<string> BuildCandidates(ResourceItem item)
    {
        var candidates = new List<string>();
        void Add(string url)
        {
            if (!string.IsNullOrWhiteSpace(url) && !candidates.Contains(url))
                candidates.Add(url);
        }
        Add(item.Url);
        foreach (var mirror in item.Mirrors)
        {
            if (string.IsNullOrWhiteSpace(mirror))
                continue;
            Add(mirror.Contains("{url}") ? mirror.Replace("{url}", item.Url) : mirror);
        }
        if (item.Url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var prefix in new[] { "https://ghfast.top/", "https://gh-proxy.com/", "https://github.moeyy.xyz/" })
                Add(prefix + item.Url);
        }
        return candidates;
    }

    public static async Task<(string? Result, string Error)> DownloadWithFallbackAsync(
        ResourceItem item, string targetDir, string? fileName = null,
        IProgress<DownloadProgress>? progress = null, IProgress<string>? sourceNote = null,
        CancellationToken cancel = default)
    {
        var candidates = BuildCandidates(item);
        var errors = new List<string>();

        var payloadName = fileName ?? GetUrlFileName(item.Url);
        var payload = AppConfig.FindPayload(payloadName ?? "");
        if (payload != null)
        {
            sourceNote?.Report("随包文件");
            var (payloadResult, payloadError) = UsePayload(item, targetDir, payload);
            if (payloadResult != null)
                return (payloadResult, payloadError);
            errors.Add($"随包文件（{Path.GetFileName(payload)}）：{payloadError}");
        }

        for (var index = 0; index < candidates.Count; index++)
        {
            sourceNote?.Report($"源 {index + 1}/{candidates.Count}");
            var attempt = new ResourceItem
            {
                Key = item.Key,
                Name = item.Name,
                Url = candidates[index],
                Unzip = false,
            };
            var (result, error) = await DownloadAsync(attempt, targetDir, fileName, progress, cancel);
            if (result != null)
            {
                return Extract(item, targetDir, result);
            }
            if (cancel.IsCancellationRequested)
                return (null, "已取消");
            errors.Add($"源 {index + 1}（{HostOf(candidates[index])}）：{error}");
        }
        return (null, "所有源都失败了。" + string.Join("；", errors));
    }

    public static bool HasPayload(ResourceItem item)
    {
        var name = GetUrlFileName(item.Url);
        return name.Length > 0 && AppConfig.FindPayload(name) != null;
    }

    private static (string? Result, string Error) UsePayload(ResourceItem item, string targetDir, string source)
    {
        string dest;
        try
        {
            Directory.CreateDirectory(targetDir);
            dest = Path.Combine(targetDir, Path.GetFileName(source));
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, dest, overwrite: true);
        }
        catch (Exception exc)
        {
            return (null, $"使用随包文件失败：{exc.Message}");
        }
        return Extract(item, targetDir, dest);
    }

    private static (string? Result, string Error) Extract(ResourceItem item, string targetDir, string destPath)
    {
        if (!item.Unzip || !destPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return (destPath, "");
        var extractDir = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(destPath));
        try
        {
            Directory.CreateDirectory(extractDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(destPath, extractDir, overwriteFiles: true);
        }
        catch (Exception exc)
        {
            return (destPath, $"解压失败（zip 已保留在 {destPath}）：{exc.Message}");
        }
        return (extractDir, "");
    }

    private static string HostOf(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return url;
        }
    }

    public static async Task<(string? Result, string Error)> DownloadAsync(
        ResourceItem item, string targetDir, string? fileName = null,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(item.Url))
            return (null, "未配置直链：请点右上角编辑 resources.json 填入 url");
        Directory.CreateDirectory(targetDir);
        var name = fileName ?? GetUrlFileName(item.Url);
        if (string.IsNullOrWhiteSpace(name))
            name = item.Key.Length > 0 ? item.Key + ".bin" : "download.bin";
        var destPath = Path.Combine(targetDir, name);
        var tmpPath = destPath + ".tmp";

        long total;
        try
        {
            using var response = await Http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode)
                return (null, $"服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}");
            total = response.Content.Headers.ContentLength ?? -1;

            var buffer = new byte[256 * 1024];
            long received = 0;
            var lastReport = DateTime.UtcNow;
            var lastReportBytes = 0L;

            await using var stream = await response.Content.ReadAsStreamAsync(cancel);
            await using var file = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                buffer.Length, useAsync: true);

            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            stallCts.CancelAfter(60000);

            int read;
            while ((read = await stream.ReadAsync(buffer, stallCts.Token)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                received += read;
                stallCts.CancelAfter(60000);

                var now = DateTime.UtcNow;
                if ((now - lastReport).TotalMilliseconds >= 200)
                {
                    var dt = (now - lastReport).TotalSeconds;
                    var speed = dt > 0.2 ? (long)(Math.Max(0, received - lastReportBytes) / dt) : 0;
                    lastReport = now;
                    lastReportBytes = received;
                    progress?.Report(new DownloadProgress(total, received, speed));
                }
            }
            progress?.Report(new DownloadProgress(total, received, 0));

            if (total > 0 && received != total)
            {
                TryDelete(tmpPath);
                return (null, $"下载不完整（应为 {FileTools.FmtSize(total)}，实收 {FileTools.FmtSize(received)}）");
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            TryDelete(tmpPath);
            return (null, "已取消");
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmpPath);
            return (null, "下载停滞（60 秒无数据），已中止。检查网络或换直链后重试。");
        }
        catch (Exception exc)
        {
            TryDelete(tmpPath);
            return (null, $"下载失败：{exc.Message}");
        }

        File.Move(tmpPath, destPath, overwrite: true);

        return Extract(item, targetDir, destPath);
    }

    public static string GetUrlFileName(string url)
    {
        try
        {
            var uri = new Uri(url);
            var last = Uri.UnescapeDataString(uri.Segments[^1].TrimEnd('/'));
            return last.Replace('/', '_');
        }
        catch
        {
            return "";
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
