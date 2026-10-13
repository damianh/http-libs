// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>
/// Evaluates the proxy authorization policy against a real or synthetic <see cref="HttpContext"/>, with the
/// destination as the resource. On failure the challenge or forbid result is written to the context's response.
/// </summary>
internal sealed class ProxyAuthorizer(IOptions<ForwardProxyOptions> options, IServiceProvider services)
{
    private readonly ForwardProxyOptions _options = options.Value;

    public bool IsAnonymous => _options.AuthorizationPolicy is null && _options.AuthorizationPolicyName is null;

    /// <summary>Returns <see langword="true"/> when the request may proceed.</summary>
    public async Task<bool> AuthorizeAsync(HttpContext context, ForwardProxyDestination destination)
    {
        var policy = await ResolvePolicyAsync().ConfigureAwait(false);
        if (policy is null)
        {
            return true;
        }

        var evaluator = context.RequestServices.GetRequiredService<IPolicyEvaluator>();
        var authentication = await evaluator.AuthenticateAsync(policy, context).ConfigureAwait(false);
        var authorization = await evaluator.AuthorizeAsync(policy, authentication, context, destination).ConfigureAwait(false);
        if (authorization.Succeeded)
        {
            return true;
        }

        if (authorization.Challenged)
        {
            await ForEachScheme(policy, scheme => context.ChallengeAsync(scheme)).ConfigureAwait(false);
        }
        else
        {
            await ForEachScheme(policy, scheme => context.ForbidAsync(scheme)).ConfigureAwait(false);
        }

        if (_options.TranslateChallenges)
        {
            TranslateChallenge(context.Response);
        }

        return false;
    }

    /// <summary>Converts an origin-style 401 challenge into a proxy 407 challenge.</summary>
    internal static void TranslateChallenge(HttpResponse response)
    {
        if (response.StatusCode == StatusCodes.Status401Unauthorized)
        {
            response.StatusCode = StatusCodes.Status407ProxyAuthenticationRequired;
        }

        if (response.Headers.Remove(HeaderNames.WWWAuthenticate, out var challenges))
        {
            response.Headers.Append(HeaderNames.ProxyAuthenticate, challenges);
        }
    }

    private async Task<AuthorizationPolicy?> ResolvePolicyAsync()
    {
        if (_options.AuthorizationPolicy is { } policy)
        {
            return policy;
        }

        if (_options.AuthorizationPolicyName is not { } name)
        {
            return null;
        }

        var provider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        return await provider.GetPolicyAsync(name).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Forward proxy authorization policy '{name}' was not found.");
    }

    private static async Task ForEachScheme(AuthorizationPolicy policy, Func<string?, Task> action)
    {
        if (policy.AuthenticationSchemes.Count == 0)
        {
            await action(null).ConfigureAwait(false);
            return;
        }

        foreach (var scheme in policy.AuthenticationSchemes)
        {
            await action(scheme).ConfigureAwait(false);
        }
    }
}
