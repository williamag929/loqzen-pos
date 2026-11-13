using System;
using Core.Domain.Common;

namespace Core.Domain.Entities;

/// <summary>
/// Store configuration for receipts, reports, and customer display
/// </summary>
public class StoreConfiguration : BaseEntity
{
    public string StoreName { get; set; } = "LOQZEN POS";
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string TaxId { get; set; } = string.Empty;
    public string TaxRegime { get; set; } = string.Empty; // e.g. "Responsable de IVA"
    public string ReceiptFooterMessage { get; set; } = "Gracias por su compra!";
    public string ReceiptLogoAscii { get; set; } = string.Empty; // Optional ASCII art logo
    public string Currency { get; set; } = "COP"; // ISO 4217
    public string CultureCode { get; set; } = "es-CO";
    
    // Electronic invoicing
    public bool EnableElectronicInvoicing { get; set; }
    public string InvoicingProviderUrl { get; set; } = string.Empty;
    public string InvoicingApiKey { get; set; } = string.Empty;
    
    // Receipt settings
    public int ReceiptWidth { get; set; } = 32; // Characters per line (typical 80mm thermal)
    public bool PrintCustomerCopy { get; set; } = true;
    public bool PrintMerchantCopy { get; set; } = false;
    
    // Singleton pattern - only one store config allowed
    public bool IsActive { get; set; } = true;
}