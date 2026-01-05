using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnthropicTextContent = Anthropic.SDK.Messaging.TextContent;
using Function = Anthropic.SDK.Common.Function;

namespace Agent.Core.Services;

public sealed class AnthropicChatCompletionService(
    string apiKey,
    string modelId = AnthropicModels.Claude41Opus,
    ILogger? logger = null) : IChatCompletionService
{
    private readonly AnthropicClient _client = new(apiKey);

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>
    {
        ["ModelId"] = modelId,
        ["ServiceType"] = "Anthropic"
    };

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken ct = default)
    {
        var messages = ConvertMessages(chatHistory);
        var systemPrompt = ExtractSystemPrompt(chatHistory);
        var tools = kernel is not null ? ConvertTools(kernel) : null;

        var request = new MessageParameters
        {
            Model = modelId,
            MaxTokens = GetMaxTokens(executionSettings),
            Messages = messages,
            Temperature = GetTemperature(executionSettings)
        };

        if (!string.IsNullOrEmpty(systemPrompt)) request.System = [new SystemMessage(systemPrompt)];

        if (tools is { Count: > 0 })
        {
            request.Tools = tools;
            request.ToolChoice = new ToolChoice { Type = ToolChoiceType.Auto };
            logger?.LogInformation("Registered {Count} tools", tools.Count);
        }

        var response = await _client.Messages.GetClaudeMessageAsync(request, ct);

        if (response.StopReason == "tool_use" && kernel is not null)
        {
            return await HandleToolCallsAsync(response, chatHistory, kernel, executionSettings, ct);
        }

        return [CreateMessageContent(response)];
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new MessageParameters
        {
            Model = modelId,
            MaxTokens = GetMaxTokens(executionSettings),
            Messages = ConvertMessages(chatHistory),
            Temperature = GetTemperature(executionSettings),
            Stream = true
        };
        
        var systemPrompt = ExtractSystemPrompt(chatHistory);
        if (!string.IsNullOrEmpty(systemPrompt)) request.System = [new SystemMessage(systemPrompt)];

        await foreach (var response in _client.Messages.StreamClaudeMessageAsync(request, ct))
        {
            if (response.Delta?.Text is not null)
            {
                yield return new StreamingChatMessageContent(AuthorRole.Assistant, response.Delta.Text) { ModelId = modelId };
            }
        }
    }

    private async Task<IReadOnlyList<ChatMessageContent>> HandleToolCallsAsync(
        MessageResponse response,
        ChatHistory chatHistory,
        Kernel kernel,
        PromptExecutionSettings? settings,
        CancellationToken ct,
        int round = 1,
        List<Message>? accumulated = null)
    {
        if (round > 50) throw new InvalidOperationException("Max tool rounds reached");

        var toolResults = new List<ContentBase>();
        var assistantContent = new List<ContentBase>();

        foreach (var block in response.Content)
        {
            if (block is ToolUseContent toolUse)
            {
                assistantContent.Add(toolUse);
                var result = await InvokeFunctionAsync(kernel, toolUse, ct);
                if (result.Length > 4000) result = result[..4000] + "... (truncated)";
                
                toolResults.Add(new ToolResultContent { ToolUseId = toolUse.Id, Content = [new AnthropicTextContent { Text = result }] });
            }
            else if (block is AnthropicTextContent text) assistantContent.Add(text);
        }

        var newMessages = accumulated ?? ConvertMessages(chatHistory);
        newMessages.Add(new Message { Role = RoleType.Assistant, Content = assistantContent });
        newMessages.Add(new Message { Role = RoleType.User, Content = toolResults });

        var followUpReq = new MessageParameters
        {
            Model = modelId,
            MaxTokens = GetMaxTokens(settings),
            Messages = newMessages,
            Temperature = GetTemperature(settings),
            Tools = ConvertTools(kernel),
            ToolChoice = new ToolChoice { Type = ToolChoiceType.Auto }
        };

        var system = ExtractSystemPrompt(chatHistory);
        if (!string.IsNullOrEmpty(system)) followUpReq.System = [new SystemMessage(system)];

        var followUpResp = await _client.Messages.GetClaudeMessageAsync(followUpReq, ct);

        if (followUpResp.StopReason == "tool_use")
            return await HandleToolCallsAsync(followUpResp, chatHistory, kernel, settings, ct, round + 1, newMessages);

        return [CreateMessageContent(followUpResp)];
    }

    private async Task<string> InvokeFunctionAsync(Kernel kernel, ToolUseContent toolUse, CancellationToken ct)
    {
        try
        {
            KernelFunction? function = null;
            foreach (var p in kernel.Plugins)
            {
                if (p.TryGetFunction(toolUse.Name, out function)) break;
                
                var parts = toolUse.Name.Split('_', 2);
                if (parts.Length > 1 && p.Name == parts[0] && p.TryGetFunction(parts[1], out function)) break;
            }

            if (function is null) return JsonSerializer.Serialize(new { error = $"Function {toolUse.Name} not found" });

            var args = new KernelArguments();
            if (toolUse.Input is JsonNode node)
            {
                foreach (var kvp in node.AsObject())
                {
                    args[kvp.Key] = kvp.Value?.ToString();
                }
            }

            var res = await function.InvokeAsync(kernel, args, ct);
            return res.ToString() ?? "null";
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static ChatMessageContent CreateMessageContent(MessageResponse response)
    {
        var text = string.Join("", response.Content.OfType<AnthropicTextContent>().Select(c => c.Text));
        return new ChatMessageContent(AuthorRole.Assistant, text)
        {
            ModelId = response.Model,
            Metadata = new Dictionary<string, object?> { ["StopReason"] = response.StopReason }
        };
    }

    private static List<Message> ConvertMessages(ChatHistory history) =>
        history.Where(m => m.Role != AuthorRole.System)
               .Select(m => new Message
               {
                   Role = m.Role == AuthorRole.User ? RoleType.User : RoleType.Assistant,
                   Content = [new AnthropicTextContent { Text = m.Content ?? "" }]
               }).ToList();

    private static string ExtractSystemPrompt(ChatHistory history) =>
        string.Join("\n\n", history.Where(m => m.Role == AuthorRole.System).Select(m => m.Content));

    private static List<Anthropic.SDK.Common.Tool> ConvertTools(Kernel kernel)
    {
        var tools = new List<Anthropic.SDK.Common.Tool>();
        foreach (var plugin in kernel.Plugins)
        foreach (var func in plugin)
        {
            var props = new JsonObject();
            var required = new JsonArray();
            foreach (var p in func.Metadata.Parameters)
            {
                props[p.Name] = new JsonObject { ["type"] = "string", ["description"] = p.Description ?? "" };
                if (p.IsRequired) required.Add(p.Name);
            }
            
            var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0) schema["required"] = required;

            tools.Add(new Function($"{plugin.Name}_{func.Name}", func.Description ?? "", schema));
        }
        return tools;
    }

    private static int GetMaxTokens(PromptExecutionSettings? s) => 
        s?.ExtensionData?.TryGetValue("max_tokens", out var v) == true ? Convert.ToInt32(v) : 4096;

    private static decimal GetTemperature(PromptExecutionSettings? s) => 
        s?.ExtensionData?.TryGetValue("temperature", out var v) == true ? Convert.ToDecimal(v) : 0.1m;
}
