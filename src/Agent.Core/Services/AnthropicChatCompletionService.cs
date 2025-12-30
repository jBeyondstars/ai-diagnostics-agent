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

/// <summary>
/// Custom IChatCompletionService implementation for Anthropic Claude.
/// Bridges Semantic Kernel with the Anthropic SDK v5.8+.
/// </summary>
public sealed class AnthropicChatCompletionService : IChatCompletionService
{
    private readonly AnthropicClient _client;
    private readonly string _modelId;
    private readonly ILogger? _logger;

    private readonly Dictionary<string, object?> _attributes = new();

    public IReadOnlyDictionary<string, object?> Attributes => _attributes;

    public AnthropicChatCompletionService(
        string apiKey,
        string modelId = AnthropicModels.Claude41Opus,
        ILogger? logger = null)
    {
        _client = new AnthropicClient(apiKey);
        _modelId = modelId;
        _logger = logger;

        _attributes["ModelId"] = modelId;
        _attributes["ServiceType"] = "Anthropic";
    }

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        var messages = ConvertToAnthropicMessages(chatHistory);
        var systemPrompt = ExtractSystemPrompt(chatHistory);
        var tools = kernel is not null ? ConvertKernelFunctionsToTools(kernel) : null;

        var request = new MessageParameters
        {
            Model = _modelId,
            MaxTokens = GetMaxTokens(executionSettings),
            Messages = messages,
            Temperature = GetTemperature(executionSettings)
        };

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            request.System = [new SystemMessage(systemPrompt)];
        }

        if (tools is not null && tools.Count > 0)
        {
            request.Tools = tools;
            request.ToolChoice = new ToolChoice { Type = ToolChoiceType.Auto };
            _logger?.LogInformation("Registered {ToolCount} tools for Claude", tools.Count);
        }

        _logger?.LogDebug("Sending request to Claude {Model} with {MessageCount} messages",
            _modelId, messages.Count);

        var response = await _client.Messages.GetClaudeMessageAsync(request, cancellationToken);

        if (response.StopReason == "tool_use" && kernel is not null)
        {
            return await HandleToolCallsAsync(response, chatHistory, kernel, executionSettings, cancellationToken);
        }

        var content = ExtractTextContent(response);

        return
        [
            new ChatMessageContent(AuthorRole.Assistant, content)
            {
                ModelId = _modelId,
                Metadata = new Dictionary<string, object?>
                {
                    ["StopReason"] = response.StopReason,
                    ["InputTokens"] = response.Usage?.InputTokens,
                    ["OutputTokens"] = response.Usage?.OutputTokens
                }
            }
        ];
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = ConvertToAnthropicMessages(chatHistory);
        var systemPrompt = ExtractSystemPrompt(chatHistory);
        var tools = kernel is not null ? ConvertKernelFunctionsToTools(kernel) : null;

        var request = new MessageParameters
        {
            Model = _modelId,
            MaxTokens = GetMaxTokens(executionSettings),
            Messages = messages,
            Temperature = GetTemperature(executionSettings),
            Stream = true
        };

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            request.System = [new SystemMessage(systemPrompt)];
        }

        if (tools is not null && tools.Count > 0)
        {
            request.Tools = tools;
            request.ToolChoice = new ToolChoice { Type = ToolChoiceType.Auto };
        }

        await foreach (var response in _client.Messages.StreamClaudeMessageAsync(request, cancellationToken))
        {
            if (response.Delta?.Text is not null)
            {
                yield return new StreamingChatMessageContent(
                    AuthorRole.Assistant,
                    response.Delta.Text)
                {
                    ModelId = _modelId
                };
            }
        }
    }

    private const int MaxToolResultLength = 4000;
    private const int MaxToolCallRounds = 50;

    private async Task<IReadOnlyList<ChatMessageContent>> HandleToolCallsAsync(
        MessageResponse response,
        ChatHistory chatHistory,
        Kernel kernel,
        PromptExecutionSettings? executionSettings,
        CancellationToken cancellationToken,
        int currentRound = 1,
        List<Message>? accumulatedMessages = null)
    {
        _logger?.LogInformation("Tool call round {Round}/{Max}", currentRound, MaxToolCallRounds);

        var toolResultContents = new List<ContentBase>();
        var assistantContent = new List<ContentBase>();

        foreach (var block in response.Content)
        {
            if (block is ToolUseContent toolUse)
            {
                assistantContent.Add(toolUse);

                _logger?.LogInformation("Claude calling tool: {ToolName}", toolUse.Name);

                var result = await InvokeKernelFunctionAsync(kernel, toolUse, cancellationToken);

                // Truncate long results to save tokens
                if (result.Length > MaxToolResultLength)
                {
                    result = result[..MaxToolResultLength] + "\n... (truncated)";
                }

                toolResultContents.Add(new ToolResultContent
                {
                    ToolUseId = toolUse.Id,
                    Content = [new AnthropicTextContent { Text = result }]
                });
            }
            else if (block is AnthropicTextContent textContent)
            {
                assistantContent.Add(textContent);
            }
        }

        var newMessages = accumulatedMessages ?? ConvertToAnthropicMessages(chatHistory);

        newMessages.Add(new Message
        {
            Role = RoleType.Assistant,
            Content = assistantContent
        });

        newMessages.Add(new Message
        {
            Role = RoleType.User,
            Content = toolResultContents
        });

        var followUpRequest = new MessageParameters
        {
            Model = _modelId,
            MaxTokens = GetMaxTokens(executionSettings),
            Messages = newMessages,
            Temperature = GetTemperature(executionSettings),
            Tools = ConvertKernelFunctionsToTools(kernel),
            ToolChoice = new ToolChoice { Type = ToolChoiceType.Auto }
        };

        var systemPrompt = ExtractSystemPrompt(chatHistory);
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            followUpRequest.System = [new SystemMessage(systemPrompt)];
        }

        var followUpResponse = await _client.Messages.GetClaudeMessageAsync(followUpRequest, cancellationToken);

        if (followUpResponse.StopReason == "tool_use")
        {
            if (currentRound >= MaxToolCallRounds)
            {
                _logger?.LogWarning("Max tool call rounds ({Max}) reached, forcing completion", MaxToolCallRounds);
                followUpRequest.Tools = null;
                followUpRequest.ToolChoice = null;
                followUpResponse = await _client.Messages.GetClaudeMessageAsync(followUpRequest, cancellationToken);
            }
            else
            {
                return await HandleToolCallsAsync(followUpResponse, chatHistory, kernel, executionSettings,
                    cancellationToken, currentRound + 1, newMessages);
            }
        }

        return
        [
            new ChatMessageContent(AuthorRole.Assistant, ExtractTextContent(followUpResponse))
            {
                ModelId = _modelId
            }
        ];
    }

    private async Task<string> InvokeKernelFunctionAsync(
        Kernel kernel,
        ToolUseContent toolUse,
        CancellationToken cancellationToken)
    {
        try
        {
            var parts = toolUse.Name.Split(['_', '-'], 2);
            var pluginName = parts.Length > 1 ? parts[0] : string.Empty;
            var functionName = parts.Length > 1 ? parts[1] : toolUse.Name;

            KernelFunction? function = null;

            if (!string.IsNullOrEmpty(pluginName) && kernel.Plugins.TryGetPlugin(pluginName, out var plugin))
            {
                plugin.TryGetFunction(functionName, out function);
            }

            if (function is null)
            {
                foreach (var p in kernel.Plugins)
                {
                    if (p.TryGetFunction(toolUse.Name, out function) ||
                        p.TryGetFunction(functionName, out function))
                    {
                        break;
                    }
                }
            }

            if (function is null)
            {
                _logger?.LogWarning("Function not found: {FunctionName}", toolUse.Name);
                return JsonSerializer.Serialize(new { error = $"Function '{toolUse.Name}' not found" });
            }

            var arguments = new KernelArguments();
            if (toolUse.Input is JsonNode jsonInput)
            {
                foreach (var prop in jsonInput.AsObject())
                {
                    arguments[prop.Key] = prop.Value?.ToString();
                }
            }

            var result = await function.InvokeAsync(kernel, arguments, cancellationToken);
            return result.ToString() ?? "null";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error invoking function {FunctionName}", toolUse.Name);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static List<Anthropic.SDK.Common.Tool>? ConvertKernelFunctionsToTools(Kernel kernel)
    {
        var tools = new List<Anthropic.SDK.Common.Tool>();

        foreach (var plugin in kernel.Plugins)
        {
            foreach (var function in plugin)
            {
                var toolName = $"{plugin.Name}_{function.Name}";
                var description = function.Description ?? $"Function {function.Name} from plugin {plugin.Name}";

                var properties = new JsonObject();
                var required = new JsonArray();

                foreach (var param in function.Metadata.Parameters)
                {
                    var paramSchema = new JsonObject
                    {
                        ["type"] = GetJsonType(param.ParameterType),
                        ["description"] = param.Description ?? param.Name
                    };

                    properties[param.Name] = paramSchema;

                    if (param.IsRequired)
                    {
                        required.Add(param.Name);
                    }
                }

                var inputSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties
                };

                if (required.Count > 0)
                {
                    inputSchema["required"] = required;
                }

                var tool = new Function(toolName, description, inputSchema);
                tools.Add(tool);
            }
        }

        return tools.Count > 0 ? tools : null;
    }

    private static string GetJsonType(Type? type)
    {
        if (type is null) return "string";

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => "boolean",
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16
                or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 => "integer",
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal => "number",
            _ => "string"
        };
    }

    private static List<Message> ConvertToAnthropicMessages(ChatHistory chatHistory)
    {
        var messages = new List<Message>();

        foreach (var msg in chatHistory)
        {
            if (msg.Role == AuthorRole.System)
                continue;

            var role = msg.Role == AuthorRole.User ? RoleType.User : RoleType.Assistant;

            messages.Add(new Message
            {
                Role = role,
                Content = [new AnthropicTextContent { Text = msg.Content ?? string.Empty }]
            });
        }

        return messages;
    }

    private static string ExtractSystemPrompt(ChatHistory chatHistory)
    {
        var systemMessages = chatHistory
            .Where(m => m.Role == AuthorRole.System)
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrEmpty(c));

        return string.Join("\n\n", systemMessages);
    }

    private static string ExtractTextContent(MessageResponse response)
    {
        return string.Join("", response.Content
            .OfType<AnthropicTextContent>()
            .Select(c => c.Text));
    }

    private static int GetMaxTokens(PromptExecutionSettings? settings)
    {
        if (settings?.ExtensionData?.TryGetValue("max_tokens", out var maxTokens) == true)
        {
            return Convert.ToInt32(maxTokens);
        }
        return 4096;
    }

    private static decimal GetTemperature(PromptExecutionSettings? settings)
    {
        if (settings?.ExtensionData?.TryGetValue("temperature", out var temp) == true)
        {
            return Convert.ToDecimal(temp);
        }
        return 0.1m;
    }
}
