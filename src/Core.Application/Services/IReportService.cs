using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;

namespace Core.Application.Services;

/// <summary>
/// Service for generating sales reports and analytics
/// </summary>
public interface IReportService
{
    /// <summary>
    /// Generates daily sales report for a specific date
    /// </summary>
    Task<DailySalesReportDto> GetDailySalesReportAsync(DateTime date, CancellationToken ct = default);

    /// <summary>
    /// Generates Z-Report (cash shift closing report)
    /// </summary>
    Task<ZReportDto> GetZReportAsync(Guid cashShiftId, CancellationToken ct = default);

    /// <summary>
    /// Gets sales summary for a date range
    /// </summary>
    Task<SalesPeriodReportDto> GetSalesPeriodReportAsync(DateTime startDate, DateTime endDate, CancellationToken ct = default);

    /// <summary>
    /// Gets top selling products for a date range
    /// </summary>
    Task<System.Collections.Generic.List<ProductSalesDto>> GetTopProductsAsync(DateTime startDate, DateTime endDate, int topCount = 10, CancellationToken ct = default);
}