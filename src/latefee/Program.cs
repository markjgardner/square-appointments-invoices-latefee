using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Square;
using Square;
using Square.Invoices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        // Initialize Key Vault client
        var keyVaultUrl = Environment.GetEnvironmentVariable("KEYVAULT_URI");
        if (string.IsNullOrEmpty(keyVaultUrl))
        {
            Console.WriteLine("Error: KEYVAULT_URI is not set.");
            return;
        }
        var secretClient = new SecretClient(new Uri(keyVaultUrl), new DefaultAzureCredential());

        // Retrieve the Square refresh token from Key Vault
        var squareRefreshToken = await secretClient.GetSecretValueAsync("SquareRefreshToken");
        var squareAccessToken = await secretClient.GetSecretValueAsync("SquareAccessToken");

        // Exchange the refresh token for a new access token
        var baseUrl = Environment.GetEnvironmentVariable("SQUARE_BASE_URL");
        var clientId = Environment.GetEnvironmentVariable("SQUARE_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("SQUARE_CLIENT_SECRET");

        using (var httpClient = new HttpClient())
        {
            squareAccessToken = await RefreshTokenAsync(baseUrl, clientId, clientSecret, squareRefreshToken, httpClient, secretClient);
        }

        // Initialize Square client with the new access token
        var squareClient = new SquareClient(squareAccessToken,
            new ClientOptions
            {
                BaseUrl = baseUrl
            });

        try
        {
            // Query unpaid invoices older than 30 days
            var invoices = await GetUnpaidInvoicesOlderThan30Days(squareClient);

            if (!invoices.Any())
            {
                Console.WriteLine("No unpaid invoices older than 30 days found.");
                return;
            }

            // Add $10 late fee to each invoice and update them in parallel
            var updateTasks = invoices.Select(invoice => AddLateFeeAndUpdateInvoice(squareClient, invoice));
            await Task.WhenAll(updateTasks);

            Console.WriteLine("All invoices updated successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error: {ex.Message}");
        }
    }

    private static async Task<List<Invoice>> GetUnpaidInvoicesOlderThan30Days(SquareClient square)
    {
        var result = new List<Invoice>();
        var request = new SearchInvoicesRequest(
            Query: new InvoiceQuery
            {
                Filter: new InvoiceFilter
                {
                    Status = new List<string> { "UNPAID" },
                    DueDateRange = new DateRange
                    {
                        StartAt = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd"),
                        EndAt = DateTime.UtcNow.ToString("yyyy-MM-dd")
                    }
                },
                Sort: new List<SortField> { new SortField("DUE_DATE", SortOrder.ASC) }
            }
        )
        var response = await square.Invoices.GetAsync()

        foreach (var invoice in response.Invoices)
        {
            if (invoice.Status == "UNPAID" && invoice.DueDate.HasValue &&
                DateTime.Parse(invoice.DueDate) < DateTime.UtcNow.AddDays(-30))
            {
                result.Add(invoice);
            }
        }

        return result;
    }

    private static async Task AddLateFeeAndUpdateInvoice(InvoicesApi invoicesApi, Invoice invoice)
    {
        try
        {
            // Add $10 late fee to the invoice
            var updatedAmount = invoice.PaymentRequests.Sum(pr => pr.TotalAmountMoney.Amount) + 1000;
            invoice.PaymentRequests.Add(new InvoicePaymentRequest
            {
                TotalAmountMoney = new Money { Amount = 1000, Currency = "USD" },
                Description = "Late Fee"
            });

            // Update the invoice
            await invoicesApi.UpdateInvoiceAsync(invoice.Id, invoice);
            Console.WriteLine($"Invoice {invoice.Id} updated with late fee.");
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"Failed to update invoice {invoice.Id}: {ex.Message}");
        }
    }

    private static async Task<string> RefreshTokenAsync(string baseUrl, string clientId, string clientSecret, string refreshToken, HttpClient httpClient, SecretClient secretClient)
   
    {
        var parms = new Dictionary<string, string>
        {
            { "client_id", clientId },
            { "client_secret", clientSecret },
            { "grant_type", "refresh_token" },
            { "refresh_token", refreshToken }
        };

        var response = await httpClient.PostAsync($"{baseUrl}/oauth2/token", new FormUrlEncodedContent(parms));

        if (response.IsSuccessStatusCode)
        {
            // Store the access token and refresh token securely in Azure KeyVault
            var responseContent = await response.Content.ReadAsStringAsync();
            var responseJson = System.Text.Json.JsonDocument.Parse(responseContent).RootElement;

            var accessToken = responseJson.GetProperty("access_token").GetString();
            var newRefreshToken = responseJson.GetProperty("refresh_token").GetString();

            await secretClient.SetSecretAsync("SquareAccessToken", accessToken);
            await secretClient.SetSecretAsync("SquareRefreshToken", newRefreshToken);

            return accessToken;
        }
        else
        {
            throw new InvalidOperationException($"Failed to refresh token: {response.StatusCode}");
        }
    }
}

public static class SecretClientExtensions
{
    public static async Task<string> GetSecretValueAsync(this SecretClient secretClient, string secretName)
    {
        try
        {
            var secret = await secretClient.GetSecretAsync(secretName);
            if (secret.Value == null || string.IsNullOrEmpty(secret.Value.Value))
            {
                throw new InvalidOperationException($"Error: Secret value '{secretName}' is empty.");
            }

            return secret.Value.Value;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error retrieving secret '{secretName}' from Key Vault: {ex.Message}", ex);
        }
    }
}