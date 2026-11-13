using System;
using System.Collections.Generic;

namespace Core.Application.DTOs;

/// <summary>
/// Daily sales summary report
/// </summary>
public class DailySalesReportDto
{
    public DateTime Date { get; set; }
    public int TotalOrders { get; set; }
    public decimal TotalSales { get; set; }
    public decimal TotalTax { get; set; }
    public decimal TotalDiscounts { get; set; }
    public decimal NetSales { get; set; }
    public Dictionary<string, decimal> SalesByPaymentMethod { get; set; } = new();
    public List<ProductSalesDto> TopProducts { get; set; } = new();
}

/// <summary>
/// Product sales summary
/// </summary>
public class ProductSalesDto
{
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public decimal QuantitySold { get; set; }
    public decimal TotalRevenue { get; set; }
    public int OrderCount { get; set; }
}

/// <summary>
/// Cash shift closing report (Z-Report)
/// </summary>
public class ZReportDto
{
    public Guid CashShiftId { get; set; }
    public int ShiftNumber { get; set; }
    public string CashierName { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    
    public decimal OpeningAmount { get; set; }
    public decimal ClosingAmount { get; set; }
    public decimal ExpectedAmount { get; set; }
    public decimal Difference { get; set; }
    
    public int TotalOrders { get; set; }
    public decimal GrossSales { get; set; }
    public decimal TotalTax { get; set; }
    public decimal TotalDiscounts { get; set; }
    public decimal NetSales { get; set; }
    
    public Dictionary<string, int> OrdersByPaymentMethod { get; set; } = new();
    public Dictionary<string, decimal> SalesByPaymentMethod { get; set; } = new();
    
    public List<ProductSalesDto> TopProducts { get; set; } = new();
    public string? Notes { get; set; }
}

/// <summary>
/// Sales period summary (custom date range)
/// </summary>
public class SalesPeriodReportDto
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int TotalOrders { get; set; }
    public decimal TotalSales { get; set; }
    public decimal AverageSale { get; set; }
    public List<DailySalesReportDto> DailySummaries { get; set; } = new();
    public List<ProductSalesDto> TopProducts { get; set; } = new();
}