using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace M3Undle.Web.Api;

internal static class OpenApiConfiguration
{
    internal const string ManagementDocName = "management";

    internal static IServiceCollection AddM3UndleOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(ManagementDocName, options =>
        {
            options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0;

            options.AddDocumentTransformer((doc, ctx, ct) =>
            {
                doc.Info = new OpenApiInfo
                {
                    Title = "M3Undle Management API",
                    Version = "v1",
                    Description =
                        "REST API for managing providers, profiles, channels, EPG sources, " +
                        "downstream integrations, and site settings in M3Undle. " +
                        "All endpoints require authentication via the UiAccess policy.",
                };

                var components = doc.Components ??= new OpenApiComponents();
                components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                components.SecuritySchemes["cookieAuth"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Cookie,
                    Name = ".AspNetCore.Identity.Application",
                    Description = "ASP.NET Core Identity cookie. Log in via /Account/Login to obtain it.",
                };

                return Task.CompletedTask;
            });

            // Stamp every management operation with the cookieAuth security requirement.
            // This avoids having to annotate every individual route.
            options.AddOperationTransformer((op, ctx, ct) =>
            {
                op.Security ??= [];
                op.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("cookieAuth", hostDocument: null, externalResource: null)] = [],
                });
                return Task.CompletedTask;
            });
        });

        return services;
    }

    internal static IEndpointRouteBuilder MapM3UndleOpenApiEndpoints(
        this IEndpointRouteBuilder app, IWebHostEnvironment env)
    {
        if (!env.IsDevelopment())
            return app;

        app.MapOpenApi("/openapi/{documentName}.json")
           .ExcludeFromDescription();

        app.MapScalarApiReference("/scalar", options =>
        {
            options.WithTitle("M3Undle API Reference");
            options.WithOpenApiRoutePattern("/openapi/{documentName}.json");
            options.WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
            options.Authentication = new ScalarAuthenticationOptions
            {
                PreferredSecuritySchemes = ["cookieAuth"],
            };
        }).ExcludeFromDescription();

        return app;
    }
}
