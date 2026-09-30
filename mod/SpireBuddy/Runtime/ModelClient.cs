using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

internal sealed class ModelClient
{
    readonly HttpClient http;
    internal ModelClient(HttpClient http) => this.http = http;

    // Sends one API request. Complete asks for an SSE stream; a provider that
    // answers with plain JSON is still parsed, so both transports share the
    // same downstream response shape.
    internal async Task<JsonNode> Request(JsonObject settings, string path, JsonNode? body, CancellationToken ct, bool stream = false, bool responses = false)
    {
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, settings.Text("base_url").TrimEnd('/') + path);
        var key = settings.Text("api_key");
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (stream) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (body != null) request.Content = new StringContent(body.WriteString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode is 400 or 413 or 422 &&
                new[] { "context_length", "context window", "maximum context", "context limit", "too many tokens", "max_tokens", "prompt is too long" }
                    .Any(term => error.Contains(term, StringComparison.OrdinalIgnoreCase)))
                throw new ContextLimitException();
            throw new InvalidOperationException($"API request failed ({(int)response.StatusCode}). Check endpoint, model and credentials.");
        }
        if (stream && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            // HttpClient.Timeout stops applying once the headers are read, so keep
            // a bounded body read: a stalled stream must not hang a decision.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(http.Timeout);
            await using var content = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var reader = new StreamReader(content, Encoding.UTF8);
            return responses ? await ReadResponses(reader, deadline.Token) : await ReadChat(reader, deadline.Token);
        }
        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("API returned empty JSON.");
    }

    internal static JsonObject Message(string role, string text) => new() { ["role"] = role, ["content"] = text };
    internal async Task<JsonObject> Complete(JsonObject settings, JsonArray history, JsonArray tools, string cacheKey, CancellationToken ct)
    {
        bool responses = settings.Text("api_type") == "responses";
        var payload = (JsonArray)history.DeepClone()!;
        if (!responses)
            // The developer role is Responses-only; chat completions providers such as
            // Z.ai reject it with "Incorrect role information" (error 1214).
            foreach (var message in payload.OfType<JsonObject>().ToList())
                if (message.Text("role") == "developer") message["role"] = "system";
        var body = new JsonObject { ["model"] = settings.Text("model"), [responses ? "input" : "messages"] = payload, ["stream"] = true };
        if (!responses) body["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (tools.Count > 0)
        {
            body["tools"] = responses ? tools.DeepClone() : new JsonArray(tools.OfType<JsonObject>().Select(t => (JsonNode)new JsonObject { ["type"] = "function", ["function"] = new JsonObject(t.Where(p => p.Key != "type").Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))) }).ToArray());
            // Read-only inspections may be batched in one turn; the runtime executes
            // them serially and rejects a premature take_action bundled with them.
            body["parallel_tool_calls"] = true;
        }
        var effort = settings.Text("reasoning_effort");
        if (effort.Length > 0 && effort != "default") body[responses ? "reasoning" : "reasoning_effort"] = responses ? new JsonObject { ["effort"] = effort } : JsonValue.Create(effort);
        body["prompt_cache_key"] = cacheKey;
        if (settings.Text("model").StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase)) body["prompt_cache_options"] = new JsonObject { ["ttl"] = "30m" };
        if (responses) { body["store"] = false; body["include"] = new JsonArray("reasoning.encrypted_content"); }
        var response = await Request(settings, responses ? "/responses" : "/chat/completions", body, ct, stream: true, responses: responses);
        var calls = new JsonArray(); var text = new StringBuilder();
        if (responses)
        {
            foreach (var item in response["output"].Items())
            {
                history.Add(item.DeepClone());
                if (item.Text("type") == "function_call") calls.Add(new JsonObject { ["id"] = item.Text("call_id"), ["name"] = item.Text("name"), ["arguments"] = item.Text("arguments") });
                foreach (var content in item["content"].Items()) if (content.Text("type") == "output_text") text.Append(content.Text("text"));
            }
        }
        else
        {
            var message = response["choices"]?[0]?["message"] ?? throw new InvalidOperationException("API response has no message.");
            history.Add(message.DeepClone()); text.Append(message.Text("content"));
            foreach (var call in message["tool_calls"].Items()) calls.Add(new JsonObject { ["id"] = call.Text("id"), ["name"] = call["function"].Text("name"), ["arguments"] = call["function"].Text("arguments") });
        }
        return new JsonObject { ["calls"] = calls, ["text"] = text.ToString(), ["usage"] = response["usage"]?.DeepClone() };
    }
    internal static void Result(JsonArray history, JsonObject settings, string id, JsonNode result)
    {
        history.Add(settings.Text("api_type") == "responses"
            ? new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = result.WriteString() }
            : new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result.WriteString() });
    }
    internal static JsonObject Tool(string name, string description, params string[] arguments)
    {
        var properties = new JsonObject(); foreach (var arg in arguments) properties[arg] = new JsonObject { ["type"] = "string" };
        return new JsonObject { ["type"] = "function", ["name"] = name, ["description"] = description, ["strict"] = true,
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(arguments.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()), ["additionalProperties"] = false } };
    }

    // The SSE payload of one event, or null for comments, blank separators and
    // other field types. A single event may carry multiple data lines; OpenAI
    // sends one, so only the first non-empty line is parsed.
    static string? EventData(string line)
    {
        if (line.Length == 0 || line[0] == ':') return null;
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var payload = line[5..].TrimStart();
        return payload.Length == 0 ? null : payload;
    }

    // Responses streams a terminal response.completed event carrying the full
    // non-streaming object. When a proxy omits it, the pieces are rebuilt from
    // output_item.done plus any text/argument deltas.
    static async Task<JsonNode> ReadResponses(TextReader reader, CancellationToken ct)
    {
        var items = new SortedDictionary<int, JsonObject>();
        var text = new Dictionary<(int Output, int Part), StringBuilder>();
        var done = new HashSet<int>();
        JsonNode? terminal = null;
        string? line;
        while (terminal == null && (line = await reader.ReadLineAsync(ct)) != null)
        {
            var data = EventData(line);
            if (data == null) continue;
            if (data == "[DONE]") break;
            JsonNode eventNode;
            try { eventNode = JsonNode.Parse(data)!; } catch (System.Text.Json.JsonException) { continue; }
            switch (eventNode.Text("type"))
            {
                case "response.output_item.added":
                    if (eventNode["item"] is JsonObject added) items[eventNode.Num("output_index") ?? items.Count] = (JsonObject)added.DeepClone();
                    break;
                case "response.output_item.done":
                    if (eventNode["item"] is JsonObject finished)
                    {
                        var index = eventNode.Num("output_index") ?? items.Count;
                        items[index] = (JsonObject)finished.DeepClone();
                        done.Add(index);
                    }
                    break;
                case "response.output_text.delta":
                    var key = (eventNode.Num("output_index") ?? 0, eventNode.Num("content_index") ?? 0);
                    if (!text.TryGetValue(key, out var burst)) text[key] = burst = new StringBuilder();
                    burst.Append(eventNode.Text("delta"));
                    break;
                case "response.function_call_arguments.delta":
                    if (items.TryGetValue(eventNode.Num("output_index") ?? 0, out var call))
                        call["arguments"] = call.Text("arguments") + eventNode.Text("delta");
                    break;
                case "response.completed":
                case "response.incomplete":
                    if (eventNode["response"] is JsonNode complete) terminal = complete.DeepClone();
                    break;
            }
        }
        if (terminal != null) return terminal;
        var output = new JsonArray();
        foreach (var (index, item) in items)
        {
            if (!done.Contains(index) && text.TryGetValue((index, 0), out var body))
                item["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = body.ToString() });
            output.Add(item);
        }
        if (output.Count == 0) throw new InvalidOperationException("Streaming response ended without output.");
        return new JsonObject { ["output"] = output };
    }

    // Chat Completions streams role/content fragments and indexed tool calls;
    // usage arrives in a final include_usage chunk. Rebuild one message object.
    static async Task<JsonNode> ReadChat(TextReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, JsonObject>();
        JsonNode? usage = null; string finish = "";
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            var data = EventData(line);
            if (data == null) continue;
            if (data == "[DONE]") break;
            JsonNode eventNode;
            try { eventNode = JsonNode.Parse(data)!; } catch (System.Text.Json.JsonException) { continue; }
            if (eventNode["usage"] is JsonNode reported) usage = reported.DeepClone();
            foreach (var choice in (eventNode["choices"]?.AsArray() ?? new JsonArray()).OfType<JsonObject>())
            {
                var reason = choice.Text("finish_reason");
                if (reason.Length > 0) finish = reason;
                var delta = choice["delta"];
                if (delta == null) continue;
                text.Append(delta.Text("content"));
                foreach (var call in (delta["tool_calls"]?.AsArray() ?? new JsonArray()).OfType<JsonObject>())
                {
                    var index = call.Num("index") ?? calls.Count;
                    if (!calls.TryGetValue(index, out var accumulated))
                        calls[index] = accumulated = new JsonObject { ["id"] = "", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "", ["arguments"] = "" } };
                    if (call["id"]?.ToString() is { Length: > 0 } id) accumulated["id"] = id;
                    accumulated["type"] = call.Text("type", accumulated.Text("type"));
                    if (call["function"] is JsonNode function)
                    {
                        var target = accumulated["function"]!.AsObject();
                        target["name"] = target.Text("name") + function.Text("name");
                        target["arguments"] = target.Text("arguments") + function.Text("arguments");
                    }
                }
            }
        }
        var message = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
        if (calls.Count > 0) message["tool_calls"] = new JsonArray(calls.Select(pair => (JsonNode)pair.Value.DeepClone()).ToArray());
        var result = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) };
        if (finish.Length > 0) result["choices"]![0]!["finish_reason"] = finish;
        if (usage != null) result["usage"] = usage;
        return result;
    }
}

internal sealed class ContextLimitException : InvalidOperationException
{
    internal ContextLimitException() : base("The model context limit was exceeded.") { }
}
