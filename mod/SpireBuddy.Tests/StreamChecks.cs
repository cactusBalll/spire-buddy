using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class StreamChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    internal static async Task Run()
    {
        await Chat();
        await Responses();
        Console.WriteLine("PASS model client SSE streaming for both API formats");
    }

    static async Task Chat()
    {
        var handler = new SseHandler(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"Deal "}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"6 damage."}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_7","type":"function","function":{"name":"lookup_","arguments":"{\"enemy_id\":\""}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"enemy_moves","arguments":"FROG\"}"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":11,"completion_tokens":4,"total_tokens":15}}""",
            "[DONE]");
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var settings = new JsonObject { ["base_url"] = "http://model/v1", ["model"] = "test", ["api_type"] = "chat_completions", ["api_key"] = "k" };
        var history = new JsonArray();
        var response = await new ModelClient(http).Complete(settings, history, new JsonArray(), "stream-chat", CancellationToken.None);
        Check(response.Text("text") == "Deal 6 damage.", "chat streaming accumulates content deltas");
        var calls = response["calls"]!.AsArray();
        Check(calls.Count == 1 && calls[0]!.Text("name") == "lookup_enemy_moves"
            && calls[0]!.Text("arguments") == """{"enemy_id":"FROG"}""", "chat streaming reassembles tool calls split across chunks");
        Check(response["usage"]!.Text("total_tokens") == "15", "chat streaming keeps the include_usage totals");
        Check(history.Count == 1 && history[0]!.Text("role") == "assistant"
            && history[0]!["tool_calls"]!.AsArray()[0]!["function"]!.Text("name") == "lookup_enemy_moves",
            "chat streaming replays the assembled assistant turn");
        Check(handler.Body!.Flag("stream") && handler.Body!["stream_options"]!.Flag("include_usage"), "chat streaming requests usage chunks");
        Check(handler.Accept == "text/event-stream", "streaming sends Accept: text/event-stream");
    }

    static async Task Responses()
    {
        // Incremental events without a terminal event: the client rebuilds the
        // message from text deltas and the call from argument deltas.
        var incremental = new SseHandler(
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","role":"assistant","content":[]}}""",
            """{"type":"response.output_text.delta","output_index":0,"content_index":0,"delta":"Path "}""",
            """{"type":"response.output_text.delta","output_index":0,"content_index":0,"delta":"cleared."}""",
            """{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","call_id":"call_9","name":"lookup_enemy_moves","arguments":""}}""",
            """{"type":"response.function_call_arguments.delta","output_index":1,"delta":"{\"enemy_id\":"}""",
            """{"type":"response.function_call_arguments.delta","output_index":1,"delta":"\"FROG\"}"}""");
        using var http = new HttpClient(incremental) { Timeout = TimeSpan.FromSeconds(30) };
        var settings = new JsonObject { ["base_url"] = "http://model/v1", ["model"] = "test", ["api_type"] = "responses", ["api_key"] = "k" };
        var response = await new ModelClient(http).Complete(settings, new JsonArray(), new JsonArray(), "stream-responses", CancellationToken.None);
        Check(response.Text("text") == "Path cleared.", "responses streaming rebuilds text from output_text deltas");
        var calls = response["calls"]!.AsArray();
        Check(calls.Count == 1 && calls[0]!.Text("arguments") == """{"enemy_id":"FROG"}""", "responses streaming rebuilds a function call from argument deltas");
        Check(incremental.Body!.Flag("stream"), "responses streaming sets stream on the request body");

        // The common case is a terminal response.completed event carrying the
        // full non-streaming object, including usage.
        var terminal = new SseHandler(
            """{"type":"response.completed","response":{"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hi."}]},{"type":"function_call","call_id":"call_1","name":"lookup_enemy_moves","arguments":"{}"}],"usage":{"input_tokens":8,"output_tokens":2}}}""");
        using var terminalHttp = new HttpClient(terminal) { Timeout = TimeSpan.FromSeconds(30) };
        var terminalResponse = await new ModelClient(terminalHttp).Complete(settings, new JsonArray(), new JsonArray(), "stream-terminal", CancellationToken.None);
        Check(terminalResponse.Text("text") == "Hi." && terminalResponse["calls"]!.AsArray().Count == 1, "responses terminal event drives the non-streaming shape");
        Check(terminalResponse["usage"]!.Text("input_tokens") == "8", "responses terminal event keeps usage");
    }

    sealed class SseHandler(params string[] events) : HttpMessageHandler
    {
        internal JsonNode? Body { get; private set; }
        internal string Accept { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            Accept = request.Headers.Accept.ToString();
            var builder = new StringBuilder();
            foreach (var item in events)
            {
                if (item.StartsWith('{')) builder.Append("event: message\n");
                builder.Append("data: ").Append(item).Append('\n').Append('\n');
            }
            var content = new StringContent(builder.ToString(), Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
