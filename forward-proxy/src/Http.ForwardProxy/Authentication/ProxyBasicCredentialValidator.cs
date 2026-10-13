// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;

namespace DamianH.Http.ForwardProxy.Authentication;

/// <summary>Validates a proxy client's Basic credentials.</summary>
/// <param name="context">The proxy request context.</param>
/// <param name="userName">The user-id from <c>Proxy-Authorization</c>.</param>
/// <param name="password">The password from <c>Proxy-Authorization</c>.</param>
/// <returns><see langword="true"/> when the credentials are valid.</returns>
public delegate ValueTask<bool> ProxyBasicCredentialValidator(HttpContext context, string userName, string password);
