using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Moq;
using Square.Models;
using Square.Apis;
using System.Threading.Tasks;

public class UnitTests
{
    [Fact]
    public async Task GetUnpaidInvoicesOlderThan30Days_ReturnsCorrectInvoices()
    {
        // Arrange
        var mockInvoicesApi = new Mock<IInvoicesApi>();
        var invoices = new List<Invoice>
        {
            new Invoice { Id = "1", Status = "UNPAID", DueDate = DateTime.UtcNow.AddDays(-31).ToString("yyyy-MM-dd") },
            new Invoice { Id = "2", Status = "PAID", DueDate = DateTime.UtcNow.AddDays(-40).ToString("yyyy-MM-dd") },
            new Invoice { Id = "3", Status = "UNPAID", DueDate = DateTime.UtcNow.AddDays(-20).ToString("yyyy-MM-dd") }
        };

        mockInvoicesApi.Setup(api => api.ListInvoicesAsync())
            .ReturnsAsync(new ListInvoicesResponse { Invoices = invoices });

        // Act
        var result = await Program.GetUnpaidInvoicesOlderThan30Days(mockInvoicesApi.Object);

        // Assert
        Assert.Single(result);
        Assert.Equal("1", result.First().Id);
    }

    [Fact]
    public async Task AddLateFeeAndUpdateInvoice_AddsLateFeeSuccessfully()
    {
        // Arrange
        var mockInvoicesApi = new Mock<IInvoicesApi>();
        var invoice = new Invoice
        {
            Id = "1",
            PaymentRequests = new List<InvoicePaymentRequest>
            {
                new InvoicePaymentRequest { TotalAmountMoney = new Money { Amount = 5000, Currency = "USD" } }
            }
        };

        mockInvoicesApi.Setup(api => api.UpdateInvoiceAsync(It.IsAny<string>(), It.IsAny<Invoice>()))
            .ReturnsAsync(new UpdateInvoiceResponse { Invoice = invoice });

        // Act
        await Program.AddLateFeeAndUpdateInvoice(mockInvoicesApi.Object, invoice);

        // Assert
        Assert.Equal(2, invoice.PaymentRequests.Count);
        Assert.Equal(1000, invoice.PaymentRequests.Last().TotalAmountMoney.Amount);
        Assert.Equal("Late Fee", invoice.PaymentRequests.Last().Description);
    }
}