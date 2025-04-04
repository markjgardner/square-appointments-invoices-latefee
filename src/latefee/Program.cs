using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Square;
using Square.Models;
using Square.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
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
        var refreshTokenSecretName = "SquareRefreshToken";
        KeyVaultSecret refreshTokenSecret;
        try
        {
            refreshTokenSecret = await secretClient.GetSecretAsync(refreshTokenSecretName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error retrieving refresh token from Key Vault: {ex.Message}");
            return;
        }

        string squareRefreshToken = refreshTokenSecret.Value;
        if (string.IsNullOrEmpty(squareRefreshToken))
        {
            Console.WriteLine("Error: Square refresh token is empty.");
            return;
        }

        // Exchange the refresh token for a new access token
        var oAuthApi = new Square.Apis.OAuthApi();
        Square.Models.ObtainTokenResponse tokenResponse;
        try
        {
            tokenResponse = await oAuthApi.ObtainTokenAsync(new Square.Models.ObtainTokenRequest
            {
                ClientId = Environment.GetEnvironmentVariable("SQUARE_CLIENT_ID"),
                ClientSecret = Environment.GetEnvironmentVariable("SQUARE_CLIENT_SECRET"),
                GrantType = "refresh_token",
                RefreshToken = squareRefreshToken
            });
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"Error exchanging refresh token: {ex.Message}");
            return;
        }

        string squareAccessToken = tokenResponse.AccessToken;
        if (string.IsNullOrEmpty(squareAccessToken))
        {
            Console.WriteLine("Error: Failed to retrieve Square access token.");
            return;
        }

        // Update the refresh token in Key Vault
        try
        {
            await secretClient.SetSecretAsync(refreshTokenSecretName, tokenResponse.RefreshToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating refresh token in Key Vault: {ex.Message}");
            return;
        }

        // Initialize Square client with the new access token
        var client = new SquareClient.Builder()
            .AccessToken(squareAccessToken)
            .Environment(Square.Environment.Production)
            .Build();

        try
        {
            // Query unpaid invoices older than 30 days
            var invoicesApi = client.InvoicesApi;
            var invoices = await GetUnpaidInvoicesOlderThan30Days(invoicesApi);

            if (!invoices.Any())
            {
                Console.WriteLine("No unpaid invoices older than 30 days found.");
                return;
            }

            // Add $10 late fee to each invoice and update them in parallel
            var updateTasks = invoices.Select(invoice => AddLateFeeAndUpdateInvoice(invoicesApi, invoice));
            await Task.WhenAll(updateTasks);

            Console.WriteLine("All invoices updated successfully.");
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"Square API error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error: {ex.Message}");
        }
    }

    private static async Task<List<Invoice>> GetUnpaidInvoicesOlderThan30Days(InvoicesApi invoicesApi)
    {
        var result = new List<Invoice>();
        var response = await invoicesApi.ListInvoicesAsync();

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
}
