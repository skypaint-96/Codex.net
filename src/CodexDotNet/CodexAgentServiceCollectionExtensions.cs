using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace CodexDotNet;

public static class CodexAgentServiceCollectionExtensions
{
    public static IServiceCollection AddCodex(
        this IServiceCollection services,
        CodexOptions? options = null,
        CodexAgentOptions? agentOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(options ?? CodexOptions.FromEnvironment());
        services.AddSingleton(agentOptions ?? new CodexAgentOptions());
        services.AddHttpClient<ICodexAuthManager, CodexAuthManager>()
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<ICodexClient, CodexClient>()
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        return services;
    }

    public static IServiceCollection AddCodexAgent(
        this IServiceCollection services,
        CodexOptions? options = null,
        CodexAgentOptions? agentOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddCodex(options, agentOptions);
        services.AddSingleton<CodexAgent>();
        services.AddSingleton<AIAgent>(provider => provider.GetRequiredService<CodexAgent>());
        return services;
    }

    public static IServiceCollection AddCodexChatClient(
        this IServiceCollection services,
        CodexOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddCodex(options);
        services.AddSingleton<CodexAgentFrameworkChatClient>();
        services.AddSingleton<IChatClient>(provider => provider.GetRequiredService<CodexAgentFrameworkChatClient>());
        return services;
    }
}
