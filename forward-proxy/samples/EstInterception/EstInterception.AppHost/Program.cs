// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

var builder = DistributedApplication.CreateBuilder(args);

var password = builder.AddParameter("est-password", secret: true);
var interceptedHosts = builder.AddParameter("intercepted-hosts", value: "httpbin.org");
var caCertificatePath = Path.Combine(Path.GetTempPath(), "http-libs-est-interception-ca.pem");

var caHost = builder.AddProject<Projects.EstInterception_CaHost>("ca-host")
    .WithHttpsEndpoint(port: 5443, name: "https")
    .WithHttpEndpoint(port: 5444, name: "http")
    .WithEnvironment("Est__Password", password)
    .WithEnvironment("Interception__CaCertificatePath", caCertificatePath)
    .WithEnvironment("Interception__AllowedHosts", interceptedHosts)
    .WithHttpHealthCheck("/healthz", endpointName: "http");

builder.AddProject<Projects.EstInterception_Proxy>("proxy")
    .WithReference(caHost.GetEndpoint("https"))
    .WaitFor(caHost)
    .WithEnvironment("Est__Url", caHost.GetEndpoint("https"))
    .WithEnvironment("Est__User", "proxy")
    .WithEnvironment("Est__Password", password)
    .WithEnvironment("Est__CaCertificatePath", caCertificatePath)
    .WithEnvironment("Interception__Hosts", interceptedHosts)
    .WithHttpEndpoint(port: 8080, name: "proxy", isProxied: false);

builder.Build().Run();
