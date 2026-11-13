using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

public class ReportService : IReportService
{
    private readonly PosDbContext _dbContext;

    public ReportService(PosDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DailySalesReportDto> GetDailySalesReportAsync(DateTime date, CancellationToken ct = default)
    {
        var startOfDay = date.Date;
        var endOfDay = startOfDay.AddDays(1);

        var orders = await _dbContext.Orders
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .Where(o => o.CreatedAt >= startOfDay && o.CreatedAt < endOfDay && o.Status == OrderStatus.Completed)
            .ToListAsync(ct);

        var report = new DailySalesReportDto
        {
            Date = date.Date,
            TotalOrders = orders.Count,
            TotalSales = orders.Sum(o => o.Total),
            TotalTax = orders.Sum(o => o.TaxTotal),
            TotalDiscounts = orders.Sum(o => o.DiscountTotal),
            NetSales = orders.Sum(o => o.Subtotal - o.DiscountTotal)
        };

        var paymentGroups = orders
            .SelectMany(o => o.Payments)
            .GroupBy(p => p.Method.ToString())
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        report.SalesByPaymentMethod = paymentGroups;

        var productSales = orders
            .SelectMany(o => o.Items)
            .GroupBy(i => new { i.ProductId, i.ProductName })
            .Select(g => new ProductSalesDto
            {
                ProductId = g.Key.ProductId,
                ProductName = g.Key.ProductName,
                Sku = "",
                QuantitySold = g.Sum(i => i.Quantity),
                TotalRevenue = g.Sum(i => i.Total),
                OrderCount = g.Count()
            })
            .OrderByDescending(p => p.TotalRevenue)
            .Take(10)
            .ToList();
        report.TopProducts = productSales;

        return report;
    }

    public async Task<ZReportDto> GetZReportAsync(Guid cashShiftId, CancellationToken ct = default)
    {
        var cashShift = await _dbContext.CashShifts.FindAsync(new object[] { cashShiftId }, ct);
        if (cashShift == null)
            throw new InvalidOperationException($"Cash shift {cashShiftId} not found.");

        var orders = await _dbContext.Orders
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .Where(o => o.CashShiftId == cashShiftId && o.Status == OrderStatus.Completed)
            .ToListAsync(ct);

        var report = new ZReportDto
        {
            CashShiftId = cashShift.Id,
            ShiftNumber = cashShift.ShiftNumber,
            CashierName = cashShift.CashierName,
            OpenedAt = cashShift.OpenedAt,
            ClosedAt = cashShift.ClosedAt,
            OpeningAmount = cashShift.OpeningAmount,
            ClosingAmount = cashShift.ClosingAmount,
            ExpectedAmount = cashShift.ExpectedAmount,
            Difference = cashShift.Difference,
            TotalOrders = orders.Count,
            GrossSales = orders.Sum(o => o.Total),
            TotalTax = orders.Sum(o => o.TaxTotal),
            TotalDiscounts = orders.Sum(o => o.DiscountTotal),
            NetSales = orders.Sum(o => o.Subtotal - o.DiscountTotal),
            Notes = cashShift.Notes
        };

        var paymentStats = orders
            .SelectMany(o => o.Payments)
            .GroupBy(p => p.Method.ToString())
            .Select(g => new { Method = g.Key, Count = g.Count(), Total = g.Sum(p => p.Amount) })
            .ToList();
        report.OrdersByPaymentMethod = paymentStats.ToDictionary(x => x.Method, x => x.Count);
        report.SalesByPaymentMethod = paymentStats.ToDictionary(x => x.Method, x => x.Total);

        var productSales = orders
            .SelectMany(o => o.Items)
            .GroupBy(i => new { i.ProductId, i.ProductName })
            .Select(g => new ProductSalesDto
            {
                ProductId = g.Key.ProductId,
                ProductName = g.Key.ProductName,
                Sku = "",
                QuantitySold = g.Sum(i => i.Quantity),
                TotalRevenue = g.Sum(i => i.Total),
                OrderCount = g.Count()
            })
            .OrderByDescending(p => p.TotalRevenue)
            .Take(10)
            .ToList();
        report.TopProducts = productSales;

        return report;
    }

    public async Task<SalesPeriodReportDto> GetSalesPeriodReportAsync(DateTime startDate, DateTime endDate, CancellationToken ct = default)
    {
        var start = startDate.Date;
        var end = endDate.Date.AddDays(1);

        var orders = await _dbContext.Orders
            .Include(o => o.Items)
            .Where(o => o.CreatedAt >= start && o.CreatedAt < end && o.Status == OrderStatus.Completed)
            .ToListAsync(ct);

        var report = new SalesPeriodReportDto
        {
            StartDate = startDate.Date,
            EndDate = endDate.Date,
            TotalOrders = orders.Count,
            TotalSales = orders.Sum(o => o.Total),
            AverageSale = orders.Any() ? orders.Average(o => o.Total) : 0
        };

        var dailyGroups = orders
            .GroupBy(o => o.CreatedAt.Date)
            .Select(g => new DailySalesReportDto
            {
                Date = g.Key,
                TotalOrders = g.Count(),
                TotalSales = g.Sum(o => o.Total),
                TotalTax = g.Sum(o => o.TaxTotal),
                TotalDiscounts = g.Sum(o => o.DiscountTotal),
                NetSales = g.Sum(o => o.Subtotal - o.DiscountTotal)
            })
            .OrderBy(d => d.Date)
            .ToList();
        report.DailySummaries = dailyGroups;

        var productSales = orders
            .SelectMany(o => o.Items)
            .GroupBy(i => new { i.ProductId, i.ProductName })
            .Select(g => new ProductSalesDto
            {
                ProductId = g.Key.ProductId,
                ProductName = g.Key.ProductName,
                Sku = "",
                QuantitySold = g.Sum(i => i.Quantity),
                TotalRevenue = g.Sum(i => i.Total),
                OrderCount = g.Count()
            })
            .OrderByDescending(p => p.TotalRevenue)
            .Take(20)
            .ToList();
        report.TopProducts = productSales;

        return report;
    }

    public async Task<List<ProductSalesDto>> GetTopProductsAsync(DateTime startDate, DateTime endDate, int topCount = 10, CancellationToken ct = default)
    {
        var start = startDate.Date;
        var end = endDate.Date.AddDays(1);

        return await _dbContext.OrderItems
            .Include(i => i.Order)
            .Where(i => i.Order != null && i.Order.CreatedAt >= start && i.Order.CreatedAt < end && i.Order.Status == OrderStatus.Completed)
            .GroupBy(i => new { i.ProductId, i.ProductName })
            .Select(g => new ProductSalesDto
            {
                ProductId = g.Key.ProductId,
                ProductName = g.Key.ProductName,
                Sku = "",
                QuantitySold = g.Sum(i => i.Quantity),
                TotalRevenue = g.Sum(i => i.Total),
                OrderCount = g.Count()
            })
            .OrderByDescending(p => p.TotalRevenue)
            .Take(topCount)
            .ToListAsync(ct);
    }
}
