using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AetherChat.Core;

public sealed class AiClient : ICompletionClient, IDisposable
{
    private readonly HttpClient http;
    public AiClient(HttpMessageHandler? handler = null) => http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public static Uri Endpoint(CompletionOptions options)
    {
        if (!Uri.TryCreate(options.BaseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(root.UserInfo) || !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment))
            throw new InvalidOperationException("接口地址需为 http(s) 基础地址，例如 http://127.0.0.1:1234/v1。" );
        if (root.Scheme == "http" && !root.IsLoopback) throw new InvalidOperationException("远程接口请使用 HTTPS；HTTP 仅限本机回环地址。" );
        if (string.IsNullOrWhiteSpace(options.Model)) throw new InvalidOperationException("请先填写服务支持的模型 ID。" );
        var suffix = options.Protocol switch { ApiProtocol.OpenAiResponses => "responses", ApiProtocol.AnthropicMessages => "messages", _ => "chat/completions" };
        if (root.AbsolutePath.TrimEnd('/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("填写基础地址即可，请去掉末尾的 /" + suffix + "。" );
        return new Uri(root, suffix);
    }

    public async Task<string> CompleteAsync(CompletionOptions options, IReadOnlyList<Turn> messages, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(options));
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            if (options.Protocol == ApiProtocol.AnthropicMessages) request.Headers.Add("x-api-key", options.ApiKey.Trim());
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());
        }
        var turns = messages.Select(t => new { role = t.Role, content = t.Content }).ToArray();
        object body;
        switch (options.Protocol)
        {
            case ApiProtocol.OpenAiResponses:
                body = new { model = options.Model.Trim(), instructions = options.SystemPrompt, input = turns, max_output_tokens = options.MaxTokens, store = false, stream = false };
                break;
            case ApiProtocol.AnthropicMessages:
                request.Headers.Add("anthropic-version", "2023-06-01");
                body = new { model = options.Model.Trim(), system = options.SystemPrompt, messages = turns, max_tokens = options.MaxTokens, stream = false };
                break;
            default:
                body = new { model = options.Model.Trim(), messages = new[] { new { role = "system", content = options.SystemPrompt } }.Concat(turns), max_tokens = options.MaxTokens, stream = false };
                break;
        }
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"接口返回 HTTP {(int)response.StatusCode}。请检查地址、模型、密钥及额度。" );
            // A broken local server must not allocate an unbounded response inside the game.
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > 2_000_000) throw new InvalidOperationException("模型响应过大，已停止读取。" );
                buffer.Write(bytes, 0, read);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            var result = Parse(options.Protocol, json.RootElement);
            if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException("模型未返回文字；可检查模型 ID 或提高输出 token 上限。" );
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InvalidOperationException("请求超时，可提高超时时间或使用更快的模型。" ); }
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接模型服务，请检查服务是否启动及网络设置。" ); }
        catch (JsonException) { throw new InvalidOperationException("接口响应不是有效 JSON，请检查协议和基础地址。" ); }
        catch (KeyNotFoundException) { throw new InvalidOperationException("接口返回格式不匹配，请检查所选协议。" ); }
    }

    public static string Parse(ApiProtocol protocol, JsonElement root)
    {
        if (protocol == ApiProtocol.ChatCompletions)
        {
            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return "";
            var content = choices[0].GetProperty("message").GetProperty("content");
            return content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : "";
        }
        if (protocol == ApiProtocol.AnthropicMessages)
            return string.Concat(root.GetProperty("content").EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
        return string.Concat(root.GetProperty("output").EnumerateArray().Where(o => o.GetProperty("type").GetString() == "message").SelectMany(o => o.GetProperty("content").EnumerateArray()).Where(b => b.GetProperty("type").GetString() == "output_text").Select(b => b.GetProperty("text").GetString()));
    }
    public void Dispose() => http.Dispose();
}
