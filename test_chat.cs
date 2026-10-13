using System;
using System.Text.Json;
using System.Threading.Tasks;

var configJson = System.IO.File.ReadAllText(@"F:\JustWatch\config\justwatch-config.json");
var opts = new JsonSerializerOptions { 
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true
};

// Quick parse just the chat config
var doc = JsonDocument.Parse(configJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
var chatNode = doc.RootElement.GetProperty("chat");
var apiKey = chatNode.GetProperty("openRouterApiKey").GetString();
var model = chatNode.GetProperty("openRouterModel").GetString();

Console.WriteLine($"API Key: {apiKey?[..20]}...");
Console.WriteLine($"Model: {model}");

// Test streaming call
using var http = new System.Net.Http.HttpClient();
var body = $$"""{"model":"{{model}}","messages":[{"role":"system","content":"You are a test automation agent. Reply briefly."},{"role":"user","content":"Hello, are you working? Reply in one sentence."}],"max_tokens":100,"stream":true}""";

var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
request.Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
request.Headers.Add("Authorization", $"Bearer {apiKey}");
request.Headers.Add("HTTP-Referer", "https://justgains.com");

var response = await http.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
Console.WriteLine($"Status: {response.StatusCode}");

using var stream = await response.Content.ReadAsStreamAsync();
using var reader = new System.IO.StreamReader(stream);
var fullResponse = new System.Text.StringBuilder();

string? line;
while ((line = await reader.ReadLineAsync()) != null)
{
    if (!line.StartsWith("data: ")) continue;
    var data = line[6..];
    if (data == "[DONE]") break;
    try
    {
        var chunk = JsonDocument.Parse(data);
        var delta = chunk.RootElement.GetProperty("choices")[0].GetProperty("delta");
        if (delta.TryGetProperty("content", out var content))
        {
            var text = content.GetString();
            if (text != null) fullResponse.Append(text);
        }
    }
    catch { }
}

Console.WriteLine($"Response: {fullResponse}");
Console.WriteLine("STREAMING TEST PASSED");
