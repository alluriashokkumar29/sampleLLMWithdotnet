using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// strongly-typed config
builder.Services.Configure<LlamaOptions>(builder.Configuration.GetSection("Llama"));

// choose backend
var llamaOpts = builder.Configuration.GetSection("Llama").Get<LlamaOptions>() ?? new();
if (llamaOpts.Provider?.Equals("Ollama", StringComparison.OrdinalIgnoreCase) == true)
{
    builder.Services.AddHttpClient<IModelClient, OllamaClient>(client =>
    {
        client.BaseAddress = new Uri(llamaOpts.Ollama!.BaseUrl!);
        client.Timeout = TimeSpan.FromSeconds(90);
    });
}
else
{
    builder.Services.AddHttpClient<IModelClient, OpenAICompatibleClient>(client =>
    {
        client.BaseAddress = new Uri(llamaOpts.OpenAICompatible!.BaseUrl!);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", llamaOpts.OpenAICompatible!.ApiKey);
        client.Timeout = TimeSpan.FromSeconds(90);
    });
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Llama API", Version = "v1" });
});

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();

// health
app.MapGet("/healthz", () => Results.Ok(new { ok = true, ts = DateTimeOffset.UtcNow }));

// Simple generate endpoint
app.MapPost("/generate", async (GenerateRequest req, IModelClient client, IConfiguration cfg) =>
{
    var opts = cfg.GetSection("Llama").Get<LlamaOptions>() ?? new();
    var sys = req.SystemPrompt ?? "You are a helpful assistant.";
    var user = req.Prompt ?? "";

    var messages = new List<ChatMessage>
    {
        new("system", sys),
        new("user", user)
    };

    var result = await client.ChatAsync(new ChatInput
    {
        Model = req.Model ?? opts.Model ?? opts.OpenAICompatible?.Model ?? opts.Ollama?.Model,
        Messages = messages,
        MaxTokens = req.MaxTokens ?? opts.Defaults?.MaxTokens ?? 512,
        Temperature = req.Temperature ?? opts.Defaults?.Temperature ?? 0.7,
        TopP = req.TopP ?? opts.Defaults?.TopP ?? 1.0
    });

    return Results.Ok(result);
});

// Optional: OpenAI-compatible route for easy client reuse
app.MapPost("/v1/chat/completions", async (OpenAIChatRequest req, IModelClient client, IConfiguration cfg) =>
{
    var opts = cfg.GetSection("Llama").Get<LlamaOptions>() ?? new();

    var result = await client.ChatAsync(new ChatInput
    {
        Model = req.Model ?? opts.Model ?? opts.OpenAICompatible?.Model ?? opts.Ollama?.Model,
        Messages = req.Messages.Select(m => new ChatMessage(m.Role, m.Content)).ToList(),
        MaxTokens = req.MaxTokens ?? opts.Defaults?.MaxTokens ?? 512,
        Temperature = (double?)(req.Temperature ?? (opts.Defaults?.Temperature ?? 0.7)),
        TopP = (double?)(req.TopP ?? (opts.Defaults?.TopP ?? 1.0))
    });

    // Map to OpenAI-style response
    var mapped = new OpenAIChatResponse
    {
        Id = $"chatcmpl_{Guid.NewGuid()}",
        Object = "chat.completion",
        Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        Model = result.Model ?? (opts.Model ?? ""),
        Choices = new[]
        {
            new OpenAIChatChoice
            {
                Index = 0,
                FinishReason = result.FinishReason ?? "stop",
                Message = new OpenAIMessage { Role = "assistant", Content = result.Content ?? "" }
            }
        }
    };

    return Results.Ok(mapped);
});

app.Run();

#region Contracts & Clients

record GenerateRequest(
    string? Prompt,
    string? SystemPrompt,
    string? Model,
    int? MaxTokens,
    double? Temperature,
    double? TopP
);

class LlamaOptions
{
    public string? Provider { get; set; } // "Ollama" | "OpenAICompatible"
    public string? Model { get; set; }    // optional default
    public OllamaOptions? Ollama { get; set; }
    public OpenAICompatibleOptions? OpenAICompatible { get; set; }
    public DefaultsOptions? Defaults { get; set; }
}
class OllamaOptions
{
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
}
class OpenAICompatibleOptions
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
}
class DefaultsOptions
{
    public int MaxTokens { get; set; } = 512;
    public double Temperature { get; set; } = 0.7;
    public double TopP { get; set; } = 1.0;
}

// Internal neutral chat model
record ChatInput
{
    public string? Model { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();
    public int? MaxTokens { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
}
record ChatMessage(string Role, string Content);
record ChatResult
{
    public string? Model { get; set; }
    public string? Content { get; set; }
    public string? FinishReason { get; set; }
    public object? Raw { get; set; }
}

// Interface
interface IModelClient
{
    Task<ChatResult> ChatAsync(ChatInput input, CancellationToken ct = default);
}

// Ollama client Self hosted
class OllamaClient : IModelClient
{
    private readonly HttpClient _http;
    private readonly LlamaOptions _opts;
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OllamaClient(HttpClient http, Microsoft.Extensions.Options.IOptions<LlamaOptions> opts)
    {
        _http = http;
        _opts = opts.Value;
    }

    public async Task<ChatResult> ChatAsync(ChatInput input, CancellationToken ct = default)
    {
        var reqBody = new
        {
            model = input.Model ?? _opts.Model ?? _opts.Ollama?.Model ?? "llama3.1:8b",
            messages = input.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            options = new
            {
                temperature = input.Temperature,
                num_predict = input.MaxTokens,
                top_p = input.TopP
            },
            stream = false
        };

        var resp = await _http.PostAsync("v1/api/chat",
            new StringContent(JsonSerializer.Serialize(reqBody, _json), Encoding.UTF8, "application/json"), ct);

        resp.EnsureSuccessStatusCode();
        using var s = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

        // Ollama returns: { message: { content }, model, done_reason }
        var root = doc.RootElement;
        var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
        string? content = null;
        if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
            content = c.GetString();
        var finish = root.TryGetProperty("done_reason", out var dr) ? dr.GetString() : "stop";

        return new ChatResult { Model = model, Content = content, FinishReason = finish, Raw = JsonDocument.Parse(root.GetRawText()) };
    }
}

// OpenAI-compatible client
class OpenAICompatibleClient : IModelClient
{
    private readonly HttpClient _http;
    private readonly LlamaOptions _opts;
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAICompatibleClient(HttpClient http, Microsoft.Extensions.Options.IOptions<LlamaOptions> opts)
    {
        _http = http;
        _opts = opts.Value;
    }

    public async Task<ChatResult> ChatAsync(ChatInput input, CancellationToken ct = default)
    {
        var body = new
        {
            model = input.Model ?? _opts.Model ?? _opts.OpenAICompatible?.Model,
            temperature = input.Temperature,
            max_tokens = input.MaxTokens,
            top_p = input.TopP,
            messages = input.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream = false
        };

        var resp = await _http.PostAsync("/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json"), ct);

        resp.EnsureSuccessStatusCode();
        using var s = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

        // Expect: choices[0].message.content, model, choices[0].finish_reason
        var root = doc.RootElement;
        var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
        var choice0 = root.GetProperty("choices")[0];
        var content = choice0.GetProperty("message").GetProperty("content").GetString();
        var finish = choice0.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : "stop";

        return new ChatResult { Model = model, Content = content, FinishReason = finish, Raw = JsonDocument.Parse(root.GetRawText()) };
    }
}

#endregion

#region OpenAI-compatible DTOs for the optional route
public class OpenAIChatRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("messages")] public List<OpenAIMessage> Messages { get; set; } = new();
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("temperature")] public float? Temperature { get; set; }
    [JsonPropertyName("top_p")] public float? TopP { get; set; }
}
public class OpenAIMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}
public class OpenAIChatResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = default!;
    [JsonPropertyName("object")] public string Object { get; set; } = "chat.completion";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = default!;
    [JsonPropertyName("choices")] public OpenAIChatChoice[] Choices { get; set; } = default!;
}
public class OpenAIChatChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    [JsonPropertyName("message")] public OpenAIMessage Message { get; set; } = default!;
}
#endregion
