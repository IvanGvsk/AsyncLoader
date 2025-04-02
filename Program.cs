using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class Config
{
    public string? SavePath { get; set; }
    public string? UrlsFilePath { get; set; }
    public int MaxAtOneTime { get; set; } = 5;
    public int HttpTimeout { get; set; } = 2000;
    public int BufferSize { get; set; } = 8192;
    public int RetryCount { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 2000;
}

public static class Logger
{
    private static readonly object _lock = new();
    public static void LogError(Exception ex)
    {
        string message = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | Ошибка: {ex.Message}\n";
        lock (_lock)
        {
            File.AppendAllText("errors.log", message);
        }
        Console.WriteLine(message);
    }
}

public static class FileDownloader
{
    private static HttpClient httpClient = new();

    public static void ConfigureHttpClient(Config config)
    {
        httpClient.Timeout = TimeSpan.FromMilliseconds(config.HttpTimeout);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; FileDownloader/1.0)");
        Console.WriteLine($"[Настройка HttpClient] Таймаут: {config.HttpTimeout} мс");
    }

    public static async Task DownloadAsync(string url, Config config, int row, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        string fileName = Path.GetFileName(url);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"file_{row}_{Guid.NewGuid()}";
        }

        string filePath = Path.Combine(config.SavePath, fileName);

        for (int attempt = 1; attempt <= config.RetryCount; attempt++)
        {
            try
            {
                Console.WriteLine($"[Попытка {attempt}/{config.RetryCount}] Скачивание {url} в {filePath}");

                using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();

                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, config.BufferSize, true);
                await response.Content.CopyToAsync(fileStream, token);

                Console.WriteLine($"Файл {fileName} успешно скачан.");
                return;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"Отмена скачивания {url}");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Попытка {attempt} не удалась: {ex.Message}. Повтор через {config.RetryDelayMs}мс.");
                if (attempt == config.RetryCount) throw;
                await Task.Delay(config.RetryDelayMs, token); 
            }
        }
    }
}

class Program
{
    static async Task Main()
    {
        Config? config = LoadConfig("appconfig.json");
        Console.WriteLine($"{config.SavePath} {config.HttpTimeout}");
        if (config == null) return;

        FileDownloader.ConfigureHttpClient(config);

        if (string.IsNullOrWhiteSpace(config.SavePath))
        {
            Console.WriteLine("Ошибка: Путь для сохранения файлов не задан.");
            return;
        }

        Directory.CreateDirectory(config.SavePath);

        string urlsFilePath = config.UrlsFilePath ?? "urls.txt";
        if (!File.Exists(urlsFilePath))
        {
            Console.WriteLine("Ошибка: Файл с URL-ами не найден.");
            return;
        }

        string[] urls = await File.ReadAllLinesAsync(urlsFilePath);
        Console.WriteLine($"Загружено {urls.Length} URL-ов из файла.");

        using var semaphore = new SemaphoreSlim(config.MaxAtOneTime);
        using var cts = new CancellationTokenSource();

        var tasks = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select((url, index) => DownloadWithSemaphore(url, config, index, semaphore, cts.Token))
            .ToList();

        Console.WriteLine($"Запущено {tasks.Count} задач на скачивание.");
        await Task.WhenAll(tasks);
        Console.WriteLine("Все задачи завершены.");
    }

    static Config? LoadConfig(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Console.WriteLine("Ошибка: Конфигурационный файл не найден.");
                return null;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true 
            };

            string configContent = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Config>(configContent, options) ?? new Config();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Ошибка загрузки конфигурации: " + ex.Message);
            return null;
        }
    }

    private static async Task DownloadWithSemaphore(string url, Config config, int row, SemaphoreSlim semaphore, CancellationToken token)
    {
        await semaphore.WaitAsync(token);
        try
        {
            await FileDownloader.DownloadAsync(url, config, row, token);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
