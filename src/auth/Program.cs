using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SecretClient>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var keyVaultUri = new Uri(configuration["KeyVault:Uri"]);
    return new SecretClient(keyVaultUri, new DefaultAzureCredential());
});

builder.Services.AddHttpClient();

var app = builder.Build();

var stateStore = new ConcurrentDictionary<string, string>();

app.MapGet("/auth", (IConfiguration configuration) =>
{
    var baseUrl = configuration["Square:BaseUrl"];
    var clientId = configuration["Square:ClientId"];
    var scopes = configuration["Square:Scope"] ?? "MERCHANT_PROFILE_READ"; // Default scope if not set
    var session = "false"; 
    stateStore["CSRF"] = Guid.NewGuid().ToString(); // create and store CSRF token

    var query = new QueryBuilder
    {
        { "client_id", clientId },
        { "scope", scopes },
        { "session", session },
        { "state", stateStore["state"] }
    };

    var redirectUrl = $"{baseUrl}/oauth2/authorize{query.ToQueryString()}";
    
    return Results.Redirect(redirectUrl);
});

app.MapGet("/auth/callback", async (string state, string code, SecretClient secretClient, IConfiguration configuration, HttpClient httpClient) =>
{
    if (!stateStore["CSRF"].Equals(state, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("Failed CSRF validation.");
    }

    try
    {
        var baseUrl = configuration["Square:BaseUrl"];
        var parms = new Dictionary<string, string>
        {
            { "client_id", configuration["Square:ClientId"] },
            { "client_secret", configuration["Square:ClientSecret"] },
            { "code", code },
            { "grant_type", "authorization_code" },
            { "scopes", configuration["Square:Scopes"] ?? "MERCHANT_PROFILE_READ" }
        };

        var response = await httpClient.PostAsync($"{baseUrl}/oauth2/token", new FormUrlEncodedContent(parms));

        if (response.IsSuccessStatusCode)
        {
            // Store the access token and refresh token securely in Azure KeyVault
            var responseContent = await response.Content.ReadAsStringAsync();
            var responseJson = System.Text.Json.JsonDocument.Parse(responseContent).RootElement;

            var accessToken = responseJson.GetProperty("access_token").GetString();
            var refreshToken = responseJson.GetProperty("refresh_token").GetString();

            await secretClient.SetSecretAsync("SquareAccessToken", accessToken);
            await secretClient.SetSecretAsync("SquareRefreshToken", refreshToken);

            return Results.Ok();
        }
        else
        {
            return Results.BadRequest(response.StatusCode.ToString());
        }
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.Run();
